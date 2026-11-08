using System.Collections.Generic;

namespace AlbionCompanion.Core.DamageMeter
{
    /// <summary>
    /// causer 解析结果（设计 §4.3）：TrackerKey 为归档键（0=我 / 正数=真实玩家 / -1~-3=未知来源 / -9=怪物），
    /// Countable=false 表示不计入统计（怪物对玩家造成伤害，仅记事件流）。
    /// </summary>
    public readonly struct CauserInfo
    {
        public readonly long TrackerKey;
        public readonly string DisplayName;
        public readonly bool Countable;

        public CauserInfo(long trackerKey, string displayName, bool countable)
        {
            TrackerKey = trackerKey;
            DisplayName = displayName;
            Countable = countable;
        }
    }

    /// <summary>
    /// R2/C4：统一 causer 解析（伤害与治疗共用，消除原两段复制粘贴逻辑的分叉）。
    /// 负数哨兵键空间（设计 §4.2）：
    ///   0  = 本地玩家（我），展示名 = DisplaySelfName（未确认时「我（识别中）」）
    ///   >0 = 真实玩家 ObjectId
    ///  -1  = 未知玩家（参数缺失，或 id 两字典都查不到——可被 evt=29 后补认领）
    ///  -2  = 未知召唤物/宠物（预留：本批不产出，仅提供显示名）
    ///  -3  = 未知环境/DoT 来源（显式 causer==0 或解析失败；与「参数缺失」语义不同）
    ///  -9  = 怪物对玩家造成伤害（不进 tracker，仅事件流）
    /// </summary>
    public static class CauserResolver
    {
        /// <summary>
        /// 解析 causer。hasParam = 事件里是否存在参数 6（缺失与显式 0 语义不同，分别走 -1 / -3）。
        /// mobNames / playerNames 由调用方（ChatSniffer）传入，本类保持无状态。
        /// </summary>
        public static CauserInfo Resolve(long causerId, bool hasParam,
            IReadOnlyDictionary<long, string> mobNames,
            IReadOnlyDictionary<long, string> playerNames)
        {
            // 1. 怪物 causer → 不统计（保留原有语义：怪物打玩家的伤害不进榜）
            if (mobNames.ContainsKey(causerId))
                return new CauserInfo(-9, "怪物", false);

            // 2. 本地玩家 → key=0（展示名随身份状态：真名或「我（识别中）」）
            if (LocalPlayerIdentity.IsSelf(causerId))
                return new CauserInfo(0, LocalPlayerIdentity.DisplaySelfName, true);

            // 3. 已知玩家 → 真实 id
            if (playerNames.TryGetValue(causerId, out var name))
                return new CauserInfo(causerId, name, true);

            // 4. 参数缺失 → 未知玩家（可被后补认领）
            if (!hasParam)
                return new CauserInfo(-1, "未知玩家", true);

            // 5. 显式 0 / 负值（解析失败） → 未知环境/DoT 来源
            if (causerId <= 0)
                return new CauserInfo(-3, "未知来源", true);

            // 6. 有 id 但两字典都没有 → 未知玩家（可被后补认领）
            return new CauserInfo(-1, "未知玩家", true);
        }

        /// <summary>
        /// 负键 → 展示名映射（设计 §4.2 / T-07）。0 = 我的当前展示名；正数键在正常流程
        /// 不会走到这里（Resolve 已带真实名），仅作兜底显示。
        /// </summary>
        public static string DisplayName(long key)
        {
            switch (key)
            {
                case -1: return "未知玩家";
                case -2: return "未知召唤物";
                case -3: return "未知来源";
                case -9: return "怪物";
                case 0: return LocalPlayerIdentity.DisplaySelfName;
                default: return "玩家 " + key;
            }
        }
    }
}
