using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AlbionCompanion.Core;
using AlbionCompanion.Core.Income;

// 收益 + 身份 模块回归（由 income 负责人填写）
// 断言入口：LogicTests.Check / LogicTests.Eq（internal static，同程序集可见）。
// 约定：每个用例自己 Reset + 关闭 JSONL 持久化，结束时把状态清回基线，避免污染后续模块。
static class IncomeModuleTests
{
    public static void Run()
    {
        RunCandidateTtlTests();          // P0-3：候选 10 分钟 TTL 自愈（队友先捡 → 锁死 → TTL 后自愈）
        RunUnattributedPickupTests();    // P0-3：候选期他人拾取计「未归属」，不改归属语义
        RunPendingCapTests();            // P2：pending 超 1000 丢最旧必须计数暴露，不静默
        RunSessionTotalTests();          // 口径：总收益（含历史）≠ 本次会话收益
        RunCsvExportTests();             // CSV 8 列固定不变
        RunIdentityAnchorRegressionTests(); // 复核修复（evt=278 名字锚定/换图重绑/角色切换）不回归

        // 收尾：测试隔离（后续模块 + 主工程都不该继承本模块的状态/持久化目录）
        IncomeTracker.SetPersistDir(null);
        IncomeTracker.Reset();
        LocalPlayerIdentity.Reset();
    }

    // ─────────────────────────────────────────────────────────────
    // P0-3（保守修复，不改归属语义）：候选加 10 分钟 TTL。
    // 缺陷原状：_candidateSince 只写不读；Unknown 下第一个 evt=62 拾取者被记为候选后，
    // 若那是队友（先捡），候选会永久锁死在队友 ObjectId 上，之后本人所有拾取都走
    // 「候选之外的拾取者 → Ignore」→ 本人收益永远不涨，且没有任何 UI 提示。
    // 修复口径：候选超过 10 分钟未被任何权威证据确认 → 回 Unknown（挂起丢弃），
    // 下一次拾取重新起候选；归属判定语义不变（候选期他人拾取仍 Ignore）。
    // ─────────────────────────────────────────────────────────────
    static void RunCandidateTtlTests()
    {
        IncomeTracker.SetPersistDir(null);
        IncomeTracker.Reset();
        LocalPlayerIdentity.Reset();

        const long teammateId = 91001;
        const long selfId = 91002;

        // ① 队友先捡 → 候选锁在队友 id 上，挂起但不入合计（与旧语义一致）
        var d1 = LocalPlayerIdentity.OnTakeSilver(teammateId, 150000);
        LogicTests.Eq("P0-3 TTL：队友先捡 → 挂起（候选）", d1, TakeSilverDisposition.Pending);
        LogicTests.Eq("P0-3 TTL：候选 ObjectId = 队友", LocalPlayerIdentity.CandidateObjectId, teammateId);
        LogicTests.Eq("P0-3 TTL：候选起始时刻已记录", LocalPlayerIdentity.CandidateSinceUtc != default(DateTime), true);
        IncomeTracker.AddPending(new IncomeRecord
        {
            TimeUtc = DateTime.UtcNow, Source = IncomeSource.SilverPickup, SourceLabel = "拾取银币",
            PickerId = teammateId, SilverAfterTax = 15
        });
        var since = LocalPlayerIdentity.CandidateSinceUtc;

        // ② 锁死期间本人捡：裁决仍 Ignore（明确不改归属语义），但必须计「未归属拾取」
        var d2 = LocalPlayerIdentity.OnTakeSilver(selfId, 200000);
        LogicTests.Eq("P0-3 TTL：候选锁死时本人拾取仍 Ignore（不改归属语义）", d2, TakeSilverDisposition.Ignore);
        LogicTests.Eq("P0-3 TTL：未归属拾取计数 = 1", LocalPlayerIdentity.UnattributedPickups, 1);
        LogicTests.Eq("P0-3 TTL：未归属的钱不入账", IncomeTracker.GetSummary().ConfirmedTotal, 0L);
        LogicTests.Eq("P0-3 TTL：候选仍是首个拾取者", LocalPlayerIdentity.CandidateObjectId, teammateId);

        // ③ 未超时（9 分钟）不得误重置，否则真实候选会被提前掐断
        LogicTests.Check("P0-3 TTL：9 分钟未超时不重置", !LocalPlayerIdentity.SweepCandidate(since.AddMinutes(9)), "9 分钟就重置了");
        LogicTests.Eq("P0-3 TTL：9 分钟仍是候选", LocalPlayerIdentity.State, IdentityState.Candidate);

        // ④ 正好 10 分钟 → 超时自愈：回 Unknown、清候选、丢挂起（挂起本就不在任何合计里）
        LogicTests.Check("P0-3 TTL：10 分钟整超时自愈", LocalPlayerIdentity.SweepCandidate(since.AddMinutes(10)), "10 分钟未重置");
        LogicTests.Eq("P0-3 TTL：超时后回 Unknown", LocalPlayerIdentity.State, IdentityState.Unknown);
        LogicTests.Check("P0-3 TTL：超时后候选 id 清空", LocalPlayerIdentity.CandidateObjectId == null,
              "候选 id=" + LocalPlayerIdentity.CandidateObjectId);
        LogicTests.Eq("P0-3 TTL：超时后挂起条数清空", IncomeTracker.GetSummary().UnconfirmedRecords, 0);
        LogicTests.Eq("P0-3 TTL：超时自愈次数 = 1", LocalPlayerIdentity.CandidateTtlExpiries, 1);
        LogicTests.Eq("P0-3 TTL：超时不会凭空产生收益", IncomeTracker.GetSummary().ConfirmedTotal, 0L);

        // ⑤ 自愈后本人再捡：重新起候选（这次是自己），随后 evt=278 正常确认、挂起转正
        var d3 = LocalPlayerIdentity.OnTakeSilver(selfId, 300000);
        LogicTests.Eq("P0-3 TTL：自愈后本人拾取 → Pending（重新起候选）", d3, TakeSilverDisposition.Pending);
        LogicTests.Eq("P0-3 TTL：新候选 = 本人 id", LocalPlayerIdentity.CandidateObjectId, selfId);
        IncomeTracker.AddPending(new IncomeRecord
        {
            TimeUtc = DateTime.UtcNow, Source = IncomeSource.SilverPickup, SourceLabel = "拾取银币",
            PickerId = selfId, SilverAfterTax = 30
        });
        LocalPlayerIdentity.OnSelfSilverGained(selfId, "TtlSelf");
        LogicTests.Eq("P0-3 TTL：自愈后 evt=278 正常确认本人", LocalPlayerIdentity.ConfirmedObjectId, selfId);
        LogicTests.Eq("P0-3 TTL：自愈后挂起收益转正 = 30", IncomeTracker.GetSummary().ConfirmedTotal, 30L);
        LogicTests.Eq("P0-3 TTL：自愈后挂起清零", IncomeTracker.GetSummary().UnconfirmedRecords, 0);

        // ⑥ TTL 不因候选本人继续拾取而续期（否则队友持续拾取 = 无限锁死）
        IncomeTracker.Reset();
        LocalPlayerIdentity.Reset();
        LocalPlayerIdentity.OnTakeSilver(teammateId, 100);
        var first = LocalPlayerIdentity.CandidateSinceUtc;
        LocalPlayerIdentity.OnTakeSilver(teammateId, 100);
        LogicTests.Eq("P0-3 TTL：候选本人再拾取不续期", LocalPlayerIdentity.CandidateSinceUtc, first);
        LogicTests.Check("P0-3 TTL：仍按首次拾取计时超时", LocalPlayerIdentity.SweepCandidate(first.AddMinutes(10)), "未超时");

        // ⑦ Completed/Unknown 下 Sweep 是 no-op（不得错误清掉已确认身份）
        LocalPlayerIdentity.OnJoinResponse(92001, "AfterTtl");
        LogicTests.Check("P0-3 TTL：Confirmed 态 Sweep 不动", !LocalPlayerIdentity.SweepCandidate(DateTime.UtcNow.AddDays(1)),
              "Confirmed 被误重置");
        LogicTests.Eq("P0-3 TTL：Confirmed 身份保持", LocalPlayerIdentity.State, IdentityState.Confirmed);
        LogicTests.Eq("P0-3 TTL：Confirmed id 保持", LocalPlayerIdentity.ConfirmedObjectId, 92001L);

        IncomeTracker.Reset();
        LocalPlayerIdentity.Reset();
    }

    // ─────────────────────────────────────────────────────────────
    // P0-3：「未归属拾取」必须可见（收益页/状态栏读 LocalPlayerIdentity.UnattributedPickups）。
    // 语义边界：只计数，不产生 pending / 不入账 / 不改变候选。
    // ─────────────────────────────────────────────────────────────
    static void RunUnattributedPickupTests()
    {
        IncomeTracker.SetPersistDir(null);
        IncomeTracker.Reset();
        LocalPlayerIdentity.Reset();

        LocalPlayerIdentity.OnTakeSilver(93001, 500); // 首个拾取 = 候选
        int before = LocalPlayerIdentity.UnattributedPickups;
        LogicTests.Eq("P0-3 未归属：初始计数 = 0", before, 0);

        for (int i = 0; i < 3; i++)
        {
            var d = LocalPlayerIdentity.OnTakeSilver(93002 + i, 200);
            LogicTests.Check("P0-3 未归属：他人拾取裁决 = Ignore #" + i, d == TakeSilverDisposition.Ignore, d.ToString());
        }
        LogicTests.Eq("P0-3 未归属：3 次他人拾取计数 = 3", LocalPlayerIdentity.UnattributedPickups, 3);
        LogicTests.Eq("P0-3 未归属：不产生挂起条目", IncomeTracker.GetSummary().UnconfirmedRecords, 0);
        LogicTests.Eq("P0-3 未归属：不产生收益", IncomeTracker.GetSummary().ConfirmedTotal, 0L);
        LogicTests.Eq("P0-3 未归属：候选不被他人拾取改掉", LocalPlayerIdentity.CandidateObjectId, 93001L);

        // 确认身份后走既有 Confirmed 语义，不再计入「候选期未归属」
        LocalPlayerIdentity.OnJoinResponse(93001, "UnattrSelf");
        LocalPlayerIdentity.OnTakeSilver(93999, 300);
        LogicTests.Eq("P0-3 未归属：确认后计数不增长", LocalPlayerIdentity.UnattributedPickups, 3);

        // Reset 清空（测试隔离 / 会话重置语义）
        LocalPlayerIdentity.Reset();
        LogicTests.Eq("P0-3 未归属：Reset 归零", LocalPlayerIdentity.UnattributedPickups, 0);
        LogicTests.Eq("P0-3 未归属：Reset 后 TTL 计数也归零", LocalPlayerIdentity.CandidateTtlExpiries, 0);
    }

    // ─────────────────────────────────────────────────────────────
    // P2：pending 超 1000 静默丢最旧 → 计数并通过 GetSummary().DroppedPendingCount 暴露。
    // 旧实现 RemoveRange(0, n) 完全静默：用户只看到「待确认没涨」却不知道最旧的被丢了。
    // ─────────────────────────────────────────────────────────────
    static void RunPendingCapTests()
    {
        IncomeTracker.SetPersistDir(null);
        IncomeTracker.Reset();
        LocalPlayerIdentity.Reset();

        LogicTests.Eq("P2 挂起上限：初始丢弃计数 = 0", IncomeTracker.DroppedPendingRecords, 0L);

        const int overflow = 7;
        const int total = 1000 + overflow;
        for (int i = 0; i < total; i++)
        {
            IncomeTracker.AddPending(new IncomeRecord
            {
                TimeUtc = DateTime.UtcNow.AddMilliseconds(i),
                Source = IncomeSource.SilverPickup,
                SourceLabel = "拾取银币",
                PickerId = i + 1, // 1..1007，最旧 1..7 应在溢出时被丢弃
                SilverAfterTax = 1
            });
        }

        var s = IncomeTracker.GetSummary();
        LogicTests.Eq("P2 挂起上限：pending 条数封顶 1000", s.UnconfirmedRecords, 1000);
        LogicTests.Eq("P2 挂起上限：丢弃计数 = 7（不再静默）", s.DroppedPendingCount, (long)overflow);
        LogicTests.Eq("P2 挂起上限：静态读口与 summary 一致", IncomeTracker.DroppedPendingRecords, (long)overflow);
        LogicTests.Eq("P2 挂起上限：保留的是最新 1000 条", s.PendingSilver, 1000L);

        var snap = IncomeTracker.Snapshot(5000);
        LogicTests.Eq("P2 挂起上限：快照 1000 条", snap.Length, 1000);
        LogicTests.Check("P2 挂起上限：最新一条仍在（1007）",
              snap.Length == 1000 && snap[0].PickerId == 1007L,
              snap.Length == 0 ? "空快照" : "最新=" + snap[0].PickerId);
        LogicTests.Check("P2 挂起上限：最旧 7 条已丢弃（1..7 不在快照）",
              !snap.Any(r => r.PickerId >= 1L && r.PickerId <= 7L),
              "最旧条目仍在");

        IncomeTracker.Reset();
        LogicTests.Eq("P2 挂起上限：Reset 后丢弃计数归零", IncomeTracker.DroppedPendingRecords, 0L);
    }

    // ─────────────────────────────────────────────────────────────
    // 收益页口径：总收益（含 JSONL 历史恢复）与「本次会话收益」必须是两个独立数字。
    // 实机 bug 背景：旧实现把历史恢复并进本次收益 → 启动即显示历史累计、时薪 = 历史/会话时长。
    // ─────────────────────────────────────────────────────────────
    static void RunSessionTotalTests()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ac-inc-sess-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "income")); // 写线程只在日期切换时建目录，测试先建好
        try
        {
            IncomeTracker.SetPersistDir(dir);
            IncomeTracker.Reset();
            IncomeTracker.AddConfirmed(new IncomeRecord
            {
                TimeUtc = DateTime.UtcNow, Source = IncomeSource.SilverPickup, SourceLabel = "拾取银币",
                PickerId = 1, SilverBeforeTax = 100, TaxPaid = 30, SilverAfterTax = 70
            });
            IncomeTracker.FlushPendingWrites(5000);

            // 模拟重启：历史恢复（只有总数变化，本次会话仍是 0）
            IncomeTracker.Reset();
            IncomeTracker.LoadRecent();
            var hist = IncomeTracker.GetSummary();
            LogicTests.Eq("口径：历史恢复计入总收益（含历史）= 70", hist.ConfirmedTotal, 70L);
            LogicTests.Eq("口径：历史恢复不计入本次会话收益", hist.SessionTotal, 0L);
            LogicTests.Eq("口径：历史恢复不计入本次条数", hist.SessionCount, 0);

            // 本次会话入账
            IncomeTracker.AddConfirmed(new IncomeRecord
            {
                TimeUtc = DateTime.UtcNow, Source = IncomeSource.SilverPickup, SourceLabel = "拾取银币",
                PickerId = 1, SilverBeforeTax = 50, TaxPaid = 20, SilverAfterTax = 30
            });
            var live = IncomeTracker.GetSummary();
            LogicTests.Eq("口径：总收益 = 历史 + 本次 = 100", live.ConfirmedTotal, 100L);
            LogicTests.Eq("口径：本次会话收益只算会话内 = 30", live.SessionTotal, 30L);
            LogicTests.Eq("口径：本次会话条数只算会话内 = 1", live.SessionCount, 1);
            LogicTests.Eq("口径：来源小计不混入历史口径（拾取 = 100）", live.SilverPickupTotal, 100L);
        }
        finally
        {
            IncomeTracker.SetPersistDir(null);
            IncomeTracker.Reset();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // CSV 导出（§8.3.4）：列数固定 8 列，待确认条目（pending / 金额待确认）必须落盘为「是」。
    // 本模块新增的「未归属拾取 / 溢出丢弃」只是计数，不得改变 CSV 列结构。
    // ─────────────────────────────────────────────────────────────
    static void RunCsvExportTests()
    {
        IncomeTracker.SetPersistDir(null);
        IncomeTracker.Reset();
        LocalPlayerIdentity.Reset();

        IncomeTracker.AddConfirmed(new IncomeRecord
        {
            TimeUtc = DateTime.UtcNow, Source = IncomeSource.SilverPickup, SourceLabel = "拾取银币",
            PickerId = 1, SilverBeforeTax = 100, TaxPaid = 10, SilverAfterTax = 90, AttributionNote = "p8=12"
        });
        IncomeTracker.AddConfirmed(new IncomeRecord
        {
            TimeUtc = DateTime.UtcNow, Source = IncomeSource.PartySilver, SourceLabel = "队伍分成",
            AmountUnconfirmed = true, AttributionNote = "金额字段未确认"
        });
        IncomeTracker.AddPending(new IncomeRecord
        {
            TimeUtc = DateTime.UtcNow, Source = IncomeSource.SilverPickup, SourceLabel = "拾取银币",
            PickerId = 9, SilverAfterTax = 55
        });

        string path = Path.Combine(Path.GetTempPath(), "ac-inc-csv-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".csv");
        try
        {
            string err;
            LogicTests.Check("CSV：导出成功", IncomeTracker.ExportCsv(path, out err), err);
            byte[] bytes = File.ReadAllBytes(path);
            LogicTests.Check("CSV：带 UTF8 BOM", bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "无 BOM");

            string text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            string[] rows = text.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            LogicTests.Eq("CSV：行数 = 表头 + 3 条（含挂起）", rows.Length, 4);
            LogicTests.Eq("CSV：表头仍为固定 8 列", rows[0], "时间,来源,明细,税前,税,实收,待确认,备注");
            LogicTests.Check("CSV：每条记录都是 8 列（未归属/溢出计数不改变列结构）",
                  rows.All(r => r.Split(',').Length == 8),
                  "列数异常行：" + string.Join(" | ", rows.Select(r => r.Split(',').Length.ToString()).ToArray()));
            LogicTests.Check("CSV：挂起条目待确认列 = 是", rows.Any(r => r.Contains("是")), "未找到待确认行");
            LogicTests.Eq("CSV：挂起条目仍不计入已确认合计", IncomeTracker.GetSummary().ConfirmedTotal, 90L);
            LogicTests.Eq("CSV：挂起条目在快照中可见", IncomeTracker.GetSummary().UnconfirmedRecords, 1);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            IncomeTracker.Reset();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 复核修复（HEAD 已含、不得回退）的收益/身份锚定回归：
    // evt=278 按角色名锚定本人；换图换 ObjectId 同名重绑；换角色重绑且本次收益不清零。
    // ─────────────────────────────────────────────────────────────
    static void RunIdentityAnchorRegressionTests()
    {
        IncomeTracker.SetPersistDir(null);
        IncomeTracker.Reset();
        LocalPlayerIdentity.Reset();

        LocalPlayerIdentity.SetManualName("IncomeSelf");
        LocalPlayerIdentity.OnSelfSilverGained(88001, "IncomeSelf");
        LogicTests.Eq("回归：evt=278 手选名命中 → 确认本人", LocalPlayerIdentity.ConfirmedObjectId, 88001L);
        LogicTests.Eq("回归：确认来源 = SilverGained", LocalPlayerIdentity.CurrentSource, ConfirmSource.SilverGained);

        // 换图/换实例：名字不变、ObjectId 变 → 重新绑定（不清收益）
        LocalPlayerIdentity.OnSelfSilverGained(88002, "IncomeSelf");
        LogicTests.Eq("回归：同名换图 → 重绑到新 ObjectId", LocalPlayerIdentity.ConfirmedObjectId, 88002L);
        LogicTests.Check("回归：新 id 算本人", LocalPlayerIdentity.IsSelf(88002L), "false");
        LogicTests.Check("回归：旧 id 不再算本人", !LocalPlayerIdentity.IsSelf(88001L), "true");

        // 换角色：已确认态出现不同角色名 → 重绑（本次会话收益保留）
        IncomeTracker.AddConfirmed(new IncomeRecord
        {
            TimeUtc = DateTime.UtcNow, Source = IncomeSource.SilverPickup, SourceLabel = "拾取银币",
            PickerId = 88002, SilverBeforeTax = 88, TaxPaid = 0, SilverAfterTax = 88
        });
        LocalPlayerIdentity.OnSelfSilverGained(88003, "IncomeSelf2");
        LogicTests.Eq("回归：角色切换 → 重绑到新角色 id", LocalPlayerIdentity.ConfirmedObjectId, 88003L);
        LogicTests.Eq("回归：角色切换 → 名字更新", LocalPlayerIdentity.ConfirmedName, "IncomeSelf2");
        LogicTests.Eq("回归：角色切换不清本次会话收益", IncomeTracker.GetSummary().SessionTotal, 88L);
        LogicTests.Eq("回归：角色切换保留总收益", IncomeTracker.GetSummary().ConfirmedTotal, 88L);

        IncomeTracker.Reset();
        LocalPlayerIdentity.Reset();
    }
}
