using System;
using System.Collections.Generic;

namespace AlbionCompanion.Core.DamageMeter
{
    /// <summary>玩家伤害统计（照搬 SAT，适配 long ObjectId）。</summary>
    internal sealed class DamageStatsPlayer
    {
        public long ObjectId { get; init; }
        public string PlayerName { get; set; } = string.Empty;
        public long BiggestHit { get; set; }
        public long BiggestHeal { get; set; }
        public long TotalDamage { get; set; }
        public long PveDamage { get; set; }
        public long PvpDamage { get; set; }
        public long EffectiveHealing { get; set; }
        public long Overheal { get; set; }

        // ---- P0-1 影子桶（隔离期专用）----
        // 该键被 DamageStatsTracker.QuarantineKey 冻结期间，新伤害/治疗只累加到这里，
        // 可见累计（TotalDamage/PveDamage/PvpDamage/EffectiveHealing/Biggest*）一律不再增长。
        // 为什么需要：身份冲突时若「只保留不冻结」，换角色后新伤害会继续并进旧角色的历史账，
        // 榜单直接错人；冻结后可见那份纯粹是旧角色的历史，不再被污染，且随时可并入/拆分/丢弃。
        // 冻结期窗口（Burst*）与三个目标集合也不更新——冻结的语义就是「这条可疑行停止演化」。
        public long PendingTotalDamage { get; set; }
        public long PendingPveDamage { get; set; }
        public long PendingPvpDamage { get; set; }
        public long PendingEffectiveHealing { get; set; }
        public long PendingBiggestHit { get; set; }
        public long PendingBiggestHeal { get; set; }

        /// <summary>影子桶是否为空（解冻时用于判断是否有要并入的东西）。</summary>
        public bool HasPending
        {
            get
            {
                return PendingTotalDamage != 0 || PendingPveDamage != 0 || PendingPvpDamage != 0
                    || PendingEffectiveHealing != 0 || PendingBiggestHit != 0 || PendingBiggestHeal != 0;
            }
        }

        /// <summary>清空影子桶（并入可见值之后 / 丢弃之后调用）。</summary>
        public void ClearPending()
        {
            PendingTotalDamage = 0;
            PendingPveDamage = 0;
            PendingPvpDamage = 0;
            PendingEffectiveHealing = 0;
            PendingBiggestHit = 0;
            PendingBiggestHeal = 0;
        }

        public RollingDamageWindow BurstDamageFiveSeconds { get; } = new(TimeSpan.FromSeconds(5));
        public RollingDamageWindow BurstDamageTenSeconds { get; } = new(TimeSpan.FromSeconds(10));
        public HashSet<long> LastHitTargetObjectIds { get; } = new();
        public HashSet<long> MobLastHitTargetObjectIds { get; } = new();
        public HashSet<long> AttackedTargetObjectIds { get; } = new();
    }
}
