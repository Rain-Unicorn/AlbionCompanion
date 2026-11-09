# AlbionCompanion 交接文档

## 项目概述
阿尔比恩Online第三方悬浮窗助手，C#/.NET 8 WinForms，通过RawSocket抓Photon UDP包实现伤害统计、队伍识别、银币收益等功能。参考SAT（StatisticsAnalysisTool）开源项目逻辑。

## GitHub仓库
- **地址**: https://github.com/Rain-Unicorn/AlbionCompanion
- **分支**: main
- **代理**: git已配置 http://127.0.0.1:10808 代理（国内网络必须）

## 构建与运行
```bash
cd AlbionCompanion
dotnet build
# 必须管理员权限运行（RawSocket抓包）
bin\Debug\net8.0-windows\AlbionCompanion.exe
```
- 桌面快捷方式已创建：`Desktop\AlbionCompanion.lnk`（已勾选管理员运行）
- 运行必须管理员权限，否则RawSocket抓包失败

## 技术栈
- .NET 8 Windows Forms
- RawSocket抓UDP包（非Pcap）
- Photon Protocol 18 解析
- 无外部依赖、无OCR、不接入wanlink.tech接口

## 项目结构
```
src/
  Program.cs              # 入口，启动抓包+UI
  GlobalUsings.cs
  Core/
    AppCore.cs            # 应用核心
    Runtime.cs            # 运行时状态
    ChatSniffer.cs        # 抓包主逻辑+事件分发+文件日志(events.log)
    PhotonParser.cs      # Photon协议解析
    GameEvents.cs        # 全局状态（总伤害/治疗/击杀/队伍成员/本地玩家ObjectId）
    PacketMonitor.cs
    ItemDatabase.cs / ItemCatalog.cs / ItemDb.cs
    MarketClient.cs / MarketDb.cs / Arbitrage.cs
    MobDatabase.cs
    GatherCategories.cs
    OcrHelper.cs         # OCR辅助（当前未启用）
    DamageMeter/
      RollingDamageWindow.cs   # 5秒滑动窗口DPS
      DamageStatsTracker.cs    # 伤害/治疗统计，Pve/Pvp筛选
      DamageStatsPlayer.cs     # 单人统计（PveDamage/PvpDamage字段）
      DamageStatsEvent.cs
    Protocol18/
      Protocol18Deserializer.cs / Reader.cs / Stream.cs / Type.cs
      Photon/  # CrcCalculator, EventData, NumberDeserializer, OperationRequest/Response
  UI/
    CompanionApp.cs      # 主窗口
    OverlayForm.cs       # 悬浮窗
    PanelForm.cs         # 侧边栏面板
    Theme.cs             # 主题色
    Widgets.cs
    Tabs/
      CombatTab.cs       # 战斗统计（按妙搭原型重写）
      OverviewTab.cs     # 总览
      IncomeTab.cs      # 收益
      NearbyPlayersTab.cs
      MarketTab.cs / TradeTab.cs / BuildTab.cs / HarvestTab.cs
      LiveTab.cs / LocalDbTab.cs / AutoUpdateTab.cs / SettingsTab.cs
      TabBase.cs
```

## 已实现功能
1. **伤害统计**: 按causerId汇总，DPS/峰值/最大单次伤害/百分比，PVE/PVP筛选
2. **多人伤害拆分bug修复**: 本地玩家不同伤害源（直接攻击/召唤物/DoT）统一用trackerKey=0合并
3. **队伍识别**: evt=230(PartyJoined)，p9=名字数组（已包含本地玩家），组队时过滤只显示队伍成员
4. **银币收益**: evt=62(TakeSilver)，p0=拾取者ObjectId与LocalPlayerObjectId比对，银币=(税前-区域税-公会税-联盟税)/10000
5. **战斗Tab UI**: 按妙搭原型重写——大数字统计、pill筛选按钮、卡片式伤害/治疗列表

## 实际抓包确认的事件码
| 事件码 | 含义 | 关键参数 |
|--------|------|----------|
| 230 | PartyJoined | p8=GUID字节数组, p9=名字数组 |
| 234 | PartyPlayerJoined | p0=ObjectId, p1=GUID字节 |
| 237 | PartyDisbanded | 预估，未实际抓到验证 |
| 239 | PartyPlayerUpdated | p0=名字, p1=True, p3=5 |
| 241 | PartyLeaderChanged | |
| 242 | PartyLootSettingChangedPlayer | |
| 243 | PartySilverGained | |
| 62 | TakeSilver | p0=拾取者ObjectId, p3=税前, p4=区域税, p5=公会税, p6=联盟税, p8=倍率 |
| 81 | UpdateMoney | p0=ObjectId, p1=当前银币(FixPoint/10000) |
| 61 | HarvestFinished | |
| 72 | UpdateFame | |
| 104 | NewCharacter | |

**注意**: SAT源码EventCodes注释里的252:xxx是旧协议码，实际事件码是枚举序号（从Unused=0开始计数），必须用events.log抓包验证。

## 关键技术决策
- **FixPoint定点数**: 游戏内银币=InternalValue/10000
- **本地玩家ObjectId**: 从JoinResponse(OperationCode=0)获取，但中途启动会错过。Fallback：第一次TakeSilver时自动把拾取者ID识别为本地玩家
- **队伍状态重启丢失**: PartyJoined事件只在刚进队瞬间发一次，中途启动抓包会错过，需退组重进
- **self伤害合并**: 不在_playerNames也不在_mobNames的causerId统一用trackerKey=0
- **UI配色**: Bg(15,17,21), Card(23,26,32), Accent金(216,169,74), Good绿(94,200,148), Bad红(226,108,108)

## 已知问题 / TODO
1. **队伍状态重启丢失**（R7/C6 已解决）: party-cache.json 持久化跨会话名单，启动时 LoadOrRestore 载入并降级 verified=false（StaleFromPreviousSession），战斗页显示「缓存未验证」横幅 + 「本次不使用旧名单」按钮；进队（evt=230/234）自动转正
2. **PartyDisbanded事件码(预估237)未验证**（C6: OnPartyDisbanded 已实现，evt=237 分支打印原始参数待实机确认；若实机发现 237 不是解散，改为只打日志）
3. **evt=234(PartyPlayerJoined)只打日志**（C6 已解决）: GUID 匹配补 ObjectId；未命中新增 pendingName 成员，evt=29 OnNewCharacter 反查补名（唯一候选才提交，10 分钟超时降级 UnresolvedMember）
4. **LocalPlayerObjectId fallback局限**: 只在第一次捡钱时触发，启动后一直没捡钱则LocalPlayerObjectId为null
5. **收益模块**: evt=71(UpdateMoney)在旧版注释里，实际是evt=81，已改用TakeSilver方式
6. **数据目录**: data/目录（items.json/mobs.json等）已在.gitignore中，需要从SAT项目或Albion Data Project获取
7. **evt=239/241/243 参数布局未实机验证**（C6）: 每个分支 ReportStatus 打印完整原始参数（DescribeParams），实机确认前按保守语义处理（239 不加人、241 只换队长、243 只刷活跃度不碰金额——金额归 C7 收益模块）

## C6 队伍状态架构（R7）
- **唯一来源**: `src/Core/Party/PartyState.cs`——五事件增量重建（230 全量重建/GUID 绑定、234 GUID 精确匹配或 pendingName 入队、239 保守刷新、241 双路兜底换队长、243 只刷活跃度）+ evt=29 pending 反查
- **落盘**: `src/Core/Party/PartyCacheStore.cs`——party-cache.json（与 settings.json 同目录），schemaVersion=1，原子写（.tmp+File.Replace），2 秒防抖，解析失败/schemaVersion>1 降级空名单
- **数据诚实性**: verified=false 时战斗页横幅「缓存未验证」+ 卡片标题「（名单未验证）」+「本次不使用旧名单」按钮；仍按旧名单过滤（比不过滤更接近预期）但必须显式标注为推断
- **GameEvents 转发**: IsInParty/PartyMemberCount/PartyMemberSnapshot/IsPartyMember 全部转发 PartyState；旧 ClearParty/AddPartyName 已删除（写路径收敛）
- **测试**: T-11（增量重建 16 断言）、T-12（pending 反查三态 5 断言）、T-13/T-13b（缓存降级与抑制 19 断言），累计 216 断言全绿

## C7 收益与总览架构（§8）
- **IncomeTracker 扩展**: GetSummary() 增加来源小计（SilverPickup/PartySilver/GatheringSale）与金额待确认计数；evt=243 记「队伍分成、金额=0、待确认」条目（AttributionNote=金额字段未确认，AmountUnconfirmed=true），金额字段位置实机确认前（V5）绝不猜测
- **evt=61 归属裁决**: 设计内部矛盾（§8.3.2 表说采集出售无抓包事件/手工账不并入，T-15/V7 写「61 记 GatheringSale」）——按详表 §8.3.2 裁决：evt=61 只落 GatheringTracker（采集统计），不产生 GatheringSale 收益条目；手工账本（Runtime.IncomeLedger）维持现状不并入抓包合计
- **GatheringTracker**: `src/Core/Gathering/GatheringTracker.cs`——Add/UpdateLastFame/GetSummary（总量/声望/次数/按类别小计）/Snapshot/Reset；类别复用 GatherCategories（非矿石 baseId 是「假设待确认」，1020~1024 与矿石 4 级附魔 ID 重叠，非矿石类别归属不可靠——实机校准前总览小计仅矿石可信）
- **JSONL 持久化**: `{DefaultDir}/income/income-YYYY-MM-DD.jsonl`（本地日期切分），入账即入队、后台写线程追加（不在抓包线程 IO），挂起条目不落盘（转正时补写）；启动 LoadRecent() 只加载当天+前一天；中文不转义（UnsafeRelaxedJsonEscaping）
- **CSV 导出**: IncomeTab「导出 CSV」按钮 → SaveFileDialog（默认名 income-YYYYMMDD-HHMM.csv）；8 列固定顺序、UTF8 BOM、\r\n、Quote 复用 IncomeLedger.Quote（已改 internal）
- **同源取数**: 总览采集卡片/收益卡片、收益页大数字、明细表全部改读 GatheringTracker.GetSummary()/IncomeTracker.GetSummary()/Snapshot()；收益明细列改「时间/来源/银币/待确认」，pending 与金额待确认条目金额列显示「待确认」
- **测试**: T-15（来源分流+243 形态+采集聚合+小计自洽）、T-15b（JSONL 落盘/重载/CSV BOM+表头+行数），累计 246 断言全绿；编译 0 错误/130 警告（与基线零新增）

## P0-1 JoinResponse 取证（复核补齐 2026-11-09）

- **现状**：判定已收紧为「OperationCode 精确等值匹配」，但权威码值尚未实机取证 → 默认 `JoinResponseOperationCode = null`，
  **完全不确认身份**（有意保守：不再「非零即接受」，避免无关响应把用户累计伤害清零）。
- **取证**：实机启动游戏并登录一次；`events.log` 会出现 `[OpResp] opCode=.. ret=.. keys=[..]` 行
  （复核前该行只进 UI、不落盘，取证链路是断的，现已修复为同时写 events.log）。
  同时 `[OpRespBytes] len=.. hex=..` 记录原始字节（前 30 条）：实机首跑发现 9 条不同 keys 的响应
  **opCode 全为 1**，说明 opCode 读取位置/协议布局与实现不符 —— 在字节布局确认前，**禁止**把 1 填进白名单
  （那会让任意带 p0/p2 的响应确认身份，正是 P0-1 要封的洞）。
- **收口**：取 `ret=0` 且 `keys` 含 0（UserObjectId）、2（Username）的那条稳定 `opCode`，填进
  `%AppData%\AlbionCompanion\settings.json` 的 `JoinResponseOperationCode`（0=禁用），重启即启用。
  码值越界/负值会回落 0，绝不把脏配置当白名单。
- **启用后**：身份主路径（服务器权威）恢复；evt=81 余额严格匹配 / evt=29 名字命中 / 手选名兜底保持不变。
- **未清 P0（需产品口径）**：evt=62「首个拾取者即候选」在组队场景可能锁到队友，本人拾取会被静默 Ignore
  （候选无超时、无人工出口）。修法涉及归属语义，未擅自改，见复核报告 P0-3。

## 实机身份证据补充（2026-11-09 取证）

- **角色名来自运行时事件**（示例 `TestChar` / 另一角色 `TestChar2`）：程序不写死任何玩家的名字或 ObjectId。
- **`evt=278` = 本人银币入袋**：`p0=ObjectId`、`p2=角色名`、`p3=True`、`p5=税后原始值`。
  实测 `p5` 与 `evt=62` 的「p3−税」、`evt=81` 的余额差**逐位相等**；同一时刻 62/81/278 只有本地玩家一个 id。
- **ObjectId 会随换图/换实例变化**（实测每次换图都不同），
  因此身份不能只按 id 锚定：已按「角色名」重绑（`LocalPlayerIdentity.OnSelfSilverGained`）。
- **`evt=29` 里从来没有自己**（实测 85 个名字无本人）→ 「设置里填角色名 + 等 evt=29」兜底实机不可用；
  建议在设置页把「我的角色」填成真实角色名，`evt=278` 一到即确认。

## 调试
- 事件日志: `bin\Debug\net8.0-windows\events.log`（记录所有游戏事件码和参数）
- 调试时通过events.log确认新事件的真实码和参数布局

## 用户偏好
- 用户玩《阿尔比恩》，使用鲜牛加速器（XianNiu.exe），优化电脑时保留加速器进程
- 用户在大连，国内网络，GitHub访问需代理
- 用户偏好基于SAT源码逻辑实现，拒绝重复试错
- 项目对齐SAT开源项目：https://github.com/Rezaford/AlbionOnline-StatisticsAnalysis
