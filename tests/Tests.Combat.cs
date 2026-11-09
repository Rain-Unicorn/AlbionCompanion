using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AlbionCompanion.Core;
using AlbionCompanion.Core.DamageMeter;
using static LogicTests; // 断言入口：Check / Eq（同程序集 internal static）

// 战斗 模块回归（combat 负责人）
// 断言入口：LogicTests.Check / LogicTests.Eq（internal static，同程序集可见）。
// 约定：每个用例自己 Reset / Clear，结束时把共享状态清回基线（本模块运行在 Income 之后、Gathering 之前）。
static class CombatModuleTests
{
    public static void Run()
    {
        RunSelfTargetNameTests();             // 1) 本人目标显示「我（识别中）/真名」，不再叫「未知玩家」
        RunUnknownAttributionWindowTests();   // 2) 窗口结算：同帧多角色不错挂，唯一候选才合并
        RunUnknownAttributionGlueTests();     // 2) 窗口结算 → DamageTracker(-1) 真实合并/保持未知
        RunPlayerEntryImmutableTests();       // 3) PlayerEntry immutable + 整体替换（含并发读）
        RunCombatFilterTests();               // 5) PVE/PVP 高亮/筛选跟随（数据侧口径）
        RunCombatResetTests();                // 5) 「清空」只清战斗三处，收益/采集不受影响
        RunUnknownRowConsistencyTests();      // 5) unknown 行与 self 行不自洽（不混进 self、不被队伍过滤吞掉）

        // 收尾：测试隔离（后续模块 + 主工程都不该继承本模块的全局状态）
        GameEvents.DamageTracker.Clear();
        ChatSniffer.ResetUnknownAttributionForTests();
        LocalPlayerIdentity.Reset();
    }

    // ─────────────────────────────────────────────────────────────
    // 1) TargetNameResolver：isSelf 目标名 = LocalPlayerIdentity.DisplaySelfName。
    // 缺陷原状：本地玩家目标未解析时也返回「未知玩家」，与「路人不明」混为一谈；
    // 确认身份后仍显示「未知玩家」（用户看不到自己的目标是谁）。
    // 注意：非本人未解析目标仍必须是「未知玩家」（C8 既有语义不回归）。
    // ─────────────────────────────────────────────────────────────
    static void RunSelfTargetNameTests()
    {
        LocalPlayerIdentity.Reset();
        var objectNames = new Dictionary<long, string>();
        var mobNames = new Dictionary<long, string>();
        var playerNames = new Dictionary<long, string>();

        // 未确认身份：本人 → 「我（识别中）」，不是「未知玩家」，且不含裸 ObjectId
        var selfUnknown = TargetNameResolver.Resolve(4242, true, objectNames, mobNames, playerNames);
        Eq("战斗: 本人目标未解析 → 我（识别中）", selfUnknown.DisplayName, "我（识别中）");
        Check("战斗: 本人目标不再显示「未知玩家」", selfUnknown.DisplayName != "未知玩家", selfUnknown.DisplayName);
        Eq("战斗: 本人目标类型 = Player", selfUnknown.Kind, TargetKind.Player);
        Check("战斗: 本人目标仍需挂 pending 补名", selfUnknown.NeedsPendingLookup, "NeedsPendingLookup=false");
        Check("战斗: 本人目标文案不含 ObjectId", !selfUnknown.DisplayName.Contains("4242"), selfUnknown.DisplayName);
        Eq("战斗: 本人显示名与身份状态机同源", selfUnknown.DisplayName, LocalPlayerIdentity.DisplaySelfName);

        // 确认身份后：本人目标显示确认名
        LocalPlayerIdentity.OnJoinResponse(4242, "测试角色甲");
        var selfConfirmed = TargetNameResolver.Resolve(4242, true, objectNames, mobNames, playerNames);
        Eq("战斗: 本人目标确认后 → 真实角色名", selfConfirmed.DisplayName, "测试角色甲");
        Check("战斗: 确认名与「未知玩家」不冲突", selfConfirmed.DisplayName != "未知玩家", selfConfirmed.DisplayName);

        // 名字字典命中优先于身份展示名：目标名已知时用真实名字（不回归）
        objectNames[4242] = "字典名乙";
        var selfKnown = TargetNameResolver.Resolve(4242, true, objectNames, mobNames, playerNames);
        Eq("战斗: 本人目标名字字典命中 → 字典真名", selfKnown.DisplayName, "字典名乙");

        // 非本人未解析玩家：仍是「未知玩家」（C8 既有语义）
        playerNames[5001] = "";
        var otherUnknown = TargetNameResolver.Resolve(5001, false, objectNames, mobNames, playerNames);
        Eq("战斗: 非本人未解析玩家 → 未知玩家（不回归）", otherUnknown.DisplayName, "未知玩家");

        LocalPlayerIdentity.Reset();
    }

    // ─────────────────────────────────────────────────────────────
    // 2a) UnknownAttributionWindow 纯状态机：
    //     收集期只登记，窗口到期才判定「唯一候选」。
    //     旧实现逐事件判定 → 同帧两条 evt=29 时第一个路人吞掉整桶 -1（错挂）。
    // ─────────────────────────────────────────────────────────────
    static void RunUnknownAttributionWindowTests()
    {
        var t0 = DateTime.UtcNow;

        // A. 唯一候选是唯一次优：10 秒窗口到期，合并给该候选
        {
            var w = new UnknownAttributionWindow();
            Check("战斗: 首次 -1 命中不产生结算", !w.RecordUnknownHit(t0).HasValue, "不应结算");
            Check("战斗: 首个新角色登记不产生结算", !w.RecordNewCharacter(6101, "路人甲", t0.AddSeconds(1)).HasValue,
                "收集期不应结算");
            Eq("战斗: 收集期候选数 = 1", w.PendingCandidateCount, 1);
            var s = w.Tick(t0.AddSeconds(11)); // 窗口到期
            Check("战斗: 窗口到期产生结算", s.HasValue, "无结算");
            Check("战斗: 唯一候选 → 允许合并", s.HasValue && s.Value.Merged, "应合并");
            Eq("战斗: 合并候选 id", s.HasValue ? s.Value.CandidateId : 0, 6101L);
            Eq("战斗: 合并候选名", s.HasValue ? s.Value.CandidateName : null, "路人甲");
            Eq("战斗: 合并计数 = 1", w.MergedWindowCount, 1);
            Eq("战斗: 合并后放弃计数 = 0", w.GaveUpWindowCount, 0);
            Eq("战斗: 结算后窗口关闭", w.IsWindowOpen, false);
        }

        // B. 同帧多条 evt=29（本 bug 的核心场景）：两个新角色 → 不合并，保持未知并计数
        {
            var w = new UnknownAttributionWindow();
            w.RecordUnknownHit(t0);
            w.RecordNewCharacter(6201, "路人甲", t0.AddSeconds(1));
            w.RecordNewCharacter(6202, "路人乙", t0.AddSeconds(2)); // 同帧第二条
            Eq("战斗: 同帧两候选都登记", w.PendingCandidateCount, 2);
            var s = w.Tick(t0.AddSeconds(11));
            Check("战斗: 多候选产生结算", s.HasValue, "无结算");
            Check("战斗: 多候选不得合并（不错挂给第一个）", s.HasValue && !s.Value.Merged, "不应合并");
            Eq("战斗: 多候选结算计数 = 2", s.HasValue ? s.Value.CandidateCount : 0, 2);
            Eq("战斗: 多候选合并计数 = 0", w.MergedWindowCount, 0);
            Eq("战斗: 多候选放弃计数 = 1", w.GaveUpWindowCount, 1);
            Eq("战斗: 最近放弃窗口候选数 = 2", w.LastGaveUpCandidateCount, 2);
        }

        // C. 「下一次超窗」触发结算：第二个新角色到达时先结算上一窗口
        {
            var w = new UnknownAttributionWindow();
            w.RecordUnknownHit(t0);
            w.RecordNewCharacter(6301, "路人甲", t0.AddSeconds(1));
            // 12 秒后下一个新角色出现：先结算旧窗口（唯一候选 → 合并），再把新角色放进新窗口
            var s = w.RecordNewCharacter(6302, "路人乙", t0.AddSeconds(12));
            Check("战斗: 下一次超窗时结算旧窗口", s.HasValue, "无结算");
            Check("战斗: 超窗结算唯一候选可合并", s.HasValue && s.Value.Merged, "应合并");
            Eq("战斗: 超窗结算候选 id", s.HasValue ? s.Value.CandidateId : 0, 6301L);
            Eq("战斗: 新窗口只含新角色", w.PendingCandidateCount, 1);
            Eq("战斗: 结算后仍无放弃计数", w.GaveUpWindowCount, 0);
        }

        // D. 条件①（-1 命中）不满足：先出现新角色、后才有 -1 命中 → 不归因（保守，不新造错挂路径）
        {
            var w = new UnknownAttributionWindow();
            w.RecordNewCharacter(6401, "路人甲", t0);                 // 窗口由候选开启
            w.RecordUnknownHit(t0.AddSeconds(2));                     // 命中在候选之后
            var s = w.Tick(t0.AddSeconds(11));
            Check("战斗: 命中在候选之后不归因", !(s.HasValue && s.Value.Merged), "不应合并");
            Eq("战斗: 该场景合并计数 = 0", w.MergedWindowCount, 0);
        }

        // E. 合并后清掉命中基准：同一桶 -1 不得被后续候选二次认领
        {
            var w = new UnknownAttributionWindow();
            w.RecordUnknownHit(t0);
            w.RecordNewCharacter(6501, "路人甲", t0.AddSeconds(1));
            var first = w.Tick(t0.AddSeconds(11));
            Check("战斗: 第一次合并成功", first.HasValue && first.Value.Merged, "应合并");
            // 窗口关闭后立刻出现的新角色：没有新的 -1 命中，不得再合并（同桶只认领一次）
            var next = w.RecordNewCharacter(6502, "路人乙", t0.AddSeconds(11.2));
            Check("战斗: 新窗口首个角色不产生结算", !next.HasValue, "不应结算");
            var noSecond = w.Tick(t0.AddSeconds(22));
            Check("战斗: 同一桶 -1 不被二次认领", !(noSecond.HasValue && noSecond.Value.Merged), "不应二次合并");
            Eq("战斗: 合并计数仍 = 1", w.MergedWindowCount, 1);
        }

        // F. 复位：计数与窗口状态归零（测试隔离入口）
        {
            var w = new UnknownAttributionWindow();
            w.RecordUnknownHit(t0);
            w.RecordNewCharacter(6601, "路人甲", t0.AddSeconds(1));
            w.Tick(t0.AddSeconds(11));
            w.Reset();
            Eq("战斗: Reset 清空合并计数", w.MergedWindowCount, 0);
            Eq("战斗: Reset 清空放弃计数", w.GaveUpWindowCount, 0);
            Eq("战斗: Reset 关闭窗口", w.IsWindowOpen, false);
            Eq("战斗: Reset 清空候选", w.PendingCandidateCount, 0);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 2b) ChatSniffer 窗口结算接线 → DamageTracker(-1) 的最终落点：
    //     多候选时 -1 保持未知；唯一候选时才把 -1 累计真正合并进去。
    // ─────────────────────────────────────────────────────────────
    static void RunUnknownAttributionGlueTests()
    {
        var tracker = GameEvents.DamageTracker;
        tracker.Clear();
        ChatSniffer.ResetUnknownAttributionForTests();
        var t0 = DateTime.UtcNow;

        // 多候选：整桶 -1 必须原样保留（旧实现会错挂给第一个新角色）
        tracker.RecordDamage(-1, "未知玩家", 100, 300, -1, true);
        ChatSniffer.NotifyUnknownPlayerHit(t0);
        ChatSniffer.TryAttributeUnknownPlayer(7101, "路人甲", false, t0.AddSeconds(1));
        ChatSniffer.TryAttributeUnknownPlayer(7102, "路人乙", false, t0.AddSeconds(2));
        ChatSniffer.TickUnknownAttribution(t0.AddSeconds(12));

        var all = tracker.GetAllPlayers();
        Check("战斗: 多候选时 -1 未知桶保留", all.Exists(p => p.Key == -1), "-1 被错挂合并了");
        Check("战斗: 多候选时 -1 伤害未减少", all.Find(p => p.Key == -1).TotalDamage == 300, "伤害被改");
        Check("战斗: 多候选未创建候选角色行", !all.Exists(p => p.Key == 7101 || p.Key == 7102), "错建行");
        Eq("战斗: 多候选放弃计数 = 1", ChatSniffer.UnknownAttributionGaveUpCountForTests, 1);
        Eq("战斗: 多候选合并计数 = 0", ChatSniffer.UnknownAttributionMergedCountForTests, 0);

        // 唯一候选：-1 的累计（含历史伤害）并入真实角色
        ChatSniffer.ResetUnknownAttributionForTests();
        tracker.Clear();
        var t1 = DateTime.UtcNow;
        tracker.RecordDamage(-1, "未知玩家", 100, 250, -1, true);
        ChatSniffer.NotifyUnknownPlayerHit(t1);
        ChatSniffer.TryAttributeUnknownPlayer(7201, "路人丙", false, t1.AddSeconds(1));
        ChatSniffer.TickUnknownAttribution(t1.AddSeconds(12));

        all = tracker.GetAllPlayers();
        Check("战斗: 唯一候选后 -1 已消失", !all.Exists(p => p.Key == -1), "-1 仍在");
        var mergedRow = all.Find(p => p.Key == 7201);
        Eq("战斗: -1 伤害整体并入唯一候选", mergedRow.TotalDamage, 250L);
        Eq("战斗: 并入后角色名 = 候选名", mergedRow.Name, "路人丙");
        Eq("战斗: 唯一候选合并计数 = 1", ChatSniffer.UnknownAttributionMergedCountForTests, 1);
        Eq("战斗: 唯一候选放弃计数 = 0", ChatSniffer.UnknownAttributionGaveUpCountForTests, 0);

        // 收尾
        tracker.Clear();
        ChatSniffer.ResetUnknownAttributionForTests();
    }

    // ─────────────────────────────────────────────────────────────
    // 3) PlayerEntry immutable + 整体替换：
    //     旧实现抓包线程原地写 LastSeen/Guild，UI 线程并发读会撕裂/不可见。
    //     现在字段只读，更新必须产生新实例并整条替换进 ConcurrentDictionary。
    // ─────────────────────────────────────────────────────────────
    static void RunPlayerEntryImmutableTests()
    {
        const long id = 88001;
        try
        {
            GameEvents.Players.TryRemove(id, out _);

            bool added = GameEvents.UpsertSeenPlayer(id, "邻居甲", "公会A");
            Check("战斗: 首次 Upsert 返回新增", added, "added=false");
            var first = GameEvents.Players[id];
            Eq("战斗: 新增条目 Name", first.Name, "邻居甲");
            Eq("战斗: 新增条目 Guild", first.Guild, "公会A");
            Check("战斗: 新增条目 FirstSeen 已填", first.FirstSeen != default(DateTime), "FirstSeen 未填");

            // 刷新 LastSeen：必须换新实例，旧实例字段不变（不可变语义）
            GameEvents.TouchPlayerLastSeen(id);
            var second = GameEvents.Players[id];
            Check("战斗: 刷新 LastSeen 产生新实例（整体替换）", !ReferenceEquals(first, second), "仍是同一实例");
            Check("战斗: 旧实例 LastSeen 不被原地改写", first.LastSeen == second.FirstSeen || first.LastSeen <= second.FirstSeen,
                "旧实例被改了");
            Eq("战斗: 刷新后 Name 保持", second.Name, "邻居甲");
            Eq("战斗: 刷新后 FirstSeen 保持", second.FirstSeen, first.FirstSeen);
            Check("战斗: 刷新后 LastSeen 前进/不变", second.LastSeen >= first.LastSeen, "LastSeen 倒退");

            // 再次 Upsert（Guild 为空 → 保留旧值；返回 false 表示不是首次）
            bool addedAgain = GameEvents.UpsertSeenPlayer(id, "邻居甲", "");
            Check("战斗: 重复 Upsert 返回非新增", !addedAgain, "added=true");
            var third = GameEvents.Players[id];
            Eq("战斗: 空 Guild 不覆盖旧公会名", third.Guild, "公会A");
            Check("战斗: 重复 Upsert 也是整体替换", !ReferenceEquals(second, third), "仍是同一实例");

            // 并发读：抓包线程反复整体替换、UI 线程并发读，条目必须始终是完整对象（不出现半更新）
            Exception? threadError = null;
            var reader = new Thread(delegate ()
            {
                try
                {
                    for (int i = 0; i < 500; i++)
                    {
                        if (GameEvents.Players.TryGetValue(id, out var p))
                        {
                            if (p.Name != "邻居甲" || p.Guild != "公会A" || p.ObjectId != id)
                                throw new InvalidOperationException("读到半更新条目");
                        }
                    }
                }
                catch (Exception ex) { threadError = ex; }
            });
            reader.IsBackground = true;
            reader.Start();
            for (int i = 0; i < 500; i++) GameEvents.TouchPlayerLastSeen(id);
            reader.Join();
            Check("战斗: 并发读替换中的条目无撕裂", threadError == null,
                threadError == null ? "" : threadError.Message);
            Eq("战斗: 并发后条目仍完整", GameEvents.Players[id].Name, "邻居甲");
        }
        finally
        {
            GameEvents.Players.TryRemove(id, out _);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 5a) PVE/PVP 筛选跟随（数据侧）：FilterMode 同时作用于榜单与 GetTotals 大数字，
    //     高亮按钮的 Active 跟随由 ToggleState 语义保证（LogicTests.RunToggleStateTests 已钉死）。
    // ─────────────────────────────────────────────────────────────
    static void RunCombatFilterTests()
    {
        var trk = new DamageStatsTracker();
        trk.RecordDamage(9001, "甲", 1, 100, -1, true);    // 甲 PvE 100
        trk.RecordDamage(9001, "甲", 2, 50, -1, false);    // 甲 PvP 50
        trk.RecordDamage(9002, "乙", 3, 30, -1, false);    // 乙 PvP 30

        trk.FilterMode = DamageStatsTracker.DamageFilter.All;
        var allRows = trk.GetTopDamage();
        Eq("战斗: 全部筛选行数 = 2", allRows.Count, 2);
        Eq("战斗: 全部筛选首位 = 甲", allRows[0].PlayerName, "甲");
        Eq("战斗: 全部筛选首位伤害 = 150", allRows[0].TotalDamage, 150L);
        Eq("战斗: 全部筛选大数字 = 180", trk.GetTotals(DamageStatsTracker.DamageFilter.All).TotalDamage, 180L);

        trk.FilterMode = DamageStatsTracker.DamageFilter.Pve;
        var pveRows = trk.GetTopDamage();
        Eq("战斗: PVE 筛选只剩有 PVE 伤害的角色", pveRows.Count, 1);
        Eq("战斗: PVE 筛选首位 = 甲", pveRows[0].PlayerName, "甲");
        Eq("战斗: PVE 筛选伤害 = 100", pveRows[0].TotalDamage, 100L);
        Eq("战斗: PVE 大数字与榜单同口径", trk.GetTotals(DamageStatsTracker.DamageFilter.Pve).TotalDamage, 100L);

        trk.FilterMode = DamageStatsTracker.DamageFilter.Pvp;
        var pvpRows = trk.GetTopDamage();
        Eq("战斗: PVP 筛选行数 = 2", pvpRows.Count, 2);
        Eq("战斗: PVP 筛选首位 = 甲（降序）", pvpRows[0].PlayerName, "甲");
        Eq("战斗: PVP 筛选甲伤害 = 50", pvpRows[0].TotalDamage, 50L);
        Eq("战斗: PVP 筛选乙伤害 = 30", pvpRows[1].TotalDamage, 30L);
        Eq("战斗: PVP 大数字 = 80", trk.GetTotals(DamageStatsTracker.DamageFilter.Pvp).TotalDamage, 80L);

        // DPS/峰值字段仍随玩家行返回（UI 排序按伤害、副标题展示 DPS，两者不混用同一数字）
        Check("战斗: 榜单按筛选后伤害降序", pvpRows[0].TotalDamage >= pvpRows[1].TotalDamage, "未降序");
        Check("战斗: 行内 DPS 字段可读", pvpRows[0].CurrentDps5s >= 0, "DPS 异常");
    }

    // ─────────────────────────────────────────────────────────────
    // 5b) 「清空」只清战斗三处：
    //     GameEvents.ResetAll = DamageTracker 全部玩家 + 战斗事件流 + 击杀/死亡计数器；
    //     收益/采集事件流不受影响（§8.5，战斗页清空不得清掉别的模块数据）。
    // ─────────────────────────────────────────────────────────────
    static void RunCombatResetTests()
    {
        var tracker = GameEvents.DamageTracker;
        tracker.Clear();
        int incomeBefore = GameEvents.CountIncome();
        int gatheringBefore = GameEvents.CountGathering();
        int killsBefore = GameEvents.KillCount;
        int deathsBefore = GameEvents.DeathCount;

        tracker.RecordDamage(9101, "甲", 1, 120, -1, true);
        GameEvents.AddCombat(new GameEvents.CombatEntry
        {
            Time = DateTime.Now, Target = "目标甲", Damage = 120, Type = "damage", CauserName = "甲"
        });
        GameEvents.AddCombat(new GameEvents.CombatEntry
        {
            Time = DateTime.Now, Target = "目标乙", Damage = 0, Type = "kill"
        });
        GameEvents.AddCombat(new GameEvents.CombatEntry
        {
            Time = DateTime.Now, Target = "自己", Damage = 0, Type = "death"
        });
        Eq("战斗: 清空前击杀计数 +1", GameEvents.KillCount, killsBefore + 1);
        Eq("战斗: 清空前死亡计数 +1", GameEvents.DeathCount, deathsBefore + 1);
        Check("战斗: 清空前 tracker 有数据", tracker.GetTopDamage().Count > 0, "无数据");

        GameEvents.ResetAll();

        Eq("战斗: 清空后战斗事件流 = 0", GameEvents.CountCombat(), 0);
        Eq("战斗: 清空后击杀计数 = 0", GameEvents.KillCount, 0);
        Eq("战斗: 清空后死亡计数 = 0", GameEvents.DeathCount, 0);
        Eq("战斗: 清空后伤害榜 = 0 行", tracker.GetTopDamage().Count, 0);
        var totals = tracker.GetTotals(DamageStatsTracker.DamageFilter.All);
        Eq("战斗: 清空后大数字伤害 = 0", totals.TotalDamage, 0L);
        Eq("战斗: 清空后大数字治疗 = 0", totals.TotalHealing, 0L);

        // 收益/采集事件流不受「清空战斗」影响
        Eq("战斗: 清空不影响收益事件流", GameEvents.CountIncome(), incomeBefore);
        Eq("战斗: 清空不影响采集事件流", GameEvents.CountGathering(), gatheringBefore);
    }

    // ─────────────────────────────────────────────────────────────
    // 5c) unknown 行与 self 行不自洽（修正 + 回归）：
    //     ① 负键未知行不得显示成 self（Key=0）的名字；
    //     ② 组队过滤时未知行不得被「非队友」规则一起吞掉（大数字含未知伤害、
    //        榜单却一行没有 → 不自洽）；CombatTab 的过滤条件改为 `Key < 0 || 队友`。
    // ─────────────────────────────────────────────────────────────
    static void RunUnknownRowConsistencyTests()
    {
        LocalPlayerIdentity.Reset();
        string selfName = LocalPlayerIdentity.DisplaySelfName;
        Eq("战斗: 未确认时 self 展示名 = 我（识别中）", selfName, "我（识别中）");

        var trk = new DamageStatsTracker();
        trk.RecordDamage(0, selfName, 1, 100, -1, true);          // self 100
        trk.RecordDamage(-1, "未知玩家", 2, 60, -1, true);       // 未知 60
        trk.RecordDamage(-3, "未知来源", 3, 20, -1, true);       // 未知来源 20
        trk.RecordDamage(9201, "队友甲", 4, 40, -1, true);       // 队友 40

        var rows = trk.GetTopDamage();
        var selfRow = rows.Find(r => r.Key == 0);
        var unknownRow = rows.Find(r => r.Key == -1);
        var unknownSourceRow = rows.Find(r => r.Key == -3);
        Check("战斗: self 行存在", selfRow.Key == 0 && selfRow.PlayerName.Length > 0, "缺 self 行");
        Check("战斗: unknown 行存在（Key=-1）", unknownRow.Key == -1, "缺 unknown 行");
        Check("战斗: unknown 行名字不是 self", unknownRow.PlayerName != selfRow.PlayerName, "unknown 混进 self");
        Eq("战斗: unknown 行名字 = 未知玩家", unknownRow.PlayerName, "未知玩家");
        Check("战斗: 未知来源行独立（Key=-3）", unknownSourceRow.Key == -3, "缺 -3 行");
        Check("战斗: 未知行不会冒充 self Key", unknownRow.Key != 0 && unknownSourceRow.Key != 0, "负键变 self");

        // 负数键行不得被 IsPartyMember 误判为队友（否则组队过滤会把它当自己人）
        Check("战斗: IsPartyMember(未知玩家) = false", !GameEvents.IsPartyMember("未知玩家"), "误判为队友");
        Check("战斗: IsPartyMember(self 展示名) = true", GameEvents.IsPartyMember(selfName), "self 未识别");
        Check("战斗: 未知玩家与 self 是两行", unknownRow.PlayerName != selfName, "混行");

        // CombatTab 的组队过滤条件：Key<0 始终保留 → 大数字含未知伤害时榜单不吞行
        bool isInParty = GameEvents.IsInParty;
        var shown = isInParty
            ? rows.FindAll(d => d.Key < 0 || GameEvents.IsPartyMember(d.PlayerName))
            : rows;
        Check("战斗: 组队过滤后 unknown 行仍可见", shown.Exists(d => d.Key == -1), "unknown 行被吞");
        Check("战斗: 组队过滤后未知来源行仍可见", shown.Exists(d => d.Key == -3), "未知来源行被吞");
        Check("战斗: 组队过滤后 self 行仍可见", shown.Exists(d => d.Key == 0), "self 行被吞");

        // 大数字恒等式：榜单（含未知行）求和 = GetTotals（两套口径不分裂）
        long boardSum = 0;
        foreach (var r in rows) boardSum += r.TotalDamage;
        Eq("战斗: 榜单求和 = 大数字（同源）", boardSum, trk.GetTotals(DamageStatsTracker.DamageFilter.All).TotalDamage);

        LocalPlayerIdentity.Reset();
    }
}
