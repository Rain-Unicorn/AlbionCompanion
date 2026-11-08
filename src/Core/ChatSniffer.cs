using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

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

        private static void UpdatePlayerLastSeen(long objectId)
        {
            if (GameEvents.Players.TryGetValue(objectId, out var player))
            {
                player.LastSeen = DateTime.Now;
            }
        }

        private static bool _running;
        private static Thread _thread;
        private static readonly List<Socket> _sockets = new List<Socket>();
        private static readonly ConcurrentDictionary<string, bool> _seenPairs = new ConcurrentDictionary<string, bool>();
        private static long _lastSilver = -1; // 上次银币总量，-1表示未初始化

        static ChatSniffer()
        {
            PhotonParser.OnDebug += delegate(string s) { ReportStatus(s); };
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
                // 81 = UpdateMoney (本地玩家银币总量更新) - 只用于校准，不计入收益
                if (evt.GameEventCode == 81 && evt.Parameters != null)
                {
                    object val;
                    if (evt.Parameters.TryGetValue(1, out val))
                    {
                        try
                        {
                            long silver = Convert.ToInt64(val) / 10000;
                            _lastSilver = silver;
                        }
                        catch { }
                    }
                }
                // 62 = TakeSilver (拾取银币) - SAT方式：只记录本地玩家自己拾取的
                // p0=ObjectId(拾取者), p3=YieldPreTax(FixPoint), p4=ClusterTax, p5=GuildTax, p6=AlliancePenalty
                else if (evt.GameEventCode == 62 && evt.Parameters != null)
                {
                    try
                    {
                        long pickerId = Convert.ToInt64(evt.Parameters[0]);
                        // Fallback：如果还没获取到本地玩家ObjectId（启动晚错过JoinResponse），
                        // 第一次捡到银币时自动识别为本地玩家
                        if (!GameEvents.LocalPlayerObjectId.HasValue)
                        {
                            GameEvents.LocalPlayerObjectId = pickerId;
                            ReportStatus($"[收益] 自动识别本地玩家 ObjectId={pickerId}");
                        }
                        // 只统计本地玩家自己拾取的
                        if (pickerId == GameEvents.LocalPlayerObjectId.Value)
                        {
                            long yieldPreTax = Convert.ToInt64(evt.Parameters[3]);
                            long clusterTax = evt.Parameters.TryGetValue(4, out var ct) ? Convert.ToInt64(ct) : 0;
                            long guildTax = evt.Parameters.TryGetValue(5, out var gt) ? Convert.ToInt64(gt) : 0;
                            long alliancePenalty = evt.Parameters.TryGetValue(6, out var ap) ? Convert.ToInt64(ap) : 0;
                            long afterTax = Math.Max(0, yieldPreTax - clusterTax - guildTax - alliancePenalty);
                            long silver = afterTax / 10000;
                            if (silver > 0)
                            {
                                GameEvents.AddIncome(new GameEvents.IncomeEntry
                                {
                                    Time = DateTime.Now,
                                    Source = "拾取银币",
                                    Silver = silver
                                });
                            }
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
                                _mobNames[id] = $"怪物 {id}";
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

                            // 也更新造成伤害的玩家
                            object causerObj;
                            long causer = 0;
                            if (evt.Parameters.TryGetValue(6, out causerObj))
                            {
                                causer = Convert.ToInt64(causerObj);
                                UpdatePlayerLastSeen(causer);
                            }

                            if (change < 0)
                            {
                                // 血量下降 = 伤害
                                // 完全照搬 SAT：只有当造成伤害的人不是怪物时才记录（也就是说，可能是玩家）
                                // 怪物打我们的伤害不记录
                                if (!_mobNames.ContainsKey(causer))
                                {
                                    long damage = (long)Math.Abs(change);
                                    // 名字 + tracker key 判定：
                                    // - 已知玩家字典里的 = 其他玩家，用真实 causerId 做 key
                                    // - 不在任何字典里 = 自己（含直接攻击/召唤物/DoT等），统一用 key=0 合并成一条
                                    string causerName;
                                    long trackerKey;
                                    if (GameEvents.LocalPlayerObjectId.HasValue && causer == GameEvents.LocalPlayerObjectId.Value && !string.IsNullOrEmpty(GameEvents.LocalPlayerName))
                                    {
                                        causerName = GameEvents.LocalPlayerName;
                                        trackerKey = 0;
                                    }
                                    else if (_playerNames.TryGetValue(causer, out var cn))
                                    {
                                        causerName = cn;
                                        trackerKey = causer;
                                    }
                                    else
                                    {
                                        causerName = "我自己";
                                        trackerKey = 0; // 所有"自己"相关伤害合并到同一条记录
                                    }
                                    string targetName = _objectNames.TryGetValue(target, out var tn) ? tn : $"目标 {target}";
                                    GameEvents.AddCombat(new GameEvents.CombatEntry
                                    {
                                        Time = DateTime.Now,
                                        Target = targetName,
                                        Damage = (int)damage,
                                        Type = "damage",
                                        CauserName = causerName
                                    });

                                    // 同时调用 SAT 的核心伤害追踪器（照搬 SAT）
                                    bool isMobTarget = _mobNames.ContainsKey(target);
                                    double newHealthValue = 0;
                                    GameEvents.DamageTracker.RecordDamage(trackerKey, causerName, target, damage, newHealthValue, isMobTarget);
                                }
                            }
                            else if (change > 0)
                            {
                                // 血量上升 = 治疗
                                // 完全照搬 SAT：只有当造成治疗的人不是怪物时才记录
                                if (!_mobNames.ContainsKey(causer))
                                {
                                    long heal = (long)change;
                                    string causerName;
                                    long trackerKey;
                                    if (GameEvents.LocalPlayerObjectId.HasValue && causer == GameEvents.LocalPlayerObjectId.Value && !string.IsNullOrEmpty(GameEvents.LocalPlayerName))
                                    {
                                        causerName = GameEvents.LocalPlayerName;
                                        trackerKey = 0;
                                    }
                                    else if (_playerNames.TryGetValue(causer, out var cn))
                                    {
                                        causerName = cn;
                                        trackerKey = causer;
                                    }
                                    else
                                    {
                                        causerName = "我自己";
                                        trackerKey = 0; // 所有"自己"相关治疗合并到同一条记录
                                    }
                                    string targetName = _objectNames.TryGetValue(target, out var tn) ? tn : $"目标 {target}";
                                    GameEvents.AddCombat(new GameEvents.CombatEntry
                                    {
                                        Time = DateTime.Now,
                                        Target = targetName,
                                        Damage = (int)heal,
                                        Type = "heal",
                                        CauserName = causerName
                                    });

                                    GameEvents.DamageTracker.RecordHeal(trackerKey, causerName, heal);
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
                            string killedName = killedNameObj?.ToString() ?? "未知玩家";
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
                            string diedName = diedNameObj?.ToString() ?? "未知";
                            string killedBy = "";
                            object killedByObj;
                            if (evt.Parameters.TryGetValue(10, out killedByObj))
                                killedBy = killedByObj?.ToString() ?? "";

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
                // 实际抓包确认：p8=GUID字节数组(32b=2个GUID)，p9=名字数组 ["laowang0729","aYi777"]
                else if (evt.GameEventCode == 230 && evt.Parameters != null)
                {
                    try
                    {
                        GameEvents.ClearParty();
                        // SAT: 参数9 = 名字数组，参数8 = GUID字节数组
                        if (evt.Parameters.TryGetValue(9, out var namesObj) && namesObj != null)
                        {
                            AddNamesFromArray(namesObj);
                        }
                        ReportStatus($"[队伍] 已加入队伍，成员 {GameEvents.PartyMemberCount} 人: {string.Join(", ", GameEvents.PartyMemberSnapshot())}");
                        GameEvents.FireUpdate();
                    }
                    catch { }
                }
                // 234 = PartyPlayerJoined (队伍成员加入) - 实际抓包确认
                // p0=ObjectId, p1=GUID字节
                else if (evt.GameEventCode == 234 && evt.Parameters != null)
                {
                    try
                    {
                        // 暂时只记录，名字需要从后续事件获取
                        ReportStatus($"[队伍] 新成员加入 (ObjectId={evt.Parameters[0]})");
                    }
                    catch { }
                }
                // 237 = PartyDisbanded (队伍解散) - 预估
                else if (evt.GameEventCode == 237)
                {
                    GameEvents.ClearParty();
                    ReportStatus("[队伍] 队伍已解散");
                    GameEvents.FireUpdate();
                }
            }
        }

        private static void ReportStatus(string s)
        {
            if (OnStatus != null) try { OnStatus(s); } catch { }
        }

        /// <summary>从 Photon 数组参数中提取玩家名字，加入队伍集合</summary>
        private static void AddNamesFromArray(object namesObj)
        {
            if (namesObj is string[] strNames)
            {
                foreach (var n in strNames)
                {
                    if (!string.IsNullOrEmpty(n)) GameEvents.AddPartyName(n);
                }
            }
            else if (namesObj is object[] objNames)
            {
                foreach (var o in objNames)
                {
                    var n = o?.ToString();
                    if (!string.IsNullOrEmpty(n)) GameEvents.AddPartyName(n);
                }
            }
        }
    }
}
