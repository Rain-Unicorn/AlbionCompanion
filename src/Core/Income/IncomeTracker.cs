using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;

namespace AlbionCompanion.Core.Income
{
    /// <summary>收益来源（设计 §8.3.1）。SilverPickup=evt=62 本地拾取；PartySilver=evt=243 队伍分成（金额待确认）；GatheringSale=手工账本口径。</summary>
    public enum IncomeSource { SilverPickup, PartySilver, GatheringSale, Unknown }

    /// <summary>一条收益记录（金额均为显示单位：原始 FixPoint ÷ 10000）。</summary>
    public sealed class IncomeRecord
    {
        public DateTime TimeUtc;
        public IncomeSource Source;
        public string SourceLabel = "";    // 展示用："拾取银币" / "队伍分成" / "采集出售"
        public string Detail = "";         // 如物品名、目标名
        public long PickerId;             // 拾取者 ObjectId（pending 按 pickerId 分流用；正式入账后仍保留备查）
        public long SilverAfterTax;
        public long SilverBeforeTax;      // 仅 SilverPickup 有值，其余等于 AfterTax
        public long TaxPaid;              // cluster + guild + alliance 合计
        public bool Pending;              // true = 身份未确认期间挂起
        public bool AmountUnconfirmed;    // true = 金额字段未实机确认（evt=243：只记 0 值待确认条目，§8.3.2）
        public string AttributionNote = ""; // 如 "p8=..."（倍率原值，不参与计算）/"金额字段未确认"
    }

    /// <summary>收益聚合结果（合计只含已确认条目；待确认单独计数）。</summary>
    public sealed class IncomeSummary
    {
        public long ConfirmedTotal;       // 已确认合计（银币）
        public long SelfTotal;            // 本地玩家已确认合计（确认入口只有「是我」一条路径，恒等于 ConfirmedTotal）
        public long SilverPickupTotal;    // 来源小计：拾取银币（已确认，§8.3.2）
        public long PartySilverTotal;     // 来源小计：队伍分成（金额未确认前恒为 0）
        public long GatheringSaleTotal;   // 来源小计：采集出售（手工账口径，不并入抓包合计；仅构造记录时计入）
        public int ConfirmedCount;
        public int UnconfirmedRecords;    // pending 条数
        public int AmountUnconfirmedCount; // 金额待确认条数（evt=243）
        public long PendingSilver;        // pending 合计（显示单位）
        public int RecordCount;
    }

    /// <summary>
    /// 收益唯一权威源（设计 §8.3），并承担 R1 的 pending 桶：
    /// 候选期间挂起的拾取记 Pending=true；身份确认后按 pickerId 分流（是我→转正，不是我→丢弃）。
    /// C7 补齐：来源细分（§8.3.2）、evt=243 待确认条目、JSONL 持久化（§8.3.3）、CSV 导出（§8.3.4）。
    /// 依赖方向铁律（§8.1）：聚合器之间互不引用——本类只依赖 LocalPlayerIdentity（经调用方裁决传入）与 GameEvents 事件流镜像。
    /// </summary>
    public static class IncomeTracker
    {
        private const int MaxConfirmed = 2000;
        private const int MaxPending = 1000;

        private static readonly object _lock = new object();
        private static readonly List<IncomeRecord> _confirmed = new List<IncomeRecord>();
        private static readonly List<IncomeRecord> _pending = new List<IncomeRecord>();

        // ---- JSONL 持久化（§8.3.3）：入账即追加，单独写线程消费队列，按日切分 ----
        private static readonly ConcurrentQueue<string> _jsonlQueue = new ConcurrentQueue<string>();
        private static readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private static readonly object _writerGate = new object();
        private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions { IncludeFields = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }; // 中文不转义，JSONL 人类可读
        private static volatile string? _persistDir;
        private static Thread? _writer;
        private static long _enqueued;
        private static long _written;

        public static long TotalSilver
        {
            get { lock (_lock) { return SumLocked(_confirmed); } }
        }

        /// <summary>正式入账（已确认是我的收益）。</summary>
        public static void AddConfirmed(IncomeRecord r)
        {
            if (r == null) return;
            lock (_lock)
            {
                r.Pending = false;
                _confirmed.Add(r);
                if (_confirmed.Count > MaxConfirmed) _confirmed.RemoveRange(0, _confirmed.Count - MaxConfirmed);
            }
            EnqueueJsonl(r);
            MirrorToGameEvents(r);
        }

        /// <summary>
        /// evt=243 队伍分成（§8.3.2）：语义为「队友捡银币后分配给我的份额」，不走 evt=62、不构成重复入账。
        /// 金额字段位置未实机确认——只记「金额=0、待确认」条目（AttributionNote=金额字段未确认），绝不猜测字段。
        /// </summary>
        public static void AddPartySilverUnconfirmed()
        {
            AddConfirmed(new IncomeRecord
            {
                TimeUtc = DateTime.UtcNow,
                Source = IncomeSource.PartySilver,
                SourceLabel = "队伍分成",
                Detail = "",
                SilverAfterTax = 0,
                SilverBeforeTax = 0,
                TaxPaid = 0,
                AmountUnconfirmed = true,
                AttributionNote = "金额字段未确认"
            });
        }

        /// <summary>挂起入账（候选期间，金额未计入任何合计，也不落盘）。</summary>
        public static void AddPending(IncomeRecord r)
        {
            if (r == null) return;
            lock (_lock)
            {
                r.Pending = true;
                _pending.Add(r);
                if (_pending.Count > MaxPending) _pending.RemoveRange(0, _pending.Count - MaxPending);
            }
        }

        /// <summary>
        /// 身份已确认：pending 按 pickerId 分流——
        /// pickerId == selfObjectId → 转为正式入账（rename 而非丢弃，T-03；转正时补写 JSONL）；
        /// 其余（含旧候选的误挂起）→ 整体丢弃。candidateId 仅用于日志。
        /// </summary>
        public static void ResolvePending(long selfObjectId, long? candidateId)
        {
            List<IncomeRecord> promoted = new List<IncomeRecord>();
            lock (_lock)
            {
                for (int i = 0; i < _pending.Count; i++)
                {
                    var r = _pending[i];
                    if (r.PickerId == selfObjectId) promoted.Add(r);
                }
                int dropped = _pending.Count - promoted.Count;
                _pending.Clear();
                foreach (var r in promoted)
                {
                    r.Pending = false;
                    _confirmed.Add(r);
                }
                if (_confirmed.Count > MaxConfirmed) _confirmed.RemoveRange(0, _confirmed.Count - MaxConfirmed);
                if (dropped > 0 && candidateId != null && candidateId != selfObjectId)
                    System.Diagnostics.Debug.WriteLine("[IncomeTracker] 身份确认：候选 " + candidateId + " 的 "
                        + dropped + " 笔挂起收益非本人，已丢弃");
            }
            // 锁外：补写 JSONL + 镜像，避免在 IncomeTracker 锁内触发 UI 回调链
            foreach (var r in promoted) { EnqueueJsonl(r); MirrorToGameEvents(r); }
        }

        /// <summary>候选被推翻：挂起条目整桶丢弃（设计 §3.1 原则 3）。</summary>
        public static void DiscardPending()
        {
            lock (_lock) { _pending.Clear(); }
        }

        public static IncomeSummary GetSummary()
        {
            lock (_lock)
            {
                var s = new IncomeSummary();
                s.ConfirmedTotal = SumLocked(_confirmed);
                s.SelfTotal = s.ConfirmedTotal; // 确认入口只有「是我」一条路径，二者恒等（§8.3.1）
                s.SilverPickupTotal = SumSourceLocked(_confirmed, IncomeSource.SilverPickup);
                s.PartySilverTotal = SumSourceLocked(_confirmed, IncomeSource.PartySilver);
                s.GatheringSaleTotal = SumSourceLocked(_confirmed, IncomeSource.GatheringSale);
                s.ConfirmedCount = _confirmed.Count;
                s.UnconfirmedRecords = _pending.Count;
                s.AmountUnconfirmedCount = CountAmountUnconfirmedLocked(_confirmed);
                s.PendingSilver = SumLocked(_pending);
                s.RecordCount = _confirmed.Count + _pending.Count;
                return s;
            }
        }

        /// <summary>快照：新在前，已确认与挂起条目按时间混排，最多 max 条。</summary>
        public static IncomeRecord[] Snapshot(int max)
        {
            if (max <= 0) return new IncomeRecord[0];
            lock (_lock)
            {
                var all = new List<IncomeRecord>(_confirmed.Count + _pending.Count);
                all.AddRange(_confirmed);
                all.AddRange(_pending);
                all.Sort(delegate (IncomeRecord a, IncomeRecord b) { return b.TimeUtc.CompareTo(a.TimeUtc); });
                int n = Math.Min(max, all.Count);
                return all.GetRange(0, n).ToArray();
            }
        }

        public static void Reset()
        {
            lock (_lock) { _confirmed.Clear(); _pending.Clear(); }
        }

        // ---- CSV 导出（§8.3.4）：8 列固定顺序、UTF8 BOM、\r\n、Quote 复用 IncomeLedger ----

        /// <summary>导出全部明细（含挂起条目）为 CSV。成功返回 true；失败置 error 并返回 false。</summary>
        public static bool ExportCsv(string path, out string error)
        {
            error = "";
            if (string.IsNullOrEmpty(path)) { error = "导出路径为空"; return false; }
            try
            {
                var records = Snapshot(int.MaxValue); // 内存快照，不与写线程争文件（§8.3.3）
                var sb = new StringBuilder();
                sb.Append("时间,来源,明细,税前,税,实收,待确认,备注\r\n");
                for (int i = 0; i < records.Length; i++)
                {
                    var r = records[i];
                    sb.Append(r.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")).Append(',');
                    sb.Append(IncomeLedger.Quote(r.SourceLabel)).Append(',');
                    sb.Append(IncomeLedger.Quote(r.Detail)).Append(',');
                    sb.Append(r.SilverBeforeTax).Append(',');
                    sb.Append(r.TaxPaid).Append(',');
                    sb.Append(r.SilverAfterTax).Append(',');
                    sb.Append(r.Pending || r.AmountUnconfirmed ? "是" : "否").Append(',');
                    sb.Append(IncomeLedger.Quote(r.AttributionNote)).Append("\r\n");
                }
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ---- JSONL 持久化（§8.3.3）----

        /// <summary>
        /// 设置持久化目录（SettingsStore.DefaultDir()）。传 null 关闭（测试隔离用）。
        /// 写线程懒启动且常驻后台；入账即入队，由写线程追加到 income/income-YYYY-MM-DD.jsonl（本地日期切分）。
        /// </summary>
        public static void SetPersistDir(string? dir)
        {
            _persistDir = dir;
            if (dir != null && _writer == null)
            {
                lock (_writerGate)
                {
                    if (_writer == null)
                    {
                        _writer = new Thread(WriterLoop)
                        {
                            IsBackground = true,
                            Name = "income-jsonl-writer"
                        };
                        _writer.Start();
                    }
                }
            }
            _wake.Set();
        }

        /// <summary>启动时加载当天 + 前一天的 JSONL（§8.3.3：不全量扫历史）。直接入 _confirmed，不回写文件、不镜像事件流。</summary>
        public static void LoadRecent()
        {
            string? dir = _persistDir;
            if (dir == null) return;
            var loaded = new List<IncomeRecord>();
            for (int back = 1; back >= 0; back--) // 先昨天后今天，保持时间顺序
            {
                string path = Path.Combine(dir, "income", "income-" + DateTime.Now.AddDays(-back).ToString("yyyy-MM-dd") + ".jsonl");
                if (!File.Exists(path)) continue;
                try
                {
                    foreach (var line in File.ReadLines(path))
                    {
                        string t = line.Trim();
                        if (t.Length == 0) continue;
                        try
                        {
                            var r = JsonSerializer.Deserialize<IncomeRecord>(t, _jsonOpts);
                            if (r != null) loaded.Add(r);
                        }
                        catch { /* 单行损坏跳过，不拖垮整个文件 */ }
                    }
                }
                catch { /* 文件级异常：降级为不加载该天 */ }
            }
            if (loaded.Count == 0) return;
            lock (_lock)
            {
                foreach (var r in loaded) { r.Pending = false; _confirmed.Add(r); }
                if (_confirmed.Count > MaxConfirmed) _confirmed.RemoveRange(0, _confirmed.Count - MaxConfirmed);
            }
        }

        /// <summary>等待队列写穿（测试与优雅关闭用）：最多等 timeoutMs 毫秒。</summary>
        public static void FlushPendingWrites(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (Interlocked.Read(ref _written) < Interlocked.Read(ref _enqueued))
            {
                if (sw.ElapsedMilliseconds >= timeoutMs) return;
                _wake.Set();
                Thread.Sleep(20);
            }
        }

        /// <summary>写线程主体：独占文件、按本地日期切分；持久化关闭时丢弃队列内容（计数仍前进避免 Flush 卡死）。</summary>
        private static void WriterLoop()
        {
            string? currentDay = null;
            while (true)
            {
                try
                {
                    while (_jsonlQueue.TryDequeue(out string? line))
                    {
                        string? dir = _persistDir;
                        if (dir == null || line == null)
                        {
                            Interlocked.Increment(ref _written);
                            continue;
                        }
                        string day = DateTime.Now.ToString("yyyy-MM-dd");
                        string incomeDir = Path.Combine(dir, "income");
                        if (day != currentDay)
                        {
                            if (!Directory.Exists(incomeDir)) Directory.CreateDirectory(incomeDir);
                            currentDay = day;
                        }
                        File.AppendAllText(Path.Combine(incomeDir, "income-" + day + ".jsonl"), line + "\n", new UTF8Encoding(false));
                        Interlocked.Increment(ref _written);
                    }
                }
                catch { /* 单条写失败不中断写线程；该行已出队，计数在 finally 语义下可能滞后，Flush 有超时兜底 */ }
                _wake.WaitOne(500);
            }
        }

        private static void EnqueueJsonl(IncomeRecord r)
        {
            if (_persistDir == null) return;
            try
            {
                _jsonlQueue.Enqueue(JsonSerializer.Serialize(r, _jsonOpts));
                Interlocked.Increment(ref _enqueued);
                _wake.Set();
            }
            catch { /* 序列化失败不影响内存账 */ }
        }

        private static long SumLocked(List<IncomeRecord> list)
        {
            long sum = 0;
            for (int i = 0; i < list.Count; i++) sum += list[i].SilverAfterTax;
            return sum;
        }

        private static long SumSourceLocked(List<IncomeRecord> list, IncomeSource source)
        {
            long sum = 0;
            for (int i = 0; i < list.Count; i++) if (list[i].Source == source) sum += list[i].SilverAfterTax;
            return sum;
        }

        private static int CountAmountUnconfirmedLocked(List<IncomeRecord> list)
        {
            int n = 0;
            for (int i = 0; i < list.Count; i++) if (list[i].AmountUnconfirmed) n++;
            return n;
        }

        /// <summary>
        /// 事件流镜像：C5 起各 UI 大数字已直读 GetSummary()，此镜像仅保留给 GameEvents.AddIncome
        /// 事件流（总览合并流 / 战斗日志页）作数据源。挂起条目不镜像（未确认的钱不应出现在任何合计里）。
        /// </summary>
        private static void MirrorToGameEvents(IncomeRecord r)
        {
            GameEvents.AddIncome(new GameEvents.IncomeEntry
            {
                Time = r.TimeUtc.ToLocalTime(),
                Silver = r.SilverAfterTax,
                Source = string.IsNullOrEmpty(r.SourceLabel) ? "拾取银币" : r.SourceLabel
            });
        }
    }
}
