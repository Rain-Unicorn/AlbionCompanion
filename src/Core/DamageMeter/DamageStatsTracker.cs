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

        /// <summary>
        /// P0-1：被冻结（隔离待复验）的键。冻结期间该键的新伤害/治疗只进
        /// <see cref="DamageStatsPlayer"/> 的影子桶，可见累计不再增长。默认空集合 = 无冻结。
        /// </summary>
        private readonly HashSet<long> _quarantinedKeys = new();

        /// <summary>P0-1：冻结原因（按键记录，供日志/UI 说明「为什么这行不动了」）。</summary>
        private readonly Dictionary<long, string> _quarantineReasons = new();

        /// <summary>P0-1：是否有关键被冻结（UI 可据此显示「待复验」徽标）。</summary>
        public bool HasQuarantinedKeys
        {
            get { lock (_syncLock) { return _quarantinedKeys.Count > 0; } }
        }

        /// <summary>P0-1：某个键当前是否处于冻结态。</summary>
        public bool IsQuarantined(long key)
        {
            lock (_syncLock) { return _quarantinedKeys.Contains(key); }
        }

        /// <summary>P0-1：冻结原因（未冻结返回 null）。</summary>
        public string? QuarantineReason(long key)
        {
            lock (_syncLock) { return _quarantineReasons.TryGetValue(key, out var r) ? r : null; }
        }

        /// <summary>P0-1：当前所有冻结键（快照，UI 只读）。</summary>
        public List<long> GetQuarantinedKeys()
        {
            lock (_syncLock) { return new List<long>(_quarantinedKeys); }
        }

        /// <summary>
        /// P0-1：冻结某个键（设计 §4.4 的可逆替代）——**不清零**，只让该键停止演化。
        /// 已累计的数值保持原样可见可读；之后的新伤害/治疗进影子桶（Pending*），
        /// 待 <see cref="ResolveQuarantineKeep"/> / <see cref="SplitQuarantine"/> /
        /// <see cref="DiscardQuarantine"/> 三个显式出口之一裁决。
        /// 幂等：已冻结的键重复调用只更新原因，不会重复计数或丢弃影子桶。
        /// 返回 true = 本次调用新加了冻结（false = 该键此前已在冻结态）。
        /// </summary>
        public bool QuarantineKey(long key, string reason)
        {
            lock (_syncLock)
            {
                _quarantineReasons[key] = reason ?? "";
                return _quarantinedKeys.Add(key);
            }
        }

        /// <summary>
        /// P0-1 出口 A：确认「还是同一角色」——影子桶并入可见累计并解冻。
        /// 用于身份冲突被后续权威证据推翻（服务器把身份确认回原 id）的自动解冻路径。
        /// 返回 true = 原本确有冻结被解除；false = 该键未处于冻结态（无操作）。
        /// </summary>
        public bool ResolveQuarantineKeep(long key)
        {
            lock (_syncLock)
            {
                if (!_quarantinedKeys.Remove(key)) return false;
                _quarantineReasons.Remove(key);
                if (_players.TryGetValue(key, out var p)) MergePendingIntoVisible(p);
                return true;
            }
        }

        /// <summary>
        /// P0-1 出口 B：确认真换了角色——把 key 上「冻结期之前」的历史账搬到一个独立行
        /// （<paramref name="oldKey"/> / <paramref name="oldName"/>），key 自身只保留影子桶里的新账。
        /// 语义：谁都不丢，只是各归各位。key 不存在时返回 false。
        /// </summary>
        public bool SplitQuarantine(long key, long oldKey, string? oldName, string? newName)
        {
            if (oldKey == key) return false;
            lock (_syncLock)
            {
                if (!_players.TryGetValue(key, out var cur)) return false;

                // ① 旧角色的历史账（冻结前的可见累计）→ 搬到 oldKey 行（该行若已存在则相加）
                var old = new DamageStatsPlayer
                {
                    ObjectId = oldKey,
                    PlayerName = string.IsNullOrEmpty(oldName) ? cur.PlayerName : oldName,
                    BiggestHit = cur.BiggestHit,
                    BiggestHeal = cur.BiggestHeal,
                    TotalDamage = cur.TotalDamage,
                    PveDamage = cur.PveDamage,
                    PvpDamage = cur.PvpDamage,
                    EffectiveHealing = cur.EffectiveHealing,
                    Overheal = cur.Overheal,
                };
                old.LastHitTargetObjectIds.UnionWith(cur.LastHitTargetObjectIds);
                old.MobLastHitTargetObjectIds.UnionWith(cur.MobLastHitTargetObjectIds);
                old.AttackedTargetObjectIds.UnionWith(cur.AttackedTargetObjectIds);

                if (_players.TryGetValue(oldKey, out var existingOld))
                {
                    existingOld.TotalDamage += old.TotalDamage;
                    existingOld.PveDamage += old.PveDamage;
                    existingOld.PvpDamage += old.PvpDamage;
                    existingOld.EffectiveHealing += old.EffectiveHealing;
                    existingOld.Overheal += old.Overheal;
                    if (old.BiggestHit > existingOld.BiggestHit) existingOld.BiggestHit = old.BiggestHit;
                    if (old.BiggestHeal > existingOld.BiggestHeal) existingOld.BiggestHeal = old.BiggestHeal;
                    existingOld.LastHitTargetObjectIds.UnionWith(old.LastHitTargetObjectIds);
                    existingOld.MobLastHitTargetObjectIds.UnionWith(old.MobLastHitTargetObjectIds);
                    existingOld.AttackedTargetObjectIds.UnionWith(old.AttackedTargetObjectIds);
                }
                else
                {
                    _players[oldKey] = old;
                }

                // ② key 自身重建为只有影子桶内容的新角色行
                cur.TotalDamage = cur.PendingTotalDamage;
                cur.PveDamage = cur.PendingPveDamage;
                cur.PvpDamage = cur.PendingPvpDamage;
                cur.EffectiveHealing = cur.PendingEffectiveHealing;
                cur.BiggestHit = cur.PendingBiggestHit;
                cur.BiggestHeal = cur.PendingBiggestHeal;
                cur.LastHitTargetObjectIds.Clear();
                cur.MobLastHitTargetObjectIds.Clear();
                cur.AttackedTargetObjectIds.Clear();
                cur.ClearPending();
                if (!string.IsNullOrEmpty(newName)) cur.PlayerName = newName;

                _quarantinedKeys.Remove(key);
                _quarantineReasons.Remove(key);
                return true;
            }
        }

        /// <summary>
        /// P0-1 出口 C：用户明确认定这份是脏数据——**唯一真正的删除路径**，且只能由人显式触发。
        /// 同时清掉影子桶（那是同一份可疑账的延续）。
        /// </summary>
        public bool DiscardQuarantine(long key)
        {
            lock (_syncLock)
            {
                _quarantinedKeys.Remove(key);
                _quarantineReasons.Remove(key);
                if (!_players.TryGetValue(key, out var p)) return false;
                p.BurstDamageFiveSeconds.Clear();
                p.BurstDamageTenSeconds.Clear();
                p.ClearPending();
                return _players.Remove(key);
            }
        }

        /// <summary>影子桶并入可见累计（保留语义）；调用方必须已持有 _syncLock。</summary>
        private static void MergePendingIntoVisible(DamageStatsPlayer p)
        {
            if (!p.HasPending) return;
            p.TotalDamage += p.PendingTotalDamage;
            p.PveDamage += p.PendingPveDamage;
            p.PvpDamage += p.PendingPvpDamage;
            p.EffectiveHealing += p.PendingEffectiveHealing;
            if (p.PendingBiggestHit > p.BiggestHit) p.BiggestHit = p.PendingBiggestHit;
            if (p.PendingBiggestHeal > p.BiggestHeal) p.BiggestHeal = p.PendingBiggestHeal;
            p.ClearPending();
        }

        /// <summary>
        /// P0-1：只清冻结态与影子桶，**不动**可见累计。用于身份状态机复位（测试隔离 / 会话重置）——
        /// 冻结是「身份待复验」的产物，身份都复位了，冻结标记也必须跟着消失，
        /// 否则下一个会话会在一个没人知道为什么冻结的键上继续静默吞数据。
        /// </summary>
        public void ResetQuarantine()
        {
            lock (_syncLock)
            {
                _quarantinedKeys.Clear();
                _quarantineReasons.Clear();
                foreach (var p in _players.Values) p.ClearPending();
            }
        }

        /// <summary>P0-1：读某键的影子桶（伤害, 治疗）。测试与 UI 提示用；未冻结/无键返回 (0,0)。</summary>
        public (long Damage, long Healing) GetPendingTotals(long key)
        {
            lock (_syncLock)
            {
                if (!_players.TryGetValue(key, out var p)) return (0, 0);
                return (p.PendingTotalDamage, p.PendingEffectiveHealing);
            }
        }

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

                // P0-1 冻结期：新伤害只进影子桶，可见累计/窗口/目标集合一律不更新。
                // 语义：这条可疑行「停止演化」，等三个显式出口裁决后再并入或拆分。
                if (_quarantinedKeys.Contains(playerObjectId))
                {
                    player.PendingTotalDamage += value;
                    if (isMobTarget) player.PendingPveDamage += value;
                    else player.PendingPvpDamage += value;
                    if (value > player.PendingBiggestHit) player.PendingBiggestHit = value;
                    return;
                }

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

                // P0-1 冻结期：治疗同样只进影子桶。
                if (_quarantinedKeys.Contains(playerObjectId))
                {
                    player.PendingEffectiveHealing += value;
                    if (value > player.PendingBiggestHeal) player.PendingBiggestHeal = value;
                    return;
                }

                player.BiggestHeal = Math.Max(player.BiggestHeal, value);
                player.EffectiveHealing += value;
            }
        }

        /// <summary>
        /// 获取所有玩家的总伤害列表（按伤害降序，受筛选影响）。-9（怪物）不进主榜。
        /// R2：返回归档键 Key（0=我 / 正数=玩家 / 负数=未知来源），让 UI 能把「未知玩家」行
        /// 与 self 行分开处理（组队过滤时未知行不得被当成队友一起隐藏，也不得冒充 self）。
        /// </summary>
        public List<(long Key, string PlayerName, long TotalDamage, long BiggestHit, long CurrentDps5s, long PeakBurst5s)> GetTopDamage()
        {
            lock (_syncLock)
            {
                return _players.Values
                    .Where(x => x.ObjectId != -9)
                    .Select(x => new {
                        Key = x.ObjectId,
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
                    .Select(x => (Key: x.Key, x.PlayerName, TotalDamage: x.Damage, x.BiggestHit,
                                  CurrentDps5s: x.Dps, PeakBurst5s: x.Peak))
                    .ToList();
            }
        }

        /// <summary>获取所有玩家的治疗列表（按治疗降序）。-9（怪物）不进主榜。R2：带归档键 Key。</summary>
        public List<(long Key, string PlayerName, long TotalHeal, long BiggestHeal)> GetTopHealing()
        {
            lock (_syncLock)
            {
                return _players.Values
                    .Where(x => x.EffectiveHealing > 0 && x.ObjectId != -9)
                    .OrderByDescending(x => x.EffectiveHealing)
                    .Select(x => (Key: x.ObjectId, x.PlayerName, TotalHeal: x.EffectiveHealing, x.BiggestHeal))
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
        /// P0-1：单键合计（伤害, 治疗）。用于「拆分角色」等出口的验证与 UI 单行读取——
        /// 全表 <see cref="GetTotals"/> 会把所有键加在一起，验证不了「某一段账到底落在哪一行」。
        /// 键不存在返回 (0,0)。
        /// </summary>
        public (long TotalDamage, long TotalHealing) GetTotalsForKey(long key)
        {
            lock (_syncLock)
            {
                return _players.TryGetValue(key, out var p)
                    ? (p.TotalDamage, p.EffectiveHealing)
                    : (0L, 0L);
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
        /// 删除某个键的全部累计（设计 §4.4 的**显式**删除路径）。
        /// 口语化口径变更（P0-1）：身份冲突**不再**默认走这里。冲突时的默认动作是
        /// <see cref="QuarantineKey"/>「保留已累计 + 冻结该键 + 待复验」；
        /// 真正要删必须由人显式触发（<see cref="DiscardQuarantine"/>），且那条路径会一并清掉影子桶。
        /// 本方法保留给「用户点清空 / 会话重置」这类明确意图，不作为冲突兜底。
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
                if (_players.TryGetValue(toKey, out to))
                {
                    if (!string.IsNullOrEmpty(name)) to.PlayerName = name;
                }
                else
                {
                    to = new DamageStatsPlayer { ObjectId = toKey, PlayerName = string.IsNullOrEmpty(name) ? from.PlayerName : name };
                    _players[toKey] = to;
                }
                if (to == null) return false;

                // P0-1：目标键冻结时，来源键的可见累计也只能进影子桶——
                // 否则一次「并入 key=0」就能让冻结行的可见数字涨起来，冻结失效。
                bool toQuarantined = _quarantinedKeys.Contains(toKey);
                if (toQuarantined)
                {
                    to.PendingTotalDamage += from.TotalDamage;
                    to.PendingPveDamage += from.PveDamage;
                    to.PendingPvpDamage += from.PvpDamage;
                    to.PendingEffectiveHealing += from.EffectiveHealing;
                    if (from.BiggestHit > to.PendingBiggestHit) to.PendingBiggestHit = from.BiggestHit;
                    if (from.BiggestHeal > to.PendingBiggestHeal) to.PendingBiggestHeal = from.BiggestHeal;
                    to.Overheal += from.Overheal;
                }
                else
                {
                    to.TotalDamage += from.TotalDamage;
                    to.PveDamage += from.PveDamage;
                    to.PvpDamage += from.PvpDamage;
                    to.EffectiveHealing += from.EffectiveHealing;
                    to.Overheal += from.Overheal;
                    if (from.BiggestHit > to.BiggestHit) to.BiggestHit = from.BiggestHit;
                    if (from.BiggestHeal > to.BiggestHeal) to.BiggestHeal = from.BiggestHeal;
                }
                // RollingWindow 不合并：历史窗口已过期，直接用 toKey 的
                to.LastHitTargetObjectIds.UnionWith(from.LastHitTargetObjectIds);
                to.MobLastHitTargetObjectIds.UnionWith(from.MobLastHitTargetObjectIds);
                to.AttackedTargetObjectIds.UnionWith(from.AttackedTargetObjectIds);

                // P0-1：影子桶「影随影」——来源键的影子账永远只并入目标键的影子桶，
                // 无论目标键是否冻结，都不会在这一步悄悄转正。
                to.PendingTotalDamage += from.PendingTotalDamage;
                to.PendingPveDamage += from.PendingPveDamage;
                to.PendingPvpDamage += from.PendingPvpDamage;
                to.PendingEffectiveHealing += from.PendingEffectiveHealing;
                if (from.PendingBiggestHit > to.PendingBiggestHit) to.PendingBiggestHit = from.PendingBiggestHit;
                if (from.PendingBiggestHeal > to.PendingBiggestHeal) to.PendingBiggestHeal = from.PendingBiggestHeal;

                from.BurstDamageFiveSeconds.Clear();
                from.BurstDamageTenSeconds.Clear();
                _players.Remove(fromKey);

                // P0-1：冻结态跟随键搬迁——否则 fromKey 解冻了、toKey 反而永远冻住（或反之）。
                // 影子桶的数值已随上面的相加并入 toKey 的可见值，这里只需搬冻结标记。
                if (_quarantinedKeys.Remove(fromKey))
                {
                    _quarantineReasons.TryGetValue(fromKey, out var reason);
                    _quarantineReasons.Remove(fromKey);
                    _quarantinedKeys.Add(toKey);
                    _quarantineReasons[toKey] = reason ?? "";
                }
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
                    player.ClearPending();   // P0-1：清空语义要连同影子桶一起清，否则残留会在下次解冻时冒出来
                }
                _players.Clear();
                _quarantinedKeys.Clear();
                _quarantineReasons.Clear();
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
