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

## 调试
- 事件日志: `bin\Debug\net8.0-windows\events.log`（记录所有游戏事件码和参数）
- 调试时通过events.log确认新事件的真实码和参数布局

## 用户偏好
- 用户玩《阿尔比恩》，使用鲜牛加速器（XianNiu.exe），优化电脑时保留加速器进程
- 用户在大连，国内网络，GitHub访问需代理
- 用户偏好基于SAT源码逻辑实现，拒绝重复试错
- 项目对齐SAT开源项目：https://github.com/Rezaford/AlbionOnline-StatisticsAnalysis
