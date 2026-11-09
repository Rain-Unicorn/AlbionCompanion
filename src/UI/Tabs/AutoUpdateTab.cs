using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using AlbionCompanion.Core;

namespace AlbionCompanion.UI.Tabs
{
    /// <summary>自动更新：打开即扫 AODP 收集物品 ID，分批拉行情入库，每轮后 5 分钟自动下一轮。</summary>
    public class AutoUpdateTab : TabBase
    {
        private readonly Label _stats;
        private readonly Label _phase;
        private readonly Label _progress;
        private readonly ListBox _log;
        private readonly Panel _bar;
        private readonly Label _barLabel;
        private readonly Button _toggleBtn;
        private bool _running;
        private Thread _worker;
        private int _phaseProgress; // 0-100
        private int _phaseTotal;
        /// <summary>取消源：用户点「停止」时中断在途请求（MarketDataService 已接调用方 ct）。</summary>
        private CancellationTokenSource _cts = new CancellationTokenSource();

        public AutoUpdateTab(Runtime runtime) : base(runtime)
        {
            Card(0, 0, 580, 70);
            CardText("自动行情更新", 14, 10, 300, 22, Th.Text, 12f, true);
            _stats = CardText("本地库统计", 14, 36, 550, 18, Th.Sub, 9f, false);

            // 阶段 + 进度条
            Card(0, 80, 580, 90);
            _phase = CardText("启动中...", 14, 90, 550, 20, Th.Accent, 10.5f, true);
            _bar = new Panel();
            _bar.Location = new Point(14, 116);
            _bar.Size = new Size(552, 14);
            _bar.BackColor = Th.Card;
            Controls.Add(_bar);
            _bar.Paint += delegate(object s, PaintEventArgs e)
            {
                float pct = _phaseTotal > 0 ? (float)_phaseProgress / _phaseTotal : 0;
                if (pct < 0) pct = 0; if (pct > 1) pct = 1;
                int w = (int)(_bar.Width * pct);
                if (w > 0)
                {
                    using (var b = new SolidBrush(Th.Accent))
                        e.Graphics.FillRectangle(b, 0, 0, w, _bar.Height);
                }
                using (var p = new Pen(Th.Line))
                    e.Graphics.DrawRectangle(p, 0, 0, _bar.Width - 1, _bar.Height - 1);
            };
            _barLabel = new Label();
            _barLabel.Location = new Point(0, 0);
            _barLabel.Size = new Size(552, 14);
            _barLabel.BackColor = Color.Transparent;
            _barLabel.ForeColor = Th.Text;
            _barLabel.Font = new Font("Microsoft YaHei", 7.5f);
            _barLabel.TextAlign = ContentAlignment.MiddleCenter;
            _bar.Controls.Add(_barLabel);

            // 详细日志区（累积滚动）
            Card(0, 180, 580, 200);
            CardText("详细日志", 14, 188, 200, 18, Th.Text, 9.5f, true);
            _progress = CardText("等待开始...", 14, 210, 550, 20, Th.Sub, 9f, false);
            _log = new ListBox();
            _log.Location = new Point(14, 232);
            _log.Size = new Size(552, 140);
            _log.BackColor = Th.Card;
            _log.ForeColor = Th.Text;
            _log.BorderStyle = BorderStyle.FixedSingle;
            _log.Font = new Font("Consolas", 8.5f);
            _log.IntegralHeight = false;
            Controls.Add(_log);

            _toggleBtn = Ui.Primary("停止", 14, 390, 120, 28);
            _toggleBtn.Click += delegate { Toggle(); };
            Controls.Add(_toggleBtn);

            UpdateStats();
            // 手动启动：点击按钮才开始
        }

        private void UpdateStats()
        {
            var db = Rt.Db;
            var citySet = new HashSet<string>();
            foreach (var r in db.Rows) citySet.Add(r.City);
            _stats.Text = "本地库：" + db.Rows.Count + " 行 · " + db.KnownItemIds().Count + " 种物品 · " + citySet.Count + " 城市";
        }

        private void Toggle()
        {
            if (_running) Stop(); else Start();
        }

        private void Start()
        {
            if (_running) return;
            // 每次启动都换一个新的取消源：Stop 会取消旧源，复用旧源会让新一轮第一批判定为已取消
            if (_cts.IsCancellationRequested) _cts = new CancellationTokenSource();
            _running = true;
            if (!IsDisposed && IsHandleCreated)
                BeginInvoke(new MethodInvoker(delegate {
                    _toggleBtn.Text = "停止";
                    _toggleBtn.BackColor = Th.Bad;
                }));
            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Start();
        }

        private void Stop()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            _running = false;
            if (!IsDisposed && IsHandleCreated)
                BeginInvoke(new MethodInvoker(delegate {
                    _toggleBtn.Text = "开始";
                    _toggleBtn.BackColor = Th.Accent;
                    _phase.Text = "已停止";
                }));
        }

        private void SetPhase(string text, int done, int total)
        {
            _phaseProgress = done;
            _phaseTotal = total;
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new MethodInvoker(delegate
            {
                _phase.Text = text;
                _barLabel.Text = total > 0 ? done + " / " + total : "";
                _bar.Invalidate();
            }));
        }

        private void SetProgress(string text)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new MethodInvoker(delegate { _progress.Text = text; }));
        }

        private void SetDetail(string text)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new MethodInvoker(delegate {
                string line = DateTime.Now.ToString("HH:mm:ss") + " " + text;
                _log.Items.Add(line);
                if (_log.Items.Count > 200) _log.Items.RemoveAt(0);
                _log.TopIndex = _log.Items.Count - 1;
            }));
        }

        private void WorkerLoop()
        {
            string cachePath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AlbionCompanion", "itemnames.json");
            var nameCache = new NameCache();
            try { nameCache = NameCache.Load(cachePath); } catch { }

            // 物品列表单独保存，只在首次扫描
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string itemListPath = System.IO.Path.Combine(appData, "AlbionCompanion", "itemlist.txt");
            var allIds = new HashSet<string>();
            try
            {
                if (System.IO.File.Exists(itemListPath))
                {
                    foreach (string line in System.IO.File.ReadAllLines(itemListPath))
                    {
                        if (line.Trim().Length > 0) allIds.Add(line.Trim());
                    }
                }
            }
            catch { }

            try
            {
                while (_running)
                {
                    SetDetail("");
                    SetProgress("准备关键词...");
                    SetPhase("扫描物品列表", 0, 1);

                    // 收集物品列表（仅首次，后续轮次跳过）
                    bool needScan = (allIds.Count < 50); // 物品列表文件为空才重扫

                    if (needScan)
                    {
                        // M3：物品列表来自本地 ao-bin-dumps items.json 索引（离线、无 HTTP 扫描），
                        // 不再对 albion-online-data.com/items 逐关键词抓 HTML（未声明接口随时会断）。
                        if (ItemIndex.Default == null)
                        {
                            // HANDOFF 已知问题 6 合并处理：数据缺失显式告警，绝不静默跳过
                            SetProgress("物品数据未加载：缺少 data/items.json，请在市场页点击『更新物品数据』");
                            SetPhase("物品数据缺失", 0, 1);
                            Thread.Sleep(3000);
                            continue;
                        }
                        SetDetail("从本地物品索引读取 " + ItemIndex.Default.Count + " 条…");
                        foreach (string baseId in ItemIndex.Default.TieredBaseIds())
                        {
                            if (!allIds.Contains(baseId)) allIds.Add(baseId);
                            string name = ItemIndex.Default.DisplayNameOf(baseId);
                            if (!string.IsNullOrEmpty(name) && !nameCache.Map.ContainsKey(baseId))
                                nameCache.Map[baseId] = name;
                        }
                        nameCache.Save(cachePath);
                        System.IO.File.WriteAllLines(itemListPath, new List<string>(allIds));
                        SetDetail("物品库载入 " + allIds.Count + " 个（本地索引，离线完成）");
                    }
                    else
                    {
                        SetDetail("物品库已有 " + allIds.Count + " 个，跳过扫描");
                    }

                    if (!_running) break;

                    // 批量拉行情——**唯一合法出口**：MarketDataService（进程级 60rpm 限流 + 内存缓存 + 单飞去重）。
                    // 旧写法绕开服务层直接打 AODP：约 6.3k 物品 × 6 城全量扫，每批还要睡 3 秒，
                    // 既绕过限流器（→ 429），也让用户只看到一个不动的小时级队列。
                    // 现在①按 URL ≤3000 字符切批（不只是按条数）②每批上报进度/预计剩余③429 交给限流器退避，
                    // 不再在这里手搓 30/60/90 秒睡眠（那会和限流器算两遍，越等越慢）。
                    var cities = new string[] { "Caerleon", "Bridgewatch", "Martlock", "Thetford", "Fort Sterling", "Lymhurst" };
                    var staleItems = Rt.Db.GetStaleItems(new List<string>(allIds));
                    int skipped = allIds.Count - staleItems.Count;
                    SetDetail("过期物品 " + staleItems.Count + " 个（2 小时未拉），跳过近期已拉 " + skipped + " 个");

                    var chunks = PriceBatcher.CreatePriceBatches(staleItems, cities);
                    int upserted = 0;
                    int failedBatches = 0;
                    DateTime marketStart = DateTime.Now;
                    SetPhase("拉取行情", 0, Math.Max(1, chunks.Count));

                    for (int i = 0; i < chunks.Count && _running; i++)
                    {
                        DateTime t0 = DateTime.Now;
                        try
                        {
                            var quotes = MarketDataService.Instance
                                .GetPricesAsync(chunks[i], cities, MarketQueryPurpose.Valuation, null, _cts.Token)
                                .GetAwaiter().GetResult();
                            Rt.Db.MarkFetched(chunks[i]);
                            upserted += quotes.Count;
                            Rt.Db.UpsertBatch(quotes);

                            int pct = (int)((double)(i + 1) / Math.Max(1, chunks.Count) * 100);
                            int elapsed = (int)(DateTime.Now - t0).TotalMilliseconds;
                            int spendMs = (int)(DateTime.Now - marketStart).TotalMilliseconds;
                            // 预计剩余：按已完成批次的平均耗时外推（不是负数，也不假装精确）
                            int etaSec = 0;
                            if (i + 1 >= 2)
                                etaSec = (int)((double)spendMs / (i + 1) * (chunks.Count - i - 1) / 1000);
                            string eta = etaSec > 0 ? " · 预计剩余 " + etaSec + "s" : "";
                            SetPhase("拉取行情 " + (i + 1) + "/" + chunks.Count, i + 1, Math.Max(1, chunks.Count));
                            SetProgress("批次 " + (i + 1) + "/" + chunks.Count + "（" + pct + "%）· "
                                        + chunks[i].Count + " 物品 → " + quotes.Count + " 条 · " + elapsed + "ms"
                                        + eta + " · 累计 " + upserted);
                            // 不再手工 Sleep：限流节拍由 MarketRateLimiter 统一掌握（这里多睡就是双倍等待）
                        }
                        catch (OperationCanceledException)
                        {
                            SetDetail("已停止：第 " + (i + 1) + " 批被取消");
                            break;
                        }
                        catch (MarketRateLimitedException ex)
                        {
                            // 限流器已按 RateLimit-Reset / 指数退避重试过 3 次，仍被限流就停本轮（不裸重试）
                            failedBatches++;
                            SetDetail("第 " + (i + 1) + " 批仍被限流：" + ex.Message);
                            break;
                        }
                        catch (Exception ex)
                        {
                            failedBatches++;
                            SetDetail("第 " + (i + 1) + " 批失败：" + ex.Message);
                        }
                        if (!_running) break;
                    }

                    int marketMs = (int)(DateTime.Now - marketStart).TotalMilliseconds;
                    SetDetail("行情拉取完成：" + chunks.Count + " 批 / " + marketMs + "ms / " + upserted
                              + " 条 / 失败 " + failedBatches + " 批 / 缓存 " + MarketDataService.Instance.CachedEntryCount + " 条");

                    if (!_running) break;

                    SetPhase("本轮完成，等待下一轮", 1, 1);
                    SetProgress("本轮完成：收集 " + allIds.Count + " 种物品，回源 " + upserted + " 条行情");
                    SetDetail("5 分钟后自动开始下一轮...");
                    UpdateStats();

                    // 等 5 分钟，每秒更新倒计时
                    for (int s = 300; s > 0 && _running; s--)
                    {
                        SetPhase("等待下一轮（" + s + "s）", 300 - s, 300);
                        Thread.Sleep(1000);
                    }
                }
            }
            catch (Exception ex)
            {
                SetProgress("出错：" + ex.Message);
            }
            finally
            {
                _running = false;
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        _toggleBtn.Text = "开始";
                        _toggleBtn.BackColor = Th.Accent;
                        UpdateStats();
                    }));
            }
        }

        public override void RefreshData()
        {
            UpdateStats();
        }
    }
}
