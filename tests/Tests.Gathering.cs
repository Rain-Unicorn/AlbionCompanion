#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AlbionCompanion.Core;
using AlbionCompanion.Core.Gathering;

// 采集 模块回归（由 gathering 负责人填写）
// 断言入口：LogicTests.Check / LogicTests.Eq（internal static，同程序集可见）。
// 覆盖：分类归属（确认矿石 / ItemIndex 元数据 / 未知归未分类）、声望更新与重复/乱序防护、
//       小计自洽（总量/声望/次数）、Snapshot 不可变拷贝、Clear/Reset 语义、并发写读。
static class GatheringModuleTests
{
    public static void Run()
    {
        RunCategoryClassificationTests();
        RunTrackerAggregateTests();
        RunTrackerGuardTests();
        RunTrackerFameOrderingTests();
        RunSnapshotAndResetTests();
        RunConcurrencyTests();
    }

    // ─────────────────────────────────────────────────────────────
    // 分类：不再按非矿石 baseId 区间猜（1020~1024 与矿石 4 级附魔重叠）
    // ─────────────────────────────────────────────────────────────
    static void RunCategoryClassificationTests()
    {
        // ① 纯 uniqueName 词根（不依赖任何外部数据）
        LogicTests.Eq("G1 T4_ORE → 矿石", GatherCategories.ClassifyUniqueName("T4_ORE"), GatherCategories.ResourceType.Ore);
        LogicTests.Eq("G1 T7_FIBER → 纤维", GatherCategories.ClassifyUniqueName("T7_FIBER"), GatherCategories.ResourceType.Fiber);
        LogicTests.Eq("G1 T5_WOOD → 木材", GatherCategories.ClassifyUniqueName("T5_WOOD"), GatherCategories.ResourceType.Wood);
        LogicTests.Eq("G1 T4_HIDE → 皮革", GatherCategories.ClassifyUniqueName("T4_HIDE"), GatherCategories.ResourceType.Hide);
        LogicTests.Eq("G1 T8_ROCK → 石头", GatherCategories.ClassifyUniqueName("T8_ROCK"), GatherCategories.ResourceType.Stone);
        LogicTests.Eq("G1 附魔后缀不影响词根（T7_HIDE@2）", GatherCategories.ClassifyUniqueName("T7_HIDE@2"), GatherCategories.ResourceType.Hide);
        LogicTests.Eq("G1 小写前缀容错（t4_ore）", GatherCategories.ClassifyUniqueName("t4_ore"), GatherCategories.ResourceType.Ore);
        LogicTests.Eq("G1 T4_BAG 非采集资源 → Unknown", GatherCategories.ClassifyUniqueName("T4_BAG"), GatherCategories.ResourceType.Unknown);
        LogicTests.Eq("G1 T5_2H_BOW 非采集资源 → Unknown", GatherCategories.ClassifyUniqueName("T5_2H_BOW"), GatherCategories.ResourceType.Unknown);
        LogicTests.Eq("G1 空串 → Unknown", GatherCategories.ClassifyUniqueName(""), GatherCategories.ResourceType.Unknown);
        LogicTests.Eq("G1 null → Unknown", GatherCategories.ClassifyUniqueName(null), GatherCategories.ResourceType.Unknown);
        LogicTests.Eq("G1 无 T 前缀 → Unknown", GatherCategories.ClassifyUniqueName("ORE"), GatherCategories.ResourceType.Unknown);
        LogicTests.Eq("G1 类别中文名·矿石", GatherCategories.CategoryName(GatherCategories.ResourceType.Ore), "矿石");
        LogicTests.Eq("G1 类别中文名·石头", GatherCategories.CategoryName(GatherCategories.ResourceType.Stone), "石头");
        LogicTests.Eq("G1 类别中文名·未分类桶", GatherCategories.CategoryName(GatherCategories.ResourceType.Unknown), "其他");

        Func<int, string> oldResolver = GatherCategories.UniqueNameResolverForTests;
        ItemIndex oldIndex = ItemIndex.Default;
        try
        {
            // ② 物品索引数据缺失：只有实机确认的矿石数字规律可用（T4.0=1000，tier+1/ench+5）
            GatherCategories.UniqueNameResolverForTests = null;
            ItemIndex.Default = null;

            LogicTests.Eq("G2 确认矿石 T4.0=1000 → 矿石", GatherCategories.GetResourceType(1000), GatherCategories.ResourceType.Ore);
            LogicTests.Eq("G2 确认矿石 T5.1=1006 → 矿石", GatherCategories.GetResourceType(1006), GatherCategories.ResourceType.Ore);
            // 1020~1024：旧实现既可能是矿石 4 级附魔、又可能是「假设的石头 baseId」——
            // 现在只按确认的矿石规律解释，石头不再靠猜。
            LogicTests.Eq("G2 1020 按确认矿石规律（T4.4）→ 矿石，不再猜石头", GatherCategories.GetResourceType(1020), GatherCategories.ResourceType.Ore);
            LogicTests.Eq("G2 1024 按确认矿石规律（T8.4）→ 矿石", GatherCategories.GetResourceType(1024), GatherCategories.ResourceType.Ore);
            LogicTests.Eq("G2 未映射 9000 → 未分类", GatherCategories.GetResourceType(9000), GatherCategories.ResourceType.Unknown);
            LogicTests.Eq("G2 id=0 → 未分类", GatherCategories.GetResourceType(0), GatherCategories.ResourceType.Unknown);
            LogicTests.Eq("G2 id<0 → 未分类", GatherCategories.GetResourceType(-1), GatherCategories.ResourceType.Unknown);
            LogicTests.Eq("G2 阶级 T4=1000", GatherCategories.GetTier(1000), "T4");
            LogicTests.Eq("G2 阶级 T5.1=1006", GatherCategories.GetTier(1006), "T5.1");
            LogicTests.Eq("G2 阶级 T8.4=1024", GatherCategories.GetTier(1024), "T8.4");
            LogicTests.Eq("G2 未知 id 无阶级", GatherCategories.GetTier(9000), "");

            // ③ 有 uniqueName、但 ItemIndex 元数据缺失：非矿石不臆造（归未分类）；矿石仍兜底
            var map1 = new Dictionary<int, string>
            {
                { 2000, "T5_FIBER" },
                { 2001, "T4_BAG" },
                { 2002, "T4_ORE@2" },
                { 2003, "T3_ROCK" },
            };
            GatherCategories.UniqueNameResolverForTests = id => map1.ContainsKey(id) ? map1[id] : null;
            ItemIndex.Default = null;

            LogicTests.Eq("G3 元数据缺失时非矿石 → 未分类（不按词根臆造）", GatherCategories.GetResourceType(2000), GatherCategories.ResourceType.Unknown);
            LogicTests.Eq("G3 元数据缺失时矿石仍按确认规律 → 矿石", GatherCategories.GetResourceType(2002), GatherCategories.ResourceType.Ore);
            LogicTests.Eq("G3 具名非采集物 → 未分类", GatherCategories.GetResourceType(2001), GatherCategories.ResourceType.Unknown);
            LogicTests.Eq("G3 解析源可用但查不到该 ID → 未分类（数据源可用却无此物品）", GatherCategories.GetResourceType(1020), GatherCategories.ResourceType.Unknown);

            // ④ 有 ItemIndex 元数据：crafting/resources 确认 → 按词根归类；
            //    元数据明确非资源 → 未分类；词根未知 → 未分类（不臆造）
            string dir = Path.Combine(Path.GetTempPath(), "ac_gather_tax_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                ItemIndex fake = BuildFakeItemIndex(dir);
                LogicTests.Check("G4 测试索引加载非空", fake != null && fake.Count > 0, "count=" + (fake == null ? 0 : fake.Count));
                ItemIndex.Default = fake;

                var map2 = new Dictionary<int, string>
                {
                    { 3000, "T4_FIBER" },
                    { 3001, "T4_BAG" },
                    { 3002, "T4_MYSTERY" },
                    { 3003, "T4_NOMETA" },
                    { 3004, "T4_ORE@4" },
                    { 3005, "T6_ROCK" },
                    { 3006, "T8_WOOD" },
                    { 3007, "T4_HIDE" },
                    { 1020, "T6_ROCK" },
                };
                GatherCategories.UniqueNameResolverForTests = id => map2.ContainsKey(id) ? map2[id] : null;

                LogicTests.Eq("G4 元数据资源 + FIBER → 纤维", GatherCategories.GetResourceType(3000), GatherCategories.ResourceType.Fiber);
                LogicTests.Eq("G4 元数据资源 + WOOD → 木材", GatherCategories.GetResourceType(3006), GatherCategories.ResourceType.Wood);
                LogicTests.Eq("G4 元数据资源 + HIDE → 皮革", GatherCategories.GetResourceType(3007), GatherCategories.ResourceType.Hide);
                LogicTests.Eq("G4 元数据资源 + ROCK → 石头", GatherCategories.GetResourceType(3005), GatherCategories.ResourceType.Stone);
                LogicTests.Eq("G4 元数据资源 + ORE@4 → 矿石", GatherCategories.GetResourceType(3004), GatherCategories.ResourceType.Ore);
                LogicTests.Eq("G4 元数据明确非资源（bags）→ 未分类", GatherCategories.GetResourceType(3001), GatherCategories.ResourceType.Unknown);
                LogicTests.Eq("G4 元数据是资源但词根不认识 → 未分类（不臆造）", GatherCategories.GetResourceType(3002), GatherCategories.ResourceType.Unknown);
                LogicTests.Eq("G4 元数据无分类字段 → 未分类", GatherCategories.GetResourceType(3003), GatherCategories.ResourceType.Unknown);
                // 旧实现把 1020 当「假设的石头基址」或矿石 4 级附魔；有 uniqueName 时以数据为准
                LogicTests.Eq("G4 1020 有 uniqueName(T6_ROCK) 时按数据 → 石头（不是矿石）", GatherCategories.GetResourceType(1020), GatherCategories.ResourceType.Stone);
                LogicTests.Eq("G4 阶级由 uniqueName 解析：T4_FIBER → T4", GatherCategories.GetTier(3000), "T4");
                LogicTests.Eq("G4 阶级+附魔：T4_ORE@4 → T4.4", GatherCategories.GetTier(3004), "T4.4");
                LogicTests.Eq("G4 未分类 GetResourceName = 其他桶", GatherCategories.GetResourceName(3002), "其他");
                LogicTests.Eq("G4 已分类 GetResourceName = 纤维", GatherCategories.GetResourceName(3000), "纤维");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        finally
        {
            GatherCategories.UniqueNameResolverForTests = oldResolver;
            ItemIndex.Default = oldIndex;
        }
    }

    /// <summary>构造一份缩样 ItemIndex：formatted 负责 uniqueName，raw 负责 @shopcategory/@shopsubcategory1。</summary>
    static ItemIndex BuildFakeItemIndex(string dir)
    {
        string fmt = Path.Combine(dir, ItemIndex.FormattedFileName);
        string raw = Path.Combine(dir, ItemIndex.RawFileName);
        File.WriteAllText(fmt, "["
            + "{\"UniqueName\":\"T4_FIBER\"},"
            + "{\"UniqueName\":\"T8_WOOD\"},"
            + "{\"UniqueName\":\"T4_HIDE\"},"
            + "{\"UniqueName\":\"T6_ROCK\"},"
            + "{\"UniqueName\":\"T4_ORE\"},"
            + "{\"UniqueName\":\"T4_ORE@4\"},"
            + "{\"UniqueName\":\"T4_MYSTERY\"},"
            + "{\"UniqueName\":\"T4_BAG\"},"
            + "{\"UniqueName\":\"T4_NOMETA\"}"
            + "]");
        File.WriteAllText(raw, "{"
            + "\"?xml\":{\"@version\":\"1.0\"},"
            + "\"items\":{"
            + "\"craftresource\":["
            + "{\"@uniquename\":\"T4_FIBER\",\"@shopcategory\":\"crafting\",\"@shopsubcategory1\":\"resources\"},"
            + "{\"@uniquename\":\"T8_WOOD\",\"@shopcategory\":\"crafting\",\"@shopsubcategory1\":\"resources\"},"
            + "{\"@uniquename\":\"T4_HIDE\",\"@shopcategory\":\"crafting\",\"@shopsubcategory1\":\"resources\"},"
            + "{\"@uniquename\":\"T6_ROCK\",\"@shopcategory\":\"crafting\",\"@shopsubcategory1\":\"resources\"},"
            + "{\"@uniquename\":\"T4_ORE\",\"@shopcategory\":\"crafting\",\"@shopsubcategory1\":\"resources\"},"
            + "{\"@uniquename\":\"T4_MYSTERY\",\"@shopcategory\":\"crafting\",\"@shopsubcategory1\":\"resources\"}"
            + "],"
            + "\"simpleitem\":["
            + "{\"@uniquename\":\"T4_BAG\",\"@shopcategory\":\"bags\",\"@shopsubcategory1\":\"bags\"},"
            + "{\"@uniquename\":\"T4_NOMETA\"}"
            + "]}}");
        return ItemIndex.Load(fmt, raw);
    }

    // ─────────────────────────────────────────────────────────────
    // 聚合：总量 / 声望 / 次数 / 按类别小计 / 未分类计数 / 恒等式
    // ─────────────────────────────────────────────────────────────
    static void RunTrackerAggregateTests()
    {
        GatheringTracker.Reset();
        GatheringSummary empty = GatheringTracker.GetSummary();
        LogicTests.Eq("G5 空态总量 = 0", empty.TotalAmount, 0L);
        LogicTests.Eq("G5 空态声望 = 0", empty.TotalFame, 0L);
        LogicTests.Eq("G5 空态次数 = 0", empty.Count, 0);
        LogicTests.Eq("G5 空态无类别小计", empty.ByCategory.Count, 0);
        LogicTests.Eq("G5 空态未分类计数 = 0", empty.UnclassifiedCount, 0);
        LogicTests.Eq("G5 空态快照为空", GatheringTracker.Snapshot(10).Length, 0);

        var t0 = new DateTime(2026, 11, 10, 1, 0, 0, DateTimeKind.Utc);
        LogicTests.Check("G5 Add 成功返回 true",
              GatheringTracker.Add(new GatheringRecord { TimeUtc = t0, ItemId = 1000, Resource = "T4 矿石", Amount = 10 }),
              "Add returned false");
        LogicTests.Check("G5 声望写入最近一条", GatheringTracker.UpdateLastFame(5), "UpdateLastFame returned false");
        GatheringTracker.Add(new GatheringRecord { TimeUtc = t0.AddSeconds(1), ItemId = 9000, Resource = "未映射资源", Amount = 3, Fame = 2 });

        GatheringSummary s = GatheringTracker.GetSummary();
        LogicTests.Eq("G5 总量 = 13", s.TotalAmount, 13L);
        LogicTests.Eq("G5 总声望 = 7", s.TotalFame, 7L);
        LogicTests.Eq("G5 次数 = 2", s.Count, 2);
        LogicTests.Eq("G5 数量小计·矿石 = 10", s.ByCategory["矿石"], 10L);
        LogicTests.Eq("G5 数量小计·其他（未分类）= 3", s.ByCategory["其他"], 3L);
        LogicTests.Eq("G5 声望小计·矿石 = 5", s.FameByCategory["矿石"], 5L);
        LogicTests.Eq("G5 声望小计·其他 = 2", s.FameByCategory["其他"], 2L);
        LogicTests.Eq("G5 次数小计·矿石 = 1", s.CountByCategory["矿石"], 1);
        LogicTests.Eq("G5 次数小计·其他 = 1", s.CountByCategory["其他"], 1);
        LogicTests.Eq("G5 未分类数量 = 3", s.UnclassifiedAmount, 3L);
        LogicTests.Eq("G5 未分类声望 = 2", s.UnclassifiedFame, 2L);
        LogicTests.Eq("G5 未分类次数 = 1", s.UnclassifiedCount, 1);
        LogicTests.Eq("G5 恒等式：数量小计之和 = 总量", s.ByCategory.Values.Sum(), s.TotalAmount);
        LogicTests.Eq("G5 恒等式：声望小计之和 = 总声望", s.FameByCategory.Values.Sum(), s.TotalFame);
        LogicTests.Eq("G5 恒等式：次数小计之和 = 次数", s.CountByCategory.Values.Sum(), s.Count);
        LogicTests.Eq("G5 未分类桶与「其他」键一致", s.UnclassifiedAmount, s.ByCategory["其他"]);
        LogicTests.Eq("G5 CategoryOf(1000) 与摘要同源 = 矿石", GatheringTracker.CategoryOf(1000), "矿石");
        LogicTests.Eq("G5 CategoryOf(9000) 与摘要同源 = 其他", GatheringTracker.CategoryOf(9000), "其他");

        // GetSummary 每次新建：改返回值不得影响内部统计（快照语义）
        s.ByCategory["矿石"] = 999L;
        s.TotalAmount = 0L;
        GatheringSummary again = GatheringTracker.GetSummary();
        LogicTests.Eq("G5 修改返回的 summary 不影响内部总量", again.TotalAmount, 13L);
        LogicTests.Eq("G5 修改返回的 summary 不影响内部小计", again.ByCategory["矿石"], 10L);

        // 汇总 ↔ 明细同源（HarvestTab 的展示口径）
        GatheringRecord[] all = GatheringTracker.Snapshot(10000);
        long amount = 0, fame = 0;
        for (int i = 0; i < all.Length; i++)
        {
            LogicTests.Check("G5 快照元素非 null", all[i] != null, "null element at " + i);
            amount += all[i].Amount;
            fame += all[i].Fame;
        }
        LogicTests.Eq("G5 汇总总量 = 明细求和", again.TotalAmount, amount);
        LogicTests.Eq("G5 汇总声望 = 明细求和", again.TotalFame, fame);
        LogicTests.Eq("G5 汇总次数 = 明细条数", again.Count, all.Length);

        GatheringTracker.Reset();
    }

    // ─────────────────────────────────────────────────────────────
    // 防护：Add 重复/乱序/非法，UpdateLastFame 重复/非法
    // ─────────────────────────────────────────────────────────────
    static void RunTrackerGuardTests()
    {
        GatheringTracker.Reset();
        var t0 = new DateTime(2026, 11, 10, 2, 0, 0, DateTimeKind.Utc);

        // 非法记录
        LogicTests.Check("G6 null 记录被拒", !GatheringTracker.Add(null), "Add(null)=true");
        LogicTests.Check("G6 数量为负被拒", !GatheringTracker.Add(new GatheringRecord { TimeUtc = t0, ItemId = 1000, Amount = -1 }), "negative amount accepted");
        GatheringSummary s = GatheringTracker.GetSummary();
        LogicTests.Eq("G6 非法记录被拒计数 = 2", s.RejectedAdds, 2);
        LogicTests.Eq("G6 非法记录未入账", s.Count, 0);

        // 重复 Add：同一对象重投 / 同一时刻同物品同数量
        var r = new GatheringRecord { TimeUtc = t0, ItemId = 1000, Amount = 4 };
        LogicTests.Check("G6 首次 Add 成功", GatheringTracker.Add(r), "first add failed");
        LogicTests.Check("G6 同一对象重投被去重", !GatheringTracker.Add(r), "duplicate object accepted");
        LogicTests.Check("G6 同时刻同物品同数量重复事件被去重",
              !GatheringTracker.Add(new GatheringRecord { TimeUtc = t0, ItemId = 1000, Amount = 4 }),
              "duplicate value accepted");
        s = GatheringTracker.GetSummary();
        LogicTests.Eq("G6 重复 Add 计数 = 2", s.DuplicateAdds, 2);
        LogicTests.Eq("G6 去重后只保留 1 条", s.Count, 1);

        // 时间不同 → 不是重复
        LogicTests.Check("G6 时间不同的同物品记录正常入账",
              GatheringTracker.Add(new GatheringRecord { TimeUtc = t0.AddMilliseconds(1), ItemId = 1000, Amount = 4 }),
              "distinct record rejected");

        // 非法声望更新
        LogicTests.Check("G6 fame=0 被拒", !GatheringTracker.UpdateLastFame(0), "fame=0 accepted");
        LogicTests.Check("G6 fame<0 被拒", !GatheringTracker.UpdateLastFame(-5), "negative fame accepted");
        s = GatheringTracker.GetSummary();
        LogicTests.Eq("G6 非法声望更新计数 = 2", s.RejectedFameUpdates, 2);
        LogicTests.Eq("G6 非法声望未改动总声望", s.TotalFame, 0L);

        // 重复声望：最近一条已有声望后，短时间再来 72 不覆盖
        LogicTests.Check("G6 首次声望写入成功", GatheringTracker.UpdateLastFame(7), "first fame update failed");
        LogicTests.Check("G6 重复声望更新被拒", !GatheringTracker.UpdateLastFame(9), "second fame update accepted");
        s = GatheringTracker.GetSummary();
        LogicTests.Eq("G6 重复声望更新计数 = 1", s.DuplicateFameUpdates, 1);
        LogicTests.Eq("G6 已有声望不被覆盖（首个声望为准）", s.TotalFame, 7L);

        // 乱序 Add：旧记录晚到，不得冒充「最近一条」
        GatheringTracker.Reset();
        var newer = new GatheringRecord { TimeUtc = t0.AddSeconds(10), ItemId = 1000, Amount = 1 };
        var older = new GatheringRecord { TimeUtc = t0, ItemId = 1000, Amount = 2 };
        GatheringTracker.Add(newer);
        GatheringTracker.Add(older);
        GatheringRecord[] snap = GatheringTracker.Snapshot(10);
        LogicTests.Eq("G6 乱序插入后快照仍按时间新在前·条数", snap.Length, 2);
        LogicTests.Eq("G6 乱序插入后最新记录在最前", snap[0].TimeUtc, newer.TimeUtc);
        LogicTests.Eq("G6 乱序插入后旧记录在后", snap[1].TimeUtc, older.TimeUtc);
        LogicTests.Eq("G6 乱序 Add 计数 = 1", GatheringTracker.GetSummary().OutOfOrderAdds, 1);
        LogicTests.Check("G6 声望落在时间最新的记录上（不是晚到的旧记录）", GatheringTracker.UpdateLastFame(11), "fame update failed");
        snap = GatheringTracker.Snapshot(10);
        LogicTests.Eq("G6 最新记录拿到声望", snap[0].Fame, 11);
        LogicTests.Eq("G6 乱序旧记录不被误写声望", snap[1].Fame, 0);

        GatheringTracker.Reset();
    }

    // ─────────────────────────────────────────────────────────────
    // 乱序声望：evt=72 先于 evt=61 到达的暂存 / 超时 / 多义
    // ─────────────────────────────────────────────────────────────
    static void RunTrackerFameOrderingTests()
    {
        DateTime now = new DateTime(2026, 11, 10, 3, 0, 0, DateTimeKind.Utc);
        GatheringTracker.UtcNowForTests = () => now;
        try
        {
            // ① 72 先到（无记录）→ 暂存，不伪造记录；61 到达后认领
            GatheringTracker.Reset();
            LogicTests.Check("G7 无记录时声望更新不写入", !GatheringTracker.UpdateLastFame(7), "fame written without record");
            GatheringSummary s = GatheringTracker.GetSummary();
            LogicTests.Eq("G7 无记录时不伪造采集条数", s.Count, 0);
            LogicTests.Eq("G7 暂存声望条数 = 1", s.PendingFameCount, 1);
            LogicTests.Eq("G7 未归属声望计数仍为 0", s.UnattributedFameCount, 0);
            LogicTests.Check("G7 61 到达后入账", GatheringTracker.Add(new GatheringRecord { TimeUtc = now, ItemId = 1000, Amount = 2 }), "add failed");
            s = GatheringTracker.GetSummary();
            LogicTests.Eq("G7 暂存声望被最近一条记录认领", s.TotalFame, 7L);
            LogicTests.Eq("G7 认领后暂存清空", s.PendingFameCount, 0);
            LogicTests.Eq("G7 认领次数 = 1", s.PendingFameApplied, 1);

            // ② 暂存超 TTL 仍未等到 61 → 计入「未归属」，不静默
            GatheringTracker.Reset();
            now = new DateTime(2026, 11, 10, 3, 10, 0, DateTimeKind.Utc);
            GatheringTracker.UpdateLastFame(6);
            now = now.AddSeconds(10);
            GatheringTracker.Add(new GatheringRecord { TimeUtc = now, ItemId = 1000, Amount = 1 });
            s = GatheringTracker.GetSummary();
            LogicTests.Eq("G7 超时暂存声望不被误归属", s.TotalFame, 0L);
            LogicTests.Eq("G7 超时暂存计入未归属 = 1", s.UnattributedFameCount, 1);
            LogicTests.Eq("G7 超时后暂存清空", s.PendingFameCount, 0);

            // ③ 多条暂存（多义）→ 不猜归属，全部计入未归属
            GatheringTracker.Reset();
            now = new DateTime(2026, 11, 10, 3, 20, 0, DateTimeKind.Utc);
            GatheringTracker.UpdateLastFame(5);
            GatheringTracker.UpdateLastFame(6);
            GatheringTracker.Add(new GatheringRecord { TimeUtc = now, ItemId = 1000, Amount = 1 });
            s = GatheringTracker.GetSummary();
            LogicTests.Eq("G7 多义暂存不猜归属", s.TotalFame, 0L);
            LogicTests.Eq("G7 多义暂存全部计入未归属 = 2", s.UnattributedFameCount, 2);

            // ④ 最近一条已带声望、间隔较长（> 2s）的 72：可能是下一次采集的 72 先到 → 暂存等认领
            GatheringTracker.Reset();
            now = new DateTime(2026, 11, 10, 3, 30, 0, DateTimeKind.Utc);
            GatheringTracker.Add(new GatheringRecord { TimeUtc = now, ItemId = 1000, Amount = 1 });
            LogicTests.Check("G7 第一条记录声望写入", GatheringTracker.UpdateLastFame(8), "fame update failed");
            now = now.AddSeconds(5);
            LogicTests.Check("G7 间隔较长的 72 不覆盖已有记录", !GatheringTracker.UpdateLastFame(9), "overwrote existing fame");
            s = GatheringTracker.GetSummary();
            LogicTests.Eq("G7 已有记录声望保持 8", s.TotalFame, 8L);
            LogicTests.Eq("G7 间隔较长的 72 被暂存", s.PendingFameCount, 1);
            GatheringTracker.Add(new GatheringRecord { TimeUtc = now, ItemId = 1000, Amount = 1 });
            s = GatheringTracker.GetSummary();
            LogicTests.Eq("G7 暂存声望归到下一次采集（乱序 72 在 61 前）", s.TotalFame, 17L);
            LogicTests.Eq("G7 乱序认领后暂存清空", s.PendingFameCount, 0);

            // ⑤ 最近一条已带声望、间隔很短（<= 2s）的 72 → 重复事件，不覆盖不暂存
            GatheringTracker.Reset();
            now = new DateTime(2026, 11, 10, 3, 40, 0, DateTimeKind.Utc);
            GatheringTracker.Add(new GatheringRecord { TimeUtc = now, ItemId = 1000, Amount = 1 });
            GatheringTracker.UpdateLastFame(8);
            LogicTests.Check("G7 短间隔重复 72 被拒", !GatheringTracker.UpdateLastFame(9), "duplicate fame accepted");
            s = GatheringTracker.GetSummary();
            LogicTests.Eq("G7 短间隔重复 72 计入重复计数", s.DuplicateFameUpdates, 1);
            LogicTests.Eq("G7 短间隔重复 72 不改声望", s.TotalFame, 8L);
            LogicTests.Eq("G7 短间隔重复 72 不进入暂存", s.PendingFameCount, 0);
        }
        finally
        {
            GatheringTracker.UtcNowForTests = null;
            GatheringTracker.Reset();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 快照不可变拷贝 / Clear 与 Reset 语义 / 上限裁剪
    // ─────────────────────────────────────────────────────────────
    static void RunSnapshotAndResetTests()
    {
        GatheringTracker.Reset();
        var t0 = new DateTime(2026, 11, 10, 4, 0, 0, DateTimeKind.Utc);

        // 入参拷贝：Add 后修改原对象不影响统计
        var r = new GatheringRecord { TimeUtc = t0, ItemId = 1000, Amount = 4 };
        GatheringTracker.Add(r);
        r.Amount = 100;
        r.Fame = 100;
        r.ItemId = 9000;
        GatheringSummary s = GatheringTracker.GetSummary();
        LogicTests.Eq("G8 Add 入参被拷贝：改原对象不改数量", s.TotalAmount, 4L);
        LogicTests.Eq("G8 Add 入参被拷贝：改原对象不改类别", s.ByCategory["矿石"], 4L);

        // Snapshot 深拷贝：改返回值不影响内部
        GatheringTracker.Add(new GatheringRecord { TimeUtc = t0.AddSeconds(1), ItemId = 9000, Amount = 2, Fame = 1 });
        GatheringRecord[] snap = GatheringTracker.Snapshot(10);
        LogicTests.Eq("G8 快照条数 = 2", snap.Length, 2);
        LogicTests.Eq("G8 快照新在前", snap[0].ItemId, 9000);
        LogicTests.Eq("G8 快照尾部是较早记录", snap[1].ItemId, 1000);
        snap[0].Amount = 999;
        snap[0].ItemId = 1000;
        snap[0].Fame = 999;
        snap[1].Amount = 999;
        GatheringSummary afterMutate = GatheringTracker.GetSummary();
        LogicTests.Eq("G8 改快照不影响内部总量", afterMutate.TotalAmount, 6L);
        LogicTests.Eq("G8 改快照不影响内部声望", afterMutate.TotalFame, 1L);
        LogicTests.Eq("G8 改快照不影响内部未分类计数", afterMutate.UnclassifiedCount, 1);

        LogicTests.Eq("G8 Snapshot(0) 返回空", GatheringTracker.Snapshot(0).Length, 0);
        LogicTests.Eq("G8 Snapshot(-3) 返回空", GatheringTracker.Snapshot(-3).Length, 0);
        LogicTests.Eq("G8 Snapshot(1) 只取最新一条", GatheringTracker.Snapshot(1)[0].ItemId, 9000);

        // Clear：清数据、保留本次会话诊断计数；Reset：全部清零
        GatheringTracker.Reset();
        var r2 = new GatheringRecord { TimeUtc = t0, ItemId = 1000, Amount = 1 };
        GatheringTracker.Add(r2);
        GatheringTracker.Add(r2);          // 重复 → DuplicateAdds=1
        GatheringTracker.Add(null);        // 非法 → RejectedAdds=1
        GatheringSummary beforeClear = GatheringTracker.GetSummary();
        LogicTests.Eq("G8 Clear 前计数准备：重复 1", beforeClear.DuplicateAdds, 1);
        LogicTests.Eq("G8 Clear 前计数准备：非法 1", beforeClear.RejectedAdds, 1);

        GatheringTracker.Clear();
        GatheringSummary afterClear = GatheringTracker.GetSummary();
        LogicTests.Eq("G8 Clear 清空记录", afterClear.Count, 0);
        LogicTests.Eq("G8 Clear 清空总量", afterClear.TotalAmount, 0L);
        LogicTests.Eq("G8 Clear 清空快照", GatheringTracker.Snapshot(10).Length, 0);
        LogicTests.Eq("G8 Clear 保留诊断计数（重复）", afterClear.DuplicateAdds, 1);
        LogicTests.Eq("G8 Clear 保留诊断计数（非法）", afterClear.RejectedAdds, 1);

        GatheringTracker.Reset();
        GatheringSummary afterReset = GatheringTracker.GetSummary();
        LogicTests.Eq("G8 Reset 清空记录", afterReset.Count, 0);
        LogicTests.Eq("G8 Reset 清零诊断计数（重复）", afterReset.DuplicateAdds, 0);
        LogicTests.Eq("G8 Reset 清零诊断计数（非法）", afterReset.RejectedAdds, 0);
        LogicTests.Eq("G8 Reset 清零暂存声望", afterReset.PendingFameCount, 0);

        // 上限裁剪：只保留最新 MaxRecords 条，并计数
        GatheringTracker.Reset();
        const int extra = 5;
        for (int i = 0; i < GatheringTracker.MaxRecords + extra; i++)
        {
            GatheringTracker.Add(new GatheringRecord
            {
                TimeUtc = t0.AddMilliseconds(i),
                ItemId = 1000,
                Amount = 1
            });
        }
        GatheringSummary trimmed = GatheringTracker.GetSummary();
        LogicTests.Eq("G8 超过上限按 MaxRecords 裁剪", trimmed.Count, GatheringTracker.MaxRecords);
        LogicTests.Eq("G8 裁剪条数被计数", trimmed.TrimmedRecords, extra);
        LogicTests.Eq("G8 裁剪后总量 = 保留条数", trimmed.TotalAmount, (long)GatheringTracker.MaxRecords);
        LogicTests.Eq("G8 裁剪后快照条数 = MaxRecords", GatheringTracker.Snapshot(GatheringTracker.MaxRecords + 100).Length, GatheringTracker.MaxRecords);

        GatheringTracker.Reset();
    }

    // ─────────────────────────────────────────────────────────────
    // 并发：抓包线程写 + UI 线程读（GetSummary/Snapshot）不抛、内部自洽
    // ─────────────────────────────────────────────────────────────
    static void RunConcurrencyTests()
    {
        GatheringTracker.Reset();
        var t0 = new DateTime(2026, 11, 10, 5, 0, 0, DateTimeKind.Utc);
        const int threadCount = 4;
        const int perThread = 500;
        var errors = new List<string>();
        object errLock = new object();

        var writers = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            int tid = t;
            writers[t] = new Thread(delegate ()
            {
                try
                {
                    for (int i = 0; i < perThread; i++)
                    {
                        GatheringTracker.Add(new GatheringRecord
                        {
                            TimeUtc = t0.AddMilliseconds(tid * perThread + i),
                            ItemId = (tid % 2 == 0) ? 1000 : 9000,
                            Amount = 1
                        });
                        if (i % 50 == 0) GatheringTracker.UpdateLastFame(1);
                    }
                }
                catch (Exception ex)
                {
                    lock (errLock) { errors.Add("writer" + tid + ": " + ex.GetType().Name + " " + ex.Message); }
                }
            });
        }

        var reader = new Thread(delegate ()
        {
            try
            {
                for (int i = 0; i < 400; i++)
                {
                    GatheringSummary s = GatheringTracker.GetSummary();
                    if (s.ByCategory.Values.Sum() != s.TotalAmount)
                        lock (errLock) { errors.Add("summary 数量小计不自洽"); }
                    if (s.CountByCategory.Values.Sum() != s.Count)
                        lock (errLock) { errors.Add("summary 次数小计不自洽"); }
                    GatheringRecord[] snap = GatheringTracker.Snapshot(50);
                    for (int k = 0; k < snap.Length; k++)
                        if (snap[k] == null) lock (errLock) { errors.Add("snapshot 出现 null"); }
                }
            }
            catch (Exception ex)
            {
                lock (errLock) { errors.Add("reader: " + ex.GetType().Name + " " + ex.Message); }
            }
        });

        for (int t = 0; t < threadCount; t++) writers[t].Start();
        reader.Start();
        for (int t = 0; t < threadCount; t++) writers[t].Join();
        reader.Join();

        LogicTests.Check("G9 并发读写无异常", errors.Count == 0, string.Join(" | ", errors.ToArray()));
        GatheringSummary final = GatheringTracker.GetSummary();
        LogicTests.Eq("G9 并发后条数 = 线程数×每线程条数", final.Count, threadCount * perThread);
        LogicTests.Eq("G9 并发后总量 = 每条 1 的累加", final.TotalAmount, (long)(threadCount * perThread));
        LogicTests.Eq("G9 并发后数量小计自洽", final.ByCategory.Values.Sum(), final.TotalAmount);
        LogicTests.Eq("G9 并发后次数小计自洽", final.CountByCategory.Values.Sum(), final.Count);
        GatheringRecord[] all = GatheringTracker.Snapshot(threadCount * perThread + 10);
        LogicTests.Eq("G9 并发后快照条数一致", all.Length, threadCount * perThread);
        bool hasNull = false;
        for (int i = 0; i < all.Length; i++) if (all[i] == null) hasNull = true;
        LogicTests.Check("G9 并发后快照无 null 元素", !hasNull, "null element found");

        GatheringTracker.Reset();
    }
}
