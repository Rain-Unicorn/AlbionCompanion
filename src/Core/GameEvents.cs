using System;
using System.Collections.Generic;
using System.Text;

namespace AlbionCompanion.Core
{
    /// <summary>实时游戏事件汇总：战斗/收益/采集数据。</summary>
    public static class GameEvents
    {
        // 本地玩家信息（从 JoinResponse 获取）
        public static long? LocalPlayerObjectId;
        public static string LocalPlayerName = "";

        // 队伍成员名字集合（从 PartyJoined/PartyPlayerJoined 事件解析）
        public static readonly HashSet<string> PartyMemberNames = new HashSet<string>(StringComparer.Ordinal);

        // SAT 核心伤害统计追踪器（照搬 SAT 逻辑）
        public static DamageMeter.DamageStatsTracker DamageTracker = new DamageMeter.DamageStatsTracker();

        /// <summary>当前是否在队伍中</summary>
        public static bool IsInParty => PartyMemberNames.Count > 0;

        /// <summary>判断某个玩家是否是队伍成员（含本地玩家自己）</summary>
        public static bool IsPartyMember(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name == "我自己") return true;
            if (!string.IsNullOrEmpty(LocalPlayerName) && name == LocalPlayerName) return true;
            return PartyMemberNames.Contains(name);
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

        public static readonly List<CombatEntry> Combat = new List<CombatEntry>();
        public static readonly List<IncomeEntry> Income = new List<IncomeEntry>();
        public static readonly List<GatheringEntry> Gathering = new List<GatheringEntry>();
        public static readonly Dictionary<long, PlayerEntry> Players = new Dictionary<long, PlayerEntry>();

        public static event Action OnUpdate;

        public static void AddCombat(CombatEntry e)
        {
            Combat.Add(e);
            if (Combat.Count > 500) Combat.RemoveAt(0);
            FireUpdate();
        }

        public static void AddIncome(IncomeEntry e)
        {
            Income.Add(e);
            if (Income.Count > 500) Income.RemoveAt(0);
            FireUpdate();
        }

        public static void AddGathering(GatheringEntry e)
        {
            Gathering.Add(e);
            if (Gathering.Count > 500) Gathering.RemoveAt(0);
            FireUpdate();
        }

        public static long TotalIncome
        {
            get
            {
                long sum = 0;
                foreach (var e in Income) sum += e.Silver;
                return sum;
            }
        }

        public static long TotalDamage
        {
            get
            {
                long sum = 0;
                foreach (var e in Combat) if (e.Type == "damage") sum += e.Damage;
                return sum;
            }
        }

        /// <summary>按目标汇总的伤害（目标名 -> 总伤害）</summary>
        public static Dictionary<string, long> DamageByTarget
        {
            get
            {
                var dict = new Dictionary<string, long>();
                foreach (var e in Combat)
                {
                    if (e.Type == "damage" && !string.IsNullOrEmpty(e.Target))
                    {
                        if (!dict.ContainsKey(e.Target)) dict[e.Target] = 0;
                        dict[e.Target] += e.Damage;
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
                foreach (var e in Combat)
                {
                    if (e.Type == "damage" && !string.IsNullOrEmpty(e.CauserName))
                    {
                        if (!dict.ContainsKey(e.CauserName)) dict[e.CauserName] = 0;
                        dict[e.CauserName] += e.Damage;
                    }
                }
                return dict;
            }
        }

        public static long TotalHealing
        {
            get
            {
                long sum = 0;
                foreach (var e in Combat) if (e.Type == "heal") sum += e.Damage;
                return sum;
            }
        }

        public static int KillCount
        {
            get
            {
                int c = 0;
                foreach (var e in Combat) if (e.Type == "kill") c++;
                return c;
            }
        }

        public static int DeathCount
        {
            get
            {
                int c = 0;
                foreach (var e in Combat) if (e.Type == "death") c++;
                return c;
            }
        }

        public static void FireUpdate()
        {
            if (OnUpdate != null) try { OnUpdate(); } catch { }
        }
    }
}
