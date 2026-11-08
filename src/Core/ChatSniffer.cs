using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using AlbionCompanion.Core.DamageMeter;
using AlbionCompanion.Core.Gathering;
using AlbionCompanion.Core.Income;
using AlbionCompanion.Core.Party;

namespace AlbionCompanion.Core
{
    /// <summary>
    /// Socket 抓包模式（移植自 SAT SocketsPacketProvider）：Raw Socket + SIO_RCVALL，免 Npcap。
    /// </summary>
    public static class ChatSniffer
    {
        public static event Action<string> OnStatus;
        public static event Action<string> OnChatLine;

        // 目标 ObjectId -> 名字 映射
        // CD 承载：抓包线程写、UI/多抓包线程读；原先的 Dictionary 会在并发写时破坏内部结构。
        private static readonly ConcurrentDictionary<long, string> _objectNames = new ConcurrentDictionary<long, string>();
        private static readonly ConcurrentDictionary<long, string> _playerNames = new ConcurrentDictionary<long, string>(); // 玩家名字字典
        private static readonly ConcurrentDictionary<long, string> _mobNames = new ConcurrentDictionary<long, string>(); // 怪物名字字典（照搬 SAT：区分玩家和怪物）

        // C8/Bug A：未解析的「玩家侧」目标 ObjectId → 首次命中时刻。
        // 作用：evt=6 命中一个判得出是玩家、但名字字典里没有的 target 时挂起，
        // 等 evt=29 NewCharacter 写名字字典后补名（与 C6 队伍 pending 反查同一思路：
        // 先挂起、后确认、宁可未知不可错标）。
        private static readonly ConcurrentDictionary<long, DateTime> _pendingTargetIds = new ConcurrentDictionary<long, DateTime>();
        private const int MaxPendingTargets = 512;                     // 防无界增长（未知目标长期不出现时裁掉最旧的）
        private static readonly TimeSpan PendingTargetTtl = TimeSpan.FromMinutes(10); // 与 C6 队伍 pending 同一超时口径

        // R2/C4：-1「未知玩家」后补认领（设计 §4.6）——10 秒窗口内的唯一新角色才有资格认领
        private static readonly object _attrGate = new object();
        private static DateTime _lastUnknownHitUtc = DateTime.MinValue;                       // -1 最近一次命中的 UtcNow
        private static readonly List<(long Id, string Name, DateTime Utc)> _recentNewChars = new List<(long, string, DateTime)>(); // 窗口内新见到的角色

        private static void UpdatePlayerLastSeen(long objectId)
        {
            if (GameEvents.Players.TryGetValue(objectId, out var player))
            {
                player.LastSeen = DateTime.Now;
            }
        }

        // R2/C4（设计 §4.6）：evt=29 新角色出现时尝试把 -1「未知玩家」的累计并入该角色。
        // 保守三条件（任一不满足即放弃归因，-1 以「未知玩家」常驻——宁可未知，不可错挂）：
        //   ① -1 在最近 10 秒内有伤害命中；② 该 10 秒窗口内只出现了这一个新角色（唯一候选）；
        //   ③ 该 id 此前不在 _playerNames（真正的新面孔，由调用方在写字典前取快照传入）。
        private static void TryAttributeUnknownPlayer(long id, string name, bool wasKnownPlayer)
        {
            DateTime nowUtc = DateTime.UtcNow;
            lock (_attrGate)
            {
                // 记录本次新面孔，并把 10 秒窗口外的旧记录裁掉
                if (!wasKnownPlayer)
                    _recentNewChars.Add((id, name, nowUtc));
                _recentNewChars.RemoveAll(x => (nowUtc - x.Utc).TotalSeconds > 10);

                if (wasKnownPlayer) return;                    // 条件③：旧面孔不参与归因
                if (_lastUnknownHitUtc == DateTime.MinValue) return; // 从未出现过 -1 命中
                if ((nowUtc - _lastUnknownHitUtc).TotalSeconds > 10) return; // 条件①

                var inWindow = _recentNewChars;                 // 已裁剪，剩窗口内新面孔
                if (inWindow.Count != 1 || inWindow[0].Id != id) return; // 条件②：唯一候选且就是本人

                // 三条件全满足 → 把 -1 的累计并入该角色（数值相加，见 DamageStatsTracker.RenameKey）
                bool merged = GameEvents.DamageTracker.RenameKey(-1, id, string.IsNullOrEmpty(name) ? null : name);
                if (merged)
                {
                    // 归因后清窗口，避免同一次 -1 份额被第二个新角色再认领
                    _recentNewChars.Clear();
                    _lastUnknownHitUtc = DateTime.MinValue;
                    ReportStatus($"[归因] 未知玩家的战绩已并入 {name}");
                }
            }
        }

        private static bool _running;
        private static Thread _thread;
        private static readonly List<Socket> _sockets = new List<Socket>();
        private static readonly ConcurrentDictionary<string, bool> _seenPairs = new ConcurrentDictionary<string, bool>();
        // _lastSilver 已删除（R1/C3）：evt=81 余额基线由 LocalPlayerIdentity 内部维护
        // （P2-7 指出该字段只写不读；新用途——余额严格匹配——需要原始 FixPoint 值而非 ÷10000 的显示值）

        static ChatSniffer()
        {
            PhotonParser.OnDebug += delegate(string s) { ReportStatus(s); };
            LocalPlayerIdentity.OnStatus += delegate(string s) { ReportStatus(s); };
            // R7/C6：PartyState / PartyCacheStore 的状态行转发到事件日志（与抓包状态同一出口）
            PartyState.OnStatus += ReportStatus;
            PartyCacheStore.OnStatus += ReportStatus;
        }

        public static bool IsAvailable()
        {
            return true; // Socket 模式不需要外部驱动
        }

        public static void Start(string adapterName)
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(delegate()
            {
                try { RunCapture(); }
                catch (Exception ex)
                {
                    ReportStatus("异常: " + ex.Message);
                    _running = false;
                }
            });
            _thread.IsBackground = true;
            _thread.Start();
        }

        public static void Stop()
        {
            _running = false;
            lock (_sockets)
            {
                foreach (var s in _sockets)
                {
                    try { s.Close(); } catch { }
                }
                _sockets.Clear();
            }
        }

        private static void RunCapture()
        {
            // 移植自 SAT: 枚举所有本地 IPv4 地址，每个开一个 Raw Socket
            var localIPs = new List<IPAddress>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        localIPs.Add(ua.Address);
                }
            }

            ReportStatus("找到 " + localIPs.Count + " 个本地 IPv4 地址");

            foreach (var ip in localIPs)
            {
                try
                {
                    var s = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
                    s.Bind(new IPEndPoint(ip, 0));
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.HeaderIncluded, true);
                    var byTrue = new byte[] { 1, 0, 0, 0 };
                    var byOut = new byte[4];
                    s.IOControl(IOControlCode.ReceiveAll, byTrue, byOut);
                    _sockets.Add(s);
                    ReportStatus("RawSocket 已绑定: " + ip);
                }
                catch (Exception ex)
                {
                    ReportStatus("RawSocket 绑定 " + ip + " 失败: " + ex.Message);
                }
            }

            if (_sockets.Count == 0)
            {
                ReportStatus("没有可用的 RawSocket");
                _running = false;
                return;
            }

            ReportStatus("抓包已启动，等待数据包...");

            // 每个 socket 一个接收线程
            var threads = new List<Thread>();
            foreach (var s in _sockets.ToArray())
            {
                var t = new Thread(delegate() { ReceiveLoop(s); });
                t.IsBackground = true;
                t.Start();
                threads.Add(t);
            }

            foreach (var t in threads) t.Join();
        }

        private static void ReceiveLoop(Socket socket)
        {
            var buffer = new byte[65535];
            while (_running)
            {
                try
                {
                    int bytes = socket.Receive(buffer);
                    if (bytes <= 0) continue;

                    // 复制一份再处理
                    var frame = new byte[bytes];
                    Array.Copy(buffer, frame, bytes);
                    ProcessIPv4(frame);
                }
                catch (SocketException)
                {
                    if (!_running) break;
                }
                catch (Exception)
                {
                    // 忽略
                }
            }
        }

        // 移植自 SAT SocketsPacketProvider.ProcessIPv4
        private static void ProcessIPv4(byte[] frame)
        {
            if (frame.Length < 20) return;

            int version = frame[0] >> 4;
            if (version != 4) return;

            int ihl = (frame[0] & 0x0F) * 4;
            if (ihl < 20 || frame.Length < ihl) return;

            // 跳过分片
            ushort flagsFrag = (ushort)((frame[6] << 8) | frame[7]);
            bool hasMoreFragments = (flagsFrag & 0x2000) != 0;
            int fragOffset = (flagsFrag & 0x1FFF) * 8;
            if (hasMoreFragments || fragOffset != 0) return;

            byte proto = frame[9];
            if (proto != 17) return; // 只处理 UDP

            if (frame.Length < ihl + 8) return;

            int udpOff = ihl;
            int srcPort = (frame[udpOff] << 8) | frame[udpOff + 1];
            int dstPort = (frame[udpOff + 2] << 8) | frame[udpOff + 3];
            int udpLen = (frame[udpOff + 4] << 8) | frame[udpOff + 5];

            int payloadOff = udpOff + 8;
            int maxPayload = frame.Length - payloadOff;
            int payloadLen = Math.Min(maxPayload, Math.Max(0, udpLen - 8));
            if (payloadLen <= 0) return;

            // 检查是否是 Photon: 端口 5055/5056/5058 或首字节 F1/F2/FE
            bool isPhotonPort = srcPort == 5055 || dstPort == 5055 ||
                               srcPort == 5056 || dstPort == 5056 ||
                               srcPort == 5058 || dstPort == 5058;

            bool looksPhoton = false;
            if (payloadOff < frame.Length)
            {
                byte b0 = frame[payloadOff];
                looksPhoton = (b0 == 0xF1 || b0 == 0xF2 || b0 == 0xFE);
            }

            if (!isPhotonPort && !looksPhoton) return;

            string pair = srcPort + "->" + dstPort;
            if (_seenPairs.TryAdd(pair, true))
            {
                ReportStatus("Photon " + pair + " len=" + payloadLen);
            }

            // 提取 UDP payload 传给解析器
            var payload = new byte[payloadLen];
            Array.Copy(frame, payloadOff, payload, 0, payloadLen);

            var events = PhotonParser.ParseUdpPayload(payload);
            foreach (var evt in events)
            {
                if (evt.GameEventCode == -1) continue; // 过滤掉无游戏事件码的普通 Photon 事件

                string desc = "[gameEvt " + evt.GameEventCode + "]";
                if (OnChatLine != null) try { OnChatLine(desc); } catch { }

                // 写入文件日志，方便调试
                try
                {
                    var sb = new StringBuilder();
                    sb.Append(DateTime.Now.ToString("HH:mm:ss.fff"));
                    sb.Append(" evt=").Append(evt.GameEventCode);
                    if (evt.Parameters != null)
                    {
                        foreach (var kv in evt.Parameters)
                        {
                            if (kv.Key == 252) continue;
                            sb.Append(" | p").Append(kv.Key).Append("=");
                            if (kv.Value is byte[] bytes)
                                sb.Append("[").Append(bytes.Length).Append("b]");
                            else if (kv.Value is object[] objs)
                                sb.Append("[").Append(objs.Length).Append("]:").Append(string.Join(",", objs));
                            else
                                sb.Append(kv.Value);
                        }
                    }
                    File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "events.log"), sb.ToString() + Environment.NewLine);
                }
                catch { }

                // 关键游戏事件处理
                // 81 = UpdateMoney (本地玩家银币总量更新) - 不直接入账；R1/C3 起：
                // p0=ObjectId、p1=当前余额（原始 FixPoint），交给身份状态机做「余额严格匹配」晋升判定
                if (evt.GameEventCode == 81 && evt.Parameters != null)
                {
                    object idVal, silverVal;
                    if (evt.Parameters.TryGetValue(0, out idVal) && evt.Parameters.TryGetValue(1, out silverVal))
                    {
                        try
                        {
                            long objectId = Convert.ToInt64(idVal);
                            long silverRaw = Convert.ToInt64(silverVal);
                            LocalPlayerIdentity.OnUpdateMoney(objectId, silverRaw);
                        }
                        catch { }
                    }
                }
                // 62 = TakeSilver (拾取银币) - R1/C3：是否入账由 LocalPlayerIdentity 裁决
                // （CountAsMine=正式入账 / Pending=候选挂起 / Ignore=别人的钱），不再「首个拾取者自动认定」。
                // p0=ObjectId(拾取者), p3=YieldPreTax(FixPoint), p4=ClusterTax, p5=GuildTax, p6=AlliancePenalty, p8=倍率(原值记档不参与计算)
                else if (evt.GameEventCode == 62 && evt.Parameters != null)
                {
                    try
                    {
                        long pickerId = Convert.ToInt64(evt.Parameters[0]);
                        long yieldPreTax = Convert.ToInt64(evt.Parameters[3]);
                        long clusterTax = evt.Parameters.TryGetValue(4, out var ct) ? Convert.ToInt64(ct) : 0;
                        long guildTax = evt.Parameters.TryGetValue(5, out var gt) ? Convert.ToInt64(gt) : 0;
                        long alliancePenalty = evt.Parameters.TryGetValue(6, out var ap) ? Convert.ToInt64(ap) : 0;
                        long afterTaxRaw = Math.Max(0, yieldPreTax - clusterTax - guildTax - alliancePenalty);

                        // 税率/FixPoint 计算逻辑保持不变；身份裁决用原始 FixPoint 值（余额严格比对）
                        var disposition = LocalPlayerIdentity.OnTakeSilver(pickerId, afterTaxRaw);
                        if (disposition != TakeSilverDisposition.Ignore && afterTaxRaw > 0)
                        {
                            string? p8note = null;
                            if (evt.Parameters.TryGetValue(8, out var p8))
                            {
                                try { p8note = "p8=" + Convert.ToInt64(p8); } catch { }
                            }
                            var rec = new IncomeRecord
                            {
                                TimeUtc = DateTime.UtcNow,
                                Source = IncomeSource.SilverPickup,
                                SourceLabel = "拾取银币",
                                PickerId = pickerId,
                                SilverBeforeTax = yieldPreTax / 10000,
                                TaxPaid = (clusterTax + guildTax + alliancePenalty) / 10000,
                                SilverAfterTax = afterTaxRaw / 10000,
                                AttributionNote = p8note ?? ""
                            };
                            if (disposition == TakeSilverDisposition.CountAsMine)
                                IncomeTracker.AddConfirmed(rec);
                            else
                                IncomeTracker.AddPending(rec);
                        }
                    }
                    catch { }
                }
                // 72 = UpdateFame (声望更新)
                // 1:TotalFame, 2:FameWithZoneMultiplier, 3:ZoneFame, 4:Multiplier, 5:IsPremiumBonus
                else if (evt.GameEventCode == 72 && evt.Parameters != null)
                {
                    object totalFame, gainedFame;
                    if (evt.Parameters.TryGetValue(1, out totalFame) && evt.Parameters.TryGetValue(2, out gainedFame))
                    {
                        try
                        {
                            long total = Convert.ToInt64(totalFame);
                            long gained = Convert.ToInt64(gainedFame);
                            string debug = "[声望] 总声望=" + total + " 本次获得=" + gained;
                            if (OnChatLine != null) try { OnChatLine(debug); } catch { }

                            // 把声望值加到最近的一条采集记录里
                            if (gained > 0)
                            {
                                // 必须走锁内更新：evt=72 与 evt=61 可能落在不同线程
                                GameEvents.TryUpdateLastGatheringFame((int)gained);
                                // C7/§8.6：聚合侧同口径覆盖最近一条的 Fame
                                GatheringTracker.UpdateLastFame((int)gained);
                            }
                        }
                        catch { }
                    }
                }
                // 61 = HarvestFinished (采集完成)
                // 0:UserObjectId, 1:ActionId, 4:ItemId, 5:StandardAmount, 6:CollectorBonus, 7:PremiumBonus
                else if (evt.GameEventCode == 61 && evt.Parameters != null)
                {
                    object itemId, amount;
                    if (evt.Parameters.TryGetValue(4, out itemId) && evt.Parameters.TryGetValue(5, out amount))
                    {
                        try
                        {
                            int id = Convert.ToInt32(itemId);
                            // 调试：打印实际 itemId
                            string debug = "[采集] ItemId=" + id + " Amount=" + amount.ToString();
                            if (OnChatLine != null) try { OnChatLine(debug); } catch { }

                            // 用 ItemDatabase 映射物品名
                            string resName = ItemDatabase.GetDisplayName(id);

                            GameEvents.AddGathering(new GameEvents.GatheringEntry
                            {
                                Time = DateTime.Now,
                                Resource = resName,
                                Amount = Convert.ToInt32(amount),
                                Fame = 0
                            });
                            // C7/§8.6：采集权威源同步落账（总览采集卡片改读 GatheringTracker）
                            // evt=61 不产生 GatheringSale 收益条目——§8.3.2：采集出售为手工账本口径，不并入抓包合计
                            GatheringTracker.Add(new GatheringRecord
                            {
                                TimeUtc = DateTime.UtcNow,
                                ItemId = id,
                                Resource = resName,
                                Amount = Convert.ToInt32(amount),
                                Fame = 0
                            });
                        }
                        catch { }
                    }
                }
                // 62 = TakeSilver (拾取银币)
                // 0:ObjectId, 1:TimeStamp, 2:TargetEntityId, 3:YieldPreTax, 4:ClusterTax, 5:GuildTax, 6:AlliancePenalty
                // TODO: 暂时关闭，因为我们还不知道玩家自己的 ObjectId，会把别人捡的钱也记成我们的
                // else if (evt.GameEventCode == 62 && evt.Parameters != null)
                // {
                //     object yieldPreTaxObj, targetEntityObj;
                //     if (evt.Parameters.TryGetValue(3, out yieldPreTaxObj) && evt.Parameters.TryGetValue(2, out targetEntityObj))
                //     {
                //         try
                //         {
                //             long pickerId = Convert.ToInt64(targetEntityObj);
                //
                //             // 只有当拾取者不是我们认识的玩家时，才记录为收益
                //             // （我们自己的 ObjectId 不会触发 NewCharacter 事件，不在字典里）
                //             if (!_objectNames.ContainsKey(pickerId))
                //             {
                //                 // FixPoint 定点数：实际值 = InternalValue / 10000
                //                 long yieldPreTaxInternal = Convert.ToInt64(yieldPreTaxObj);
                //                 long clusterTaxInternal = evt.Parameters.TryGetValue(4, out var clusterTaxObj) ? Convert.ToInt64(clusterTaxObj) : 0;
                //                 long guildTaxInternal = evt.Parameters.TryGetValue(5, out var guildTaxObj) ? Convert.ToInt64(guildTaxObj) : 0;
                //                 long alliancePenaltyInternal = evt.Parameters.TryGetValue(6, out var alliancePenaltyObj) ? Convert.ToInt64(alliancePenaltyObj) : 0;
                //
                //                 long yieldAfterTaxInternal = Math.Max(0, yieldPreTaxInternal - clusterTaxInternal - guildTaxInternal - alliancePenaltyInternal);
                //                 long silver = yieldAfterTaxInternal / 10000; // FixPoint 转整数
                //
                //                 GameEvents.AddIncome(new GameEvents.IncomeEntry
                //                 {
                //                     Time = DateTime.Now,
                //                     Silver = silver,
                //                     Source = "拾取银币"
                //                 });
                //             }
                //         }
                //         catch { }
                //     }
                // }
                // 29 = NewCharacter (新角色出现)
                // 0:ObjectId, 1:Name, 7:Guid, 8:GuildName
                else if (evt.GameEventCode == 29 && evt.Parameters != null)
                {
                    object objId, name;
                    if (evt.Parameters.TryGetValue(0, out objId) && evt.Parameters.TryGetValue(1, out name))
                    {
                        try
                        {
                            long id = Convert.ToInt64(objId);
                            string charName = name.ToString();
                            // R2/C4：是否属于「真正的新面孔」——在写字典前先取快照（§4.6 条件③）
                            bool wasKnownPlayer = _playerNames.ContainsKey(id);
                            _objectNames[id] = charName;
                            _playerNames[id] = charName; // 标记这是玩家

                            // 存到附近玩家列表
                            string guild = "";
                            object guildObj;
                            if (evt.Parameters.TryGetValue(8, out guildObj))
                                guild = guildObj?.ToString() ?? "";

                            if (GameEvents.Players.TryGetValue(id, out var existing))
                            {
                                existing.LastSeen = DateTime.Now;
                                if (!string.IsNullOrEmpty(guild)) existing.Guild = guild;
                            }
                            else
                            {
                                GameEvents.Players[id] = new GameEvents.PlayerEntry
                                {
                                    ObjectId = id,
                                    Name = charName,
                                    FirstSeen = DateTime.Now,
                                    LastSeen = DateTime.Now,
                                    Guild = guild
                                };
                                GameEvents.FireUpdate();
                            }

                            // R1/C3：名字字典写完之后再联动身份状态机（设计 §3.5 固定顺序，
                            // 确保后续逻辑能读到自己）。C6 将在此追加 PartyState.OnNewCharacter。
                            LocalPlayerIdentity.OnNewCharacter(id, charName);

                            // R2/C4：-1 未知玩家的后补认领（设计 §4.6，保守规则）——
                            // 仅当 ① -1 在最近 10 秒内有命中；② 该窗口内只新增了这一个角色（唯一候选）；
                            // ③ 此 id 此前不在 _playerNames（真正的新面孔）。三者同时成立才把 -1 的累计
                            // 并入该角色；否则 -1 以「未知玩家」常驻（宁可未知，不可错挂队友头上）。
                            TryAttributeUnknownPlayer(id, charName, wasKnownPlayer);

                            // R7/C6：队伍 pending ObjectId 反查入口（设计 §7.4——evt=234 给 id 不给名，
                            // 用 evt=29 的名字补齐；只有唯一候选才提交）
                            PartyState.OnNewCharacter(id, charName);

                            // C8/Bug A：目标名 pending 反查——名字此刻已写入 _objectNames，
                            // 该 id 若曾被标成「未知玩家」即完成补名（之后同一目标的 evt=6 直接取名字）
                            ResolvePendingTarget(id, charName, DateTime.UtcNow);
                        }
                        catch { }
                    }
                }
                // 123 = NewMob (新怪物出现)
                // 0:ObjectId, 1:MobIndex
                else if (evt.GameEventCode == 123 && evt.Parameters != null)
                {
                    object objId, mobIndex;
                    if (evt.Parameters.TryGetValue(0, out objId) && evt.Parameters.TryGetValue(1, out mobIndex))
                    {
                        try
                        {
                            long id = Convert.ToInt64(objId);
                            int idx = Convert.ToInt32(mobIndex);
                            string mobName = MobDatabase.GetMobNameByIndex(idx);
                            if (!string.IsNullOrEmpty(mobName))
                            {
                                _objectNames[id] = mobName;
                                _mobNames[id] = mobName; // 标记这是怪物
                                if (OnChatLine != null) try { OnChatLine("[怪物] ObjectId=" + id + " MobIndex=" + idx + " Name=" + mobName); } catch { }
                            }
                            else
                            {
                                // 即使没找到名字，也标记为怪物
                                // C8/Bug A 同类修复：旧代码把 ObjectId 拼成「怪物 {id}」写进 _mobNames，
                                // 而 _mobNames 的值会经 _objectNames 流到战斗页/总览页的用户可见目标名。
                                // 缺名就显式说缺名，不拿裸 id 充数。
                                _mobNames[id] = ChatSnifferNames.UnknownUnit;
                                if (OnChatLine != null) try { OnChatLine("[怪物] ObjectId=" + id + " MobIndex=" + idx + " (未找到名字)"); } catch { }
                            }
                        }
                        catch { }
                    }
                }
                // 6 = HealthUpdate (血量更新)
                // 0:AffectedObjectId, 1:TimeStamp, 2:HealthChange, 3:NewHealthValue, 4:EffectType, 6:CauserId, 7:SpellIndex
                else if (evt.GameEventCode == 6 && evt.Parameters != null)
                {
                    object healthChange, targetId;
                    if (evt.Parameters.TryGetValue(2, out healthChange) && evt.Parameters.TryGetValue(0, out targetId))
                    {
                        try
                        {
                            double change = Convert.ToDouble(healthChange);
                            long target = Convert.ToInt64(targetId);

                            // 更新玩家最后出现时间
                            UpdatePlayerLastSeen(target);

                            // R2/C4：causer 存在性单独判定（参数缺失 ≠ 显式 0），不再默认 causer=0
                            bool hasCauserParam = evt.Parameters.ContainsKey(6);
                            long causer = 0;
                            if (hasCauserParam)
                            {
                                causer = Convert.ToInt64(evt.Parameters[6]);
                                UpdatePlayerLastSeen(causer);
                            }

                            // 统一 causer 解析（伤害/治疗共用，消除原两段复制粘贴分叉）：
                            // 怪物 → -9 不统计；我 → 0；已知玩家 → 真实 id；未知 → -1/-3 负键
                            var ci = CauserResolver.Resolve(causer, hasCauserParam, _mobNames, _playerNames);
                            if (ci.Countable)
                            {
                                if (ci.TrackerKey == -1)
                                {
                                    // 未知玩家命中时间戳，供 evt=29 后补认领的 10 秒窗口判定（设计 §4.6）
                                    lock (_attrGate) { _lastUnknownHitUtc = DateTime.UtcNow; }
                                }

                                string targetName = TargetDisplayName(target);
                                if (change < 0)
                                {
                                    // 血量下降 = 伤害（怪物打人已在 ResolveCauser 过滤，不进榜）
                                    long damage = (long)Math.Abs(change);
                                    GameEvents.AddCombat(new GameEvents.CombatEntry
                                    {
                                        Time = DateTime.Now,
                                        Target = targetName,
                                        Damage = (int)damage,
                                        Type = "damage",
                                        CauserName = ci.DisplayName
                                    });

                                    // P1-5：读真实 p3（NewHealthValue）；缺失传 -1 表示未知，
                                    // 禁止再用硬编码 0 冒充「刚好打死」（那会让每次命中都被记为最后一击）
                                    double newHp = evt.Parameters.TryGetValue(3, out var hp) ? Convert.ToDouble(hp) : -1;
                                    bool isMobTarget = _mobNames.ContainsKey(target);
                                    GameEvents.DamageTracker.RecordDamage(ci.TrackerKey, ci.DisplayName, target, damage, newHp, isMobTarget);
                                }
                                else if (change > 0)
                                {
                                    // 血量上升 = 治疗
                                    long heal = (long)change;
                                    GameEvents.AddCombat(new GameEvents.CombatEntry
                                    {
                                        Time = DateTime.Now,
                                        Target = targetName,
                                        Damage = (int)heal,
                                        Type = "heal",
                                        CauserName = ci.DisplayName
                                    });

                                    GameEvents.DamageTracker.RecordHeal(ci.TrackerKey, ci.DisplayName, heal);
                                }
                            }
                        }
                        catch { }
                    }
                }
                // 21 = CastHit (攻击命中)
                else if (evt.GameEventCode == 21 && evt.Parameters != null)
                {
                    // 攻击命中事件（保留，但不再硬编码伤害值，实际伤害从 HealthUpdate 获取）
                }
                // 163 = KilledPlayer (击杀玩家)
                // 0:KillerObjectId, 1:KilledPlayerObjectId, 2:KilledPlayerName
                else if (evt.GameEventCode == 163 && evt.Parameters != null)
                {
                    object killedNameObj;
                    if (evt.Parameters.TryGetValue(2, out killedNameObj))
                    {
                        try
                        {
                            // C8/Bug A 同类修复：旧代码 killedNameObj?.ToString() ?? "未知玩家"，
                            // 如果 p2 实际给的是 ObjectId，数字会直接进用户可见文案。
                            // 改走统一口径：非空名字照用，数值一律当 ObjectId 走解析（绝不原样显示）。
                            object? killedIdObj;
                            evt.Parameters.TryGetValue(1, out killedIdObj);
                            string killedName = NameOrUnknown(killedNameObj, killedIdObj, "未知玩家");
                            GameEvents.AddCombat(new GameEvents.CombatEntry
                            {
                                Time = DateTime.Now,
                                Target = killedName,
                                Damage = 0,
                                Type = "kill"
                            });
                        }
                        catch { }
                    }
                }
                // 164 = Died (玩家死亡)
                // 1:DiedObjectId, 2:Died（名字）, 10:KilledBy（击杀者名字）
                else if (evt.GameEventCode == 164 && evt.Parameters != null)
                {
                    object diedNameObj;
                    if (evt.Parameters.TryGetValue(2, out diedNameObj))
                    {
                        try
                        {
                            // C8/Bug A 同类修复：旧代码 diedNameObj?.ToString() ?? "未知" 与
                            // killedByObj?.ToString() ?? ""，两者若实际给的是 ObjectId，裸数字都会
                            // 经 Target 拼成「119428 被 119429 击杀」进用户可见事件流。
                            // 统一走 NameOrUnknown：非空名字照用，数值一律按 ObjectId 解析成
                            // 「未知玩家 / 未知单位」，绝不原样显示。
                            object? diedIdObj;
                            evt.Parameters.TryGetValue(1, out diedIdObj);
                            string diedName = NameOrUnknown(diedNameObj, diedIdObj, "未知");

                            string killedBy = "";
                            object killedByObj;
                            if (evt.Parameters.TryGetValue(10, out killedByObj))
                                killedBy = NameOrUnknown(killedByObj, null, "");

                            string target;
                            string type;
                            if (!string.IsNullOrEmpty(killedBy))
                            {
                                // 他杀：被其他玩家击杀
                                target = diedName + " 被 " + killedBy + " 击杀";
                                type = "death";
                            }
                            else
                            {
                                // 自杀/意外死亡：摔死、毒死等
                                target = diedName + " 意外死亡";
                                type = "suicide";
                            }

                            GameEvents.AddCombat(new GameEvents.CombatEntry
                            {
                                Time = DateTime.Now,
                                Target = target,
                                Damage = 0,
                                Type = type
                            });
                        }
                        catch { }
                    }
                }
                // 257 = InCombatStateUpdate (战斗状态更新)
                // 0:ObjectId, 1:true=玩家打敌人, 2:true=敌人打玩家
                else if (evt.GameEventCode == 257 && evt.Parameters != null)
                {
                    object inCombatVal;
                    if (evt.Parameters.TryGetValue(1, out inCombatVal))
                    {
                        try
                        {
                            bool inCombat = Convert.ToBoolean(inCombatVal);
                            if (inCombat)
                            {
                                GameEvents.AddCombat(new GameEvents.CombatEntry
                                {
                                    Time = DateTime.Now,
                                    Target = "战斗中",
                                    Damage = 0,
                                    Type = "enter"
                                });
                            }
                        }
                        catch { }
                    }
                }
                // 169 = KilledPlayer (击杀玩家)
                else if (evt.GameEventCode == 169)
                {
                    GameEvents.AddCombat(new GameEvents.CombatEntry
                    {
                        Time = DateTime.Now,
                        Target = "玩家",
                        Damage = 0,
                        Type = "kill"
                    });
                }
                // 170 = Died (死亡)
                else if (evt.GameEventCode == 170)
                {
                    GameEvents.AddCombat(new GameEvents.CombatEntry
                    {
                        Time = DateTime.Now,
                        Target = "死亡",
                        Damage = 0,
                        Type = "death"
                    });
                }
                // 230 = PartyJoined (加入队伍，收到完整队伍名单)
                // 实际抓包确认：p8=GUID字节数组(16B/成员)，p9=名字数组 ["laowang0729","aYi777"]
                // R7/C6：整段迁入 PartyState.OnPartyJoined（全量重建 + GUID 绑定 + 落盘，§7.3）
                else if (evt.GameEventCode == 230 && evt.Parameters != null)
                {
                    try
                    {
                        evt.Parameters.TryGetValue(8, out var guidObj);
                        evt.Parameters.TryGetValue(9, out var namesObj);
                        PartyState.OnPartyJoined(guidObj, namesObj);
                        ReportStatus($"[队伍] evt=230 参数：p8={(guidObj is byte[] gb ? gb.Length + "B" : "无")} p9={namesObj}");
                        GameEvents.FireUpdate();
                    }
                    catch { }
                }
                // 234 = PartyPlayerJoined (队伍成员加入) - 实际抓包确认：p0=ObjectId, p1=GUID字节
                else if (evt.GameEventCode == 234 && evt.Parameters != null)
                {
                    try
                    {
                        evt.Parameters.TryGetValue(0, out var idObj);
                        evt.Parameters.TryGetValue(1, out var guidObj);
                        ReportStatus($"[队伍] evt=234 参数：p0={idObj} p1={(guidObj is byte[] gb ? gb.Length + "B" : guidObj)}");
                        PartyState.OnPartyPlayerJoined(idObj, guidObj);
                        GameEvents.FireUpdate();
                    }
                    catch { }
                }
                // 237 = PartyDisbanded (队伍解散) - 预估事件码，未实机验证
                // R7/C6：状态迁入 PartyState；参数布局必须打印原始值以便实机确认（§7.3）
                else if (evt.GameEventCode == 237)
                {
                    ReportStatus("[队伍] evt=237 参数：" + DescribeParams(evt.Parameters));
                    PartyState.OnPartyDisbanded();
                    GameEvents.FireUpdate();
                }
                // 239 = PartyPlayerUpdated - 参数语义未完全确认（HANDOFF：p0=名字, p1=True, p3=5）
                // 保守处理：不新增成员不改 id，仅刷新 lastSeen（§7.3）
                else if (evt.GameEventCode == 239 && evt.Parameters != null)
                {
                    ReportStatus("[队伍] evt=239 参数：" + DescribeParams(evt.Parameters));
                    PartyState.OnPartyPlayerUpdated(evt.Parameters.TryGetValue(0, out var n239) ? n239 : null);
                }
                // 241 = PartyLeaderChanged - 参数布局未实机验证
                // 只更新队长，不影响成员集合（§7.3）
                else if (evt.GameEventCode == 241 && evt.Parameters != null)
                {
                    ReportStatus("[队伍] evt=241 参数：" + DescribeParams(evt.Parameters));
                    PartyState.OnPartyLeaderChanged(evt.Parameters.TryGetValue(0, out var p241) ? p241 : null);
                    GameEvents.FireUpdate();
                }
                // 243 = PartySilverGained - 参数布局未实机验证
                // 队伍侧只刷成员活跃度；银币金额归收益模块（§8.3，C7 处理），PartyState 不碰钱
                else if (evt.GameEventCode == 243 && evt.Parameters != null)
                {
                    ReportStatus("[队伍] evt=243 参数：" + DescribeParams(evt.Parameters));
                    PartyState.OnPartySilverGained(evt.Parameters.TryGetValue(0, out var p243) ? p243 : null);
                    // C7/§8.3.2：收益侧记「队伍分成、金额=0、待确认」条目——金额字段位置未实机确认（V5），
                    // 确认前绝不猜测字段（否则与 evt=62 重复计数产生幻觉收益）
                    IncomeTracker.AddPartySilverUnconfirmed();
                }
            }
        }

        /// <summary>
        /// C8/Bug A：evt=6 目标名的唯一解析口径——**任何未解析目标都不得把裸 ObjectId 写进用户可见文案**。
        /// 类型判定复用现有三份字典（与 CauserResolver 同一口径），不引入新的猜测规则：
        ///   ① 名字字典命中 → 真实名字；
        ///   ② _mobNames 命中 → 非玩家 → 「未知单位」；
        ///   ③ 本地玩家或 _playerNames 命中 → 玩家 → 「未知玩家」+ 挂 pending 等 evt=29 补名；
        ///   ④ id&lt;=0 或三份字典都判不出来 → 保守按非玩家 → 「未知单位」。
        /// ④ 的取向是刻意的：宁可显示「未知单位」，也不给玩家挂错标签
        /// （与 C6 队伍反查「宁可未知，不可错认」同一条数据诚实性原则）。
        /// </summary>
        private static string TargetDisplayName(long target)
        {
            var display = TargetNameResolver.Resolve(target,
                LocalPlayerIdentity.IsSelf(target), _objectNames, _mobNames, _playerNames);

            if (display.NeedsPendingLookup)
                MarkPendingTarget(target);      // 玩家侧未解析 → 挂 pending，等 evt=29 落名字
            else
                _pendingTargetIds.TryRemove(target, out _); // 已解析（或判定为非玩家）→ 无需再挂

            return display.DisplayName;
        }

        /// <summary>
        /// C8/Bug A：把未解析的玩家侧目标挂进 pending（10 分钟 TTL，最多 512 条，超出裁最旧）。
        /// 名字由 evt=29 写入字典 —— 之后同一 target 的 evt=6 就能直接取到名字，这就是「补名」。
        /// 历史条目按 GameEvents 的不可变约定不回填，补名只对之后发生的事件生效。
        /// </summary>
        internal static void MarkPendingTarget(long targetId)
        {
            if (targetId <= 0) return;

            // 先按真实时间清掉超时的陈旧条目：TTL 不能只依赖 evt=29 到达时才触发清理，
            // 否则长时间等不到 NewCharacter 时，过期 id 会一直占着位置直到撞上 512 上限。
            SweepPendingTargets(DateTime.UtcNow);

            _pendingTargetIds[targetId] = DateTime.UtcNow;

            if (_pendingTargetIds.Count <= MaxPendingTargets) return;
            long oldestId = 0;
            DateTime oldest = DateTime.MaxValue;
            foreach (var kv in _pendingTargetIds)
            {
                if (kv.Value < oldest) { oldest = kv.Value; oldestId = kv.Key; }
            }
            if (oldestId != 0) _pendingTargetIds.TryRemove(oldestId, out _);
        }

        /// <summary>C8/Bug A：evt=29 到达时核对 pending —— 该 id 已被写入名字字典即算补名成功。</summary>
        private static void ResolvePendingTarget(long id, string name, DateTime nowUtc)
        {
            SweepPendingTargets(nowUtc);

            if (!_pendingTargetIds.TryRemove(id, out _)) return;
            if (!string.IsNullOrEmpty(name))
                ReportStatus($"[归因] 未知玩家已补名：{name}（ObjectId={id}）");
        }

        /// <summary>C8/Bug A：pending 目标的超时清理（与 C6 队伍 pending 同一 10 分钟口径）。</summary>
        internal static void SweepPendingTargets(DateTime nowUtc)
        {
            foreach (var kv in _pendingTargetIds)
            {
                if ((nowUtc - kv.Value) > PendingTargetTtl) _pendingTargetIds.TryRemove(kv.Key, out _);
            }
        }

        /// <summary>测试钩子：pending 目标数量（只读校验用）。</summary>
        internal static int PendingTargetCount { get { return _pendingTargetIds.Count; } }

        /// <summary>测试钩子：清空 pending（测试之间隔离用）。</summary>
        internal static void ResetPendingTargets() { _pendingTargetIds.Clear(); }

        /// <summary>
        /// C8/Bug A 同类修复：把事件里「可能是名字、也可能是 ObjectId」的参数转成用户可见文案。
        /// 非空且非纯数字 → 原样当名字用；纯数字 → 说明给的是 ObjectId，按统一口径解析成
        /// 「未知玩家 / 未知单位」，绝不把裸 id 显示给用户；null/空 → 用调用方给的兜底文案。
        /// </summary>
        internal static string NameOrUnknown(object? nameObj, object? idObj, string fallback)
        {
            string? raw = nameObj?.ToString();
            if (!string.IsNullOrWhiteSpace(raw) && !long.TryParse(raw, out _))
                return raw!;

            long id = 0;
            if (raw != null && long.TryParse(raw, out var fromRaw)) id = fromRaw;
            else if (idObj != null) { try { id = Convert.ToInt64(idObj); } catch { id = 0; } }

            if (id == 0) return fallback;

            var display = TargetNameResolver.Resolve(id, LocalPlayerIdentity.IsSelf(id),
                _objectNames, _mobNames, _playerNames);
            return display.DisplayName;
        }

        private static void ReportStatus(string s)
        {
            if (OnStatus != null) try { OnStatus(s); } catch { }
        }

        /// <summary>R7/C6：打印事件原始参数（237/239/241/243 参数布局未实机验证，必须留原始证据）</summary>
        private static string DescribeParams(Dictionary<byte, object>? parameters)
        {
            if (parameters == null || parameters.Count == 0) return "(无)";
            var parts = new List<string>();
            foreach (var kv in parameters)
            {
                string v = kv.Value is byte[] b ? b.Length + "B[" + Convert.ToHexString(b, 0, Math.Min(b.Length, 16)) + (b.Length > 16 ? "…" : "") + "]"
                          : kv.Value?.ToString() ?? "null";
                parts.Add("p" + kv.Key + "=" + v);
            }
            return string.Join(" ", parts);
        }


    }

    /// <summary>Mob 名字缺失时的统一显示名（C8/Bug A：不拿 ObjectId 充数）。</summary>
    internal static class ChatSnifferNames
    {
        internal const string UnknownPlayer = "未知玩家";
        internal const string UnknownUnit = "未知单位";
    }

    /// <summary>被命中目标的实体类型判定结果（C8/Bug A）。</summary>
    internal enum TargetKind
    {
        Player, // 判定为玩家（含本地玩家）
        Unit,   // 判定为非玩家（怪物 / 环境 / 判不出来）
    }

    /// <summary>目标显示名解析结果：DisplayName 是唯一允许写进用户可见文案的值。</summary>
    internal readonly struct TargetDisplay
    {
        internal readonly string DisplayName;
        internal readonly TargetKind Kind;
        /// <summary>true = 判得出是玩家但名字未知，调用方应挂 pending，等 evt=29 补名。</summary>
        internal readonly bool NeedsPendingLookup;

        internal TargetDisplay(string displayName, TargetKind kind, bool needsPendingLookup)
        {
            DisplayName = displayName;
            Kind = kind;
            NeedsPendingLookup = needsPendingLookup;
        }
    }

    /// <summary>
    /// C8/Bug A：目标名解析的纯函数口径（无静态状态，可脱离抓包直接单测）。
    /// 规则与 ChatSniffer.TargetDisplayName 一一对应，见该方法注释。
    /// </summary>
    internal static class TargetNameResolver
    {
        internal static TargetDisplay Resolve(
            long targetId,
            bool isSelf,
            IReadOnlyDictionary<long, string> objectNames,
            IReadOnlyDictionary<long, string> mobNames,
            IReadOnlyDictionary<long, string> playerNames)
        {
            // ① 名字字典命中：真实名字（ObjectId 不进文案）
            if (targetId > 0 && objectNames != null
                && objectNames.TryGetValue(targetId, out var name)
                && !string.IsNullOrEmpty(name))
            {
                var kind = mobNames != null && mobNames.ContainsKey(targetId) ? TargetKind.Unit : TargetKind.Player;
                return new TargetDisplay(name, kind, false);
            }

            // ② 非玩家（怪物/单位）：只判出「是单位」，名字仍缺 → 未知单位，不需要玩家侧反查
            if (mobNames != null && mobNames.ContainsKey(targetId))
                return new TargetDisplay(ChatSnifferNames.UnknownUnit, TargetKind.Unit, false);

            // ③ 玩家侧（本地玩家 / evt=29 已建档玩家）：名字还没落到字典 → 未知玩家 + 挂 pending 等补名
            if (isSelf || (targetId > 0 && playerNames != null && playerNames.ContainsKey(targetId)))
                return new TargetDisplay(ChatSnifferNames.UnknownPlayer, TargetKind.Player, true);

            // ④ 非正数 id 或三份字典都判不出来：保守按非玩家
            return new TargetDisplay(ChatSnifferNames.UnknownUnit, TargetKind.Unit, false);
        }
    }

    /// <summary>
    /// C8/Bug B：自绘控件的布尔状态位——把「值变化才算变化」这条语义从 WinForms 控件里剥离出来，
    /// 使其可以脱离句柄/消息循环直接单测（tests 工程只编译 src/Core，不引 WinForms）。
    ///
    /// 约定：<see cref="Set"/> 返回 true 表示值**真的变化**，调用方据此触发一次 Invalidate()；
    /// 返回 false 表示值没变，调用方**不得**重绘（避免切换筛选时未受影响的按钮跟着闪）。
    /// </summary>
    public sealed class ToggleState
    {
        private bool _value;

        public bool Value { get { return _value; } }

        /// <summary>已发生的「值变化」次数（= 应触发重绘的次数），供测试直接断言。</summary>
        public int ChangeCount { get; private set; }

        /// <summary>设置新值；只有真正变化时才返回 true 并累加一次变化计数。</summary>
        public bool Set(bool value)
        {
            if (_value == value) return false;
            _value = value;
            ChangeCount++;
            return true;
        }
    }
}
