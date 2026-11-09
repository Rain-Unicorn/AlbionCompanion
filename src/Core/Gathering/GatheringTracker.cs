#nullable disable
using System;
using System.Collections.Generic;

namespace AlbionCompanion.Core.Gathering
{
    /// <summary>一条采集记录（evt=61 HarvestFinished）。</summary>
    public sealed class GatheringRecord
    {
        public DateTime TimeUtc;
        public int ItemId;
        public string Resource = "";   // 展示名（ItemDatabase.GetDisplayName）
        public int Amount;
        public int Fame;
    }

    /// <summary>
    /// 采集聚合结果（设计 §8.6）：总数量、总声望、次数、按类别小计（数量/声望/次数）。
    /// 额外暴露「未分类」计数与防护/诊断计数：所有被丢弃/未归属的数据都必须有计数器，绝不静默。
    /// 本对象每次 GetSummary() 都新建，持有它不会看到后续变化（快照语义）。
    /// </summary>
    public sealed class GatheringSummary
    {
        public long TotalAmount;
        public long TotalFame;
        public int Count;

        /// <summary>类别 → 数量小计（含「其他」= 未分类桶）。保持既有字段/类型不变（既有断言依赖）。</summary>
        public Dictionary<string, long> ByCategory = new Dictionary<string, long>();
        /// <summary>类别 → 声望小计。</summary>
        public Dictionary<string, long> FameByCategory = new Dictionary<string, long>();
        /// <summary>类别 → 次数小计。</summary>
        public Dictionary<string, int> CountByCategory = new Dictionary<string, int>();

        // 未分类（「其他」桶）单独再暴露一份，UI 无需从字典里按 key 猜
        public long UnclassifiedAmount;
        public long UnclassifiedFame;
        public int UnclassifiedCount;

        // ── 防护/诊断计数（本次会话累计；Clear 不清、Reset 清零）──
        /// <summary>重复 Add 被丢弃的条数（同一对象重投 / 同一时刻同物品同数量）。</summary>
        public int DuplicateAdds;
        /// <summary>时间戳早于已有最新记录、被按时间插入的条数（乱序到达）。</summary>
        public int OutOfOrderAdds;
        /// <summary>重复声望更新被忽略的次数（最近一条已带声望 → 首个声望为准，不再覆盖）。</summary>
        public int DuplicateFameUpdates;
        /// <summary>非法记录被拒（null / 数量或声望为负）的条数。</summary>
        public int RejectedAdds;
        /// <summary>非法声望更新被拒（fame&lt;=0）的次数。</summary>
        public int RejectedFameUpdates;
        /// <summary>超出 MaxRecords 被裁剪的最旧记录条数。</summary>
        public int TrimmedRecords;
        /// <summary>evt=72 先于 evt=61 到达、当前暂存等待归属的声望条数。</summary>
        public int PendingFameCount;
        /// <summary>暂存声望成功归到采集记录上的次数。</summary>
        public int PendingFameApplied;
        /// <summary>暂存声望超时/多义竞争而无法归属的次数（不静默，UI 可见）。</summary>
        public int UnattributedFameCount;
    }

    /// <summary>
    /// 采集唯一权威源（设计 §8.6，与 IncomeTracker 同构）。
    /// 类别归类复用 <see cref="GatherCategories"/>——§8.6 明令禁止重写 HarvestTab 的 Contains("矿石") 字符串匹配。
    /// evt=72 声望回流走 <see cref="UpdateLastFame"/>。
    ///
    /// 并发与乱序（evt=61/evt=72 可能落在不同线程）：
    ///   - Add 在锁内按 TimeUtc 有序插入；乱序到达的旧记录不会冒充「最近一条」。
    ///   - Add 去重：同一对象重复投递，或 TimeUtc/ItemId/Amount 完全相同的重复事件被丢弃并计数。
    ///   - UpdateLastFame 只覆盖「最近一条尚未带声望」的记录：已带声望不覆盖（防重复 72 /
    ///     防后到的其它声望事件把采集声望改掉）；记录为空或最近一条不可用时暂存（TTL 5s），
    ///     由 5s 内到达的下一条采集记录认领；超时/多义按「未归属」计数。
    ///   - 所有对外返回都是拷贝：Add 入参、Snapshot、GetSummary 都不与内部状态共享引用。
    /// </summary>
    public static class GatheringTracker
    {
        public const int MaxRecords = 2000;

        /// <summary>未分类桶 key（既有断言已钉死「其他」；UI 展示为「未分类」）。</summary>
        public const string UnclassifiedCategory = GatherCategories.CategoryUnknown;

        /// <summary>UI 固定展示顺序：已确认类别在前，未分类最后。</summary>
        public static readonly string[] CategoryDisplayOrder = new string[]
        {
            GatherCategories.CategoryOre,
            GatherCategories.CategoryFiber,
            GatherCategories.CategoryWood,
            GatherCategories.CategoryHide,
            GatherCategories.CategoryStone,
            UnclassifiedCategory
        };

        /// <summary>evt=72 早于 evt=61 到达时的暂存有效期（超过则计入未归属）。</summary>
        public static readonly TimeSpan PendingFameTtl = TimeSpan.FromSeconds(5);

        /// <summary>
        /// 最近一条已带声望后，多快之内再来的 72 判定为「同一次采集的重复声望事件」。
        /// 超过该窗口再来的 72 更可能是「下一次采集的 72 比它的 61 先到」→ 暂存等认领（乱序防护）。
        /// </summary>
        public static readonly TimeSpan DuplicateFameWindow = TimeSpan.FromSeconds(2);

        private static readonly object _lock = new object();
        private static readonly List<GatheringRecord> _records = new List<GatheringRecord>();
        private static readonly List<PendingFame> _pendingFames = new List<PendingFame>();

        private static int _duplicateAdds;
        private static int _outOfOrderAdds;
        private static int _duplicateFameUpdates;
        private static int _rejectedAdds;
        private static int _rejectedFameUpdates;
        private static int _trimmedRecords;
        private static int _pendingFameApplied;
        private static int _unattributedFameCount;
        private static DateTime _lastFameSetUtc = DateTime.MinValue;

        /// <summary>测试注入时钟（null = DateTime.UtcNow）。用于确定性验证暂存 TTL，生产不设置。</summary>
        internal static Func<DateTime> UtcNowForTests = null;

        private static DateTime UtcNow
        {
            get
            {
                var clock = UtcNowForTests;
                return clock != null ? clock() : DateTime.UtcNow;
            }
        }

        private struct PendingFame
        {
            public int Fame;
            public DateTime SeenUtc;
        }

        /// <summary>
        /// 落一条采集记录。返回 false = 被丢弃（null/非法/重复），丢弃原因计入 summary 的防护计数。
        /// 记录会被拷贝后保存，调用方之后修改入参不影响统计。
        /// </summary>
        public static bool Add(GatheringRecord r)
        {
            if (r == null)
            {
                lock (_lock) { _rejectedAdds++; }
                return false;
            }
            if (r.Amount < 0 || r.Fame < 0)
            {
                lock (_lock) { _rejectedAdds++; }
                return false;
            }

            lock (_lock)
            {
                var record = Clone(r);

                // 去重：抓包层重复投递不会带来新信息——同一对象，或同一时刻同物品同数量
                if (_records.Count > 0)
                {
                    GatheringRecord last = _records[_records.Count - 1];
                    if (ReferenceEquals(r, last) ||
                        (r.TimeUtc == last.TimeUtc && r.ItemId == last.ItemId && r.Amount == last.Amount))
                    {
                        _duplicateAdds++;
                        return false;
                    }
                }

                // 乱序防护：按 TimeUtc 有序插入，保证「最近一条」= 时间最新
                int insertAt = _records.Count;
                if (insertAt > 0 && record.TimeUtc < _records[insertAt - 1].TimeUtc)
                {
                    insertAt = _records.Count - 1;
                    while (insertAt > 0 && _records[insertAt - 1].TimeUtc > record.TimeUtc) insertAt--;
                    _records.Insert(insertAt, record);
                    _outOfOrderAdds++;
                }
                else
                {
                    _records.Add(record);
                }

                if (_records.Count > MaxRecords)
                {
                    int trim = _records.Count - MaxRecords;
                    _records.RemoveRange(0, trim);
                    _trimmedRecords += trim;
                }

                // 乱序：evt=72 先到（记录为空/旧）时被暂存，evt=61 落账后立即认领
                if (ReferenceEquals(_records[_records.Count - 1], record) && record.Fame == 0)
                    TryApplyPendingFameLocked(record);

                return true;
            }
        }

        /// <summary>
        /// evt=72 声望回流：覆盖「最近一条尚未带声望」的采集记录的 Fame。
        /// 返回 true = 已写入；false = 未写入（空/重复/非法，原因见 summary 计数）。
        /// 已带声望的记录不会被覆盖：重复 72 或其它来源的声望事件不能改写已归属的采集声望。
        /// </summary>
        public static bool UpdateLastFame(int fame)
        {
            if (fame <= 0)
            {
                lock (_lock) { _rejectedFameUpdates++; }
                return false;
            }

            lock (_lock)
            {
                DateTime now = UtcNow;
                SweepPendingFameLocked(now);

                if (_records.Count == 0)
                {
                    // 乱序：72 先到、61 尚未落账 → 暂存（短时间内由下一条 61 认领）
                    _pendingFames.Add(new PendingFame { Fame = fame, SeenUtc = now });
                    return false;
                }

                GatheringRecord last = _records[_records.Count - 1];
                if (last.Fame > 0)
                {
                    // 最近一条已带声望：短时间内再来 72 = 同一次采集的重复/多来源声望 → 重复计数，不覆盖
                    if (now - _lastFameSetUtc <= DuplicateFameWindow)
                    {
                        _duplicateFameUpdates++;
                        return false;
                    }
                    // 间隔较长：可能是「下一次采集的 72 比它的 61 先到」→ 暂存，由 5s 内的下一条记录认领
                    _pendingFames.Add(new PendingFame { Fame = fame, SeenUtc = now });
                    return false;
                }

                last.Fame = fame;
                _lastFameSetUtc = now;
                return true;
            }
        }

        /// <summary>聚合快照：总量/声望/次数 + 按类别小计 + 未分类/防护计数。每次新建对象，不共享内部状态。</summary>
        public static GatheringSummary GetSummary()
        {
            var summary = new GatheringSummary();
            lock (_lock)
            {
                SweepPendingFameLocked(UtcNow);

                summary.Count = _records.Count;
                for (int i = 0; i < _records.Count; i++)
                {
                    GatheringRecord record = _records[i];
                    string category = CategoryOf(record.ItemId);

                    summary.TotalAmount += record.Amount;
                    summary.TotalFame += record.Fame;

                    summary.ByCategory[category] =
                        (summary.ByCategory.ContainsKey(category) ? summary.ByCategory[category] : 0L) + record.Amount;
                    summary.FameByCategory[category] =
                        (summary.FameByCategory.ContainsKey(category) ? summary.FameByCategory[category] : 0L) + record.Fame;
                    summary.CountByCategory[category] =
                        (summary.CountByCategory.ContainsKey(category) ? summary.CountByCategory[category] : 0) + 1;

                    if (category == UnclassifiedCategory)
                    {
                        summary.UnclassifiedAmount += record.Amount;
                        summary.UnclassifiedFame += record.Fame;
                        summary.UnclassifiedCount++;
                    }
                }

                summary.DuplicateAdds = _duplicateAdds;
                summary.OutOfOrderAdds = _outOfOrderAdds;
                summary.DuplicateFameUpdates = _duplicateFameUpdates;
                summary.RejectedAdds = _rejectedAdds;
                summary.RejectedFameUpdates = _rejectedFameUpdates;
                summary.TrimmedRecords = _trimmedRecords;
                summary.PendingFameCount = _pendingFames.Count;
                summary.PendingFameApplied = _pendingFameApplied;
                summary.UnattributedFameCount = _unattributedFameCount;
            }
            return summary;
        }

        /// <summary>快照：新在前，最多 max 条；返回深拷贝，改返回值不影响统计。</summary>
        public static GatheringRecord[] Snapshot(int max)
        {
            if (max <= 0) return new GatheringRecord[0];
            lock (_lock)
            {
                int count = Math.Min(max, _records.Count);
                var result = new GatheringRecord[count];
                for (int i = 0; i < count; i++)
                    result[i] = Clone(_records[_records.Count - 1 - i]);
                return result;
            }
        }

        /// <summary>数字 ID → 类别中文名（与 GetSummary 小计同源）；判不出来返回「其他」（未分类桶）。</summary>
        public static string CategoryOf(int itemId)
        {
            return GatherCategories.GetResourceName(itemId);
        }

        /// <summary>清空采集记录与待归属声望（UI「清空」语义）；本次会话诊断计数保留。</summary>
        public static void Clear()
        {
            lock (_lock)
            {
                _records.Clear();
                _pendingFames.Clear();
                _lastFameSetUtc = DateTime.MinValue;
            }
        }

        /// <summary>全部清零：记录 + 待归属声望 + 诊断计数（会话重置 / 测试隔离）。</summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _records.Clear();
                _pendingFames.Clear();
                _duplicateAdds = 0;
                _outOfOrderAdds = 0;
                _duplicateFameUpdates = 0;
                _rejectedAdds = 0;
                _rejectedFameUpdates = 0;
                _trimmedRecords = 0;
                _pendingFameApplied = 0;
                _unattributedFameCount = 0;
                _lastFameSetUtc = DateTime.MinValue;
            }
        }

        // ── 内部实现 ──────────────────────────────────────────────────────

        private static GatheringRecord Clone(GatheringRecord source)
        {
            return new GatheringRecord
            {
                TimeUtc = source.TimeUtc,
                ItemId = source.ItemId,
                Resource = source.Resource ?? "",
                Amount = source.Amount,
                Fame = source.Fame
            };
        }

        /// <summary>清掉超 TTL 的暂存声望，并计入「未归属」——丢失必须可见。</summary>
        private static void SweepPendingFameLocked(DateTime now)
        {
            for (int i = _pendingFames.Count - 1; i >= 0; i--)
            {
                if (now - _pendingFames[i].SeenUtc > PendingFameTtl)
                {
                    _pendingFames.RemoveAt(i);
                    _unattributedFameCount++;
                }
            }
        }

        /// <summary>最新采集记录认领暂存声望：恰好一条才归属；多条说明无法判定归属 → 全部计入未归属。</summary>
        private static void TryApplyPendingFameLocked(GatheringRecord newest)
        {
            SweepPendingFameLocked(UtcNow);
            if (_pendingFames.Count == 1)
            {
                newest.Fame = _pendingFames[0].Fame;
                _pendingFames.Clear();
                _pendingFameApplied++;
                _lastFameSetUtc = UtcNow;
            }
            else if (_pendingFames.Count > 1)
            {
                _unattributedFameCount += _pendingFames.Count;
                _pendingFames.Clear();
            }
        }
    }
}
