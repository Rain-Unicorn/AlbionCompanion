using System;

namespace AlbionCompanion.Core.DamageMeter
{
    /// <summary>伤害事件（时间戳 + 数值）。</summary>
    internal readonly record struct DamageStatsEvent(DateTime Timestamp, long Value);
}
