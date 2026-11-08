using System;
using System.Collections.Generic;

namespace AlbionCompanion.Core.DamageMeter
{
    /// <summary>滚动伤害窗口，用于计算 5 秒/10 秒峰值伤害和实时DPS（照搬 SAT）。</summary>
    internal sealed class RollingDamageWindow
    {
        private readonly TimeSpan _duration;
        private readonly Queue<DamageStatsEvent> _events = new();
        private long _currentDamage;

        public RollingDamageWindow(TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(duration));
            }

            _duration = duration;
        }

        /// <summary>窗口内累计伤害（实时）</summary>
        public long CurrentDamage
        {
            get
            {
                TrimExpired();
                return _currentDamage;
            }
        }

        /// <summary>历史最高累计伤害（峰值爆发）</summary>
        public long HighestDamage { get; private set; }

        /// <summary>实时DPS = 当前窗口内伤害 / 窗口秒数</summary>
        public double CalculateDps()
        {
            TrimExpired();
            return _currentDamage / _duration.TotalSeconds;
        }

        public void AddDamage(DamageStatsEvent damageEvent)
        {
            _events.Enqueue(damageEvent);
            _currentDamage += damageEvent.Value;

            TrimExpired();

            HighestDamage = Math.Max(HighestDamage, _currentDamage);
        }

        /// <summary>清空窗口全部事件，重置统计</summary>
        public void Clear()
        {
            _events.Clear();
            _currentDamage = 0;
            HighestDamage = 0;
        }

        /// <summary>移除已过期的事件（在 Add 和 CalculateDps 时调用）</summary>
        private void TrimExpired()
        {
            DateTime now = DateTime.UtcNow;
            while (_events.TryPeek(out var oldestEvent) && now - oldestEvent.Timestamp > _duration)
            {
                _events.Dequeue();
                _currentDamage -= oldestEvent.Value;
            }
        }
    }
}
