using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AlbionCompanion.Core;
using AlbionCompanion.Core.Income;

namespace AlbionCompanion.UI.Tabs
{
    /// <summary>
    /// 收益台账：实时显示银币收入（C7：来源细分 + 待确认标记 + CSV 导出，设计 §8.3.5）。
    /// 复核修复：口径分两行——「总收益（含历史，JSONL 恢复）」与「本次会话收益」不得混成同一个大数字；
    /// 待确认 / 未归属拾取 / 挂起溢出丢弃必须可见（全部读 IncomeTracker.GetSummary 唯一权威源，§9.7）。
    /// </summary>
    public class IncomeTab : TabBase
    {
        private readonly Label _summary;       // 第 1 行：总收益（含历史）+ 本次会话
        private readonly Label _detail;        // 第 2 行：来源细分 + 待确认 + 未归属 + 溢出丢弃
        private readonly Label _identityBadge; // R1/C3：身份三态徽标（候选含 TTL 剩余）
        private readonly Button _exportBtn;    // C7/§8.3.4：导出 CSV（列数保持 8 列不变）
        private readonly SimpleTable _table;
        private readonly Timer _refresh;

        public IncomeTab(Runtime runtime)
            : base(runtime)
        {
            Card(0, 0, 580, 82);
            CardText("收益统计（实时抓包）", 14, 8, 300, 20, Th.Text, 11f, true);
            _identityBadge = CardText("未识别", 300, 8, 266, 20, Th.Sub, 8.5f, true);
            _summary = CardText("总收益（含历史）：0 银币 · 本次会话：0 银币", 14, 30, 460, 20, Th.Accent, 10f, true);
            _detail = CardText("拾取 0 · 分成 0 · 出售 0", 14, 56, 550, 20, Th.Sub, 8.25f, false);

            _exportBtn = new Button();
            _exportBtn.Text = "导出 CSV";
            _exportBtn.Location = new Point(478, 29);
            _exportBtn.Size = new Size(88, 22);
            _exportBtn.FlatStyle = FlatStyle.Flat;
            _exportBtn.FlatAppearance.BorderColor = Th.Sub;
            _exportBtn.FlatAppearance.BorderSize = 1;
            _exportBtn.BackColor = Th.Card;
            _exportBtn.ForeColor = Th.Sub;
            _exportBtn.Font = Th.F(7.5f);
            _exportBtn.Cursor = Cursors.Hand;
            _exportBtn.FlatAppearance.MouseOverBackColor = Th.CardHi;
            _exportBtn.Click += delegate { ExportCsv(); };
            Controls.Add(_exportBtn);

            Card(0, 88, 580, 0);

            // C7/§8.3.5：列改「时间/来源/银币」+「待确认」标记列（挂起 / evt=243 金额待确认都会显示「是」）
            Heads(new string[] { "时间", "来源", "银币", "待确认" },
                  new float[] { 0.7f, 1.1f, 0.9f, 0.6f },
                  new bool[] { false, false, true, false },
                  0, 98, 580, 36);
            _table = Table(new float[] { 0.7f, 1.1f, 0.9f, 0.6f },
                           new bool[] { false, false, true, false },
                           0, 122, 580, 500);
            _table.EmptyText = "等待收益数据...";

            GameEvents.OnUpdate += delegate { try { if (IsHandleCreated) BeginInvoke(new MethodInvoker(Render)); } catch { } };

            _refresh = new Timer();
            _refresh.Interval = 1000;
            _refresh.Tick += delegate { Render(); };
            _refresh.Start();
            Render();
        }

        private void Render()
        {
            // P0-3：候选 10 分钟 TTL 自愈（UI 定时器兜底：即使没有新的 evt=62，超期候选也会回 Unknown）
            LocalPlayerIdentity.SweepCandidate(DateTime.UtcNow);

            // C7/§8.3.5：总收益与来源细分同源取自 GetSummary()（与总览卡片/悬浮条一致，§9.7）
            var s = IncomeTracker.GetSummary();
            // 复核修复：总收益（含 JSONL 历史）与「本次会话收益」分开展示，禁止合成同一个数
            _summary.Text = "总收益（含历史）：" + Th.Money(s.ConfirmedTotal)
                            + " · 本次会话：" + Th.Money(s.SessionTotal);

            var parts = new List<string>();
            parts.Add("拾取 " + Th.Money(s.SilverPickupTotal));
            parts.Add("分成 " + Th.Money(s.PartySilverTotal));
            parts.Add("出售 " + Th.Money(s.GatheringSaleTotal));
            if (s.UnconfirmedRecords > 0)
                parts.Add("待确认 " + s.UnconfirmedRecords + " 笔 / " + Th.Money(s.PendingSilver) + " 银币");
            if (s.AmountUnconfirmedCount > 0)
                parts.Add("金额待确认 " + s.AmountUnconfirmedCount + " 笔");
            if (s.DroppedPendingCount > 0)
                parts.Add("挂起溢出丢弃 " + s.DroppedPendingCount + " 笔"); // P2：不再静默
            int unattributed = LocalPlayerIdentity.UnattributedPickups;
            if (unattributed > 0)
                parts.Add("未归属拾取 " + unattributed + " 次"); // P0-3：候选期他人拾取（不入账）
            _detail.Text = string.Join(" · ", parts.ToArray());
            _detail.ForeColor = (s.DroppedPendingCount > 0 || unattributed > 0) ? Th.Accent : Th.Sub;

            // R1/C3：身份三态徽标（已确认=绿 / 待确认=琥珀 / 未识别=灰），只读、内部加锁，可在 UI 线程直接读
            var state = LocalPlayerIdentity.State;
            if (state == IdentityState.Confirmed)
            {
                string? name = LocalPlayerIdentity.ConfirmedName;
                _identityBadge.Text = string.IsNullOrEmpty(name)
                    ? "已确认（等待名字）"
                    : "已确认: " + name;
                _identityBadge.ForeColor = Th.Good;
            }
            else if (state == IdentityState.Candidate)
            {
                // P0-3：显示候选剩余有效期，让用户知道候选会自愈
                string remain = "";
                var since = LocalPlayerIdentity.CandidateSinceUtc;
                if (since != default(DateTime))
                {
                    double leftMin = (LocalPlayerIdentity.CandidateTtl - (DateTime.UtcNow - since)).TotalMinutes;
                    remain = " · 剩 " + Math.Max(0, (int)Math.Ceiling(leftMin)) + " 分";
                }
                _identityBadge.Text = "待确认：候选 " + LocalPlayerIdentity.CandidateSilverHits + " 次 / "
                                      + Th.Money(LocalPlayerIdentity.CandidateSilverTotal) + " 银币" + remain;
                _identityBadge.ForeColor = Th.Accent;
            }
            else
            {
                _identityBadge.Text = LocalPlayerIdentity.CandidateTtlExpiries > 0
                    ? "未识别（候选超时重置 " + LocalPlayerIdentity.CandidateTtlExpiries + " 次）"
                    : "未识别";
                _identityBadge.ForeColor = Th.Sub;
            }

            // C7/§8.3.5：明细改读 IncomeTracker.Snapshot（唯一权威源，含挂起条目），不再走 GameEvents 镜像；
            // pending / 金额待确认（evt=243）条目金额列显示「待确认」而非真实金额
            var rows = new List<string[]>();
            var list = IncomeTracker.Snapshot(200);
            for (int i = 0; i < list.Length; i++)
            {
                var r = list[i];
                bool unconfirmedRow = r.Pending || r.AmountUnconfirmed;
                rows.Add(new string[]
                {
                    r.TimeUtc.ToLocalTime().ToString("HH:mm:ss"),
                    string.IsNullOrEmpty(r.SourceLabel) ? "未知来源" : r.SourceLabel,
                    unconfirmedRow ? "待确认" : "+" + Th.Money(r.SilverAfterTax),
                    unconfirmedRow ? "是" : ""
                });
            }
            _table.SetRows(rows, new List<Image>());
        }

        /// <summary>C7/§8.3.4：导出 CSV（默认名 income-YYYYMMDD-HHMM.csv，成功提示条数与路径）。列数保持 8 列不变。</summary>
        private void ExportCsv()
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "CSV 文件|*.csv";
                dlg.FileName = "income-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".csv";
                if (dlg.ShowDialog() != DialogResult.OK) return;
                string error;
                if (IncomeTracker.ExportCsv(dlg.FileName, out error))
                {
                    int n = IncomeTracker.GetSummary().RecordCount;
                    MessageBox.Show("已导出 " + n + " 条收益明细：\r\n" + dlg.FileName,
                        "导出成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show("导出失败：" + error, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        public override void RefreshData() { Render(); }
    }
}
