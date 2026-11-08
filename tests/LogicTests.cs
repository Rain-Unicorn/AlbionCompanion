using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using AlbionCompanion.Core;
using AlbionCompanion.Core.Income;

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

    // 从 albion-online-data.com/items 实际抓取的一行结果 HTML（截取三行）
    const string SampleSearchHtml = "<table class=\"items-table\"><tbody>"
        + "<tr><td class=\"items-table__index\">2888</td><td class=\"items-table__unique\"><code>T4_BAG</code></td><td class=\"items-table__name\">\r\n  老手级背包\r\n </td></tr>"
        + "<tr><td class=\"items-table__index\">2890</td><td class=\"items-table__unique\"><code>T4_BAG@2</code></td><td class=\"items-table__name\">老手级背包</td></tr>"
        + "<tr><td class=\"items-table__index\">2913</td><td class=\"items-table__unique\"><code>T4_BAG_INSIGHT</code></td><td class=\"items-table__name\">老手级洞察背包</td></tr>"
        + "</tbody></table>";

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
        Check("A4 URL 含物品列表", url.Contains("T4_BAG,T5_2H_BOW@2"), url);
        Check("A4 URL 含城市参数", url.Contains("locations=Caerleon,Bridgewatch"), url);
        Check("A4 URL 指向 AODP", url.StartsWith("https://www.albion-online-data.com/api/v2/stats/prices/"), url);

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

        // A8 物品搜索 / 图标
        string searchUrl = ItemCatalog.BuildSearchUrl("老手级背包", "ZH-CN");
        Check("A8 搜索URL指向 items 页", searchUrl.StartsWith("https://www.albion-online-data.com/items?q="), searchUrl);
        Check("A8 搜索URL带语言", searchUrl.Contains("lang=ZH-CN"), searchUrl);
        Check("A8 中文查询被 URL 编码", searchUrl.Contains("%E8"), searchUrl);

        List<ItemDef> found = ItemCatalog.ParseSearchHtml(SampleSearchHtml);
        Eq("A8 解析搜索结果条数", found.Count, 3);
        Eq("A8 结果含 T4_BAG", found[0].UniqueName, "T4_BAG");
        Eq("A8 中文名", found[0].DisplayName, "老手级背包");
        Eq("A8 结果含附魔变体", found[1].UniqueName, "T4_BAG@2");

        List<ItemDef> none = ItemCatalog.ParseSearchHtml("<p class=\"items-results__empty\">No items match \"x\".</p>");
        Eq("A8 无结果页面解析为空", none.Count, 0);

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
                        long inc = GameEvents.TotalIncome;
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
            // 合法 JoinResponse：opCode 非零、returnCode=0、ObjectId=1001、Name="AB"
            byte[] valid = new byte[] {
                42,          // opCode（非零）
                0x00, 0x00,  // returnCode = 0 (LE)
                0x08,        // debugMessage 类型码 = Null（空调试串）
                0x02,        // 参数表 2 条
                0x00, 17, 0xE9, 0x03,          // key=0, Long2, 1001 (LE)
                0x02, 7,  0x02, (byte)'A', (byte)'B' // key=2, String, "AB"
            };
            long parsedId; string parsedName;
            bool ok = PhotonParser.TryParseJoinResponse(new ReadOnlySpan<byte>(valid), out parsedId, out parsedName);
            Check("T-01 合法响应被识别为 JoinResponse", ok, "ok=false");
            Eq("T-01 ObjectId 解析 = 1001", ok ? parsedId : 0, 1001L);
            Eq("T-01 Username 解析 = AB", ok ? parsedName : null, "AB");

            // 异常输入 1：opCode=0（旧代码的致命默认值）
            byte[] zeroOp = (byte[])valid.Clone(); zeroOp[0] = 0;
            long id2; string name2;
            bool ok2 = false;
            try { ok2 = PhotonParser.TryParseJoinResponse(new ReadOnlySpan<byte>(zeroOp), out id2, out name2); }
            catch { ok2 = false; }
            Check("T-01 opCode=0 不视为 JoinResponse", !ok2, "ok2=true");

            // 异常输入 2：参数表为空（无 UserObjectId）
            byte[] emptyParams = new byte[] { 42, 0x00, 0x00, 0x08, 0x00 };
            bool ok3 = false;
            try { ok3 = PhotonParser.TryParseJoinResponse(new ReadOnlySpan<byte>(emptyParams), out id2, out name2); }
            catch { ok3 = false; }
            Check("T-01 空参数表不写身份", !ok3, "ok3=true");

            // 异常输入 3：参数表截断（在 value 中间截断）
            byte[] truncated = new byte[8];
            Array.Copy(valid, truncated, 8);
            bool ok4 = false; bool threw = false;
            try { ok4 = PhotonParser.TryParseJoinResponse(new ReadOnlySpan<byte>(truncated), out id2, out name2); }
            catch { threw = true; }
            Check("T-01 截断输入：不确认身份且不抛到调用方", (!ok4) && (!threw),
                  "ok4=" + ok4 + " threw=" + threw);

            // 异常输入 4：ObjectId 无效（0）
            byte[] zeroId = new byte[] { 42, 0x00, 0x00, 0x08, 0x01, 0x00, 17, 0x00 };
            bool ok5 = false;
            try { ok5 = PhotonParser.TryParseJoinResponse(new ReadOnlySpan<byte>(zeroId), out id2, out name2); }
            catch { ok5 = false; }
            Check("T-01 ObjectId<=0 不写身份", !ok5, "ok5=true");

            // 端到端：内核识别出的身份经 OnJoinResponse 写入状态机（C3 起 GameEvents.LocalPlayerObjectId
            // 是只读转发，禁止直写；直接写会编译失败，这正是兼容层的意图）
            LocalPlayerIdentity.Reset();
            IncomeTracker.Reset();
            if (ok)
            {
                LocalPlayerIdentity.OnJoinResponse(parsedId, parsedName);
            }
            Eq("T-01 身份经状态机写入（GameEvents 只读转发）", GameEvents.LocalPlayerObjectId, 1001L);
            Eq("T-01 身份名写入", GameEvents.LocalPlayerName, "AB");
            Eq("T-01 确认来源 = JoinResponse", LocalPlayerIdentity.CurrentSource, ConfirmSource.JoinResponse);
            Eq("T-01 状态 = Confirmed", LocalPlayerIdentity.State, IdentityState.Confirmed);

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

        Console.WriteLine();
        Console.WriteLine("passed=" + _pass + " failed=" + _fail);
        return _fail == 0 ? 0 : 1;
    }
}
