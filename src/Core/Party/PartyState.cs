using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AlbionCompanion.Core.Party
{
    public enum PartyConfidence
    {
        Confirmed,      // 有 ObjectId 有名字
        PendingObjectId, // 有名字无 ObjectId（evt=230 全量重建的初始态）
        PendingName,     // 有 ObjectId 无名字（evt=234 新队友，等 evt=29 反查）
    }

    /// <summary>队伍成员快照（UI / 测试只读，锁内拷贝，禁止持有活引用）。</summary>
    public sealed class PartyMemberSnapshot
    {
        public long ObjectId;
        public string Name = "";
        public Guid Guid;
        public string Role = "member";
        public DateTime LastSeenUtc;
        public PartyConfidence Confidence;
    }

    /// <summary>
    /// R7/C6：队伍状态唯一来源（设计 §7）。五个事件增量重建（230/234/239/241/243），
    /// evt=29 提供 pending ObjectId 反查；状态经 PartyCacheStore 防抖落盘 party-cache.json。
    /// 数据诚实性核心约定（§7.6 callout）：verified=false 时 UI 必须显式标注「缓存未验证」，
    /// 绝不能让用户误以为名单是实时的。
    /// </summary>
    public static class PartyState
    {
        private sealed class Member
        {
            public long ObjectId;
            public string Name = "";
            public Guid Guid;
            public string Role = "member";
            public DateTime LastSeenUtc;
            public PartyConfidence Confidence;
        }

        private sealed class PendingSlot
        {
            public Guid Guid;
            public DateTime JoinSeenUtc;
        }

        private static readonly object _gate = new object();
        private static readonly List<Member> _members = new List<Member>();
        private static readonly Dictionary<long, PendingSlot> _pendingObjectIds = new Dictionary<long, PendingSlot>();

        private static bool _verified;
        private static string? _verifiedReason = "ProcessStartNoPartyEvent";
        private static long _leaderObjectId;
        private static bool _suppressed; // 用户点过「本次不使用旧名单」
        private static string? _cacheDir;

        /// <summary>状态回调（由 ChatSniffer 接到事件日志）。</summary>
        public static event Action<string>? OnStatus;

        // ---------------- 读接口（UI / GameEvents 转发） ----------------

        public static bool Verified { get { lock (_gate) return _verified; } }
        public static string? VerifiedReason { get { lock (_gate) return _verifiedReason; } }
        public static long LeaderObjectId { get { lock (_gate) return _leaderObjectId; } }
        public static bool Suppressed { get { lock (_gate) return _suppressed; } }

        /// <summary>原始成员数（不受 Suppressed 影响；UI 判断「是否在队伍」用 IsInParty）。</summary>
        public static int MemberCount { get { lock (_gate) return _members.Count; } }

        /// <summary>是否按队伍过滤（§7.6：未验证但有旧名单仍过滤，只是必须标注）。</summary>
        public static bool IsInParty { get { lock (_gate) return !_suppressed && _members.Count > 0; } }

        /// <summary>存在未确认成员（pendingObjectId / pendingName），UI 据此标注「N 名队员待识别」。</summary>
        public static bool HasPendingMembers
        {
            get { lock (_gate) { SweepPendingLocked(DateTime.UtcNow); return _members.Any(m => m.Confidence != PartyConfidence.Confirmed); } }
        }

        public static bool ContainsName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            lock (_gate) return _members.Any(m => m.Name == name);
        }

        public static string[] GetMemberNames()
        {
            lock (_gate) return _members.Where(m => !string.IsNullOrEmpty(m.Name)).Select(m => m.Name).ToArray();
        }

        public static List<PartyMemberSnapshot> GetMembers()
        {
            lock (_gate)
            {
                return _members.Select(m => new PartyMemberSnapshot
                {
                    ObjectId = m.ObjectId,
                    Name = m.Name,
                    Guid = m.Guid,
                    Role = m.Role,
                    LastSeenUtc = m.LastSeenUtc,
                    Confidence = m.Confidence,
                }).ToList();
            }
        }

        // ---------------- 五个事件（ChatSniffer 调用，抓包线程） ----------------

        /// <summary>evt=230 PartyJoined：全量重建。p8=GUID 字节数组（16B/个），p9=名字数组（含本地玩家）。</summary>
        public static void OnPartyJoined(object? guidBytesObj, object? namesObj)
        {
            string[] names = ParseNames(namesObj);
            List<Guid> guids = ParseGuids(guidBytesObj);
            DateTime now = DateTime.UtcNow;

            lock (_gate)
            {
                _members.Clear();
                _pendingObjectIds.Clear();
                foreach (var n in names)
                {
                    var m = new Member
                    {
                        Name = n,
                        ObjectId = 0,
                        Confidence = PartyConfidence.PendingObjectId,
                        Role = "member",
                        LastSeenUtc = now,
                    };
                    // §7.4 规则 5：本地玩家不走反查，名字==ConfirmedName 时直接用确认的 ObjectId
                    long? localId = LocalPlayerIdentity.ConfirmedObjectId;
                    string? localName = LocalPlayerIdentity.ConfirmedName;
                    if (localId != null && !string.IsNullOrEmpty(localName) && n == localName)
                    {
                        m.ObjectId = localId.Value;
                        m.Confidence = PartyConfidence.Confirmed;
                    }
                    _members.Add(m);
                }
                int bind = Math.Min(guids.Count, _members.Count);
                for (int i = 0; i < bind; i++) _members[i].Guid = guids[i];
                if (guids.Count > 0 && guids.Count != _members.Count)
                    Status($"[队伍] GUID/名字数量不匹配 {guids.Count}/{_members.Count}");
                _leaderObjectId = 0;
                _verified = true;      // 收到真实队伍事件，本会话名单可信
                _verifiedReason = null;
                _suppressed = false;   // 真实事件覆盖旧缓存，解除用户的手动抑制
                SaveLocked();
            }
            Status($"[队伍] 已加入队伍，成员 {_members.Count} 人：{string.Join(", ", GetMemberNames())}");
        }

        /// <summary>evt=234 PartyPlayerJoined：p0=ObjectId，p1=GUID 字节。GUID 命中已有成员则补 id；未命中则新增 pendingName 等反查。</summary>
        public static void OnPartyPlayerJoined(object? idObj, object? guidObj)
        {
            long id;
            try { id = Convert.ToInt64(idObj); } catch { Status("[队伍] evt=234 ObjectId 解析失败"); return; }
            Guid guid = ParseGuid(guidObj);
            DateTime now = DateTime.UtcNow;
            bool changed = false;

            lock (_gate)
            {
                // ① 用 GUID 精确匹配已有成员
                if (guid != Guid.Empty)
                {
                    var byGuid = _members.Where(m => m.Guid == guid).ToList();
                    if (byGuid.Count == 1)
                    {
                        var m = byGuid[0];
                        m.ObjectId = id;
                        m.LastSeenUtc = now;
                        if (!string.IsNullOrEmpty(m.Name)) m.Confidence = PartyConfidence.Confirmed;
                        changed = true;
                        Status($"[队伍] 新成员 {m.Name}（ObjectId={id}）GUID 匹配成功");
                    }
                }
                // ② 同 ObjectId 已在名单 → 只补 GUID
                if (!changed)
                {
                    var byId = _members.Where(m => m.ObjectId == id).ToList();
                    if (byId.Count == 1)
                    {
                        if (guid != Guid.Empty) byId[0].Guid = guid;
                        byId[0].LastSeenUtc = now;
                        changed = true;
                    }
                }
                // ③ 全新成员：有 id 无名，进 pending 队列等 evt=29
                if (!changed)
                {
                    _members.Add(new Member
                    {
                        ObjectId = id,
                        Name = "",
                        Guid = guid,
                        Confidence = PartyConfidence.PendingName,
                        LastSeenUtc = now,
                    });
                    _pendingObjectIds[id] = new PendingSlot { Guid = guid, JoinSeenUtc = now };
                    Status($"[队伍] 新成员加入（ObjectId={id}），等待名字反查");
                }
                _verified = true;
                _verifiedReason = null;
                SaveLocked();
            }
        }

        /// <summary>evt=239 PartyPlayerUpdated：保守处理（§7.3）——不新增成员、不改 id，仅刷新 lastSeen；未知名字只记日志。</summary>
        public static void OnPartyPlayerUpdated(object? nameObj)
        {
            string name = nameObj?.ToString() ?? "";
            if (string.IsNullOrEmpty(name)) return;
            DateTime now = DateTime.UtcNow;
            lock (_gate)
            {
                var hit = _members.FirstOrDefault(m => m.Name == name);
                if (hit != null)
                {
                    hit.LastSeenUtc = now;
                    SaveLocked();
                }
                else
                {
                    Status($"[队伍] 未识别成员更新 {name}（不入名单，参数语义未验证）");
                }
            }
        }

        /// <summary>evt=241 PartyLeaderChanged：只更新 leaderObjectId / 角色，不影响成员集合。p0 可为 ObjectId 或名字。</summary>
        public static void OnPartyLeaderChanged(object? p0)
        {
            if (p0 == null) { Status("[队伍] evt=241 无参数"); return; }
            lock (_gate)
            {
                long id = 0; string? name = null;
                try { id = Convert.ToInt64(p0); } catch { name = p0.ToString(); }

                Member? hit = null;
                if (id != 0) hit = _members.FirstOrDefault(m => m.ObjectId == id);
                if (hit == null && name != null) hit = _members.FirstOrDefault(m => m.Name == name);

                if (hit != null)
                {
                    foreach (var m in _members) m.Role = "member";
                    hit.Role = "leader";
                    _leaderObjectId = hit.ObjectId;
                    Status($"[队伍] 队长变更为 {hit.Name}（ObjectId={hit.ObjectId}）");
                    SaveLocked();
                }
                else
                {
                    Status($"[队伍] evt=241 未能定位队长（p0={p0}），只记日志");
                }
            }
        }

        /// <summary>evt=243 PartySilverGained：队伍侧只刷成员活跃度；金额归收益模块（§8.3），PartyState 不碰钱。</summary>
        public static void OnPartySilverGained(object? p0)
        {
            if (p0 == null) return;
            lock (_gate)
            {
                long id = 0; string? name = null;
                try { id = Convert.ToInt64(p0); } catch { name = p0.ToString(); }
                Member? hit = id != 0 ? _members.FirstOrDefault(m => m.ObjectId == id) : null;
                if (hit == null && name != null) hit = _members.FirstOrDefault(m => m.Name == name);
                if (hit != null) hit.LastSeenUtc = DateTime.UtcNow;
                // 只刷 lastSeen，不落盘不广播（低价值高频事件）
            }
        }

        /// <summary>evt=237 PartyDisbanded（事件码未验证，调用方须打印原始参数）。</summary>
        public static void OnPartyDisbanded()
        {
            lock (_gate)
            {
                _members.Clear();
                _pendingObjectIds.Clear();
                _leaderObjectId = 0;
                _verified = true;
                _verifiedReason = "DisbandedUnverified";
                SaveLocked();
            }
            Status("[队伍] 队伍已解散（evt=237，事件码未验证）");
        }

        /// <summary>evt=29 反查入口（§7.4）：只有唯一候选才提交，多候选保持 pending。</summary>
        public static void OnNewCharacter(long id, string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            DateTime now = DateTime.UtcNow;
            bool changed = false;

            lock (_gate)
            {
                SweepPendingLocked(now);

                // ① 该 id 在 pending 队列 → 用 GUID 定位成员补名字
                if (_pendingObjectIds.TryGetValue(id, out var slot))
                {
                    var byGuid = slot.Guid == Guid.Empty
                        ? new List<Member>()
                        : _members.Where(m => m.Guid == slot.Guid).ToList();
                    if (byGuid.Count == 1)
                    {
                        var m = byGuid[0];
                        m.Name = name;
                        m.ObjectId = id;
                        m.Confidence = PartyConfidence.Confirmed;
                        _pendingObjectIds.Remove(id);
                        changed = true;
                        Status($"[队伍] 成员名字反查成功：{name}（ObjectId={id}）");
                    }
                }

                if (!changed)
                {
                    // ② 名字命中 pendingObjectId 成员 → 绑 id（唯一候选才提交，§9.8 L1）
                    var byName = _members.Where(m => m.Name == name && m.Confidence == PartyConfidence.PendingObjectId).ToList();
                    if (byName.Count == 1)
                    {
                        byName[0].ObjectId = id;
                        byName[0].Confidence = PartyConfidence.Confirmed;
                        changed = true;
                        Status($"[队伍] 成员 ObjectId 反查成功：{name}（ObjectId={id}）");
                    }
                    else if (byName.Count > 1)
                    {
                        Status($"[队伍] 名字 {name} 有 {byName.Count} 个候选，保持 pending（宁可未知，不可错认）");
                    }
                    // ③ 零候选：可能是路人，忽略
                }

                if (changed) SaveLocked();
            }
        }

        // ---------------- 启动加载 / 抑制 / 测试钩子 ----------------

        /// <summary>启动加载（§7.6）：本会话未收到任何队伍事件前，缓存一律降级 verified=false。</summary>
        public static void LoadOrRestore(string dir)
        {
            SetCacheDir(dir);
            string path = Path.Combine(dir, "party-cache.json");
            var data = PartyCacheStore.Load(path);
            lock (_gate)
            {
                _members.Clear();
                _pendingObjectIds.Clear();
                if (data == null || data.members == null)
                {
                    _verified = false;
                    _verifiedReason = "ProcessStartNoPartyEvent";
                    _leaderObjectId = 0;
                }
                else
                {
                    foreach (var c in data.members)
                    {
                        _members.Add(new Member
                        {
                            ObjectId = c.objectId,
                            Name = c.name ?? "",
                            Guid = ParseGuidBase64(c.guid),
                            Role = string.IsNullOrEmpty(c.role) ? "member" : c.role,
                            LastSeenUtc = c.lastSeenUtc,
                            Confidence = ParseConfidence(c.confidence),
                        });
                    }
                    _verified = false;                      // 即使上会话已验证，重启后也不可信
                    _verifiedReason = "StaleFromPreviousSession";
                    _leaderObjectId = data.leaderObjectId;
                    if (_members.Count > 0)
                        Status($"[队伍] 载入上次会话名单 {_members.Count} 人（缓存未验证，进队后自动刷新）");
                }
            }
        }

        /// <summary>「本次不使用旧名单」按钮（§9.8 L1）：本次会话忽略旧缓存，直到真实队伍事件到来。</summary>
        public static void SuppressStaleCache()
        {
            lock (_gate)
            {
                _suppressed = true;
                Status("[队伍] 本次不使用旧名单，榜单显示全部玩家");
            }
        }

        /// <summary>测试钩子：指定缓存目录（LoadOrRestore 内部也调用）。</summary>
        public static void SetCacheDir(string dir) { lock (_gate) _cacheDir = dir; }

        /// <summary>测试钩子：立即落盘（取消防抖）。</summary>
        public static void FlushCache()
        {
            lock (_gate) SaveLocked(immediate: true);
        }

        /// <summary>测试钩子：清空全部状态（含缓存目录与抑制标记）。</summary>
        public static void Reset()
        {
            lock (_gate)
            {
                _members.Clear();
                _pendingObjectIds.Clear();
                _verified = false;
                _verifiedReason = "ProcessStartNoPartyEvent";
                _leaderObjectId = 0;
                _suppressed = false;
                _cacheDir = null;
            }
        }

        // ---------------- 内部 ----------------

        /// <summary>pending 超时清理（§7.4 规则 4）：超 10 分钟未绑定 → 移除，verified 降为 false。</summary>
        private static void SweepPendingLocked(DateTime now)
        {
            var expired = _pendingObjectIds.Where(kv => (now - kv.Value.JoinSeenUtc).TotalMinutes > 10).ToList();
            foreach (var kv in expired) _pendingObjectIds.Remove(kv.Key);
            if (expired.Count > 0)
            {
                _verified = false;
                _verifiedReason = "UnresolvedMember";
                Status($"[队伍] {expired.Count} 名队员 10 分钟内未能识别，名单降级为未验证");
            }
        }

        private static void SaveLocked(bool immediate = false)
        {
            if (string.IsNullOrEmpty(_cacheDir)) return; // 未配置目录（测试直跑）则不落盘
            string path = Path.Combine(_cacheDir, "party-cache.json");
            var data = PartyCacheStore.NewData();
            data.verified = _verified;
            data.verifiedReason = _verifiedReason ?? "";
            data.leaderObjectId = _leaderObjectId;
            foreach (var m in _members)
            {
                data.members.Add(new PartyCacheStore.CacheMember
                {
                    objectId = m.ObjectId,
                    name = m.Name,
                    guid = m.Guid == Guid.Empty ? "" : Convert.ToBase64String(m.Guid.ToByteArray()),
                    role = m.Role,
                    lastSeenUtc = m.LastSeenUtc,
                    confidence = ConfidenceToString(m.Confidence),
                });
            }
            if (immediate) PartyCacheStore.FlushNow(path, data);
            else PartyCacheStore.SaveDebounced(path, data);
        }

        private static string[] ParseNames(object? namesObj)
        {
            var list = new List<string>();
            if (namesObj is string[] strNames)
                foreach (var n in strNames) if (!string.IsNullOrEmpty(n)) list.Add(n);
            else if (namesObj is object[] objNames)
                foreach (var o in objNames) { var n2 = o?.ToString(); if (!string.IsNullOrEmpty(n2)) list.Add(n2); }
            return list.ToArray();
        }

        private static List<Guid> ParseGuids(object? bytesObj)
        {
            var list = new List<Guid>();
            if (bytesObj is byte[] bytes)
            {
                for (int off = 0; off + 16 <= bytes.Length; off += 16)
                    list.Add(new Guid(bytes.Skip(off).Take(16).ToArray()));
            }
            return list;
        }

        private static Guid ParseGuid(object? bytesObj)
        {
            if (bytesObj is byte[] b && b.Length >= 16)
                return new Guid(b.Take(16).ToArray());
            return Guid.Empty;
        }

        private static Guid ParseGuidBase64(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return Guid.Empty;
            try { return new Guid(Convert.FromBase64String(base64)); }
            catch { return Guid.Empty; }
        }

        private static PartyConfidence ParseConfidence(string s)
        {
            return s switch
            {
                "pendingObjectId" => PartyConfidence.PendingObjectId,
                "pendingName" => PartyConfidence.PendingName,
                _ => PartyConfidence.Confirmed,
            };
        }

        private static string ConfidenceToString(PartyConfidence c)
        {
            return c switch
            {
                PartyConfidence.PendingObjectId => "pendingObjectId",
                PartyConfidence.PendingName => "pendingName",
                _ => "confirmed",
            };
        }

        private static void Status(string s)
        {
            var h = OnStatus;
            if (h != null) try { h(s); } catch { }
        }
    }
}
