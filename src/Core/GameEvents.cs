using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace AlbionCompanion.Core
{
    /// <summary>实时游戏事件汇总：战斗/收益/采集数据。</summary>
    /// <remarks>
    /// 并发约定（R3）：
    /// 1) <c>Combat</c>/<c>Income</c>/<c>Gathering</c> 三个列表各自一把细粒度锁，写入走 <c>Add*</c>，
    ///    读取一律走 <c>Snapshot*</c>（锁内 ToArray），UI 不得再持有活集合引用。
    /// 2) <c>FireUpdate()</c> 必须在锁外触发——订阅者是 UI，可能同步重入。
    /// 3) 加入集合后的 <see cref="CombatEntry"/>/<see cref="IncomeEntry"/> 不再修改任何字段（不可变约定）；
    ///    唯一例外是 <see cref="GatheringEntry.Fame"/>，写入必须走 <c>TryUpdateLastGatheringFame</c>。
    /// 4) <c>Players</c> 与队伍名集合使用并发容器，支持抓包线程写、UI 线程枚举。
    /// </remarks>
    public static class GameEvents
    {
        // 本地玩家信息（从 JoinResponse 获取）
        public static long? LocalPlayerObjectId;
        public static string LocalPlayerName = "";

        // 队伍成员名字集合（从 PartyJoined/PartyPlayerJoined 事件解析）
        // CD 承载：抓包线程 Clear/Add 与 UI 线程 Contains/枚举并发安全。
        private static readonly ConcurrentDictionary<string, byte> _partyMembers =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        // SAT 核心伤害统计追踪器（照搬 SAT 逻辑）
        public static DamageMeter.DamageStatsTracker DamageTracker = new DamageMeter.DamageStatsTracker();

        /// <summary>当前是否在队伍中</summary>
        public static bool IsInParty => _partyMembers.Count > 0;

        /// <summary>队伍成员数</summary>
        public static int PartyMemberCount => _partyMembers.Count;

        /// <summary>队伍成员快照（UI 只读，避免枚举活集合）</summary>
        public static string[] PartyMemberSnapshot() => _partyMembers.Keys.ToArray();

        /// <summary>清空队伍名单（抓包线程调用）</summary>
        public static void ClearParty() => _partyMembers.Clear();

        /// <summary>加入单个队伍成员</summary>
        public static void AddPartyName(string name)
        {
            if (!string.IsNullOrEmpty(name)) _partyMembers[name] = 0;
        }

        /// <summary>批量加入队伍成员（重复名天然幂等）</summary>
        public static void AddPartyNames(IEnumerable<string> names)
        {
            if (names == null) return;
            foreach (var n in names) AddPartyName(n);
        }

        /// <summary>判断某个玩家是否是队伍成员（含本地玩家自己）</summary>
        public static bool IsPartyMember(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name == "我自己") return true;
            if (!string.IsNullOrEmpty(LocalPlayerName) && name == LocalPlayerName) return true;
            return _partyMembers.ContainsKey(name);
        }

        // 战斗
        public class CombatEntry
        {
            public DateTime Time;
            public string Target;
            public long Damage;
            public string Type; // damage / heal / kill / death
            public string CauserName; // 造成伤害的玩家名字（照搬 SAT 逻辑）
        }

        // 收益
        public class IncomeEntry
        {
            public DateTime Time;
            public long Silver;
            public string Source; // 战斗 / 采集 / 出售 / 其他
        }

        // 采集
        public class GatheringEntry
        {
            public DateTime Time;
            public string Resource;
            public int Amount;
            public int Fame;
        }

        // 附近玩家
        public class PlayerEntry
        {
            public long ObjectId;
            public string Name;
            public DateTime FirstSeen;
            public DateTime LastSeen;
            public string Guild; // 公会名（如果能拿到）
        }

        private const int MaxEntries = 500;

        private static readonly List<CombatEntry> _combat = new List<CombatEntry>();
        private static readonly List<IncomeEntry> _income = new List<IncomeEntry>();
        private static readonly List<GatheringEntry> _gathering = new List<GatheringEntry>();

        private static readonly object _combatLock = new object();
        private static readonly object _incomeLock = new object();
        private static readonly object _gatheringLock = new object();

        /// <summary>附近玩家：抓包线程写、UI 线程枚举并清理。</summary>
        public static readonly ConcurrentDictionary<long, PlayerEntry> Players = new ConcurrentDictionary<long, PlayerEntry>();

        public static event Action OnUpdate;

        public static void AddCombat(CombatEntry e)
        {
            if (e == null) return;
            lock (_combatLock)
            {
                _combat.Add(e);
                if (_combat.Count > MaxEntries) _combat.RemoveAt(0);
            }
            FireUpdate(); // 必须在锁外
        }

        public static void AddIncome(IncomeEntry e)
        {
            if (e == null) return;
            lock (_incomeLock)
            {
                _income.Add(e);
                if (_income.Count > MaxEntries) _income.RemoveAt(0);
            }
            FireUpdate();
        }

        public static void AddGathering(GatheringEntry e)
        {
            if (e == null) return;
            lock (_gatheringLock)
            {
                _gathering.Add(e);
                if (_gathering.Count > MaxEntries) _gathering.RemoveAt(0);
            }
            FireUpdate();
        }

        /// <summary>
        /// 把声望写进「最近一条采集记录」。evt=72 与 evt=61 可能落在不同线程，
        /// 因此必须在这把锁内取末尾元素并写入，调用方不得直接索引活列表。
        /// </summary>
        public static bool TryUpdateLastGatheringFame(int fame)
        {
            bool updated;
            lock (_gatheringLock)
            {
                updated = _gathering.Count > 0;
                if (updated) _gathering[_gathering.Count - 1].Fame = fame;
            }
            if (updated) FireUpdate();
            return updated;
        }

        // ---- 快照（UI 唯一允许的读取方式）----

        public static CombatEntry[] SnapshotCombat()
        {
            lock (_combatLock) { return _combat.ToArray(); }
        }

        public static IncomeEntry[] SnapshotIncome()
        {
            lock (_incomeLock) { return _income.ToArray(); }
        }

        public static GatheringEntry[] SnapshotGathering()
        {
            lock (_gatheringLock) { return _gathering.ToArray(); }
        }

        public static int CountCombat()
        {
            lock (_combatLock) { return _combat.Count; }
        }

        public static int CountIncome()
        {
            lock (_incomeLock) { return _income.Count; }
        }

        public static int CountGathering()
        {
            lock (_gatheringLock) { return _gathering.Count; }
        }

        public static long TotalIncome
        {
            get
            {
                lock (_incomeLock)
                {
                    long sum = 0;
                    foreach (var e in _income) sum += e.Silver;
                    return sum;
                }
            }
        }

        public static long TotalDamage
        {
            get
            {
                lock (_combatLock)
                {
                    long sum = 0;
                    foreach (var e in _combat) if (e.Type == "damage") sum += e.Damage;
                    return sum;
                }
            }
        }

        /// <summary>按目标汇总的伤害（目标名 -> 总伤害）</summary>
        public static Dictionary<string, long> DamageByTarget
        {
            get
            {
                var dict = new Dictionary<string, long>();
                lock (_combatLock)
                {
                    foreach (var e in _combat)
                    {
                        if (e.Type == "damage" && !string.IsNullOrEmpty(e.Target))
                        {
                            if (!dict.ContainsKey(e.Target)) dict[e.Target] = 0;
                            dict[e.Target] += e.Damage;
                        }
                    }
                }
                return dict;
            }
        }

        /// <summary>按伤害来源（玩家）汇总的伤害（玩家名 -> 总伤害），照搬 SAT 逻辑</summary>
        public static Dictionary<string, long> DamageByCauser
        {
            get
            {
                var dict = new Dictionary<string, long>();
                lock (_combatLock)
                {
                    foreach (var e in _combat)
                    {
                        if (e.Type == "damage" && !string.IsNullOrEmpty(e.CauserName))
                        {
                            if (!dict.ContainsKey(e.CauserName)) dict[e.CauserName] = 0;
                            dict[e.CauserName] += e.Damage;
                        }
                    }
                }
                return dict;
            }
        }

        public static long TotalHealing
        {
            get
            {
                lock (_combatLock)
                {
                    long sum = 0;
                    foreach (var e in _combat) if (e.Type == "heal") sum += e.Damage;
                    return sum;
                }
            }
        }

        public static int KillCount
        {
            get
            {
                lock (_combatLock)
                {
                    int c = 0;
                    foreach (var e in _combat) if (e.Type == "kill") c++;
                    return c;
                }
            }
        }

        public static int DeathCount
        {
            get
            {
                lock (_combatLock)
                {
                    int c = 0;
                    foreach (var e in _combat) if (e.Type == "death") c++;
                    return c;
                }
            }
        }

        public static void FireUpdate()
        {
            if (OnUpdate != null) try { OnUpdate(); } catch { }
        }
    }
}
