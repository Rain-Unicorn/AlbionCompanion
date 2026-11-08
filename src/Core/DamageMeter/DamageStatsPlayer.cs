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
        public RollingDamageWindow BurstDamageFiveSeconds { get; } = new(TimeSpan.FromSeconds(5));
        public RollingDamageWindow BurstDamageTenSeconds { get; } = new(TimeSpan.FromSeconds(10));
        public HashSet<long> LastHitTargetObjectIds { get; } = new();
        public HashSet<long> MobLastHitTargetObjectIds { get; } = new();
        public HashSet<long> AttackedTargetObjectIds { get; } = new();
    }
}
