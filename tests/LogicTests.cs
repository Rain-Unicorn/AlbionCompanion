using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using AlbionCompanion.Core;
using AlbionCompanion.Core.DamageMeter;
using AlbionCompanion.Core.Income;
using AlbionCompanion.Core.Party;
using AlbionCompanion.Core.Gathering;

static class LogicTests
{
    static int _fail;
    static int _pass;

    static void Check(string name, bool ok, string detail)
    {
        if (ok) { _pass++; Console.WriteLine("PASS  " + name); }
        else { _fail++; Console.WriteLine("FAIL  " + name + "  :: " + detail); }
    }

    static void Eq(string name, object actual, object expected)
    {
        Check(name, actual != null && actual.Equals(expected),
              "expected <" + expected + "> but got <" + actual + ">");
    }

    // 模拟 ChatSniffer 在 evt=62 的入账职责（设计 §5：状态机只裁决，入账由调用方执行）
    static TakeSilverDisposition TakeSilver(long pickerId, long afterTaxRaw)
    {
        var d = LocalPlayerIdentity.OnTakeSilver(pickerId, afterTaxRaw);
        if (d == TakeSilverDisposition.CountAsMine || d == TakeSilverDisposition.Pending)
        {
            var rec = new IncomeRecord
            {
                TimeUtc = DateTime.UtcNow, Source = IncomeSource.SilverPickup,
                SourceLabel = "拾取银币", PickerId = pickerId, SilverAfterTax = afterTaxRaw
            };
            if (d == TakeSilverDisposition.CountAsMine) IncomeTracker.AddConfirmed(rec);
            else IncomeTracker.AddPending(rec);
        }
        // Ignore：别人的钱，不记录
        return d;
    }

    static string SampleJson = @"[{""item_id"":""T4_BAG"",""city"":""Caerleon"",""quality"":1,""sell_price_min"":7493,""sell_price_min_date"":""2026-11-04T14:30:00"",""sell_price_max"":7495,""sell_price_max_date"":""2026-11-04T14:30:00"",""buy_price_min"":0,""buy_price_min_date"":""0001-01-01T00:00:00"",""buy_price_max"":6999,""buy_price_max_date"":""2026-11-04T13:00:00""},{""item_id"":""T4_BAG"",""city"":""Bridgewatch"",""quality"":1,""sell_price_min"":0,""sell_price_min_date"":""0001-01-01T00:00:00"",""sell_price_max"":0,""sell_price_max_date"":""0001-01-01T00:00:00"",""buy_price_min"":0,""buy_price_min_date"":""0001-01-01T00:00:00"",""buy_price_max"":0,""buy_price_max_date"":""0001-01-01T00:00:00""}]";

    // M3：ao-bin-dumps formatted/items.json 缩样（覆盖 ZH 缺失 EN 兜底、@附魔、非 T 前缀条目）
    const string SampleItemsJson = "["
        + "{\"UniqueName\":\"T4_BAG\",\"LocalizedNames\":{\"ZH-CN\":\"老手级背包\",\"EN-US\":\"Adept's Bag\"}},"
        + "{\"UniqueName\":\"T4_BAG@1\",\"LocalizedNames\":{\"ZH-CN\":\"老手级背包\",\"EN-US\":\"Adept's Bag\"}},"
        + "{\"UniqueName\":\"T4_BAG_INSIGHT\",\"LocalizedNames\":{\"ZH-CN\":\"老手级洞察背包\",\"EN-US\":\"Adept's Insight Bag\"}},"
        + "{\"UniqueName\":\"T5_2H_BOW\",\"LocalizedNames\":{\"EN-US\":\"Expert's Bow\"}},"
        + "{\"UniqueName\":\"QUESTITEM_RABBITS_FOOT\",\"LocalizedNames\":{\"EN-US\":\"Rabbit's Foot\"}}"
        + "]";

    // M3：根目录原始版 items.json 缩样（@uniquename 小写 u + @weight 字符串 kg；含缺 weight / weight=0 边界）
    const string SampleRawItemsJson = "{"
        + "\"?xml\":{\"@version\":\"1.0\"},"
        + "\"items\":{"
        + "  \"equipmentitem\":["
        + "    {\"@uniquename\":\"T4_BAG\",\"@weight\":\"3.4\"},"
        + "    {\"@uniquename\":\"T4_BAG_INSIGHT\",\"@weight\":\"3.4\"},"
        + "    {\"@uniquename\":\"T4_2H_BOW\",\"@weight\":\"6.8\"}"
        + "  ],"
        + "  \"simpleitem\":["
        + "    {\"@uniquename\":\"T4_MAIN_SWORD\",\"@weight\":\"5.1\"},"
        + "    {\"@uniquename\":\"QUESTITEM_RABBITS_FOOT\",\"@weight\":\"0\"},"      // 0 = 异常，必须留 null
        + "    {\"@uniquename\":\"T5_2H_BOW\"}"                                     // 缺 @weight → null
        + "  ],"
        + "  \"mountskin\":["
        + "    {\"@uniquename\":\"SKIN_HORSE_STARTERPACK\"}"                        // 皮肤类整体无 weight
        + "  ]"
        + "}}";

    // ─────────────────────────────────────────────────────────────
    // C8/Bug A：未解析目标不得把裸 ObjectId 写进用户可见文案
    // 用户实测：「总览页治疗事件显示『目标119428』」。这里直接把解析口径钉死：
    //   玩家侧未解析 → 「未知玩家」（且标记挂 pending，等 evt=29 补名）
    //   非玩家侧未解析 → 「未知单位」
    // 另加一条哨兵断言：任何分支的返回都不允许包含 ObjectId 数字本身。
    // ─────────────────────────────────────────────────────────────
    static void RunTargetNameTests()
    {
        var objectNames = new Dictionary<long, string>();
        var mobNames = new Dictionary<long, string>();
        var playerNames = new Dictionary<long, string>();

        // --- 玩家侧：判得出是玩家（在 _playerNames 里），但名字字典里没有 → 未知玩家 + 挂 pending ---
        playerNames[119428] = ""; // 只标类型、无名字
        var p = TargetNameResolver.Resolve(119428, false, objectNames, mobNames, playerNames);
        Eq("C8/A 玩家侧未解析 → 未知玩家", p.DisplayName, "未知玩家");
        Eq("C8/A 玩家侧目标类型 = Player", p.Kind, TargetKind.Player);
        Check("C8/A 玩家侧未解析需挂 pending 反查", p.NeedsPendingLookup, "NeedsPendingLookup=false");
        Check("C8/A 玩家侧文案不含 ObjectId", !p.DisplayName.Contains("119428"), p.DisplayName);

        // --- 非玩家侧：在 _mobNames 里但名字缺失 → 未知单位 ---
        mobNames[555666] = ""; // 无名字的怪物/单位
        var u = TargetNameResolver.Resolve(555666, false, objectNames, mobNames, playerNames);
        Eq("C8/A 非玩家侧未解析 → 未知单位", u.DisplayName, "未知单位");
        Eq("C8/A 非玩家侧目标类型 = Unit", u.Kind, TargetKind.Unit);
        Check("C8/A 非玩家侧不需要玩家反查", !u.NeedsPendingLookup, "NeedsPendingLookup=true");
        Check("C8/A 非玩家侧文案不含 ObjectId", !u.DisplayName.Contains("555666"), u.DisplayName);

        // --- 三份字典都判不出来 → 保守按「未知单位」（宁可未知单位，不给玩家挂错标签） ---
        var unknown = TargetNameResolver.Resolve(777888999, false, objectNames, mobNames, playerNames);
        Eq("C8/A 判不出类型 → 保守显示未知单位", unknown.DisplayName, "未知单位");
        Eq("C8/A 判不出类型 → Unit", unknown.Kind, TargetKind.Unit);

        // --- 本地玩家自己（IsSelf=true）→ 玩家侧 ---- ---
        var self = TargetNameResolver.Resolve(4242, true, objectNames, mobNames, playerNames);
        Eq("C8/A 本地玩家未解析 → 未知玩家", self.DisplayName, "未知玩家");
        Check("C8/A 本地玩家文案不含 ObjectId", !self.DisplayName.Contains("4242"), self.DisplayName);

        // --- 已解析：名字字典命中，ObjectId 不进文案 ---
        objectNames[119428] = "路人甲";
        var known = TargetNameResolver.Resolve(119428, false, objectNames, mobNames, playerNames);
        Eq("C8/A 已解析玩家 → 真实名字", known.DisplayName, "路人甲");
        Eq("C8/A 已解析玩家类型 = Player", known.Kind, TargetKind.Player);
        Check("C8/A 已解析不再挂 pending", !known.NeedsPendingLookup, "NeedsPendingLookup=true");

        objectNames[555666] = "小蜘蛛";
        var knownMob = TargetNameResolver.Resolve(555666, false, objectNames, mobNames, playerNames);
        Eq("C8/A 已解析怪物 → 真实名字", knownMob.DisplayName, "小蜘蛛");
        Eq("C8/A 已解析怪物类型 = Unit", knownMob.Kind, TargetKind.Unit);

        // --- 非正数 id：不得进入名字字典路径，按单位保守显示 ---
        var zero = TargetNameResolver.Resolve(0, false, objectNames, mobNames, playerNames);
        Eq("C8/A target=0 → 未知单位", zero.DisplayName, "未知单位");
        var neg = TargetNameResolver.Resolve(-5, false, objectNames, mobNames, playerNames);
        Eq("C8/A target<0 → 未知单位", neg.DisplayName, "未知单位");

        // --- pending 挂起机制：玩家侧未解析会挂上，上限裁剪不崩 ---
        ChatSniffer.ResetPendingTargets();
        ChatSniffer.MarkPendingTarget(119428);
        Eq("C8/A 挂 pending 后计数=1", ChatSniffer.PendingTargetCount, 1);
        ChatSniffer.MarkPendingTarget(119428);
        Eq("C8/A 重复挂同一 id 不重复计数", ChatSniffer.PendingTargetCount, 1);
        ChatSniffer.MarkPendingTarget(-3);   // 非正数不挂
        Eq("C8/A 非正数 id 不挂 pending", ChatSniffer.PendingTargetCount, 1);
        for (long i = 1; i <= 600; i++) ChatSniffer.MarkPendingTarget(i);
        Check("C8/A pending 上限生效（<=512）", ChatSniffer.PendingTargetCount <= 512,
              "count=" + ChatSniffer.PendingTargetCount);
        ChatSniffer.ResetPendingTargets();
        Eq("C8/A 清理 hook 归零", ChatSniffer.PendingTargetCount, 0);

        // --- 同类修复（evt=163 KilledPlayer / evt=164 Died）走的 NameOrUnknown 口径 ---
        // 事件参数「可能是名字、也可能给的是 ObjectId」，两种都必须不出裸数字。
        string realName = ChatSniffer.NameOrUnknown("路人甲", 119428, "未知玩家");
        Eq("C8/A 参数是名字 → 原样使用", realName, "路人甲");

        string numericName = ChatSniffer.NameOrUnknown("119428", 119428, "未知玩家");
        Check("C8/A 参数是纯数字 → 不原样显示", !numericName.Contains("119428"), numericName);

        string idOnly = ChatSniffer.NameOrUnknown(null, 119429, "未知玩家");
        Check("C8/A 只有 ObjectId → 不显示裸 id", !idOnly.Contains("119429"), idOnly);

        string emptyBoth = ChatSniffer.NameOrUnknown(null, null, "未知玩家");
        Eq("C8/A 名字与 id 都缺 → 用兜底文案", emptyBoth, "未知玩家");

        string diedFallback = ChatSniffer.NameOrUnknown(null, 0, "未知");
        Eq("C8/A id=0 且无名字 → 用兜底文案", diedFallback, "未知");

        // --- pending 超时清理：超 TTL 的目标要被扫掉（否则 512 上限会被陈旧 id 占满） ---
        // SweepPendingTargets(nowUtc) 的语义是「此刻的时间」：传 +30 分钟即模拟已过去 30 分钟。
        ChatSniffer.ResetPendingTargets();
        ChatSniffer.MarkPendingTarget(888001);
        ChatSniffer.SweepPendingTargets(DateTime.UtcNow.AddMinutes(30));
        Eq("C8/A 超 TTL 后清理掉陈旧 pending", ChatSniffer.PendingTargetCount, 0);

        // 未超 TTL（+1 分钟）不得误删，否则补名窗口会被提前掐断
        ChatSniffer.MarkPendingTarget(888002);
        ChatSniffer.SweepPendingTargets(DateTime.UtcNow.AddMinutes(1));
        Eq("C8/A 未超 TTL 不误删", ChatSniffer.PendingTargetCount, 1);
        ChatSniffer.ResetPendingTargets();
    }

    // ─────────────────────────────────────────────────────────────
    // C8/Bug B：自绘控件「状态真的变化才重绘」
    // 能脱离 WinForms 验的就是这条状态语义，真实 MessageBox/句柄的重绘时序需实机验证。
    // ─────────────────────────────────────────────────────────────
    static void RunToggleStateTests()
    {
        var t = new ToggleState();
        Eq("C8/B 初始为未选中", t.Value, false);
        Eq("C8/B 初始变化次数=0", t.ChangeCount, 0);

        Check("C8/B 设为 false（值未变）不触发重绘", !t.Set(false), "Set 返回 true");
        Eq("C8/B 值未变不累加变化次数", t.ChangeCount, 0);

        Check("C8/B false→true 触发重绘", t.Set(true), "Set 返回 false");
        Eq("C8/B 变化后值为 true", t.Value, true);
        Eq("C8/B 变化次数=1", t.ChangeCount, 1);

        Check("C8/B 重复设为 true 不再重绘", !t.Set(true), "Set 返回 true");
        Eq("C8/B 重复设置变化次数仍为 1", t.ChangeCount, 1);

        Check("C8/B true→false 触发重绘", t.Set(false), "Set 返回 false");
        Eq("C8/B 变化次数=2", t.ChangeCount, 2);

        // 模拟切筛选：全部→PVP，三个按钮只有两个值真的变化 → 应只重绘 2 次，不是 3 次
        var all = new ToggleState(); all.Set(true);
        var pve = new ToggleState(); pve.Set(false);
        var pvp = new ToggleState(); pvp.Set(false);
        int before = all.ChangeCount + pve.ChangeCount + pvp.ChangeCount;
        all.Set(false);   // 变
        pve.Set(false);   // 未变
        pvp.Set(true);    // 变
        int after = all.ChangeCount + pve.ChangeCount + pvp.ChangeCount;
        Eq("C8/B 切筛选只重绘真正变化的 2 个按钮", after - before, 2);
        Eq("C8/B 切换后 PVP 高亮", pvp.Value, true);
        Eq("C8/B 切换后 ALL 取消高亮", all.Value, false);
    }

    // ─────────────────────────────────────────────────────────────
    // M3：ItemIndex 加载/查询/搜索/分层过滤/侧车元数据 单测
    // ─────────────────────────────────────────────────────────────
    static void RunItemIndexTests()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ac_m3_itemindex_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            string jsonPath = Path.Combine(dir, "items.json");
            File.WriteAllText(jsonPath, SampleItemsJson);

            var idx = ItemIndex.Load(jsonPath);
            Check("M3 Load 返回非空索引", idx != null, "Load returned null");
            if (idx != null)
            {
                ItemIndexEntry entry;
                Check("M3 TryGetEntry 命中 T4_BAG", idx.TryGetEntry("T4_BAG", out entry) && entry.UniqueName == "T4_BAG", "TryGetEntry failed");
                Check("M3 TryGetEntry 未命中返回 false", !idx.TryGetEntry("T9_NOT_EXIST", out entry), "expected false");
                Eq("M3 中文名", idx.DisplayNameOf("T4_BAG"), "老手级背包");
                Eq("M3 ZH 缺失回退 EN", idx.DisplayNameOf("T5_2H_BOW"), "Expert's Bow");
                Check("M3 未知物品返回 null（调用方兜底显示原 ID）", idx.DisplayNameOf("T9_NOT_EXIST") == null, "expected null");

                // 离线子串搜索：中文名 / ID
                var byZh = idx.SearchBaseIds("老手级", 10);
                Check("M3 中文名搜索命中含 T4_BAG", byZh.Contains("T4_BAG"), "got " + string.Join(",", byZh));
                var byId = idx.SearchBaseIds("T4_BAG", 10);
                Check("M3 ID 搜索命中含 T4_BAG", byId.Contains("T4_BAG"), "got " + string.Join(",", byId));
                Check("M3 ID 搜索附魔折叠 baseId", byId.Contains("T4_BAG") && !byId.Contains("T4_BAG@1"), "got " + string.Join(",", byId));
                var byMiss = idx.SearchBaseIds("不存在的物品名", 10);
                Eq("M3 搜索无结果返回空", byMiss.Count, 0);

                // 可交易分层：T 前缀全集（含 @1 折叠进 baseId）
                var tiered = idx.TieredBaseIds();
                Check("M3 TieredBaseIds 含 T4_BAG", tiered.Contains("T4_BAG"), "got " + string.Join(",", tiered));
                Check("M3 TieredBaseIds 含 T5_2H_BOW", tiered.Contains("T5_2H_BOW"), "got " + string.Join(",", tiered));
                Check("M3 TieredBaseIds 排除非 T 前缀", !tiered.Contains("QUESTITEM_RABBITS_FOOT"), "got " + string.Join(",", tiered));
                Check("M3 TieredBaseIds 排除附魔变体原 ID", !tiered.Contains("T4_BAG@1"), "got " + string.Join(",", tiered));
            }

            // 缺文件 / 损坏 JSON → 空索引不抛异常（null 告警语义由 LoadDefault 负责）
            var missIdx = ItemIndex.Load(Path.Combine(dir, "missing.json"));
            Check("M3 缺文件 Load 返回空索引", missIdx != null && missIdx.Count == 0, "expected empty index");
            string badPath = Path.Combine(dir, "bad.json");
            File.WriteAllText(badPath, "{not json");
            var badIdx = ItemIndex.Load(badPath);
            Check("M3 损坏 JSON Load 返回空索引不抛异常", badIdx != null && badIdx.Count == 0, "expected empty index");

            // 侧车元数据：保存后可读回版本号
            ItemIndexMeta.Save(jsonPath, "abcd1234", "2026-11-01T00:00:00Z", 12248);
            Eq("M3 侧车版本读回", ItemIndexMeta.LoadVersion(jsonPath), "abcd1234");
            string emptyDir = Path.Combine(dir, "no_sidecar");
            Directory.CreateDirectory(emptyDir);
            Check("M3 侧车缺文件版本为 null", ItemIndexMeta.LoadVersion(Path.Combine(emptyDir, "items.json")) == null, "expected null");

            // ── M3 修正：weight 双文件 join（formatted 版没有 weight，原始版 @weight 是 kg 字符串）──
            string wDir = Path.Combine(dir, "weights");
            Directory.CreateDirectory(wDir);
            string wFmt = Path.Combine(wDir, ItemIndex.FormattedFileName);
            string wRaw = Path.Combine(wDir, ItemIndex.RawFileName);
            File.WriteAllText(wFmt, "["
                + "{\"UniqueName\":\"T4_BAG\",\"LocalizedNames\":{\"ZH-CN\":\"老手级背包\"}},"
                + "{\"UniqueName\":\"T4_BAG@1\",\"LocalizedNames\":{\"ZH-CN\":\"老手级背包\"}},"
                + "{\"UniqueName\":\"T4_BAG_INSIGHT\",\"LocalizedNames\":{\"ZH-CN\":\"老手级洞察背包\"}},"
                + "{\"UniqueName\":\"T4_2H_BOW\",\"LocalizedNames\":{\"ZH-CN\":\"老手级战弓\"}},"
        + "{\"UniqueName\":\"T4_2H_BOW@1\",\"LocalizedNames\":{\"ZH-CN\":\"老手级战弓\"}},"
        + "{\"UniqueName\":\"T4_2H_BOW@2\",\"LocalizedNames\":{\"ZH-CN\":\"老手级战弓\"}},"
        + "{\"UniqueName\":\"T4_CAPE_BRIDGEWATCH\",\"LocalizedNames\":{\"ZH-CN\":\"老手级桥望披风\"}},"
        + "{\"UniqueName\":\"TREASURE_SILVERWARE_RARITY1\",\"LocalizedNames\":{\"ZH-CN\":\"银器\"}},"
                + "{\"UniqueName\":\"T4_MAIN_SWORD\",\"LocalizedNames\":{\"ZH-CN\":\"老手级阔剑\"}},"
                + "{\"UniqueName\":\"T5_2H_BOW\",\"LocalizedNames\":{\"ZH-CN\":\"专家级战弓\"}},"
                + "{\"UniqueName\":\"QUESTITEM_RABBITS_FOOT\",\"LocalizedNames\":{\"EN-US\":\"Rabbit's Foot\"}},"
                + "{\"UniqueName\":\"SKIN_HORSE_STARTERPACK\",\"LocalizedNames\":{\"EN-US\":\"Skin\"}}"
                + "]");
            File.WriteAllText(wRaw, SampleRawItemsJson);

            var wi = ItemIndex.Load(wFmt, wRaw);
            Check("M3 weight 双文件加载非空", wi.Count > 0, "count=" + wi.Count);
            Check("M3 weight join 已跑（WeightJoined）", wi.WeightJoined, "expected WeightJoined=true");
            Eq("M3 weight 覆盖率分母 = 条目数", wi.WeightedCount + wi.MissingWeightCount, wi.Count);
            Eq("M3 背包 weight = 3.4kg", wi.WeightKgOf("T4_BAG"), 3.4d);
            Eq("M3 附魔变体折叠到 baseId 取 weight", wi.WeightKgOf("T4_BAG@1"), 3.4d);
            Eq("M3 战弓 weight = 6.8kg", wi.WeightKgOf("T4_2H_BOW"), 6.8d);
            Eq("M3 长剑 weight = 5.1kg", wi.WeightKgOf("T4_MAIN_SWORD"), 5.1d);
            Check("M3 缺 @weight 条目 weight 为 null（不是 0）", wi.WeightKgOf("T5_2H_BOW") == null,
                  "got " + wi.WeightKgOf("T5_2H_BOW"));
            Check("M3 weight=0 视为缺失留 null（不是 0，防每公斤排序假最优）",
                  wi.WeightKgOf("QUESTITEM_RABBITS_FOOT") == null,
                  "got " + wi.WeightKgOf("QUESTITEM_RABBITS_FOOT"));
            Check("M3 皮肤类无 weight 也留 null", wi.WeightKgOf("SKIN_HORSE_STARTERPACK") == null, "expected null");
            Check("M3 未知物品 weight null", wi.WeightKgOf("T9_NOT_EXIST") == null, "expected null");
            ItemIndexEntry wEntry;
            Check("M3 条目公开 weight 字段（跑商读取名）",
                  wi.TryGetEntry("T4_BAG", out wEntry) && wEntry.Weight.HasValue && Math.Abs(wEntry.Weight.Value - 3.4d) < 1e-9,
                  "Weight getter mismatch");
            Eq("M3 有 weight 条目计数 = 4", wi.WeightedCount, 4);
            Check("M3 weight 覆盖率落在 (0,1)", wi.WeightCoverageRatio > 0d && wi.WeightCoverageRatio < 1d,
                  "ratio=" + wi.WeightCoverageRatio);

            // 只给 formatted（无原始版）→ 索引可用但 weight 全 null 且 WeightJoined=false（UI 告警依据）
            var noRaw = ItemIndex.Load(wFmt);
            Check("M3 缺原始版时索引仍可用", noRaw.Count == wi.Count, "count=" + noRaw.Count);
            Check("M3 缺原始版时 WeightJoined=false（显式告警依据）", !noRaw.WeightJoined, "expected false");
            Eq("M3 缺原始版时 weight 全部 null", noRaw.WeightedCount, 0);

            // 原始版损坏 → 不抛异常、weight 全 null、WeightJoined=false
            string badRaw = Path.Combine(wDir, "bad-raw.json");
            File.WriteAllText(badRaw, "{broken");
            var badRawIdx = ItemIndex.Load(wFmt, badRaw);
            Check("M3 原始版损坏不抛异常", badRawIdx != null && badRawIdx.Count == wi.Count, "count mismatch");
            Check("M3 原始版损坏 WeightJoined=false", !badRawIdx.WeightJoined, "expected false");
            Eq("M3 原始版损坏 weight 全 null", badRawIdx.WeightedCount, 0);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // M4：市场查询页三级联动筛选 —— 分类树 / 级联清空 / 查价键
    // 全部纯逻辑，离线可测（不依赖 WinForms、不依赖网络）。
    // ══════════════════════════════════════════════════════════════════════

    // M4 缩样：原始版 items.json 带 @shopcategory / @shopsubcategory1 / @shopsubcategory2 / @enchantmentlevel
    // 覆盖：同一 baseId 跨类目重复（取首个带分类）、附魔等级并集、无分类物品（必须计数告警不静默丢）
    const string SampleTaxonomyRawJson = "{"
        + "\"?xml\":{\"@version\":\"1.0\"},"
        + "\"items\":{"
        + "  \"equipmentitem\":["
        + "    {\"@uniquename\":\"T4_HEAD_CLOTH_SET1\",\"@shopcategory\":\"head\",\"@shopsubcategory1\":\"cloth_helmet\",\"@shopsubcategory2\":\"head_cloth_set1\",\"@enchantmentlevel\":\"0\",\"@weight\":\"1.2\"},"
        + "    {\"@uniquename\":\"T5_HEAD_CLOTH_SET1\",\"@shopcategory\":\"head\",\"@shopsubcategory1\":\"cloth_helmet\",\"@shopsubcategory2\":\"head_cloth_set1\",\"@enchantmentlevel\":\"0\",\"@weight\":\"1.2\"},"
        + "    {\"@uniquename\":\"T5_HEAD_CLOTH_SET1\",\"@enchantmentlevel\":\"1\"},"
        + "    {\"@uniquename\":\"T5_HEAD_CLOTH_SET1\",\"@enchantmentlevel\":\"2\"},"
        + "    {\"@uniquename\":\"T4_HEAD_PLATE_SET1\",\"@shopcategory\":\"head\",\"@shopsubcategory1\":\"plate_helmet\",\"@shopsubcategory2\":\"head_plate_set1\",\"@enchantmentlevel\":\"0\",\"@weight\":\"2.0\"},"
        + "    {\"@uniquename\":\"T5_ARMOR_CLOTH_SET1\",\"@shopcategory\":\"armors\",\"@shopsubcategory1\":\"cloth_armor\",\"@shopsubcategory2\":\"armors_cloth_set1\",\"@weight\":\"2.4\"},"
        + "    {\"@uniquename\":\"T4_BAG\",\"@shopcategory\":\"bags\",\"@shopsubcategory1\":\"bags\",\"@weight\":\"3.4\"}"
        + "  ],"
        + "  \"weapon\":["
        + "    {\"@uniquename\":\"T4_2H_BOW\",\"@shopcategory\":\"weapons\",\"@shopsubcategory1\":\"bow\",\"@shopsubcategory2\":\"bow_bow\",\"@weight\":\"6.8\"},"
        + "    {\"@uniquename\":\"T5_2H_BOW\",\"@shopcategory\":\"weapons\",\"@shopsubcategory1\":\"bow\",\"@shopsubcategory2\":\"bow_bow\",\"@weight\":\"6.8\"},"
        + "    {\"@uniquename\":\"T5_MAIN_SWORD\",\"@shopcategory\":\"weapons\",\"@shopsubcategory1\":\"sword\",\"@weight\":\"5.1\"}"
        + "  ],"
        + "  \"simpleitem\":["
        + "    {\"@uniquename\":\"T4_ROCK\",\"@shopcategory\":\"crafting\",\"@shopsubcategory1\":\"resources\",\"@weight\":\"1.0\"},"
        + "    {\"@uniquename\":\"T8_UNKNOWN_NO_CAT\"}"                                 // 无分类 → 计数，不进树
        + "  ],"
        + "  \"cape\":[{\"@uniquename\":\"T4_CAPE_BRIDGEWATCH\",\"@shopcategory\":\"capes\",\"@shopsubcategory1\":\"accessoires_capes_bridgewatch\",\"@weight\":\"1.0\"}]," 
        + "  \"treasure\":[{\"@uniquename\":\"TREASURE_SILVERWARE_RARITY1\",\"@shopcategory\":\"other\",\"@shopsubcategory1\":\"luxurygoods\"}]"
        + "}}";

    const string SampleTaxonomyFmtJson = "["
        + "{\"UniqueName\":\"T4_HEAD_CLOTH_SET1\",\"LocalizedNames\":{\"ZH-CN\":\"老手级学士风帽\"}},"
        + "{\"UniqueName\":\"T5_HEAD_CLOTH_SET1\",\"LocalizedNames\":{\"ZH-CN\":\"专家级学士风帽\"}},"
        + "{\"UniqueName\":\"T5_HEAD_CLOTH_SET1@1\",\"LocalizedNames\":{\"ZH-CN\":\"专家级学士风帽\"}},"
        + "{\"UniqueName\":\"T5_HEAD_CLOTH_SET1@2\",\"LocalizedNames\":{\"ZH-CN\":\"专家级学士风帽\"}},"
        + "{\"UniqueName\":\"T4_HEAD_PLATE_SET1\",\"LocalizedNames\":{\"ZH-CN\":\"老手级士兵头盔\"}},"
        + "{\"UniqueName\":\"T5_ARMOR_CLOTH_SET1\",\"LocalizedNames\":{\"ZH-CN\":\"专家级学士长袍\"}},"
        + "{\"UniqueName\":\"T4_BAG\",\"LocalizedNames\":{\"ZH-CN\":\"老手级背包\"}},"
        + "{\"UniqueName\":\"T4_2H_BOW\",\"LocalizedNames\":{\"ZH-CN\":\"老手级战弓\"}},"
        + "{\"UniqueName\":\"T4_2H_BOW@1\",\"LocalizedNames\":{\"ZH-CN\":\"老手级战弓\"}},"
        + "{\"UniqueName\":\"T4_2H_BOW@2\",\"LocalizedNames\":{\"ZH-CN\":\"老手级战弓\"}},"
        + "{\"UniqueName\":\"T4_CAPE_BRIDGEWATCH\",\"LocalizedNames\":{\"ZH-CN\":\"老手级桥望披风\"}},"
        + "{\"UniqueName\":\"TREASURE_SILVERWARE_RARITY1\",\"LocalizedNames\":{\"ZH-CN\":\"银器\"}},"
        + "{\"UniqueName\":\"T5_2H_BOW\",\"LocalizedNames\":{\"ZH-CN\":\"专家级战弓\"}},"
        + "{\"UniqueName\":\"T5_MAIN_SWORD\",\"LocalizedNames\":{\"ZH-CN\":\"专家级阔剑\"}},"
        + "{\"UniqueName\":\"T4_ROCK\",\"LocalizedNames\":{\"ZH-CN\":\"老手级石头\"}},"
        + "{\"UniqueName\":\"T8_UNKNOWN_NO_CAT\",\"LocalizedNames\":{\"ZH-CN\":\"未知物品\"}}"
        + "]";

    static void RunTaxonomyTests()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ac_m4_tax_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            string fmt = Path.Combine(dir, ItemIndex.FormattedFileName);
            string raw = Path.Combine(dir, ItemIndex.RawFileName);
            File.WriteAllText(fmt, SampleTaxonomyFmtJson);
            File.WriteAllText(raw, SampleTaxonomyRawJson);

            var idx = ItemIndex.Load(fmt, raw);
            var tax = ItemTaxonomy.Build(idx);
            Check("M4 分类树非空", tax.Categories.Count > 0, "categories=" + tax.Categories.Count);

            // 一级：按游戏集市习惯排序，装备部位在前
            var head = tax.Find("head");
            Check("M4 一级 head 存在", head != null, "head missing");
            Eq("M4 一级中文标签", head != null ? head.DisplayName : null, "头部护甲");
            var weapons = tax.Find("weapons");
            Eq("M4 一级 weapons 标签", weapons != null ? weapons.DisplayName : null, "武器");
            Check("M4 一级排序：武器在头部护甲之前",
                  tax.Categories.FindIndex(c => c.Key == "weapons") < tax.Categories.FindIndex(c => c.Key == "head"),
                  "weapons should rank before head");

            // 二级：材质分支
            Check("M4 head 下有布甲风帽与板甲头盔",
                  head != null && head.SubCategories.Exists(s => s.Key == "cloth_helmet")
                              && head.SubCategories.Exists(s => s.Key == "plate_helmet"),
                  "subcategories missing");
            var clothHelmet = head == null ? null : head.SubCategories.Find(s => s.Key == "cloth_helmet");
            Eq("M4 二级 cloth_helmet 标签", clothHelmet != null ? clothHelmet.DisplayName : null, "布甲风帽");

            // 三级族：跨阶级合并成一个族，名字去掉阶级前缀
            var fam = clothHelmet == null ? null : clothHelmet.Families.Find(f => f.Family == "HEAD_CLOTH_SET1");
            Check("M4 三级族 HEAD_CLOTH_SET1 存在", fam != null, "family missing");
            Eq("M4 族名去掉阶级前缀（老手级/专家级 → 学士风帽）",
               fam != null ? fam.DisplayName : null, "学士风帽");
            var tiers = fam == null ? new List<int>() : fam.Tiers();
            Eq("M4 族阶级集合 = T4,T5", string.Join(",", tiers.ToArray()), "4,5");

            // 附魔展开：@enchantmentlevel 并集 → (阶级,附魔) 矩阵，层级深度不变
            var ench = fam == null ? new List<int>() : fam.Enchants(5);
            Eq("M4 T5 附魔集合 = 0,1,2", string.Join(",", ench.ToArray()), "0,1,2");
            Check("M4 T4 无附魔变体只含 0", fam != null && fam.Enchants(4).Count == 1 && fam.Enchants(4)[0] == 0,
                  "T4 enchant set mismatch");
            Check("M4 附魔变体 uniqueName 正确", fam != null && fam.Find(5, 2) != null
                  && fam.Find(5, 2).UniqueName == "T5_HEAD_CLOTH_SET1@2", "variant uniqueName mismatch");
            Check("M4 无附魔变体 uniqueName 不带 @0", fam != null && fam.Find(4, 0).UniqueName == "T4_HEAD_CLOTH_SET1",
                  "T4 variant uniqueName mismatch");
            Check("M4 族标记有附魔变体", fam != null && fam.HasEnchantVariants(), "HasEnchantVariants=false");

            // 附魔全集来自 formatted 的 @N 后缀：raw 武器节点没有 @enchantmentlevel（真实数据即如此），
            // 只靠 raw 会漏掉全部武器附魔，这里必须由 formatted 补齐。
            var bow = tax.Find("weapons") == null ? null : tax.Find("weapons").SubCategories.Find(x => x.Key == "bow");
            var bowFam = bow == null ? null : bow.Families.Find(f => f.Family == "2H_BOW");
            Check("M4 raw 无附魔属性的族靠 formatted 补齐附魔", bowFam != null
                  && string.Join(",", bowFam.Enchants(4).ToArray()) == "0,1,2", "bow enchants should be 0,1,2");
            Check("M4 补齐后附魔变体 uniqueName 正确", bowFam != null && bowFam.Find(4, 2) != null
                  && bowFam.Find(4, 2).UniqueName == "T4_2H_BOW@2", "bow @2 variant missing");

            // 披风按城市分支必须有人类可读标签（真实数据 16 个分支，漏一个就显示原始 key）
            var capes = tax.Find("capes");
            Check("M4 披风一级存在", capes != null, "capes missing");
            var bridgeCape = capes == null ? null : capes.SubCategories.Find(x => x.Key == "accessoires_capes_bridgewatch");
            Eq("M4 披风城市分支标签", bridgeCape != null ? bridgeCape.DisplayName : null, "桥望披风");

            // 珍宝（luxurygoods）标签补齐，不再落到 HumanizeKey
            var otherCat = tax.Find("other");
            var luxury = otherCat == null ? null : otherCat.SubCategories.Find(x => x.Key == "luxurygoods");
            Eq("M4 珍宝二级标签", luxury != null ? luxury.DisplayName : null, "珍宝");

            // 无分类物品：必须计数告警，绝不静默丢弃也不臆造分类
            Eq("M4 无分类物品计数", tax.UnclassifiedItemCount, 1);
            Check("M4 无分类物品不进任何一级", tax.Find("head") != null
                  && !tax.Find("head").SubCategories.Exists(s => s.Families.Exists(f => f.Family == "UNKNOWN_NO_CAT")),
                  "unclassified leaked into tree");

            // 标签表缺项 → 记 UntranslatedKeys，但仍然出现在树里（丢标签不能丢物品）
            Eq("M4 未翻译 key 记录（示例数据全覆盖则为 0）", tax.UntranslatedKeys.Count, 0);

            // ── 级联清空：设计 §4.2 硬条件 ──
            var sel = new MarketSelection();
            sel.SelectCategory("head");
            sel.SelectSubCategory("cloth_helmet");
            sel.SelectFamily("HEAD_CLOTH_SET1");
            sel.SelectVariant(5, 2);
            Check("M4 三级选满", sel.IsComplete, "expected complete");
            sel.SelectCategory("weapons"); // 换一级
            Check("M4 换一级清空二级", !sel.HasSubCategory, "sub should be cleared");
            Check("M4 换一级清空三级", !sel.HasFamily && sel.Tier == 0 && sel.Enchant == 0, "family/tier should be cleared");

            sel.SelectSubCategory("bow");
            sel.SelectFamily("2H_BOW");
            sel.SelectVariant(5, 0);
            sel.SelectSubCategory("sword");
            Check("M4 换二级清空三级族", !sel.HasFamily && sel.Tier == 0, "family should be cleared");

            // ── 查价键映射：筛选产物 = (uniqueName, quality) ──
            var key = MarketQueryPlanner.Build(tax, new MarketSelection());
            Check("M4 未选满产空键（M5 不发请求）", key.IsEmpty, "expected empty key");

            var sel2 = new MarketSelection();
            sel2.SelectCategory("head");
            sel2.SelectSubCategory("cloth_helmet");
            sel2.SelectFamily("HEAD_CLOTH_SET1");
            sel2.SelectVariant(5, 1);
            var key2 = MarketQueryPlanner.Build(tax, sel2);
            Eq("M4 查价键物品数 = 1", key2.ItemCount, 1);
            Eq("M4 查价键 uniqueName 含附魔", string.Join(",", key2.UniqueNames.ToArray()), "T5_HEAD_CLOTH_SET1@1");
            Eq("M4 查价键品质全集 = 1..5", string.Join(",", key2.Qualities.ToArray()), "1,2,3,4,5");
            Eq("M4 查价键格子数 = 1×6×5", key2.CellCount(6), 30);

            // 品质收窄：只查指定品质
            sel2.Qualities.Clear();
            sel2.Qualities.Add(3);
            var key3 = MarketQueryPlanner.Build(tax, sel2);
            Eq("M4 品质收窄影响键", string.Join(",", key3.Qualities.ToArray()), "3");
            Eq("M4 品质收窄后格子数 = 1×6×1", key3.CellCount(6), 6);

            // 签名稳定：同一组合签名相同（缓存命中依据）
            var selA = new MarketSelection();
            selA.SelectCategory("head"); selA.SelectSubCategory("cloth_helmet");
            selA.SelectFamily("HEAD_CLOTH_SET1"); selA.SelectVariant(5, 1);
            var selB = new MarketSelection();
            selB.SelectCategory("head"); selB.SelectSubCategory("cloth_helmet");
            selB.SelectFamily("HEAD_CLOTH_SET1"); selB.SelectVariant(5, 1);
            Eq("M4 同一筛选签名一致", MarketQueryPlanner.Build(tax, selA).Signature,
               MarketQueryPlanner.Build(tax, selB).Signature);

            // 组合不存在 → 空键（M9）
            var selBad = new MarketSelection();
            selBad.SelectCategory("weapons"); selBad.SelectSubCategory("bow");
            selBad.SelectFamily("HEAD_CLOTH_SET1"); selBad.SelectVariant(5, 0);
            Check("M4 组合不存在产空键（M9）", MarketQueryPlanner.Build(tax, selBad).IsEmpty, "expected empty");

            // 不存在的附魔等级（T4 没有 @3）→ 空键，不臆造
            var selNoEnch = new MarketSelection();
            selNoEnch.SelectCategory("head"); selNoEnch.SelectSubCategory("cloth_helmet");
            selNoEnch.SelectFamily("HEAD_CLOTH_SET1"); selNoEnch.SelectVariant(4, 3);
            Check("M4 不存在的附魔等级产空键", MarketQueryPlanner.Build(tax, selNoEnch).IsEmpty, "expected empty");

            // 面包屑三段
            var crumbs = MarketQueryPlanner.Breadcrumbs(tax, sel2);
            Eq("M4 面包屑一级", crumbs[0], "头部护甲");
            Eq("M4 面包屑二级", crumbs[1], "布甲风帽");
            Eq("M4 面包屑三级", crumbs[2], "T5 · 附魔 .1");
            var crumbsNone = MarketQueryPlanner.Breadcrumbs(tax, new MarketSelection());
            Check("M4 未选时面包屑为空", crumbsNone[0] == null && crumbsNone[1] == null && crumbsNone[2] == null,
                  "expected all null");

            // 空索引 → 空树（UI 显式告警依据）
            var emptyTax = ItemTaxonomy.Build(null);
            Eq("M4 索引为 null 时分类树为空", emptyTax.Categories.Count, 0);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    public static int Main()
    {
        Console.WriteLine("=== Albion Companion logic tests ===");

        // A2 收益台账
        var ledger = new IncomeLedger();
        var t0 = new DateTime(2026, 11, 5, 20, 0, 0);
        ledger.Add("采集", "T4 木材", 10, 50, t0);
        ledger.Add("采集", "T5 矿石", 1, 2000, t0.AddMinutes(5));
        ledger.Add("交易", "T4 背包", 3, 100, t0.AddMinutes(9));

        Eq("A2 合计 = 2800", ledger.Total, 2800L);
        Eq("A2 明细条数 = 3", ledger.Items.Count, 3);
        Eq("A2 最新条目在最前", ledger.Items[0].Item, "T4 背包");
        var cats = ledger.TotalsByCategory();
        Eq("A2 采集分类合计 = 2500", cats["采集"], 2500L);
        Eq("A2 交易分类合计 = 300", cats["交易"], 300L);

        string csv = Path.Combine(Path.GetTempPath(), "ac_csv_test.csv");
        if (File.Exists(csv)) File.Delete(csv);
        ledger.ExportCsv(csv);
        string content = File.ReadAllText(csv, Encoding.UTF8);
        string[] lines = content.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        Eq("A2 CSV 行数 = 条数 + 1", lines.Length, 4);
        Check("A2 CSV 含表头", lines[0].Contains("时间") && lines[0].Contains("分类") && lines[0].Contains("小计"), lines[0]);
        Check("A2 CSV 含明细", content.Contains("T5 矿石"), "missing row");

        ledger.Clear();
        Eq("A2 清空后合计 = 0", ledger.Total, 0L);

        // A2 会话时钟
        Eq("A2 时长 63分30秒", SessionClock.Format(TimeSpan.FromSeconds(3810)), "1:03:30");
        Eq("A2 时长 90秒", SessionClock.Format(TimeSpan.FromSeconds(90)), "01:30");
        Eq("A2 时长 0", SessionClock.Format(TimeSpan.Zero), "00:00");
        Eq("A2 30分钟时薪 = 5600", SessionClock.PerHour(2800, TimeSpan.FromMinutes(30)), 5600.0);
        Eq("A2 不足1分钟时薪 = 0", SessionClock.PerHour(2800, TimeSpan.FromSeconds(10)), 0.0);

        // A4 市场
        string url = MarketClient.BuildPriceUrl(new[] { "T4_BAG", "T5_2H_BOW@2" }, new[] { "Caerleon", "Bridgewatch" });
        // P2-6：物品名按 RFC 3986 完整转义（'@' → %40），估算与实际同源
        Check("A4 URL 含物品列表", url.Contains("T4_BAG,T5_2H_BOW%402"), url);
        Check("A4 URL 含城市参数", url.Contains("locations=Caerleon,Bridgewatch"), url);
        Check("A4 URL 指向 AODP east 基址（M1 定案：亚服）",
            url.StartsWith(AodpServers.East + "stats/prices/"), url);
        Eq("A4 默认基址 = east", MarketClient.ServerBaseUrl, AodpServers.East);

        // M1 429 退避（RateLimitBackoff，纯函数）
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000L);
        Eq("M1 有 Reset 头：睡到该 epoch 时刻",
            RateLimitBackoff.ComputeDelay(0, "1800000060", now), TimeSpan.FromSeconds(60));
        Eq("M1 Reset 头早于当前时间：clamp 下限 1s",
            RateLimitBackoff.ComputeDelay(0, "1799999999", now), RateLimitBackoff.MinDelay);
        Eq("M1 Reset 头超上限：clamp 15min",
            RateLimitBackoff.ComputeDelay(0, "1800003600", now), RateLimitBackoff.MaxDelay);
        Eq("M1 无 Reset 头：第 1 次退避 30s",
            RateLimitBackoff.ComputeDelay(0, null, now), TimeSpan.FromSeconds(30));
        Eq("M1 无 Reset 头：第 2 次退避 60s",
            RateLimitBackoff.ComputeDelay(1, "", now), TimeSpan.FromSeconds(60));
        Eq("M1 无 Reset 头：第 3 次退避 120s",
            RateLimitBackoff.ComputeDelay(2, "not-a-number", now), TimeSpan.FromSeconds(120));
        Eq("M1 退避次数上限 3", RateLimitBackoff.MaxRetries, 3);
        Eq("M1 非法 Reset 头走指数退避",
            RateLimitBackoff.ComputeDelay(0, "abc", now), TimeSpan.FromSeconds(30));

        var quotes = MarketClient.Parse(SampleJson);
        Eq("A4 解析条数 = 1（全零行被剔除）", quotes.Count, 1);
        Eq("A4 城市", quotes[0].City, "Caerleon");
        Eq("A4 最低卖价", quotes[0].SellMin, 7493L);
        Eq("A4 最高买价", quotes[0].BuyMax, 6999L);

        // A5 配装成本
        var slots = new List<LoadoutSlot>
        {
            new LoadoutSlot { Name = "主手", Cost = 100000 },
            new LoadoutSlot { Name = "副手", Cost = 250000 }
        };
        Eq("A5 总成本 = 350000", BuildCost.Total(slots), 350000L);
        Eq("A5 掉落7000回本 = 50 次", BuildCost.BreakEvenKills(350000, 7000), 50);
        Eq("A5 掉落为0回本 = 0", BuildCost.BreakEvenKills(350000, 0), 0);

        // A6 设置持久化
        var path = Path.Combine(Path.GetTempPath(), "ac_settings_test.json");
        if (File.Exists(path)) File.Delete(path);
        var s = new AppSettings();
        s.AlwaysOnTop = false;
        s.OpacityPercent = 78;
        s.Hotkey = "Ctrl+Alt+Q";
        SettingsStore.Save(path, s);
        var loaded = SettingsStore.Load(path);
        Eq("A6 置顶持久化", loaded.AlwaysOnTop, false);
        Eq("A6 透明度持久化", loaded.OpacityPercent, 78);
        Eq("A6 热键持久化", loaded.Hotkey, "Ctrl+Alt+Q");
        var defaults = SettingsStore.Load(Path.Combine(Path.GetTempPath(), "ac_missing_settings.json"));
        Eq("A6 缺文件回退默认置顶", defaults.AlwaysOnTop, true);
        Eq("A6 缺文件回退透明度", defaults.OpacityPercent, 92);

        // A8 物品图标 / 离线解析（M3 后 HTML 抓取已删，物品元数据走 ItemIndex 本地索引）
        string iconUrl = ItemCatalog.BuildIconUrl("T4_BAG", 3);
        Eq("A8 图标URL", iconUrl, "https://render.albiononline.com/v1/item/T4_BAG.png?quality=3");

        var names = new Dictionary<string, string>();
        var resolved = ItemCatalog.ResolveTermsOffline("T4_BAG, T5_2H_BOW@2", names, new NameCache());
        Eq("A8 英文ID原样保留", string.Join(",", resolved.ToArray()), "T4_BAG,T5_2H_BOW");
        Check("A8 不含中文时无解析结果", names.Count == 0, "names=" + names.Count);

        // A9 筛选维度解析 + 本地模糊搜索
        Eq("A9 等阶解析", ItemCatalog.TierOf("T5_2H_BOW@2"), 5);
        Eq("A9 附魔解析", ItemCatalog.EnchantOf("T5_2H_BOW@2"), 2);
        Eq("A9 无附魔默认0", ItemCatalog.EnchantOf("T4_BAG"), 0);
        Eq("A9 类别=背包", ItemCatalog.CategoryOf("T4_BAG"), "背包");
        Eq("A9 类别=材料", ItemCatalog.CategoryOf("T4_ORE"), "材料");
        Eq("A9 类别=武器", ItemCatalog.CategoryOf("T5_2H_BOW"), "武器");
        Eq("A9 类别=头部护甲", ItemCatalog.CategoryOf("T4_LEATHER_HELMET"), "头部护甲");
        Eq("A9 类别=身体护甲", ItemCatalog.CategoryOf("T4_PLATE_ARMOR"), "身体护甲");
        Eq("A9 类别=披风", ItemCatalog.CategoryOf("T8_CAPE"), "披风");
        Eq("A9 类别=采集装备", ItemCatalog.CategoryOf("T4_TOOL_SICKLE"), "采集装备");

        // A12 三级分类联动
        var l1 = ItemCatalog.Taxonomy.L1List();
        Check("A12 L1 含武器", Array.IndexOf(l1, "武器") >= 0, string.Join(",", l1));
        var l2 = ItemCatalog.Taxonomy.L2List("武器");
        Check("A12 武器子类含弓", Array.IndexOf(l2, "弓") >= 0, string.Join(",", l2));
        Eq("A12 弓关键词", ItemCatalog.Taxonomy.KeywordFor("武器", "弓"), "BOW");
        Check("A12 全部子类关键词为空", ItemCatalog.Taxonomy.KeywordFor("武器", "全部") == null, "not-null");
        var l3 = ItemCatalog.Taxonomy.L3List("武器", "弓");
        Check("A12 弓子类含长弓", Array.IndexOf(l3, "长弓") >= 0, string.Join(",", l3));
        Eq("A9 类别=坐骑", ItemCatalog.CategoryOf("T6_MOUNT_HORSE"), "坐骑");
        Eq("A9 类别=消耗品", ItemCatalog.CategoryOf("T4_POTION_HEAL"), "消耗品");

        var seed = new NameCache();
        seed.Add("T4_BAG", "老手级背包");
        seed.Add("T5_BAG", "专家级背包");
        seed.Add("T4_ORE", "铁矿");
        var fuzzy = seed.FuzzyIds("背包", 10);
        Check("A9 本地模糊命中背包", fuzzy.Count == 2 && fuzzy.Contains("T4_BAG") && fuzzy.Contains("T5_BAG"),
              string.Join(",", fuzzy.ToArray()));
        var fuzzyNone = seed.FuzzyIds("不存在的东西", 10);
        Eq("A9 本地模糊无命中", fuzzyNone.Count, 0);

        var cachePath = Path.Combine(Path.GetTempPath(), "ac_namecache.json");
        if (File.Exists(cachePath)) File.Delete(cachePath);
        seed.Save(cachePath);
        var cacheLoaded = NameCache.Load(cachePath);
        Eq("A9 名称缓存持久化", cacheLoaded.Map["T4_BAG"], "老手级背包");

        var offNames = new Dictionary<string, string>();
        var fuzzyResolve = ItemCatalog.ResolveTermsOffline("背包", offNames, seed);
        Check("A9 离线模糊解析出物品ID", fuzzyResolve.Count == 2 && fuzzyResolve.Contains("T4_BAG"),
              string.Join(",", fuzzyResolve.ToArray()));

        // A10 本地行情库
        var db = new MarketDb();
        db.Upsert(new MarketQuote { ItemId = "T4_BAG", City = "Caerleon", Quality = 1,
                                    SellMin = 7000, SellMinDate = "2026-11-04T14:00:00",
                                    BuyMax = 6000, BuyMaxDate = "2026-11-04T13:00:00" });
        db.Upsert(new MarketQuote { ItemId = "T4_BAG", City = "Bridgewatch", Quality = 1,
                                    SellMin = 3000, SellMinDate = "2026-11-04T15:00:00",
                                    BuyMax = 0, BuyMaxDate = "" });
        Eq("A10 初始行数", db.Rows.Count, 2);

        // 同 (物品,城市,品质) 更新而非追加
        db.Upsert(new MarketQuote { ItemId = "T4_BAG", City = "Caerleon", Quality = 1,
                                    SellMin = 7500, SellMinDate = "2026-11-04T16:00:00",
                                    BuyMax = 6500, BuyMaxDate = "2026-11-04T15:00:00" });
        Eq("A10 更新后仍 2 行", db.Rows.Count, 2);
        Eq("A10 卖价已更新", db.Find("T4_BAG", "Caerleon", 1).SellMin, 7500L);

        var rowsCaerleon = db.Query(new List<string> { "T4_BAG" }, new List<string> { "Caerleon" });
        Eq("A10 按城市过滤", rowsCaerleon.Count, 1);
        Eq("A10 已知物品", string.Join(",", db.KnownItemIds().ToArray()), "T4_BAG");

        var chunks = MarketDb.Chunk(new List<string> { "a","b","c","d","e" }, 2);
        Eq("A10 分批块数", chunks.Count, 3);
        Eq("A10 最后一块", chunks[2].Count, 1);

        string dbPath = Path.Combine(Path.GetTempPath(), "ac_marketdb.csv");
        if (File.Exists(dbPath)) File.Delete(dbPath);
        db.Save(dbPath);
        var loadedDb = MarketDb.Load(dbPath);
        Eq("A10 持久化行数", loadedDb.Rows.Count, 2);
        Eq("A10 持久化卖价", loadedDb.Find("T4_BAG", "Caerleon", 1).SellMin, 7500L);

        // A11 跑商套利：卖税8% + 创建费2.5%
        // 卖价 10000 → 净收 10000*(1-0.08-0.025)=8950
        // 买价 6000（挂收购单）→ 成本 6000*(1+0.025)=6150
        // 单件净利 = 8950 - 6150 = 2800
        var arbQuotes = new List<MarketQuote>
        {
            new MarketQuote { ItemId = "T4_BAG", City = "Caerleon", Quality = 1, SellMin = 10000, BuyMax = 2000 },
            new MarketQuote { ItemId = "T4_BAG", City = "Bridgewatch", Quality = 1, SellMin = 15000, BuyMax = 6000 },
            new MarketQuote { ItemId = "T4_FOOD", City = "Caerleon", Quality = 1, SellMin = 5000, BuyMax = 4800 },
            new MarketQuote { ItemId = "T4_FOOD", City = "Bridgewatch", Quality = 1, SellMin = 5200, BuyMax = 4700 },
        };
        var routes = Arbitrage.Compute(arbQuotes, 0.08, 0.025, 0);
        Check("A11 出一条有效路线", routes.Count == 1, "count=" + routes.Count);
        if (routes.Count == 1)
        {
            Eq("A11 买价取最高收购", routes[0].BuyPrice, 6000L);
            Eq("A11 卖价取最低挂卖", routes[0].SellPrice, 10000L);
            Eq("A11 单件净利", routes[0].NetProfit, 2800L);
            Check("A11 卖城=Caerleon", routes[0].SellCity == "Caerleon", routes[0].SellCity);
            Check("A11 买城=Bridgewatch", routes[0].BuyCity == "Bridgewatch", routes[0].BuyCity);
        }

        // 同城里没有差价路线
        var sameCity = new List<MarketQuote>
        {
            new MarketQuote { ItemId = "X", City = "Caerleon", Quality = 1, SellMin = 10000, BuyMax = 9000 },
        };
        Eq("A11 单城无路线", Arbitrage.Compute(sameCity, 0.08, 0.025, 0).Count, 0);


        // ---------------------------------------------------------------
        // A13 P0-3 陈旧报价不得参与选价（巡检 2026-11-09 第 3 项 P0）
        // 缺陷原状：Arbitrage.cs:47-48 只看 q.SellMin > 0 就选价，
        // SellHasData / SellStale 在整个 Arbitrage.cs 零命中 → 上月死价照样算出正净利。
        // 修复口径：选价前按原始时间戳重算 24h 陈旧，陈旧点剔除并计入 droppedStale。
        // ---------------------------------------------------------------
        {
            // 注入固定 arbNow：避免用 DateTimeOffset.UtcNow 造成跨日抖动
            var arbNow = new DateTimeOffset(2026, 11, 9, 6, 40, 0, TimeSpan.Zero);
            int dropped;

            // ① 卖侧陈旧：SellMin=9000 但 SellMinDate=arbNow-30d。唯一能赚钱的卖城是死价，
            //    该物品必须不产出任何路线（巡检明确要求的复验用例）。
            var staleSell = new List<MarketQuote>
            {
                new MarketQuote { ItemId = "T4_STALESELL", City = "Caerleon", Quality = 1,
                                  SellMin = 9000, SellMinDate = arbNow.AddDays(-30).ToString("yyyy-MM-ddTHH:mm:ss"),
                                  BuyMax = 0,    BuyMaxDate = "" },
                new MarketQuote { ItemId = "T4_STALESELL", City = "Bridgewatch", Quality = 1,
                                  SellMin = 0,   SellMinDate = "",
                                  BuyMax = 6000, BuyMaxDate = arbNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss") },
            };
            var staleSellRoutes = Arbitrage.Compute(staleSell, 0.08, 0.025, 0, arbNow, out dropped);
            Eq("A13 陈旧卖价（arbNow-30d）不产出路线", staleSellRoutes.Count, 0);
            Eq("A13 陈旧卖价计入 droppedStale", dropped, 1);

            // ② 买侧陈旧：BuyMax 很高但 BuyMaxDate=arbNow-30d，同样不得选为买城
            var staleBuy = new List<MarketQuote>
            {
                new MarketQuote { ItemId = "T4_STALEBUY", City = "Caerleon", Quality = 1,
                                  SellMin = 9000, SellMinDate = arbNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss"),
                                  BuyMax = 0,     BuyMaxDate = "" },
                new MarketQuote { ItemId = "T4_STALEBUY", City = "Bridgewatch", Quality = 1,
                                  SellMin = 0,    SellMinDate = "",
                                  BuyMax = 6000,  BuyMaxDate = arbNow.AddDays(-30).ToString("yyyy-MM-ddTHH:mm:ss") },
            };
            var staleBuyRoutes = Arbitrage.Compute(staleBuy, 0.08, 0.025, 0, arbNow, out dropped);
            Eq("A13 陈旧买价（arbNow-30d）不产出路线", staleBuyRoutes.Count, 0);
            Eq("A13 陈旧买价计入 droppedStale", dropped, 1);

            // ③ 边界：恰好 24h 内（arbNow-23h）仍算新鲜，必须正常出路线
            var freshEdge = new List<MarketQuote>
            {
                new MarketQuote { ItemId = "T4_EDGE", City = "Caerleon", Quality = 1,
                                  SellMin = 10000, SellMinDate = arbNow.AddHours(-23).ToString("yyyy-MM-ddTHH:mm:ss"),
                                  BuyMax = 0,      BuyMaxDate = "" },
                new MarketQuote { ItemId = "T4_EDGE", City = "Bridgewatch", Quality = 1,
                                  SellMin = 0,     SellMinDate = "",
                                  BuyMax = 6000,   BuyMaxDate = arbNow.AddHours(-23).ToString("yyyy-MM-ddTHH:mm:ss") },
            };
            var freshEdgeRoutes = Arbitrage.Compute(freshEdge, 0.08, 0.025, 0, arbNow, out dropped);
            Eq("A13 23h 内报价仍新鲜：出路线", freshEdgeRoutes.Count, 1);
            Eq("A13 23h 内报价不产生剔除计数", dropped, 0);

            // ④ 边界：恰好超过 24h（arbNow-25h）必须剔除
            var staleEdge = new List<MarketQuote>
            {
                new MarketQuote { ItemId = "T4_EDGE2", City = "Caerleon", Quality = 1,
                                  SellMin = 10000, SellMinDate = arbNow.AddHours(-25).ToString("yyyy-MM-ddTHH:mm:ss"),
                                  BuyMax = 0,      BuyMaxDate = "" },
                new MarketQuote { ItemId = "T4_EDGE2", City = "Bridgewatch", Quality = 1,
                                  SellMin = 0,     SellMinDate = "",
                                  BuyMax = 6000,   BuyMaxDate = arbNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss") },
            };
            var staleEdgeRoutes = Arbitrage.Compute(staleEdge, 0.08, 0.025, 0, arbNow, out dropped);
            Eq("A13 25h 卖价被剔除：不出路线", staleEdgeRoutes.Count, 0);
            Eq("A13 25h 卖价计入剔除计数", dropped, 1);

            // ⑤ 陈旧卖价存在、但另有新鲜卖城：必须挑新鲜那个，而不是被死价带偏
            var preferFresh = new List<MarketQuote>
            {
                new MarketQuote { ItemId = "T4_MIX", City = "Caerleon", Quality = 1,
                                  SellMin = 8000,  SellMinDate = arbNow.AddDays(-30).ToString("yyyy-MM-ddTHH:mm:ss"),
                                  BuyMax = 0,      BuyMaxDate = "" },
                new MarketQuote { ItemId = "T4_MIX", City = "Martlock", Quality = 1,
                                  SellMin = 10000, SellMinDate = arbNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss"),
                                  BuyMax = 0,      BuyMaxDate = "" },
                new MarketQuote { ItemId = "T4_MIX", City = "Bridgewatch", Quality = 1,
                                  SellMin = 0,     SellMinDate = "",
                                  BuyMax = 6000,   BuyMaxDate = arbNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss") },
            };
            var mixRoutes = Arbitrage.Compute(preferFresh, 0.08, 0.025, 0, arbNow, out dropped);
            Eq("A13 混合场景：仍按新鲜价出 1 条路线", mixRoutes.Count, 1);
            if (mixRoutes.Count == 1)
                Check("A13 混合场景：卖城取新鲜城 Martlock 而非死价城 Caerleon",
                      mixRoutes[0].SellCity == "Martlock", mixRoutes[0].SellCity);
            Eq("A13 混合场景：陈旧卖价计入剔除计数", dropped, 1);

            // ⑥ 无时间戳（空串 / 0001 哨兵）不判陈旧 —— 与 MarketClient.IsSellStale 既有口径一致，
            //    避免把「CSV 无日期列」的历史本地库整行清空（A11 的四条无日期报价必须继续有效）
            var noDate = new List<MarketQuote>
            {
                new MarketQuote { ItemId = "T4_NODATE", City = "Caerleon", Quality = 1,
                                  SellMin = 10000, SellMinDate = "", BuyMax = 0, BuyMaxDate = "" },
                new MarketQuote { ItemId = "T4_NODATE", City = "Bridgewatch", Quality = 1,
                                  SellMin = 0, SellMinDate = "", BuyMax = 6000,
                                  BuyMaxDate = "0001-01-01T00:00:00" },
            };
            var noDateRoutes = Arbitrage.Compute(noDate, 0.08, 0.025, 0, arbNow, out dropped);
            Eq("A13 无时间戳不判陈旧：仍出路线", noDateRoutes.Count, 1);
            Eq("A13 无时间戳不产生剔除计数", dropped, 0);

            // ⑦ 原四参重载（不注入 arbNow）行为与带计数重载一致：A11 的既有断言
            //    在真实时钟下不能被陈旧逻辑破坏（arbQuotes 无日期 → 不判陈旧）
            Eq("A13 四参重载仍可用（无日期报价不误杀）",
               Arbitrage.Compute(arbQuotes, 0.08, 0.025, 0).Count, 1);

            // ⑧ 判定内核单点校验
            Check("A13 陈旧判定：arbNow-30d = 陈旧", Arbitrage.IsQuoteDateStale(
                  arbNow.AddDays(-30).ToString("yyyy-MM-ddTHH:mm:ss"), arbNow), "expected stale");
            Check("A13 陈旧判定：arbNow-23h = 新鲜", !Arbitrage.IsQuoteDateStale(
                  arbNow.AddHours(-23).ToString("yyyy-MM-ddTHH:mm:ss"), arbNow), "expected fresh");
            Check("A13 陈旧判定：空串不判陈旧", !Arbitrage.IsQuoteDateStale("", arbNow), "expected fresh");
            Check("A13 陈旧判定：0001 哨兵不判陈旧",
                  !Arbitrage.IsQuoteDateStale("0001-01-01T00:00:00", arbNow), "expected fresh");
        }


        // ---------------------------------------------------------------
        // T-16 R3 并发烟雾：4 线程各写 10 万条 + 1 线程并发 Snapshot 10 万次
        // 断言：不抛异常、快照条数单调不减、最终条数 == 500（按上限裁剪后的值）
        // 说明：本条不需要实机，纯托管线程即可判定锁与快照是否真的生效。
        // ---------------------------------------------------------------
        {
            var snapshotCounts = new List<int>();
            var snapshotErrors = new List<string>();
            var writeErrors = new List<string>();
            const int PerThread = 100000;
            const int Writers = 4;
            const int SnapshotCalls = 100000;

            var snapLock = new object();
            var writers = new List<System.Threading.Thread>();
            var stop = false;

            for (int w = 0; w < Writers; w++)
            {
                int wid = w;
                var t = new System.Threading.Thread(delegate ()
                {
                    try
                    {
                        for (int i = 0; i < PerThread; i++)
                        {
                            GameEvents.AddCombat(new GameEvents.CombatEntry
                            {
                                Time = DateTime.Now,
                                Target = "t" + wid,
                                Damage = 1,
                                Type = "damage",
                                CauserName = "w" + wid
                            });
                        }
                    }
                    catch (Exception ex) { lock (snapLock) { writeErrors.Add(ex.GetType().Name + ": " + ex.Message); } }
                });
                t.IsBackground = true;
                writers.Add(t);
            }

            var snapper = new System.Threading.Thread(delegate ()
            {
                try
                {
                    int last = 0;
                    for (int i = 0; i < SnapshotCalls; i++)
                    {
                        var arr = GameEvents.SnapshotCombat();
                        // 快照内的元素必须可读（不允许拿到半构造数组）
                        if (arr.Length > 0 && arr[arr.Length - 1] == null)
                            lock (snapLock) { snapshotErrors.Add("快照末尾出现 null 元素"); }
                        lock (snapLock)
                        {
                            if (arr.Length < last) snapshotErrors.Add("快照条数回退 " + last + " -> " + arr.Length);
                            last = arr.Length;
                            snapshotCounts.Add(arr.Length);
                        }
                    }
                }
                catch (Exception ex) { lock (snapLock) { snapshotErrors.Add(ex.GetType().Name + ": " + ex.Message); } }
                finally { stop = true; }
            });
            snapper.IsBackground = true;

            foreach (var t in writers) t.Start();
            snapper.Start();
            foreach (var t in writers) t.Join();
            snapper.Join();

            Check("T-16 写入线程无异常", writeErrors.Count == 0, string.Join(" | ", writeErrors.ToArray()));
            Check("T-16 快照线程无异常", snapshotErrors.Count == 0, string.Join(" | ", snapshotErrors.ToArray()));
            Check("T-16 快照条数单调不减", snapshotErrors.FindAll(e => e.Contains("回退")).Count == 0,
                  string.Join(" | ", snapshotErrors.ToArray()));
            Eq("T-16 最终条数按上限裁剪 = 500", GameEvents.CountCombat(), 500);
            Check("T-16 快照次数达到 10 万", snapshotCounts.Count == SnapshotCalls, "count=" + snapshotCounts.Count);

            // 三个集合的并发写同样跑一遍（更短的量，只验证不抛）
            var moreErrors = new List<string>();
            var tt = new System.Threading.Thread(delegate ()
            {
                try
                {
                    for (int i = 0; i < 20000; i++)
                    {
                        GameEvents.AddIncome(new GameEvents.IncomeEntry { Time = DateTime.Now, Silver = 1, Source = "x" });
                        GameEvents.AddGathering(new GameEvents.GatheringEntry { Time = DateTime.Now, Resource = "r", Amount = 1, Fame = 0 });
                        GameEvents.TryUpdateLastGatheringFame(i);
                    }
                }
                catch (Exception ex) { lock (snapLock) { moreErrors.Add(ex.GetType().Name + ": " + ex.Message); } }
            });
            var rt = new System.Threading.Thread(delegate ()
            {
                try
                {
                    for (int i = 0; i < 20000; i++)
                    {
                        GameEvents.SnapshotIncome();
                        GameEvents.SnapshotGathering();
                        long inc = IncomeTracker.GetSummary().ConfirmedTotal;
                        int c = GameEvents.CountIncome();
                        var byTarget = GameEvents.DamageByTarget;
                        var byCauser = GameEvents.DamageByCauser;
                    }
                }
                catch (Exception ex) { lock (snapLock) { moreErrors.Add(ex.GetType().Name + ": " + ex.Message); } }
            });
            tt.Start(); rt.Start(); tt.Join(); rt.Join();
            Check("T-16 收益/采集集合并发读写无异常", moreErrors.Count == 0, string.Join(" | ", moreErrors.ToArray()));
            Eq("T-16 收益条数按上限裁剪 = 500", GameEvents.CountIncome(), 500);
        }


        // ---------------------------------------------------------------
        // T-01 R0 OperationResponse 解析（可注入字节的内核）
        // 布局：opCode(1B) + returnCode(2B LE) + debugMessage(1B长度=0) + 参数表
        // 参数表：size(1B) + N × [key(1B) + typeCode(1B) + value]
        //   key=0 -> Long2(17) 存 UserObjectId；key=2 -> String(7) 存 Username
        // ---------------------------------------------------------------
        {
            // 构造辅助：opCode + returnCode + ObjectId + Name
            Func<byte, short, long, string, byte[]> build = (op, ret, objId, name) =>
            {
                var bytes = new List<byte>();
                bytes.Add(op);
                bytes.Add((byte)(ret & 0xFF));
                bytes.Add((byte)((ret >> 8) & 0xFF));
                bytes.Add(0x08);                                     // debugMessage 类型码 = Null
                bytes.Add((byte)(string.IsNullOrEmpty(name) ? 1 : 2)); // 参数表条数
                bytes.Add(0x00); bytes.Add(17);                      // key=0, Long2（UShort = 2 字节 LE）
                bytes.Add((byte)(objId & 0xFF));
                bytes.Add((byte)((objId >> 8) & 0xFF));
                if (!string.IsNullOrEmpty(name))
                {
                    bytes.Add(0x02); bytes.Add(7);                   // key=2, String
                    bytes.Add((byte)name.Length);
                    foreach (char c in name) bytes.Add((byte)c);
                }
                return bytes.ToArray();
            };

            // 合法响应字节：opCode 取候选值 42、returnCode=0、ObjectId=1001、Name="AB"
            byte[] valid = build(42, 0, 1001, "AB");
            long parsedId; string parsedName;

            // ---- 关键：白名单为空（默认值）时，任何响应都不得确认身份 ----
            // 这是 P0-1 修复的保守兜底：真实 Join opCode 未经实机取证前，宁可完全不确认。
            PhotonParser.JoinResponseOperationCode = null;
            bool okDefault = false;
            try { okDefault = PhotonParser.TryParseJoinResponse(new ReadOnlySpan<byte>(valid), out parsedId, out parsedName); }
            catch { okDefault = false; }
            Check("T-01 白名单为空：合法的 42 码响应也不确认身份", !okDefault, "okDefault=true");

            // ---- 设定权威码值后才做精确匹配 ----
            // 说明：42 仅为单测构造值，不代表真实 JoinResponse 码；真实码值由 V1 实机取证填入。
            PhotonParser.JoinResponseOperationCode = 42;
            long parsedId2; string parsedName2;
            bool ok = PhotonParser.TryParseJoinResponse(new ReadOnlySpan<byte>(valid), out parsedId2, out parsedName2);
            Check("T-01 精确匹配：白名单=42 时 42 码响应被识别为 JoinResponse", ok, "ok=false");
            Eq("T-01 ObjectId 解析 = 1001", ok ? parsedId2 : 0, 1001L);
            Eq("T-01 Username 解析 = AB", ok ? parsedName2 : null, "AB");

            long id2; string name2;

            // ---- 回归 1：opCode=0（解析失败/默认值的退化入口）必须 false ----
            // 旧的 `== 0` 判定其实也拒绝 0，这条继续守住「0 永远不是 Join」。
            byte[] zeroOp = build(0, 0, 1001, "AB");
            bool okZero = true;
            try { okZero = PhotonParser.TryParseJoinResponse(new ReadOnlySpan<byte>(zeroOp), out id2, out name2); }
            catch { okZero = false; }
            Check("T-01 opCode=0 不视为 JoinResponse（退化路径封堵）", !okZero, "okZero=true");

            // ---- 回归 2：任意非零非 Join 码必须 false（删除「非零即接受」） ----
            // 这是本次 P0-1 的核心回归：旧代码会让这条本应被拒的响应确认身份，
            // 进而因身份冲突触发 DamageTracker.RemovePlayer(0)，把用户自己的伤害清零。
            byte[] foreignOp = build(60, 0, 1001, "AB");
            bool okForeign = true;
            try { okForeign = PhotonParser.TryParseJoinResponse(new ReadOnlySpan<byte>(foreignOp), out id2, out name2); }
            catch { okForeign = false; }
            Check("T-01 非零非 Join 码（60 vs 白名单 42）不确认身份", !okForeign, "okForeign=true");

            // ---- 回归 3：换一个白名单值，同一批字节的判定方向必须随之翻转（证明是精确等值，不是范围放宽） ----
            PhotonParser.JoinResponseOperationCode = 60;
            bool okSwapped = false;
            try { okSwapped = PhotonParser.TryParseJoinResponse(new ReadOnlySpan<byte>(valid), out id2, out name2); }
            catch { okSwapped = false; }
            Check("T-01 白名单=60 时 42 码响应不再确认（精确等值而非范围匹配）", !okSwapped, "okSwapped=true");
            PhotonParser.JoinResponseOperationCode = 42;

            // ---- 回归 4：码值匹配但服务器掉失败码（ReturnCode != 0）仍不确认 ----
            byte[] retFail = build(42, 3, 1001, "AB");
            bool okRet = true;
            try { okRet = PhotonParser.TryParseJoinResponse(new ReadOnlySpan<byte>(retFail), out id2, out name2); }
            catch { okRet = false; }
            Check("T-01 ReturnCode!=0（失败响应）不确认身份", !okRet, "okRet=true");

            // ---- 回归 5：参数表为空（无 UserObjectId） ----
            byte[] emptyParams = new byte[] { 42, 0x00, 0x00, 0x08, 0x00 };
            bool ok3 = false;
            try { ok3 = PhotonParser.TryParseJoinResponse(new ReadOnlySpan<byte>(emptyParams), out id2, out name2); }
            catch { ok3 = false; }
            Check("T-01 空参数表不写身份", !ok3, "ok3=true");

            // ---- 回归 6：参数表截断（在 value 中间截断） ----
            byte[] truncated = new byte[8];
            Array.Copy(valid, truncated, 8);
            bool ok4 = false; bool threw = false;
            try { ok4 = PhotonParser.TryParseJoinResponse(new ReadOnlySpan<byte>(truncated), out id2, out name2); }
            catch { threw = true; }
            Check("T-01 截断输入：不确认身份且不抛到调用方", (!ok4) && (!threw),
                  "ok4=" + ok4 + " threw=" + threw);

            // ---- 回归 7：ObjectId <= 0 ----
            byte[] zeroId = new byte[] { 42, 0x00, 0x00, 0x08, 0x01, 0x00, 17, 0x00 };
            bool ok5 = false;
            try { ok5 = PhotonParser.TryParseJoinResponse(new ReadOnlySpan<byte>(zeroId), out id2, out name2); }
            catch { ok5 = false; }
            Check("T-01 ObjectId<=0 不写身份", !ok5, "ok5=true");

            // ---- 回归 8：有害场景实证——非 Join 响应绝不能触发身份冲突清零 ----
            // 场景：身份已确认为 1001，此时到达一条 opCode=60（非 Join）但 p0=9999 的响应。
            // 旧判定会把它当 Join → OnJoinResponse(9999) → 冲突 → RemovePlayer(0) → 自己的累计伤害清零。
            // 新判定：直接不确认，DamageTracker 与身份都原封不动。
            LocalPlayerIdentity.Reset();
            GameEvents.ResetAll();
            LocalPlayerIdentity.OnJoinResponse(1001, "Me");
            GameEvents.DamageTracker.RecordDamage(0, "Me", 5001, 1000, -1, true);
            Eq("T-01 前置：自己 key=0 累计伤害已就位", GameEvents.DamageTracker.GetTotals(
                  AlbionCompanion.Core.DamageMeter.DamageStatsTracker.DamageFilter.All).TotalDamage, 1000L);

            PhotonParser.JoinResponseOperationCode = 42;
            byte[] foreignConflict = build(60, 0, 9999, "SomeoneElse");
            bool okConflict = false;
            try { okConflict = PhotonParser.TryParseJoinResponse(new ReadOnlySpan<byte>(foreignConflict), out id2, out name2); }
            catch { okConflict = false; }
            Check("T-01 冲突场景：非 Join 响应不被确认为身份", !okConflict, "okConflict=true");
            Eq("T-01 冲突场景：身份未被改动（仍是 1001）", GameEvents.LocalPlayerObjectId, 1001L);
            Eq("T-01 冲突场景：自己的累计伤害未被打回 0", GameEvents.DamageTracker.GetTotals(
                  AlbionCompanion.Core.DamageMeter.DamageStatsTracker.DamageFilter.All).TotalDamage, 1000L);

            // 对照：真 Join 码换身份时，冲突清零逻辑本身仍生效（证明清零点是被「错误确认」引爆、不是被删掉了）
            long id3; string name3;
            LocalPlayerIdentity.OnJoinResponse(2002, "NewMe");
            Eq("T-01 对照：真 Join 换 id 时 key=0 仍被清（设计 §4.4 语义保留）",
               GameEvents.DamageTracker.GetTotals(
                   AlbionCompanion.Core.DamageMeter.DamageStatsTracker.DamageFilter.All).TotalDamage, 0L);

            // ---- 端到端：内核识别出的身份经 OnJoinResponse 写入状态机 ----
            LocalPlayerIdentity.Reset();
            IncomeTracker.Reset();
            GameEvents.ResetAll();
            PhotonParser.JoinResponseOperationCode = 42;
            bool okE2E;
            long e2eId; string e2eName;
            try { okE2E = PhotonParser.TryParseJoinResponse(new ReadOnlySpan<byte>(valid), out e2eId, out e2eName); }
            catch { okE2E = false; e2eId = 0; e2eName = null; }
            Check("T-01 端到端：合法响应确实被内核放行", okE2E, "okE2E=false");
            if (okE2E)
            {
                LocalPlayerIdentity.OnJoinResponse(e2eId, e2eName);
            }
            Eq("T-01 身份经状态机写入（GameEvents 只读转发）", GameEvents.LocalPlayerObjectId, 1001L);
            Eq("T-01 身份名写入", GameEvents.LocalPlayerName, "AB");
            Eq("T-01 确认来源 = JoinResponse", LocalPlayerIdentity.CurrentSource, ConfirmSource.JoinResponse);
            Eq("T-01 状态 = Confirmed", LocalPlayerIdentity.State, IdentityState.Confirmed);

            // 复原：把白名单恢复为「空」——生产默认值，避免测试副作用泄漏到后续用例
            PhotonParser.JoinResponseOperationCode = null;

            // ---------------------------------------------------------------
            // T-02 未确认态收益归零（R1）：身份 Unknown，喂 3 条 evt=62；
            // 已确认合计必须为 0，全部 3 条挂起
            // ---------------------------------------------------------------
            LocalPlayerIdentity.Reset();
            IncomeTracker.Reset();
            var d1 = TakeSilver(1001, 500);
            var d2 = TakeSilver(1001, 500);
            var d3 = TakeSilver(1001, 500);
            Eq("T-02 首条拾取置候选", LocalPlayerIdentity.State, IdentityState.Candidate);
            Check("T-02 三条全部挂起（Pending）",
                  d1 == TakeSilverDisposition.Pending && d2 == TakeSilverDisposition.Pending
                  && d3 == TakeSilverDisposition.Pending,
                  d1 + "/" + d2 + "/" + d3);
            var sum2 = IncomeTracker.GetSummary();
            Eq("T-02 未确认合计 = 0", sum2.ConfirmedTotal, 0L);
            Eq("T-02 未确认条数 = 3", sum2.UnconfirmedRecords, 3);
            Eq("T-02 候选拾取次数 = 3", LocalPlayerIdentity.CandidateSilverHits, 3);

            // ---------------------------------------------------------------
            // T-03 候选 + 余额严格匹配晋升（R1 方案 4）：
            // 先建余额基线（evt=81 无差值，只推进基线），再拾取 500，
            // 随后 evt=81 余额 +500 与挂起严格相等 → 晋升 Confirmed（UpdateMoney），
            // 历史记录被转正（rename）而非丢弃
            // ---------------------------------------------------------------
            LocalPlayerIdentity.Reset();
            IncomeTracker.Reset();
            LocalPlayerIdentity.OnUpdateMoney(1001, 10000); // 基线（无差值，不确认）
            Eq("T-03 基线 evt=81 不单独确认", LocalPlayerIdentity.State, IdentityState.Unknown);
            var dPend = TakeSilver(1001, 500);
            Eq("T-03 拾取置候选", LocalPlayerIdentity.State, IdentityState.Candidate);
            Check("T-03 拾取挂起", dPend == TakeSilverDisposition.Pending, dPend.ToString());
            LocalPlayerIdentity.OnUpdateMoney(1001, 10500); // 余额差 +500 == 挂起 500
            Eq("T-03 余额严格匹配晋升 Confirmed", LocalPlayerIdentity.State, IdentityState.Confirmed);
            Eq("T-03 确认来源 = UpdateMoney", LocalPlayerIdentity.CurrentSource, ConfirmSource.UpdateMoney);
            Eq("T-03 确认 ObjectId = 1001", LocalPlayerIdentity.ConfirmedObjectId, 1001L);
            var sum3 = IncomeTracker.GetSummary();
            Eq("T-03 挂起记录转正（合计 = 500）", sum3.ConfirmedTotal, 500L);
            Eq("T-03 挂起清零", sum3.UnconfirmedRecords, 0);
            var snap3 = IncomeTracker.Snapshot(10);
            Check("T-03 历史记录被保留而非丢弃",
                  snap3.Length == 1 && snap3[0].Pending == false && snap3[0].SilverAfterTax == 500,
                  "len=" + snap3.Length);
            // 晋升后同一拾取者 → CountAsMine
            var dMine = LocalPlayerIdentity.OnTakeSilver(1001, 300);
            Eq("T-03 确认后本人拾取直接入账", dMine, TakeSilverDisposition.CountAsMine);

            // ---------------------------------------------------------------
            // T-04 余额不匹配不晋升（R1）：拾取 500 后余额差 480（含分成干扰），
            // 必须仍是 Candidate 且候选未被清空
            // ---------------------------------------------------------------
            LocalPlayerIdentity.Reset();
            IncomeTracker.Reset();
            LocalPlayerIdentity.OnUpdateMoney(1001, 10000); // 基线
            TakeSilver(1001, 500);
            LocalPlayerIdentity.OnUpdateMoney(1001, 10480); // 余额差 +480 != 500
            Eq("T-04 余额不匹配仍为 Candidate", LocalPlayerIdentity.State, IdentityState.Candidate);
            Eq("T-04 候选未被清空", LocalPlayerIdentity.CandidateObjectId, 1001L);
            Eq("T-04 挂起仍在等待", IncomeTracker.GetSummary().UnconfirmedRecords, 1);
            // 紧接着一次精确匹配仍然可以晋升（候选继续等到了下一次匹配）
            TakeSilver(1001, 200);
            LocalPlayerIdentity.OnUpdateMoney(1001, 10680); // +200 == 200
            Eq("T-04 下一轮精确匹配后仍可晋升", LocalPlayerIdentity.State, IdentityState.Confirmed);

            // ---------------------------------------------------------------
            // T-05 推翻机制边界（R1 方案 4）：
            // Confirmed(1001,"A")，路人 NewCharacter(1002,"B") 不推翻；
            // 同 ObjectId 不同名（1001,"A2"）→ 推翻并进入 Candidate
            // ---------------------------------------------------------------
            LocalPlayerIdentity.Reset();
            IncomeTracker.Reset();
            LocalPlayerIdentity.OnJoinResponse(1001, "A");
            Eq("T-05 初始 Confirmed", LocalPlayerIdentity.State, IdentityState.Confirmed);
            LocalPlayerIdentity.OnNewCharacter(1002, "B");
            Eq("T-05 路人 NewCharacter 不推翻", LocalPlayerIdentity.State, IdentityState.Confirmed);
            Eq("T-05 身份保持 1001", LocalPlayerIdentity.ConfirmedObjectId, 1001L);
            LocalPlayerIdentity.OnNewCharacter(1001, "A2");
            Eq("T-05 同 ObjectId 不同名 → 推翻", LocalPlayerIdentity.State, IdentityState.Candidate);
            Eq("T-05 推翻后候选 = 1001", LocalPlayerIdentity.CandidateObjectId, 1001L);
            Check("T-05 推翻后不再自称已确认", LocalPlayerIdentity.ConfirmedObjectId == null,
                  "confirmed=" + LocalPlayerIdentity.ConfirmedObjectId);

            // 补充：手选名字命中 NewCharacter 的兜底路径（设计 §3.3 设置页行 / V4-B）
            LocalPlayerIdentity.Reset();
            IncomeTracker.Reset();
            LocalPlayerIdentity.SetManualName("MyChar");
            Eq("T-05 手选名不直接确认", LocalPlayerIdentity.State, IdentityState.Unknown);
            LocalPlayerIdentity.OnNewCharacter(2001, "MyChar");
            Eq("T-05 名字命中手选名 → 确认", LocalPlayerIdentity.State, IdentityState.Confirmed);
            Eq("T-05 确认来源 = NewCharacterMatch", LocalPlayerIdentity.CurrentSource, ConfirmSource.NewCharacterMatch);
            Eq("T-05 IsSelf 判定", LocalPlayerIdentity.IsSelf(2001), true);
            Eq("T-05 IsSelf 拒绝非本人", LocalPlayerIdentity.IsSelf(2002), false);
        }

        // ---------------------------------------------------------------
        // T-06 R2 统一 ResolveCauser：未知 causer 归档到 -1「未知玩家」，
        // 绝不落 key=0（key=0 是「我」专属，写错会把别人的输出算到我头上）
        // ---------------------------------------------------------------
        {
            LocalPlayerIdentity.Reset();
            GameEvents.DamageTracker.Clear();
            var mobs = new Dictionary<long, string>();
            var players = new Dictionary<long, string> { { 3001, "队友A" } };

            // 有 id 但两字典都查不到 → -1（可被后补认领）
            var ci1 = CauserResolver.Resolve(7777, true, mobs, players);
            Eq("T-06 未知 causer → key=-1", ci1.TrackerKey, -1L);
            Eq("T-06 显示名 = 未知玩家", ci1.DisplayName, "未知玩家");
            Check("T-06 未知玩家可计数", ci1.Countable, "countable=false");

            // 参数 6 缺失 → 同样 -1（与显式 0 语义不同）
            var ci2 = CauserResolver.Resolve(0, false, mobs, players);
            Eq("T-06 参数缺失 → key=-1", ci2.TrackerKey, -1L);

            // 显式 causer=0 → -3「未知来源」（环境/DoT，不该冒充未知玩家去认领）
            var ci3 = CauserResolver.Resolve(0, true, mobs, players);
            Eq("T-06 显式 0 → key=-3", ci3.TrackerKey, -3L);
            Eq("T-06 -3 显示名 = 未知来源", ci3.DisplayName, "未知来源");

            // 怪物 → -9 且不可计数（不进 tracker）
            mobs[8888] = "小蜘蛛";
            var ci4 = CauserResolver.Resolve(8888, true, mobs, players);
            Eq("T-06 怪物 → key=-9", ci4.TrackerKey, -9L);
            Check("T-06 怪物不可计数", !ci4.Countable, "countable=true");

            // 已知玩家 → 真实 id；本地玩家 → key=0
            var ci5 = CauserResolver.Resolve(3001, true, mobs, players);
            Eq("T-06 已知玩家 → key=真实 id", ci5.TrackerKey, 3001L);
            LocalPlayerIdentity.OnJoinResponse(2001, "Me");
            var ci6 = CauserResolver.Resolve(2001, true, mobs, players);
            Eq("T-06 本地玩家 → key=0", ci6.TrackerKey, 0L);
            Eq("T-06 key=0 显示名 = 确认名", ci6.DisplayName, "Me");
        }

        // ---------------------------------------------------------------
        // T-07 负键显示名：-1/-2/-3/-9 全部有非数字、非空的人类可读名
        // ---------------------------------------------------------------
        {
            LocalPlayerIdentity.Reset(); // T-06 末尾确认了身份，这里回到未确认态验证「我（识别中）」
            string[] negNames = new[] {
                CauserResolver.DisplayName(-1),
                CauserResolver.DisplayName(-2),
                CauserResolver.DisplayName(-3),
                CauserResolver.DisplayName(-9)
            };
            string[] expect = { "未知玩家", "未知召唤物", "未知来源", "怪物" };
            for (int i = 0; i < negNames.Length; i++)
            {
                Check("T-07 负键 " + (new[] { -1, -2, -3, -9 })[i] + " 显示名 = " + expect[i],
                      negNames[i] == expect[i], "got <" + negNames[i] + ">");
                Check("T-07 负键 " + (new[] { -1, -2, -3, -9 })[i] + " 非空非数字",
                      !string.IsNullOrEmpty(negNames[i]) && !long.TryParse(negNames[i], out _),
                      "got <" + negNames[i] + ">");
            }
            Eq("T-07 key=0 显示名 = 我（识别中）（未确认时）", CauserResolver.DisplayName(0), "我（识别中）");
        }

        // ---------------------------------------------------------------
        // T-08 击杀三态（P1-5）：newHp 恰好=0 才算最后一击；
        // 缺失（-1）与负值都不得当成击杀
        // ---------------------------------------------------------------
        {
            LocalPlayerIdentity.Reset();
            var trk = GameEvents.DamageTracker;
            trk.Clear();
            // 三种状态各打目标 500：恰好归零 / 缺失(-1) / 负值(-5)
            trk.RecordDamage(4001, "测试者", 500, 100, 0, true);     // 击杀
            trk.RecordDamage(4001, "测试者", 501, 100, -1, true);    // 血量未知 → 不算
            trk.RecordDamage(4001, "测试者", 502, 100, -5, true);    // 血量负值 → 不算（旧代码 <=0 会误计）
            Eq("T-08 仅恰好归零计 1 次击杀", trk.GetLastHitTargetCount(4001), 1);

            // 换目标再补一击归零 → 计数 +1（HashSet 按目标去重）
            trk.RecordDamage(4001, "测试者", 600, 50, 0, true);
            trk.RecordDamage(4001, "测试者", 600, 50, 0, true);
            Eq("T-08 同目标重复击杀去重", trk.GetLastHitTargetCount(4001), 2);

            // target<=0（无目标信息）不计击杀
            trk.RecordDamage(4001, "测试者", 0, 100, 0, true);
            Eq("T-08 无目标 id 不计击杀", trk.GetLastHitTargetCount(4001), 2);

            // 不存在的玩家 → 0 而非抛异常
            Eq("T-08 未注册玩家计数 = 0", trk.GetLastHitTargetCount(9999), 0);
        }

        // ---------------------------------------------------------------
        // T-09 大数字恒等式（R4/C5 前提）：GetTotals(All) 必须覆盖全部键
        // （含隐藏负键），UI 逐榜求和会漏隐藏项。
        // 注：-9 在生产路径永不进 tracker（Countable=false 门禁，§4.2）；
        // 本测试在 tracker 层直接注入 -9=20，验证「即使存在隐藏键，
        // GetTotals 也不丢」且排行榜仍排除 -9——两者合并读出 T 表口径 150。
        // ---------------------------------------------------------------
        {
            LocalPlayerIdentity.Reset();
            var trk = GameEvents.DamageTracker;
            trk.Clear();
            trk.RecordDamage(5001, "A", 100, 100, -1, true);   // A=100
            trk.RecordDamage(-1, "未知玩家", 100, 30, -1, true); // -1=30
            trk.RecordDamage(-9, "怪物", 100, 20, -1, true);    // -9=20（隐藏键）

            var all = trk.GetTotals(DamageStatsTracker.DamageFilter.All);
            Eq("T-09 GetTotals(All) 含隐藏项 = 150", all.TotalDamage, 150L);

            // 恒等式：GetTotals == Σ GetAllPlayers()（大数字同源，不允许两套口径）
            long sumAll = 0;
            foreach (var p in trk.GetAllPlayers()) sumAll += p.TotalDamage;
            Eq("T-09 恒等式 ΣGetAllPlayers = 150", sumAll, 150L);

            // 排行榜排除 -9（不进主榜），但 -1 以「未知玩家」身份上榜
            var top = trk.GetTopDamage();
            Check("T-09 主榜不含怪物(-9)", top.TrueForAll(x => x.PlayerName != "怪物"), "monster leaked");
            Check("T-09 主榜含未知玩家(-1)", top.Exists(x => x.PlayerName == "未知玩家"), "-1 missing");
            long topSum = 0;
            foreach (var x in top) topSum += x.TotalDamage;
            Eq("T-09 主榜求和 = 130（少 20 怪物伤害）", topSum, 130L);
        }

        // ---------------------------------------------------------------
        // T-10 RenameKey 合并语义（§L766 覆盖即丢的风险）：
        // -1=100 后 A=50，rename(-1→A) 后 A 必须 = 150 而非 50
        // ---------------------------------------------------------------
        {
            LocalPlayerIdentity.Reset();
            var trk = GameEvents.DamageTracker;
            trk.Clear();
            trk.RecordDamage(-1, "未知玩家", 100, 100, -1, true);   // -1 累计 100
            trk.RecordDamage(6001, "A", 100, 50, -1, true);         // A 累计 50
            // -1 曾单杀过目标 700（这一击本身 +10，并入后伤害与击杀数都应转移到 A 名下）
            trk.RecordDamage(-1, "未知玩家", 700, 10, 0, true);      // -1 累计 110
            Eq("T-10 前置 -1 击杀数 = 1", trk.GetLastHitTargetCount(-1), 1);

            bool ok = trk.RenameKey(-1, 6001, "A");
            Check("T-10 rename 成功", ok, "ok=false");
            Eq("T-10 合并相加：A=160（100+10+50，覆盖即丢的旧实现会只剩 110 或 50）",
               trk.GetAllPlayers().Find(p => p.Key == 6001).TotalDamage, 160L);
            Eq("T-10 合并后全表合计 = 160", trk.GetTotals(DamageStatsTracker.DamageFilter.All).TotalDamage, 160L);
            Check("T-10 -1 已消失", !trk.GetAllPlayers().Exists(p => p.Key == -1), "-1 still exists");
            Eq("T-10 击杀集合并集：A 继承 1 次击杀", trk.GetLastHitTargetCount(6001), 1);

            // toKey 不存在 → 整条搬迁（数值不丢）
            trk.Clear();
            trk.RecordDamage(-1, "未知玩家", 100, 80, -1, true);
            Check("T-10 搬迁到新键成功", trk.RenameKey(-1, 7001, "B"), "ok=false");
            var b = trk.GetAllPlayers().Find(p => p.Key == 7001);
            Check("T-10 新键承接全部数值", b.TotalDamage == 80 && b.Name == "B", "got " + (b.TotalDamage) + "/" + b.Name);

            // fromKey == toKey → 拒绝（防误用清零自己）
            Check("T-10 同键 rename 拒绝", !trk.RenameKey(7001, 7001, null), "ok=true");

            // fromKey 不存在 → false 而非抛异常
            Check("T-10 空 fromKey 返回 false", !trk.RenameKey(-12345, 7001, null), "ok=true");
        }

        // ---------------------------------------------------------------
        // T-14 R4 筛选与大数字一致：PvE=80+30、PvP=70（A 一人两种）；
        // GetTotals 必须随筛选切换，且「大数字 == 该筛选榜求和 + 隐藏项(-9)」；
        // 并验证 500 条截断不影响 tracker 大数字、ResetAll 三处齐清
        // ---------------------------------------------------------------
        {
            LocalPlayerIdentity.Reset();
            var trk = GameEvents.DamageTracker;
            trk.Clear();
            trk.RecordDamage(8001, "A", 100, 80, -1, true);    // A 打怪 80（PvE）
            trk.RecordDamage(8001, "A", 200, 70, -1, false);   // A 打人 70（PvP）
            trk.RecordDamage(-1, "未知玩家", 100, 30, -1, true); // -1 打怪 30（PvE，榜上可见）
            trk.RecordDamage(-9, "怪物", 100, 20, -1, true);    // -9 打怪 20（隐藏项，不进榜）

            // T-09 同口径：GetTotals 覆盖隐藏键，PvE 含 -9 的 20（80+30+20=130）
            Eq("T-14 GetTotals(Pve) = 130", trk.GetTotals(DamageStatsTracker.DamageFilter.Pve).TotalDamage, 130L);
            Eq("T-14 GetTotals(Pvp) = 70", trk.GetTotals(DamageStatsTracker.DamageFilter.Pvp).TotalDamage, 70L);
            Eq("T-14 GetTotals(All) = 200", trk.GetTotals(DamageStatsTracker.DamageFilter.All).TotalDamage, 200L);

            // 各筛选下「大数字 == 该筛选榜求和 + 隐藏项」（§9.5 L3 恒等式）
            trk.FilterMode = DamageStatsTracker.DamageFilter.Pve;
            long pveBoard = 0;
            foreach (var x in trk.GetTopDamage()) pveBoard += x.TotalDamage;
            Eq("T-14 Pve 榜求和 = 110（-1 在榜）", pveBoard, 110L);
            Eq("T-14 Pve 大数字 == 榜求和 + 隐藏20", pveBoard + 20, trk.GetTotals(DamageStatsTracker.DamageFilter.Pve).TotalDamage);
            trk.FilterMode = DamageStatsTracker.DamageFilter.All;

            // 截断证明（§6.5）：tracker 记 600 次，事件流只留 500 条，大数字必须全量
            trk.Clear();
            for (int i = 0; i < 600; i++)
            {
                trk.RecordDamage(8002, "B", 300, 1, -1, true);
                GameEvents.AddCombat(new GameEvents.CombatEntry { Time = DateTime.Now, Target = "t", Damage = 1, Type = "damage" });
            }
            Eq("T-14 tracker 600 次不截断 = 600", trk.GetTotals(DamageStatsTracker.DamageFilter.All).TotalDamage, 600L);
            Eq("T-14 事件流按 500 条截断 = 500", GameEvents.CountCombat(), 500);

            // 击杀计数器不受截断影响 + ResetAll 三处齐清（§9.5）
            for (int i = 0; i < 3; i++)
                GameEvents.AddCombat(new GameEvents.CombatEntry { Time = DateTime.Now, Target = "k", Damage = 0, Type = "kill" });
            Eq("T-14 击杀计数器 = 3", GameEvents.KillCount, 3);

            GameEvents.ResetAll();
            Eq("T-14 清空后 GetTotals(All) = 0", trk.GetTotals(DamageStatsTracker.DamageFilter.All).TotalDamage, 0L);
            Eq("T-14 清空后榜单为空", trk.GetTopDamage().Count, 0);
            Eq("T-14 清空后击杀归零", GameEvents.KillCount, 0);
            Eq("T-14 清空后事件流为空", GameEvents.CountCombat(), 0);
        }

        Console.WriteLine();
        // ---------------------------------------------------------------
        // T-11 R7 队伍增量重建：230(GUID+2名) → 234(新成员) → 29(名字反查) →
        // 239(保守更新) → 241(换队长)；断言 MemberCount、角色、id 稳定、队长唯一
        // ---------------------------------------------------------------
        {
            PartyState.Reset();
            LocalPlayerIdentity.Reset();
            var g1 = Guid.NewGuid(); var g2 = Guid.NewGuid(); var g3 = Guid.NewGuid();
            byte[] p8 = g1.ToByteArray().Concat(g2.ToByteArray()).ToArray(); // 2 个 GUID
            string[] p9 = { "laowang0729", "aYi777" };
            PartyState.OnPartyJoined(p8, p9);
            Eq("T-11 230 全量重建成员数 = 2", PartyState.MemberCount, 2);
            Check("T-11 230 后 Verified = true", PartyState.Verified, "verified=false");
            var mems = PartyState.GetMembers();
            Check("T-11 230 初始态均为 pendingObjectId",
                mems.All(m => m.Confidence == PartyConfidence.PendingObjectId),
                "有成员不是 pendingObjectId");

            // 234：g1（laowang0729）进战斗，GUID 匹配 → 补 ObjectId
            PartyState.OnPartyPlayerJoined(5001L, g1.ToByteArray());
            var lw = PartyState.GetMembers().Find(m => m.Name == "laowang0729");
            Eq("T-11 234 GUID 匹配后 ObjectId = 5001", lw.ObjectId, 5001L);
            Check("T-11 234 GUID 匹配后 confirmed", lw.Confidence == PartyConfidence.Confirmed,
                "confidence=" + lw.Confidence);

            // 234：全新成员（未知 GUID）→ pendingName 等反查
            PartyState.OnPartyPlayerJoined(5002L, g3.ToByteArray());
            Eq("T-11 234 新成员后成员数 = 3", PartyState.MemberCount, 3);
            var newGuy = PartyState.GetMembers().Find(m => m.ObjectId == 5002L);
            Check("T-11 234 新成员 pendingName", newGuy.Confidence == PartyConfidence.PendingName,
                "confidence=" + newGuy.Confidence);

            // 29：名字反查 → pendingName 补名（id 不变，仍是 5002）
            PartyState.OnNewCharacter(5002L, "BossKiller99");
            var named = PartyState.GetMembers().Find(m => m.ObjectId == 5002L);
            Eq("T-11 反查补名后仍同一 ObjectId = 5002", named.ObjectId, 5002L);
            Eq("T-11 反查补名 = BossKiller99", named.Name, "BossKiller99");
            Check("T-11 反查后 confirmed", named.Confidence == PartyConfidence.Confirmed,
                "confidence=" + named.Confidence);

            // 239：保守处理——已知名刷新 lastSeen，不新增
            DateTime before = PartyState.GetMembers().Find(m => m.Name == "aYi777").LastSeenUtc;
            System.Threading.Thread.Sleep(20);
            PartyState.OnPartyPlayerUpdated("aYi777");
            var ayi = PartyState.GetMembers().Find(m => m.Name == "aYi777");
            Eq("T-11 239 已知名刷新 lastSeen 成员数仍 3", PartyState.MemberCount, 3);
            Check("T-11 239 lastSeen 已刷新", ayi.LastSeenUtc > before, "lastSeen 未变");
            // 未知名不入名单
            PartyState.OnPartyPlayerUpdated("完全路过的陌生人");
            Eq("T-11 239 未知名不入名单成员数仍 3", PartyState.MemberCount, 3);

            // 241：换队长——ObjectId 双路兜底
            PartyState.OnPartyLeaderChanged(5001L);
            Eq("T-11 241 LeaderObjectId = 5001", PartyState.LeaderObjectId, 5001L);
            var leaders = PartyState.GetMembers().FindAll(m => m.Role == "leader");
            Eq("T-11 241 队长唯一 = 1", leaders.Count, 1);
            Eq("T-11 241 队长是 laowang0729", leaders[0].Name, "laowang0729");
        }

        // ---------------------------------------------------------------
        // T-12 R7 pending 反查三态（§9.8 L1）：唯一候选→提交；多候选→保持
        // pending；零候选→忽略不崩
        // ---------------------------------------------------------------
        {
            PartyState.Reset();
            LocalPlayerIdentity.Reset();
            var gA = Guid.NewGuid();
            byte[] p8 = gA.ToByteArray();
            // 唯一候选：一个 pendingObjectId 成员 "Solo"
            PartyState.OnPartyJoined(p8, new[] { "Solo" });
            PartyState.OnNewCharacter(7001L, "Solo");
            var solo = PartyState.GetMembers().Find(m => m.Name == "Solo");
            Eq("T-12 唯一候选提交 ObjectId = 7001", solo.ObjectId, 7001L);
            Check("T-12 唯一候选 confirmed", solo.Confidence == PartyConfidence.Confirmed,
                "confidence=" + solo.Confidence);

            // 多候选：两个同名 pendingObjectId 成员 "Dup"（同名入队属异常，但代码必须防错认）
            PartyState.Reset();
            PartyState.OnPartyJoined(null, new[] { "Dup", "Dup" });
            PartyState.OnNewCharacter(7002L, "Dup");
            var dups = PartyState.GetMembers().FindAll(m => m.Name == "Dup");
            Check("T-12 多候选保持 pendingObjectId",
                dups.All(m => m.Confidence == PartyConfidence.PendingObjectId),
                "有候选被错误提交");
            Check("T-12 多候选 HasPendingMembers = true", PartyState.HasPendingMembers, "false");

            // 零候选：路人名字 → 不新增不崩
            int before = PartyState.MemberCount;
            PartyState.OnNewCharacter(7003L, "纯路人");
            Eq("T-12 零候选不新增成员数不变", PartyState.MemberCount, before);
        }

        // ---------------------------------------------------------------
        // T-13 R7 缓存降级（§7.6/§7.2）：截断 JSON / schemaVersion=2 / 空文件
        // → 一律降级空名单 + Verified==false，不抛异常；正常文件 → 载入但
        // Verified==false（StaleFromPreviousSession）
        // ---------------------------------------------------------------
        {
            string tmp = Path.Combine(Path.GetTempPath(), "ac6-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            try
            {
                string partyCachePath = Path.Combine(tmp, "party-cache.json");

                // 正常文件：先真实走一遍落盘 → 重载
                PartyState.Reset();
                LocalPlayerIdentity.Reset();
                PartyState.SetCacheDir(tmp);
                PartyState.OnPartyJoined(Guid.NewGuid().ToByteArray(), new[] { "Alpha", "Beta" });
                PartyState.FlushCache();
                Check("T-13 前置 正常落盘文件存在", File.Exists(partyCachePath), "文件未生成");
                PartyState.LoadOrRestore(tmp);
                Eq("T-13 重载后成员数 = 2", PartyState.MemberCount, 2);
                Check("T-13 重载后 Verified = false（重启降级）", !PartyState.Verified, "verified=true");
                Eq("T-13 重载后 reason = StaleFromPreviousSession", PartyState.VerifiedReason, "StaleFromPreviousSession");

                // 半截 JSON
                PartyState.Reset();
                File.WriteAllText(partyCachePath, "{ \"schemaVersion\": 1, \"members\": [ { \"nam");
                PartyState.LoadOrRestore(tmp);
                Eq("T-13 半截 JSON 降级空名单", PartyState.MemberCount, 0);
                Check("T-13 半截 JSON Verified = false", !PartyState.Verified, "verified=true");

                // schemaVersion=2 → 忽略整个缓存
                PartyState.Reset();
                File.WriteAllText(partyCachePath, "{ \"schemaVersion\": 2, \"members\": [ { \"name\": \"X\", \"confidence\": \"confirmed\" } ] }");
                PartyState.LoadOrRestore(tmp);
                Eq("T-13 schemaVersion=2 降级空名单", PartyState.MemberCount, 0);
                Check("T-13 schemaVersion=2 Verified = false", !PartyState.Verified, "verified=true");

                // 空文件
                PartyState.Reset();
                File.WriteAllText(partyCachePath, "");
                PartyState.LoadOrRestore(tmp);
                Eq("T-13 空文件降级空名单", PartyState.MemberCount, 0);
                Check("T-13 空文件 Verified = false", !PartyState.Verified, "verified=true");

                // 文件不存在
                PartyState.Reset();
                File.Delete(partyCachePath);
                PartyState.LoadOrRestore(tmp);
                Eq("T-13 无文件空名单", PartyState.MemberCount, 0);
                Eq("T-13 无文件 reason = ProcessStartNoPartyEvent", PartyState.VerifiedReason, "ProcessStartNoPartyEvent");
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
            PartyState.Reset();
        }

        // ---------------------------------------------------------------
        // T-13b R7 未验证抑制：SuppressStaleCache 后 IsInParty=false（§9.8 L1），
        // 真实队伍事件（230）解除抑制
        // ---------------------------------------------------------------
        {
            PartyState.Reset();
            LocalPlayerIdentity.Reset();
            PartyState.OnPartyJoined(null, new[] { "OldMate" }); // 无 GUID 版
            Check("T-13b 前置 IsInParty = true", PartyState.IsInParty, "false");
            PartyState.SuppressStaleCache();
            Check("T-13b 抑制后 IsInParty = false", !PartyState.IsInParty, "true");
            Check("T-13b 抑制后 Suppressed = true", PartyState.Suppressed, "false");
            Eq("T-13b 抑制后原始成员数保留 = 1", PartyState.MemberCount, 1);
            // 真实事件覆盖旧缓存 → 解除抑制
            PartyState.OnPartyJoined(null, new[] { "NewMate1", "NewMate2" });
            Check("T-13b 真实 230 后 IsInParty 恢复 = true", PartyState.IsInParty, "false");
            Eq("T-13b 真实 230 后成员数 = 2", PartyState.MemberCount, 2);
            // GameEvents 转发层同步（IsInParty / IsPartyMember）
            Check("T-13b GameEvents.IsPartyMember 转发", GameEvents.IsPartyMember("NewMate1"), "false");
            PartyState.Reset();
        }

        // ---- T-15 收益来源分流（C7/§8.3.2 + §8.6）----
        {
            IncomeTracker.Reset();
            LocalPlayerIdentity.Reset();

            // ① 身份 Unknown：首个拾取者 → Pending（挂起，不入合计）
            var d0 = LocalPlayerIdentity.OnTakeSilver(7777, 150000000);
            Check("T-15 未知身份首捡裁决=Pending", d0 == TakeSilverDisposition.Pending, d0.ToString());
            IncomeTracker.AddPending(new IncomeRecord { TimeUtc = DateTime.UtcNow, Source = IncomeSource.SilverPickup, SourceLabel = "拾取银币", PickerId = 7777, SilverAfterTax = 15000, SilverBeforeTax = 15000 });
            Eq("T-15 未知身份合计 = 0", IncomeTracker.GetSummary().ConfirmedTotal, 0L);

            // ② 身份 Confirmed(9001)：他人(8888)捡 → Ignore，不入本地合计
            LocalPlayerIdentity.OnJoinResponse(9001, "MeC7");
            var d1 = LocalPlayerIdentity.OnTakeSilver(8888, 12345678);
            Check("T-15 他人捡银裁决=Ignore", d1 == TakeSilverDisposition.Ignore, d1.ToString());

            // ③ 本地(9001)捡 → CountAsMine，入 SilverPickup
            var d2 = LocalPlayerIdentity.OnTakeSilver(9001, 200000000);
            Check("T-15 本地捡银裁决=CountAsMine", d2 == TakeSilverDisposition.CountAsMine, d2.ToString());
            IncomeTracker.AddConfirmed(new IncomeRecord { TimeUtc = DateTime.UtcNow, Source = IncomeSource.SilverPickup, SourceLabel = "拾取银币", PickerId = 9001, SilverBeforeTax = 20000, TaxPaid = 3000, SilverAfterTax = 17000 });

            // ④ evt=243 → PartySilver：金额=0、待确认（金额字段未实机确认，绝不猜测）
            IncomeTracker.AddPartySilverUnconfirmed();

            // ⑤ evt=61 → GatheringTracker（采集统计；§8.3.2 裁决：采集出售为手工账口径，evt=61 不产生 GatheringSale 条目）
            GatheringTracker.Reset();
            GatheringTracker.Add(new GatheringRecord { TimeUtc = DateTime.UtcNow, ItemId = 1000, Resource = "T4 矿石", Amount = 12 });
            GatheringTracker.UpdateLastFame(37);
            // ItemId=9000 未映射 → 「其他」：GatherCategories 的非矿石 baseId 是「假设，待确认」（文件内注释），
            // 1020~1024 与矿石 4 级附魔 ID 空间重叠，类别归属不可靠——本测试只用无歧义 ID 验证小计聚合
            GatheringTracker.Add(new GatheringRecord { TimeUtc = DateTime.UtcNow, ItemId = 9000, Resource = "未映射资源", Amount = 5 });
            var gs = GatheringTracker.GetSummary();
            Eq("T-15 采集总数量 = 17", gs.TotalAmount, 17L);
            Eq("T-15 采集声望 = 37", gs.TotalFame, 37L);
            Eq("T-15 采集次数 = 2", gs.Count, 2);
            Check("T-15 采集类别小计·矿石 = 12", gs.ByCategory.ContainsKey("矿石") && gs.ByCategory["矿石"] == 12, string.Join(";", gs.ByCategory.Select(kv => kv.Key + "=" + kv.Value)));
            Check("T-15 采集类别小计·其他 = 5", gs.ByCategory.ContainsKey("其他") && gs.ByCategory["其他"] == 5, string.Join(";", gs.ByCategory.Select(kv => kv.Key + "=" + kv.Value)));

            // ⑥ 手工账口径 GatheringSale（不来自 evt；仅验证小计自洽）
            IncomeTracker.AddConfirmed(new IncomeRecord { TimeUtc = DateTime.UtcNow, Source = IncomeSource.GatheringSale, SourceLabel = "采集出售", SilverAfterTax = 500, SilverBeforeTax = 500 });

            var s15 = IncomeTracker.GetSummary();
            Eq("T-15 SilverPickup 小计 = 17000", s15.SilverPickupTotal, 17000L);
            Eq("T-15 PartySilver 小计 = 0", s15.PartySilverTotal, 0L);
            Eq("T-15 GatheringSale 小计 = 500", s15.GatheringSaleTotal, 500L);
            Eq("T-15 合计 = 各来源小计之和", s15.ConfirmedTotal, s15.SilverPickupTotal + s15.PartySilverTotal + s15.GatheringSaleTotal);

            // ⑦ PartySilver 条目形态：金额 0 + AmountUnconfirmed + 备注
            var snap15 = IncomeTracker.Snapshot(20);
            var partyRec = snap15.FirstOrDefault(r => r.Source == IncomeSource.PartySilver);
            Check("T-15 PartySilver 条目存在", partyRec != null, "未找到");
            Check("T-15 PartySilver 金额 = 0", partyRec != null && partyRec.SilverAfterTax == 0, partyRec == null ? "-" : partyRec.SilverAfterTax.ToString());
            Check("T-15 PartySilver 金额待确认", partyRec != null && partyRec.AmountUnconfirmed, "false");
            Check("T-15 PartySilver 备注含金额字段未确认", partyRec != null && partyRec.AttributionNote.Contains("金额字段未确认"), partyRec == null ? "-" : partyRec.AttributionNote);

            // ⑧ pending 分流自洽：确认 9001 后，7777 的挂起被丢弃、合计不变
            IncomeTracker.ResolvePending(9001, 7777);
            var s15b = IncomeTracker.GetSummary();
            Eq("T-15 分流后合计不变 = 17500", s15b.ConfirmedTotal, 17500L);
            Eq("T-15 分流后挂起 = 0", s15b.UnconfirmedRecords, 0);
        }

        // ---- T-15b JSONL 持久化 + CSV 导出（C7/§8.3.3 / §8.3.4）----
        {
            IncomeTracker.Reset();
            string tdir = Path.Combine(Path.GetTempPath(), "inctest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tdir);
            try
            {
                IncomeTracker.SetPersistDir(tdir);
                IncomeTracker.AddConfirmed(new IncomeRecord { TimeUtc = DateTime.UtcNow, Source = IncomeSource.SilverPickup, SourceLabel = "拾取银币", PickerId = 1, SilverBeforeTax = 100, TaxPaid = 10, SilverAfterTax = 90, AttributionNote = "p8=12" });
                IncomeTracker.AddConfirmed(new IncomeRecord { TimeUtc = DateTime.UtcNow, Source = IncomeSource.PartySilver, SourceLabel = "队伍分成", AmountUnconfirmed = true, AttributionNote = "金额字段未确认" });
                IncomeTracker.AddPending(new IncomeRecord { TimeUtc = DateTime.UtcNow, Source = IncomeSource.SilverPickup, SourceLabel = "拾取银币", PickerId = 9, SilverAfterTax = 55 }); // 挂起不落盘
                IncomeTracker.FlushPendingWrites(5000);

                string file = Path.Combine(tdir, "income", "income-" + DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl");
                Check("T-15b JSONL 文件存在", File.Exists(file), file);
                if (File.Exists(file))
                {
                    var jlLines = File.ReadAllLines(file).Where(l => l.Trim().Length > 0).ToArray();
                    Eq("T-15b JSONL 行数 = 2（挂起不落盘）", jlLines.Length, 2);
                    Check("T-15b JSONL 含待确认备注", jlLines.Any(l => l.Contains("金额字段未确认")), "missing");

                    // 模拟重启：Reset 后 LoadRecent 恢复当天+前一天
                    IncomeTracker.Reset();
                    IncomeTracker.LoadRecent();
                    var rs = IncomeTracker.GetSummary();
                    Eq("T-15b 重载后条数 = 2", rs.ConfirmedCount, 2);
                    Eq("T-15b 重载后合计 = 90", rs.ConfirmedTotal, 90L);
                    Eq("T-15b 重载后金额待确认条数 = 1", rs.AmountUnconfirmedCount, 1);
                }

                // CSV：BOM + 8 列表头 + CRLF 行尾 + 行数
                IncomeTracker.Reset();
                IncomeTracker.LoadRecent();
                string csvPath = Path.Combine(tdir, "export.csv");
                string err;
                Check("T-15b ExportCsv 成功", IncomeTracker.ExportCsv(csvPath, out err), err);
                if (File.Exists(csvPath))
                {
                    byte[] bytes = File.ReadAllBytes(csvPath);
                    Check("T-15b CSV 带 BOM", bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no BOM");
                    string csvText = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
                    string[] csvRows = csvText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                    Eq("T-15b CSV 行数 = 3（表头+2）", csvRows.Length, 3);
                    Check("T-15b CSV 表头 8 列", csvRows.Length > 0 && csvRows[0] == "时间,来源,明细,税前,税,实收,待确认,备注", csvRows.Length > 0 ? csvRows[0] : "-");
                    Check("T-15b CSV 含待确认行", csvText.Contains("是"), "missing");
                }
            }
            finally
            {
                IncomeTracker.SetPersistDir(null); // 测试隔离：关闭持久化，后续测试不落盘
                IncomeTracker.Reset();
                try { Directory.Delete(tdir, true); } catch { }
            }
        }

        // ───────────────── M2 MarketDataService：缓存 TTL 分档 / 0 价过滤 / 切批 / single-flight / 限流器 ─────────────────
        {
            // M2-1 TTL 分档
            Eq("M2 TTL 分档：查价/比价 30 分钟", MarketCacheTtl.For(MarketQueryPurpose.ManualQuery), TimeSpan.FromMinutes(30));
            Eq("M2 TTL 分档：估值 4 小时", MarketCacheTtl.For(MarketQueryPurpose.Valuation), TimeSpan.FromHours(4));

            var baseTime = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000L);
            var entry = new MarketCacheEntry
            {
                Quote = new MarketQuote { ItemId = "T4_BAG", City = "Caerleon", Quality = 1 },
                Ttl = MarketCacheTtl.For(MarketQueryPurpose.ManualQuery),
                FetchedAt = baseTime
            };
            Check("M2 30 分钟档：+29 分钟仍新鲜",
                entry.IsFresh(baseTime.AddMinutes(29), MarketCacheTtl.ManualQuery), "应新鲜");
            Check("M2 30 分钟档：+31 分钟已过期",
                !entry.IsFresh(baseTime.AddMinutes(31), MarketCacheTtl.ManualQuery), "应过期");
            // P2-5：写入档 TTL 参与判定（min(写入档, 读取方档)）——
            // 30 分钟档写入的数据，估值读者（4 小时档）也不能当成 4 小时内有效。
            Check("M2 写入 30 分钟档：估值读者 +3 小时已过期（旧价不得进估值）",
                !entry.IsFresh(baseTime.AddHours(3), MarketCacheTtl.Valuation), "应过期");
            var valEntry = new MarketCacheEntry
            {
                Quote = new MarketQuote { ItemId = "T4_BAG", City = "Caerleon", Quality = 1 },
                Ttl = MarketCacheTtl.For(MarketQueryPurpose.Valuation),
                FetchedAt = baseTime
            };
            Check("M2 4 小时档：+3 小时仍新鲜",
                valEntry.IsFresh(baseTime.AddHours(3), MarketCacheTtl.Valuation), "应新鲜");
            Check("M2 4 小时档：+4 小时 1 分已过期",
                !valEntry.IsFresh(baseTime.AddHours(4).AddMinutes(1), MarketCacheTtl.Valuation), "应过期");
            Eq("M2 缓存键三元组", MarketDataCache.Key("T4_BAG", "Caerleon", 3), "T4_BAG|Caerleon|3");

            // M2-2 0 价过滤 / 陈旧标记（Parse 层）
            string m2Json = "["
                + "{\"item_id\":\"T4_BAG\",\"city\":\"Caerleon\",\"quality\":1,\"sell_price_min\":0,\"sell_price_min_date\":\"0001-01-01T00:00:00\",\"sell_price_max\":0,\"buy_price_min\":0,\"buy_price_max\":5000,\"buy_price_max_date\":\"2026-11-08T10:00:00\"},"
                + "{\"item_id\":\"T4_BAG\",\"city\":\"Lymhurst\",\"quality\":1,\"sell_price_min\":5998,\"sell_price_min_date\":\"2026-11-08T12:00:00\",\"sell_price_max\":6100,\"buy_price_min\":0,\"buy_price_max\":5100,\"buy_price_max_date\":\"2026-11-08T11:00:00\"},"
                + "{\"item_id\":\"T4_BAG\",\"city\":\"Bridgewatch\",\"quality\":1,\"sell_price_min\":7000,\"sell_price_min_date\":\"2026-11-06T12:00:00\",\"sell_price_max\":7100,\"buy_price_min\":0,\"buy_price_max\":6100,\"buy_price_max_date\":\"2026-11-08T11:00:00\"},"
                + "{\"item_id\":\"T4_BAG\",\"city\":\"Martlock\",\"quality\":1,\"sell_price_min\":0,\"sell_price_min_date\":\"0001-01-01T00:00:00\",\"sell_price_max\":0,\"buy_price_min\":0,\"buy_price_max\":0,\"buy_price_max_date\":\"0001-01-01T00:00:00\"}"
                + "]";
            var parseNow = DateTimeOffset.Parse("2026-11-09T00:00:00Z");
            var parsed = MarketClient.Parse(m2Json, parseNow);
            Eq("M2 解析条数 = 3（全零行剔除）", parsed.Count, 3);
            var caerleon = parsed.Find(q => q.City == "Caerleon");
            Check("M2 卖价 0 → SellHasData=false（无数据，不是免费）",
                caerleon != null && !caerleon.SellHasData && caerleon.SellMin == 0, "应标记无数据");
            Check("M2 卖价 0 行 buy>0 保留（收货参考）",
                caerleon != null && caerleon.BuyMax == 5000L, "buy_max 应保留");
            var lym = parsed.Find(q => q.City == "Lymhurst");
            Check("M2 卖价>0 → SellHasData=true 且 12h 内不陈旧",
                lym != null && lym.SellHasData && !lym.SellStale, "应新鲜");
            var bridge = parsed.Find(q => q.City == "Bridgewatch");
            Check("M2 sell_price_min_date 距今 >24h → SellStale（陈旧无数据）",
                bridge != null && bridge.SellStale, "应陈旧");
            Check("M2 无数据哨兵日期解析为 null",
                MarketClient.TryParsePriceDate("0001-01-01T00:00:00") == null, "应 null");
            Check("M2 空串日期解析为 null", MarketClient.TryParsePriceDate("") == null, "应 null");
            Check("M2 正常日期解析非空",
                MarketClient.TryParsePriceDate("2026-11-08T12:00:00").HasValue, "应有值");

            // M2-3 批量切批（URL ≤ 3000 字符）
            var manyItems = new List<string>();
            for (int i = 0; i < 260; i++) manyItems.Add("T8_MAIN_FIRESTAFF_ITEM_" + i.ToString("0000"));
            var batchCities = new[] { "Caerleon", "Bridgewatch", "Lymhurst", "Martlock", "Fort Sterling", "Thetford" };
            var batches = PriceBatcher.CreatePriceBatches(manyItems, batchCities);
            Check("M2 切批：260 个长物品名切出多批", batches.Count >= 2, "批数=" + batches.Count);
            bool allUnderLimit = true, allCovered = true;
            var seenIds = new HashSet<string>();
            foreach (var b in batches)
            {
                if (b.Count == 0) { allCovered = false; break; }
                int len = PriceBatcher.EstimateUrlLength(b, batchCities);
                if (len > 3000) allUnderLimit = false;
                foreach (var id in b) if (!seenIds.Add(id)) allCovered = false;
            }
            Check("M2 切批：每批 URL ≤ 3000 字符", allUnderLimit, "有批次超限");
            Check("M2 切批：物品全覆盖无重复", allCovered && seenIds.Count == manyItems.Count,
                "覆盖 " + seenIds.Count + "/" + manyItems.Count);
            var single = PriceBatcher.CreatePriceBatches(new[] { "T4_BAG" }, batchCities);
            Eq("M2 切批：单个物品一批", single.Count, 1);
            Eq("M2 切批：空物品列表零批", PriceBatcher.CreatePriceBatches(new string[0], batchCities).Count, 0);

            // M2-4 服务：single-flight / 缓存命中 / 无数据合成行 / 陈旧重算
            int fetchCalls = 0;
            string m2Body = "["
                + "{\"item_id\":\"T4_BAG\",\"city\":\"Caerleon\",\"quality\":1,\"sell_price_min\":5998,\"sell_price_min_date\":\"2026-11-08T12:00:00\",\"sell_price_max\":6100,\"buy_price_min\":0,\"buy_price_max\":5100,\"buy_price_max_date\":\"2026-11-08T11:00:00\"}"
                + "]"; // 只有 q1 有上报；q2-5 应合成"暂无上报数据"行
            var svcClockTime = DateTimeOffset.Parse("2026-11-09T00:00:00Z");
            var svc = new MarketDataService(
                delegate (string url, CancellationToken token)
                {
                    fetchCalls++;
                    return Task.Delay(50).ContinueWith(t => m2Body);
                },
                () => svcClockTime,
                new MarketRateLimiter(60, () => svcClockTime));

            // 并发两次同参查询 → single-flight 去重为一个在途请求
            var t1 = svc.GetPricesAsync(new[] { "T4_BAG" }, new[] { "Caerleon" }, MarketQueryPurpose.ManualQuery);
            var t2 = svc.GetPricesAsync(new[] { "T4_BAG" }, new[] { "Caerleon" }, MarketQueryPurpose.ManualQuery);
            Task.WhenAll(t1, t2).GetAwaiter().GetResult();
            Eq("M2 single-flight：并发同批去重为 1 次请求", fetchCalls, 1);

            var rows = svc.GetPricesAsync(new[] { "T4_BAG" }, new[] { "Caerleon" },
                MarketQueryPurpose.ManualQuery).GetAwaiter().GetResult();
            Eq("M2 缓存命中：第二次查询不再发请求", fetchCalls, 1);
            Eq("M2 返回行数 = 5（q1-q5 全品质）", rows.Count, 5);
            var q1Row = rows.Find(r => r.Quality == 1);
            Check("M2 q1 有数据行", q1Row != null && q1Row.SellHasData && q1Row.SellMin == 5998L, "q1 应有数据");
            var q3Row = rows.Find(r => r.Quality == 3);
            Check("M2 q3 合成暂无上报数据行（SellHasData=false）",
                q3Row != null && !q3Row.SellHasData && q3Row.SellMin == 0L && q3Row.BuyMax == 0L, "应合成无数据");
            Eq("M2 缓存条目数 = 5（1 真实 + 4 合成）", svc.CachedEntryCount, 5);

            // TTL 过期后重拉：时钟推进 31 分钟 → 手动档过期 → 重新请求
            svcClockTime = svcClockTime.AddMinutes(31);
            svc.GetPricesAsync(new[] { "T4_BAG" }, new[] { "Caerleon" },
                MarketQueryPurpose.ManualQuery).GetAwaiter().GetResult();
            Eq("M2 30 分钟档过期后重拉", fetchCalls, 2);
            // 估值档 4 小时：同一批数据对估值读者仍新鲜（距上次拉取 31 分钟 < 4h）
            svcClockTime = svcClockTime.AddMinutes(1);
            svc.RefreshAsync(new[] { "T4_BAG" }, new[] { "Caerleon" }).GetAwaiter().GetResult();
            Eq("M2 估值档 4 小时内不重拉", fetchCalls, 2);

            // 陈旧重算：推进时钟使 q1 的 sell 日期距今 >24h，读取时 SellStale 应翻转为 true
            svcClockTime = DateTimeOffset.Parse("2026-11-11T00:00:00Z"); // 距 2026-11-08T12:00 > 24h
            var staleRows = svc.GetPricesAsync(new[] { "T4_BAG" }, new[] { "Caerleon" },
                MarketQueryPurpose.Valuation).GetAwaiter().GetResult();
            var staleQ1 = staleRows.Find(r => r.Quality == 1);
            Check("M2 读取时按当前时间重算陈旧（>24h → SellStale）",
                staleQ1 != null && staleQ1.SellStale, "应陈旧");

            // M2-5 限流器：滚动窗口 + 优先级
            long limiterClock = 1_800_000_000L;
            var limiter = new MarketRateLimiter(2, () => DateTimeOffset.FromUnixTimeSeconds(limiterClock));
            var a1 = limiter.AcquireAsync(false);
            var a2 = limiter.AcquireAsync(false);
            Check("M2 限流：窗口内前 2 次立即放行", a1.IsCompleted && a2.IsCompleted, "应立即完成");
            var a3 = limiter.AcquireAsync(false);
            Check("M2 限流：第 3 次排队等待", !a3.IsCompleted, "应等待");
            limiterClock += 61; // 窗口滑过
            limiter.Release();  // 归还触发权 → 唤醒队首
            Check("M2 限流：窗口滑过 + 释放后放行", a3.IsCompleted, "应完成");

            var limiter2 = new MarketRateLimiter(1, () => DateTimeOffset.FromUnixTimeSeconds(limiterClock));
            var m1 = limiter2.AcquireAsync(false); // 占用唯一名额（后台）
            Check("M2 限流：名额占用", m1.IsCompleted, "应完成");
            var bg = limiter2.AcquireAsync(false); // 后台排队
            var manual = limiter2.AcquireAsync(true); // 手动插队
            Check("M2 限流：名额满时两者均等待", !bg.IsCompleted && !manual.IsCompleted, "应等待");
            limiterClock += 61;
            limiter2.Release(); // a1 释放 + 窗口已滑过 → 名额空出
            Check("M2 优先级：手动查询先于后台刷新获得名额",
                manual.IsCompleted && !bg.IsCompleted, "手动应先完成");
            limiterClock += 61;
            limiter2.Release();
            Check("M2 优先级：后台刷新随后获得名额", bg.IsCompleted, "后台应完成");

            // M2-6 服务退避路径：拉取异常向上抛（429 用尽由调用方停止本轮）
            var failing = new MarketDataService(
                delegate (string url, CancellationToken token)
                {
                    throw new MarketRateLimitedException(3, url);
                },
                () => DateTimeOffset.UtcNow,
                new MarketRateLimiter(60));
            bool threw = false;
            try
            {
                failing.GetPricesAsync(new[] { "T4_BAG" }, new[] { "Caerleon" },
                    MarketQueryPurpose.ManualQuery).GetAwaiter().GetResult();
            }
            catch (MarketRateLimitedException) { threw = true; }
            Check("M2 限流异常上抛（不裸重试）", threw, "应抛 MarketRateLimitedException");
            Eq("M2 失败不写入缓存", failing.CachedEntryCount, 0);

            // ───── P1-1 single-flight 引用计数：第一个完成者不得删掉在途项 ─────
            // 造一个「两个 awaiter 都还没返回」的窗口：fetch 一次性放行，两个调用都进入等待，
            // 第一个 awaiter 的 finally 若把 _inFlight 删掉，第二个 awaiter 就会另发一次请求。
            {
                int calls2 = 0;
                var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var svc2 = new MarketDataService(
                    delegate (string u, CancellationToken tk) { calls2++; return gate.Task; },
                    () => svcClockTime,
                    new MarketRateLimiter(600));
                var c1 = svc2.GetPricesAsync(new[] { "T4_BAG" }, new[] { "Caerleon" }, MarketQueryPurpose.ManualQuery);
                var c2 = svc2.GetPricesAsync(new[] { "T4_BAG" }, new[] { "Caerleon" }, MarketQueryPurpose.ManualQuery);
                var c3 = svc2.GetPricesAsync(new[] { "T4_BAG" }, new[] { "Caerleon" }, MarketQueryPurpose.ManualQuery);
                Check("M2 单飞：三个并发只建 1 个在途请求", calls2 == 1, "实际请求数=" + calls2);
                Eq("M2 单飞：在途请求数 = 1", svc2.InFlightCount, 1);
                gate.SetResult(m2Body);
                Task.WhenAll(c1, c2, c3).GetAwaiter().GetResult();
                Eq("M2 单飞：三个并发调用仍只有 1 次真实请求（不是被第一个完成者删掉的假去重）", calls2, 1);
                Check("M2 单飞：三个调用都拿到结果", c1.Result.Count > 0 && c2.Result.Count > 0 && c3.Result.Count > 0,
                    "c1=" + c1.Result.Count + " c2=" + c2.Result.Count + " c3=" + c3.Result.Count);
                Eq("M2 单飞：全部完成后在途项清空", svc2.InFlightCount, 0);
            }

            // ───── P1-2 调用方 ct 不得被丢弃 ─────
            {
                var svc3 = new MarketDataService(
                    delegate (string u, CancellationToken tk) { return Task.FromResult(m2Body); },
                    () => svcClockTime,
                    new MarketRateLimiter(600));
                var pre = new CancellationTokenSource();
                pre.Cancel();
                bool cancelThrew = false;
                try
                {
                    svc3.GetPricesAsync(new[] { "T4_BAG" }, new[] { "Caerleon" },
                        MarketQueryPurpose.ManualQuery, null, pre.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { cancelThrew = true; }
                Check("M2 ct：已取消的 ct 直接抛 OperationCanceledException", cancelThrew, "应抛取消异常");

                // 在途期间的取消：fetch 挂住，取消 ct 后调用方立刻返回（不把全体 awaiter 拉下水）
                int calls3 = 0;
                var hang = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var svc4 = new MarketDataService(
                    delegate (string u, CancellationToken tk) { calls3++; return hang.Task; },
                    () => svcClockTime,
                    new MarketRateLimiter(600));
                var live = new CancellationTokenSource();
                var pending = svc4.GetPricesAsync(new[] { "T4_BAG" }, new[] { "Lymhurst" },
                    MarketQueryPurpose.ManualQuery, null, live.Token);
                live.Cancel();
                bool midCancel = false;
                try { pending.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { midCancel = true; }
                Check("M2 ct：在途中取消，调用方即时抛取消异常", midCancel && calls3 == 1, "应即时取消");
                hang.SetResult(m2Body); // 收尾，避免悬空任务
            }

            // ───── P2-6 转义：估算 = 实际，且切批不越界 ─────
            {
                var nasty = new[] { "T4_BAG'X", "T4_BAG&Y", "T4_BAG+Z", "T4_BAG%W", "T5_2H_BOW@2", "T4_AXE" };
                var nastyCities = new[] { "Fort Sterling", "Thetford" };
                foreach (var id in nasty)
                {
                    string one = MarketClient.BuildPriceUrl(new[] { id }, nastyCities);
                    Eq("P2-6 编码长度与实际一致（" + id + "）",
                        PriceBatcher.EstimateUrlLength(new[] { id }, nastyCities), one.Length);
                }
                Check("P2-6 '&' 必须转义为 %26（否则 URL 会被截断）",
                    MarketClient.EncodeSegment("A&B") == "A%26B", MarketClient.EncodeSegment("A&B"));
                Check("P2-6 ''' 必须转义为 %27", MarketClient.EncodeSegment("A'B") == "A%27B",
                    MarketClient.EncodeSegment("A'B"));
                Check("P2-6 '+' 必须转义为 %2B（否则被当成空格）",
                    MarketClient.EncodeSegment("A+B") == "A%2BB", MarketClient.EncodeSegment("A+B"));
                Check("P2-6 '%' 必须转义为 %25", MarketClient.EncodeSegment("A%B") == "A%25B",
                    MarketClient.EncodeSegment("A%B"));

                // 大批含特殊字符物品：每批实际 URL 必须 ≤ 3000（不是估算 ≤ 3000）
                var nastyMany = new List<string>();
                for (int i = 0; i < 200; i++) nastyMany.Add("T8_MAIN_FIRESTAFF_&+'%@2_" + i.ToString("0000"));
                var nastyBatches = PriceBatcher.CreatePriceBatches(nastyMany, batchCities);
                bool actualOk = true;
                string worst = "";
                foreach (var b in nastyBatches)
                {
                    int actual = MarketClient.BuildPriceUrl(b.ToArray(), batchCities).Length;
                    if (actual > PriceBatcher.DefaultUrlLimit) { actualOk = false; worst = actual + " 字符"; }
                }
                Check("P2-6 含特殊字符时每批实际 URL ≤ 3000", actualOk && nastyBatches.Count >= 2,
                    "批数=" + nastyBatches.Count + " " + worst);

                // 城市名带空格（Fort Sterling）也必须编码
                Check("P2-6 城市名空格编码为 %20",
                    MarketClient.BuildPriceUrl(new[] { "T4_BAG" }, new[] { "Fort Sterling" })
                        .Contains("Fort%20Sterling"), MarketClient.EncodeSegment("Fort Sterling"));
            }

            // ───── 唯一读源：SnapshotCache 只读、绝不发请求 ─────
            {
                int snapCalls = 0;
                var svc5 = new MarketDataService(
                    delegate (string u, CancellationToken tk) { snapCalls++; return Task.FromResult(m2Body); },
                    () => svcClockTime,
                    new MarketRateLimiter(600));
                svc5.GetPricesAsync(new[] { "T4_BAG" }, new[] { "Caerleon" },
                    MarketQueryPurpose.ManualQuery).GetAwaiter().GetResult();
                Eq("读源：首次回源 1 次请求", snapCalls, 1);

                // SnapshotCache 是 UI 渲染唯一读口：反复调用不得产生任何请求
                for (int i = 0; i < 50; i++)
                {
                    var snap = svc5.SnapshotCache();
                    if (snap.Count == 0) break;
                }
                Eq("读源：快照读 50 次仍是 1 次请求（渲染路径物理上不出网）", snapCalls, 1);
                Check("读源：快照能取到缓存条目", svc5.SnapshotCache().Count == 5,
                    "条目=" + svc5.SnapshotCache().Count);

                // 快照必须返回副本：调用方排序/清空不得改写服务内部缓存
                var copy = svc5.SnapshotCache();
                copy.Clear();
                Eq("读源：清空快照不影响内部缓存", svc5.CachedEntryCount, 5);
            }

            // ───── P2-7 非 2xx 响应体上下文：宁可截断不可丢弃 ─────
            {
                string shortBody = "Throttled, be nice to the server!";
                Check("P2-7 短响应体原样带上",
                    MarketClient.ErrorContext(shortBody).Contains(shortBody), MarketClient.ErrorContext(shortBody));
                var longBody = new StringBuilder();
                for (int i = 0; i < 400; i++) longBody.Append('x');
                string ctx = MarketClient.ErrorContext(longBody.ToString());
                Check("P2-7 长响应体不再被整段丢弃（截断并标注）",
                    ctx.Length > 0 && ctx.Contains("已截断") && ctx.Contains("400"), ctx);
                Check("P2-7 长响应体保留开头 200 字符",
                    ctx.Contains(new string('x', 200)), "应保留前 200 字符");
                Check("P2-7 空响应体不产生多余空白", MarketClient.ErrorContext("") == "" &&
                    MarketClient.ErrorContext(null) == "", "应为空串");
            }
        }

        // ───────────────── C8/Bug A：未解析目标不得显示裸 ObjectId ─────────────────
        RunTargetNameTests();

        // ───────────────── C8/Bug B：自绘控件状态变化 → 重绘 ─────────────────
        RunToggleStateTests();

        // ───────────────── M3 ItemIndex：本地物品元数据索引 ─────────────────
        RunItemIndexTests();

        // ───────────────── M4 三级筛选：元数据分类树 + 查价键 ─────────────────
        RunTaxonomyTests();

        // ───────────────── 复核 P0：限流器窗口滚动必须自动唤醒排队者（不依赖外部 Release） ─────────────────
        RunRateLimiterWakeTests();

        // ───────────────── 复核 P0：JoinResponse 码值可配置（0=禁用，越界回落 0） ─────────────────
        RunJoinResponseConfigTests();

        Console.WriteLine("passed=" + _pass + " failed=" + _fail);
        return _fail == 0 ? 0 : 1;
    }

    // ───────────────── 复核 P0：JoinResponse 码值可配置（0=禁用，越界回落 0） ─────────────────
    // 背景：P0-1 把判定收紧为「精确码等值」，但权威码未取证时白名单为 null → 身份主路径永久禁用。
    // 收口方式：实机取证后把码值写进 settings.json，启动时注入；0 = 保持禁用（绝不猜值）。
    static void RunJoinResponseConfigTests()
    {
        string path = Path.Combine(Path.GetTempPath(), "ac-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var s = new AppSettings { JoinResponseOperationCode = 37 };
            SettingsStore.Save(path, s);
            Eq("P0 JoinResponse：取证码值可从 settings.json 读回", SettingsStore.Load(path).JoinResponseOperationCode, 37);

            s.JoinResponseOperationCode = 300;
            SettingsStore.Save(path, s);
            Eq("P0 JoinResponse：越界码值回落 0（禁用，不放宽白名单）",
                SettingsStore.Load(path).JoinResponseOperationCode, 0);

            s.JoinResponseOperationCode = -1;
            SettingsStore.Save(path, s);
            Eq("P0 JoinResponse：负值回落 0（禁用）",
                SettingsStore.Load(path).JoinResponseOperationCode, 0);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    sealed class NoopDisposable : IDisposable { public void Dispose() { } }

    // ───────────────── 复核 P0：限流器窗口滚动必须自动唤醒排队者 ─────────────────
    // 旧实现只在 Acquire/Release 事件里 Pump，窗口用满后若没有新事件，排队者永久挂起
    // （真实触发：市场页大查询 79 批，第 61 批卡死，UI 停在「回源 60/79 批」）。
    static void RunRateLimiterWakeTests()
    {
        var clockNow = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
        var wakes = new List<(TimeSpan Delay, Action Callback)>();
        Func<TimeSpan, Action, IDisposable> recorder = delegate (TimeSpan delay, Action callback)
        {
            wakes.Add((delay, callback));
            return new NoopDisposable();
        };

        // 1) 窗口未满：立即放行，且不得安排任何定时器
        var noQueue = new MarketRateLimiter(60, () => clockNow, recorder);
        Check("P0 限流器：窗口有空位时立即放行", noQueue.AcquireAsync(false).IsCompleted, "应已完成");
        Eq("P0 限流器：无排队时不安排定时器", wakes.Count, 0);

        // 2) 窗口用满 + 第 61 个请求排队：必须自己安排一次唤醒（而不是等外部 Release）
        var limiter = new MarketRateLimiter(60, () => clockNow, recorder);
        for (int i = 0; i < 60; i++)
        {
            var t = limiter.AcquireAsync(i % 2 == 0);
            Check("P0 限流器：窗口内第 " + (i + 1) + " 个名额立即放行", t.IsCompleted, "应已完成");
        }
        var queued = limiter.AcquireAsync(true);
        Check("P0 限流器：第 61 个请求进入排队", !queued.IsCompleted, "不应已完成");
        Check("P0 限流器：排队时自己安排唤醒（不依赖外部 Release）", wakes.Count == 1, "wakes=" + wakes.Count);
        Check("P0 限流器：唤醒延迟 = 最早名额到期时间（≈60s）",
            wakes.Count == 1 && wakes[0].Delay.TotalSeconds > 59 && wakes[0].Delay.TotalSeconds <= 60,
            wakes.Count == 1 ? "delay=" + wakes[0].Delay.TotalSeconds : "无唤醒");

        // 3) 真实场景：没有任何新请求/Release，只有时间流逝 → 定时器到点必须放出名额
        clockNow = clockNow.AddSeconds(61);
        wakes[0].Callback();
        Check("P0 限流器：时钟越过窗口后唤醒排队者（修复前永久挂起）", queued.IsCompleted, "应已完成");

        // 4) 队列清空后不得再安排新的唤醒
        Eq("P0 限流器：队列清空后不再重复安排唤醒", wakes.Count, 1);
    }
}
