using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionCompanion.Core.DamageMeter
{
    /// <summary>伤害统计核心追踪器（照搬 SAT，适配 long ObjectId）。</summary>
    public sealed class DamageStatsTracker
    {
        public enum DamageFilter { All, Pve, Pvp }

        private readonly object _syncLock = new();
        private readonly Dictionary<long, DamageStatsPlayer> _players = new();
        public DamageFilter FilterMode { get; set; } = DamageFilter.All;

        /// <summary>记录伤害（照搬 SAT AddDamage 逻辑）。</summary>
        public void RecordDamage(
            long playerObjectId,
            string playerName,
            long targetObjectId,
            long value,
            double newHealthValue,
            bool isMobTarget)
        {
            if (value <= 0)
            {
                return;
            }

            lock (_syncLock)
            {
                var player = GetOrAddPlayer(playerObjectId, playerName);
                var damageEvent = new DamageStatsEvent(DateTime.UtcNow, value);
                player.BiggestHit = Math.Max(player.BiggestHit, value);
                player.TotalDamage += value;
                if (isMobTarget)
                    player.PveDamage += value;
                else
                    player.PvpDamage += value;
                player.BurstDamageFiveSeconds.AddDamage(damageEvent);
                player.BurstDamageTenSeconds.AddDamage(damageEvent);

                if (targetObjectId > 0)
                {
                    player.AttackedTargetObjectIds.Add(targetObjectId);
                }

                if (targetObjectId > 0 && newHealthValue <= 0)
                {
                    player.LastHitTargetObjectIds.Add(targetObjectId);
                    if (isMobTarget)
                    {
                        player.MobLastHitTargetObjectIds.Add(targetObjectId);
                    }
                }
            }
        }

        /// <summary>记录治疗（照搬 SAT AddHeal 逻辑）。</summary>
        public void RecordHeal(long playerObjectId, string playerName, long value)
        {
            if (value <= 0)
            {
                return;
            }

            lock (_syncLock)
            {
                var player = GetOrAddPlayer(playerObjectId, playerName);
                player.BiggestHeal = Math.Max(player.BiggestHeal, value);
                player.EffectiveHealing += value;
            }
        }

        /// <summary>获取所有玩家的总伤害列表（按伤害降序，受筛选影响）。</summary>
        public List<(string PlayerName, long TotalDamage, long BiggestHit, long CurrentDps5s, long PeakBurst5s)> GetTopDamage()
        {
            lock (_syncLock)
            {
                return _players.Values
                    .Select(x => new {
                        x.PlayerName,
                        Damage = FilterMode switch {
                            DamageFilter.Pve => x.PveDamage,
                            DamageFilter.Pvp => x.PvpDamage,
                            _ => x.TotalDamage
                        },
                        x.BiggestHit,
                        Dps = (long)x.BurstDamageFiveSeconds.CalculateDps(),
                        Peak = x.BurstDamageFiveSeconds.HighestDamage
                    })
                    .Where(x => x.Damage > 0)
                    .OrderByDescending(x => x.Damage)
                    .Select(x => (x.PlayerName, x.Damage, x.BiggestHit, x.Dps, x.Peak))
                    .ToList();
            }
        }

        /// <summary>获取所有玩家的治疗列表（按治疗降序）。</summary>
        public List<(string PlayerName, long TotalHeal, long BiggestHeal)> GetTopHealing()
        {
            lock (_syncLock)
            {
                return _players.Values
                    .Where(x => x.EffectiveHealing > 0)
                    .OrderByDescending(x => x.EffectiveHealing)
                    .Select(x => (x.PlayerName, x.EffectiveHealing, x.BiggestHeal))
                    .ToList();
            }
        }

        /// <summary>获取某玩家5秒窗口实时DPS</summary>
        public double GetPlayerDpsFiveSec(long playerObjectId)
        {
            lock (_syncLock)
            {
                if (!_players.TryGetValue(playerObjectId, out var player)) return 0;
                return player.BurstDamageFiveSeconds.CalculateDps();
            }
        }

        /// <summary>获取某玩家10秒窗口实时DPS</summary>
        public double GetPlayerDpsTenSec(long playerObjectId)
        {
            lock (_syncLock)
            {
                if (!_players.TryGetValue(playerObjectId, out var player)) return 0;
                return player.BurstDamageTenSeconds.CalculateDps();
            }
        }

        /// <summary>清空所有数据（同时清空窗口内部事件队列）。</summary>
        public void Clear()
        {
            lock (_syncLock)
            {
                foreach (var player in _players.Values)
                {
                    player.BurstDamageFiveSeconds.Clear();
                    player.BurstDamageTenSeconds.Clear();
                }
                _players.Clear();
            }
        }

        private DamageStatsPlayer GetOrAddPlayer(long playerObjectId, string playerName)
        {
            if (_players.TryGetValue(playerObjectId, out var player))
            {
                if (!string.IsNullOrEmpty(playerName))
                {
                    player.PlayerName = playerName;
                }
                return player;
            }

            player = new DamageStatsPlayer
            {
                ObjectId = playerObjectId,
                PlayerName = playerName
            };

            _players.Add(playerObjectId, player);
            return player;
        }
    }
}
