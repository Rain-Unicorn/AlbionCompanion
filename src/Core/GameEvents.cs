using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using AlbionCompanion.Core.Party;

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
        // 本地玩家信息：R1/C3 起改为只读转发到 LocalPlayerIdentity（唯一写入入口是状态机），
        // UI 与测试不得再直写这两个字段
        public static long? LocalPlayerObjectId => LocalPlayerIdentity.ConfirmedObjectId;
        public static string LocalPlayerName => LocalPlayerIdentity.ConfirmedName ?? "";

        // R7/C6：队伍名集合迁入 PartyState（唯一来源，含 party-cache.json 落盘与未验证标注态），
        // 本类只保留只读转发（设计 §6 迁移表：PartyMemberNames → PartyState）。
        // 旧的 ClearParty/AddPartyName/AddPartyNames 已删除——§7.7 自检要求
        // 队伍名集合的写路径全部收敛到 PartyState。

        // SAT 核心伤害统计追踪器（照搬 SAT 逻辑）
        public static DamageMeter.DamageStatsTracker DamageTracker = new DamageMeter.DamageStatsTracker();

        /// <summary>当前是否在队伍中（R7/C6 转发：含「本次不使用旧名单」抑制语义）</summary>
        public static bool IsInParty => PartyState.IsInParty;

        /// <summary>队伍成员数（转发 PartyState）</summary>
        public static int PartyMemberCount => PartyState.MemberCount;

        /// <summary>队伍成员快照（UI 只读，转发 PartyState）</summary>
        public static string[] PartyMemberSnapshot() => PartyState.GetMemberNames();

        /// <summary>判断某个玩家是否是队伍成员（含本地玩家自己；名单来源 PartyState）</summary>
        public static bool IsPartyMember(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            // R2/C4：本地玩家显示名统一走 LocalPlayerIdentity.DisplaySelfName（识别前=「我（识别中）」，
            // 确认后=确认名），废弃旧的硬编码自称字面量（设计 §4.7 自检项）。
            if (!string.IsNullOrEmpty(LocalPlayerIdentity.DisplaySelfName) && name == LocalPlayerIdentity.DisplaySelfName) return true;
            if (!string.IsNullOrEmpty(LocalPlayerName) && name == LocalPlayerName) return true;
            return PartyState.ContainsName(name);
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

        // R4/C5（设计 §6.3）：击杀/死亡计数器——不再从 500 条截断的事件流数数，
        // AddCombat 内随写随加，长会话不丢计数
        private static int _killCount;
        private static int _deathCount;

        /// <summary>附近玩家：抓包线程写、UI 线程枚举并清理。</summary>
        public static readonly ConcurrentDictionary<long, PlayerEntry> Players = new ConcurrentDictionary<long, PlayerEntry>();

        public static event Action OnUpdate;

        public static void AddCombat(CombatEntry e)
        {
            if (e == null) return;
            lock (_combatLock)
            {
                _combat.Add(e);
                if (e.Type == "kill") _killCount++;
                else if (e.Type == "death") _deathCount++;
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

        // R4/C5（设计 §9.5 / §8.5）：三个 Total* 属性已删除——大数字统一改读
        // DamageTracker.GetTotals(filter) / IncomeTracker.GetSummary().ConfirmedTotal。
        // 删除而非「保留属性改内部实现」，让编译错误守住同源取数这条线（§9.5 L1 自检：
        // grep TotalDamage/TotalHealing 应零命中）。

        /// <summary>
        /// R4/C5：战斗域的唯一清空入口（设计 §9.5「清空只清了一半」风险）。
        /// 一次清三处：DamageTracker 全部玩家、事件流 _combat、击杀/死亡计数器。
        /// 收益/采集不受影响（战斗页的清空语义只作用于战斗，§8.5）。
        /// </summary>
        public static void ResetAll()
        {
            lock (_combatLock)
            {
                _combat.Clear();
                _killCount = 0;
                _deathCount = 0;
            }
            DamageTracker.Clear();
        }

        /// <summary>仅归零击杀/死亡计数器（evt 流不清；供需要单独重置计数的场合）。</summary>
        public static void ResetCounters()
        {
            lock (_combatLock)
            {
                _killCount = 0;
                _deathCount = 0;
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

        public static int KillCount
        {
            get
            {
                // R4/C5（§6.3 方案 A）：读计数器，不受事件流 500 条截断影响
                lock (_combatLock) { return _killCount; }
            }
        }

        public static int DeathCount
        {
            get
            {
                lock (_combatLock) { return _deathCount; }
            }
        }

        public static void FireUpdate()
        {
            if (OnUpdate != null) try { OnUpdate(); } catch { }
        }
    }
}
