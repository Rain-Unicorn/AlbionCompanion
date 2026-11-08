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

        // R3：FilterMode 由 UI 线程写、抓包线程读（GetTopDamage 内做 switch），必须纳入同一把锁。
        private DamageFilter _filterMode = DamageFilter.All;
        public DamageFilter FilterMode
        {
            get { lock (_syncLock) { return _filterMode; } }
            set { lock (_syncLock) { _filterMode = value; } }
        }

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

                if (targetObjectId > 0 && newHealthValue == 0)
                {
                    // P1-5/R2：血量判据从 <=0 改为 ==0。负值 = 未知（p3 缺失时传 -1），
                    // 不得当成「刚好打死」；只有恰好归零才算最后一击
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

        /// <summary>获取所有玩家的总伤害列表（按伤害降序，受筛选影响）。-9（怪物）不进主榜。</summary>
        public List<(string PlayerName, long TotalDamage, long BiggestHit, long CurrentDps5s, long PeakBurst5s)> GetTopDamage()
        {
            lock (_syncLock)
            {
                return _players.Values
                    .Where(x => x.ObjectId != -9)
                    .Select(x => new {
                        x.PlayerName,
                        Damage = _filterMode switch {
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

        /// <summary>获取所有玩家的治疗列表（按治疗降序）。-9（怪物）不进主榜。</summary>
        public List<(string PlayerName, long TotalHeal, long BiggestHeal)> GetTopHealing()
        {
            lock (_syncLock)
            {
                return _players.Values
                    .Where(x => x.EffectiveHealing > 0 && x.ObjectId != -9)
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

        /// <summary>某玩家的「最后一击」目标数（击杀计数，T-08 断言用；只读）。</summary>
        public int GetLastHitTargetCount(long playerObjectId)
        {
            lock (_syncLock)
            {
                return _players.TryGetValue(playerObjectId, out var player) ? player.LastHitTargetObjectIds.Count : 0;
            }
        }

        /// <summary>
        /// R4/C5 前置（设计 §4.4）：按筛选求全表合计。filter 显式入参而非读可变字段，
        /// 消除 FilterMode 裸读竞态。Pve 时 TotalDamage 取 PveDamage 之和，Pvp 同理。
        /// </summary>
        public (long TotalDamage, long TotalHealing, long Pve, long Pvp, long BiggestHit, long BiggestHeal)
            GetTotals(DamageFilter filter)
        {
            lock (_syncLock)
            {
                long dmg = 0, heal = 0, pve = 0, pvp = 0, bigHit = 0, bigHeal = 0;
                foreach (var p in _players.Values)
                {
                    dmg += filter switch
                    {
                        DamageFilter.Pve => p.PveDamage,
                        DamageFilter.Pvp => p.PvpDamage,
                        _ => p.TotalDamage
                    };
                    heal += p.EffectiveHealing;
                    pve += p.PveDamage;
                    pvp += p.PvpDamage;
                    if (p.BiggestHit > bigHit) bigHit = p.BiggestHit;
                    if (p.BiggestHeal > bigHeal) bigHeal = p.BiggestHeal;
                }
                return (dmg, heal, pve, pvp, bigHit, bigHeal);
            }
        }

        /// <summary>全量快照（含负键隐藏项），供 R4 总和分流与 T-09 恒等式校验，避免 UI 多次取榜造成锁抖动。</summary>
        public List<(long Key, string Name, long TotalDamage, long TotalHealing, long Pve, long Pvp)> GetAllPlayers()
        {
            lock (_syncLock)
            {
                return _players.Values
                    .Select(x => (x.ObjectId, x.PlayerName, x.TotalDamage, x.EffectiveHealing, x.PveDamage, x.PvpDamage))
                    .ToList();
            }
        }

        /// <summary>
        /// 删除某个键的全部累计（设计 §4.4）：R1 冲突重确认时清掉已污染的 key=0 脏数据。
        /// 窗口队列随条目一并丢弃。
        /// </summary>
        public bool RemovePlayer(long key)
        {
            lock (_syncLock)
            {
                if (!_players.TryGetValue(key, out var player)) return false;
                player.BurstDamageFiveSeconds.Clear();
                player.BurstDamageTenSeconds.Clear();
                return _players.Remove(key);
            }
        }

        /// <summary>
        /// R2 关键（设计 §4.4 / T-10）：把 fromKey 的累计值**合并**进 toKey（相加而非覆盖）。
        /// 合并规则：数值项相加、Biggest* 取 max、两个 RollingWindow 不合并（保留 toKey 的）、
        /// 三个 HashSet 取并集；toKey 不存在时整条搬迁。用于 -1 后补认领与身份确认并入 key=0。
        /// </summary>
        public bool RenameKey(long fromKey, long toKey, string? name)
        {
            if (fromKey == toKey) return false;
            lock (_syncLock)
            {
                if (!_players.TryGetValue(fromKey, out var from)) return false;
                DamageStatsPlayer? to;
                if (!_players.TryGetValue(toKey, out to))
                {
                    to = new DamageStatsPlayer { ObjectId = toKey, PlayerName = string.IsNullOrEmpty(name) ? from.PlayerName : name };
                    _players[toKey] = to;
                }
                else if (!string.IsNullOrEmpty(name))
                {
                    to.PlayerName = name;
                }

                to.TotalDamage += from.TotalDamage;
                to.PveDamage += from.PveDamage;
                to.PvpDamage += from.PvpDamage;
                to.EffectiveHealing += from.EffectiveHealing;
                to.Overheal += from.Overheal;
                if (from.BiggestHit > to.BiggestHit) to.BiggestHit = from.BiggestHit;
                if (from.BiggestHeal > to.BiggestHeal) to.BiggestHeal = from.BiggestHeal;
                // RollingWindow 不合并：历史窗口已过期，直接用 toKey 的
                to.LastHitTargetObjectIds.UnionWith(from.LastHitTargetObjectIds);
                to.MobLastHitTargetObjectIds.UnionWith(from.MobLastHitTargetObjectIds);
                to.AttackedTargetObjectIds.UnionWith(from.AttackedTargetObjectIds);

                from.BurstDamageFiveSeconds.Clear();
                from.BurstDamageTenSeconds.Clear();
                _players.Remove(fromKey);
                return true;
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
