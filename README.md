# Albion Companion（阿尔比恩助手）

一个从零实现的《阿尔比恩 Online》桌面助手：常驻置顶的紧凑悬浮条，单击展开成详细面板，面板内包含总览、收益、战斗、市场、配装、设置六个功能页。

技术栈：.NET Framework 4.8 + WinForms，用系统自带 `csc.exe` 编译，不依赖任何第三方库或 NuGet 包。

## 直接运行

```
dist\AlbionCompanion.exe
```

启动后右上角出现悬浮条，没有任何安装步骤。

## 功能

| 页面 | 能做什么 |
| --- | --- |
| 悬浮条 | 显示本次在线时长、本次收益、实时时薪；单击展开面板；可拖动；右键菜单可重置会话 / 切换置顶 / 退出 |
| 总览 | 四张指标卡（在线时长、累计收益、时薪、配装成本）+ 快捷记录收益 + 最近明细 |
| 收益 | 按分类记账（采集 / 刷怪 / 副本 / 交易 / 其他），分类占比条形图、明细表、占比列、导出 CSV |
| 战斗 | 录入成员伤害 / 治疗 / 承伤 / 时长，自动算 DPS、HPS 并排序，输出承伤占比；可载入示例数据看效果 |
| 市场 | 输入物品 ID **或中文物品名**（自动解析成物品 ID），勾选城市，调用 albion-online-data.com 实时查询各城最低卖价 / 最高买价 / 更新时间；表格带官方物品图标，图标自动缓存到本地 |
| 配装 | 八个部位单价合计总成本，按每次击杀掉落估算回本次数 |
| 设置 | 窗口置顶、悬浮条透明度、全局热键、数据目录、合规说明 |

## 热键

- 默认 `Ctrl+Alt+A`：显示 / 隐藏悬浮条（面板打开时为收起面板）
- 支持格式：`Ctrl` / `Alt` / `Shift` / `Win` 组合 `A-Z`、`0-9`、`F1-F12`，在「设置」页可改
- 托盘图标双击也能展开面板

## 数据存放

配置：`%AppData%\AlbionCompanion\settings.json`
异常日志：`%AppData%\AlbionCompanion\error.log`

## 市场数据来源

行情报价：`https://www.albion-online-data.com/api/v2/stats/prices/{物品}?locations={城市}`
这是玩家上传成交报价的社区公共接口（不是官方接口），数据可能有几分钟到几小时的延迟。物品 ID 支持附魔写法，例如 `T5_2H_BOW@2`。

中文搜索：走 `https://www.albion-online-data.com/items?q=...&lang=ZH-CN`（同一域名下的检索页），
把中文物品名解析成物品 ID。注意它对中文是**全名匹配**：输入「老手级背包」可以，输入「背包」搜不到。
物品图标：`https://render.albiononline.com/v1/item/{物品ID}.png?quality={品质}`，
首次显示后缓存到 `%AppData%\AlbionCompanion\icons\`。

## 构建

```powershell
# 编译并运行逻辑测试（29 条断言）
pwsh -File build.ps1 -Test

# 编译程序到 dist\AlbionCompanion.exe
pwsh -File build.ps1

# 离线渲染检查：把六个页面导成 PNG（不抢焦点、不碰屏幕）
pwsh -c "$csc='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'; ..."   # 见 tests\VisualCheck.cs
```

`build.ps1` 会自动处理源文件编码（`/codepage:65001`），所以源码请保持 UTF-8。

## 目录结构

```
AlbionCompanion\
  src\
    Program.cs              入口、单实例、异常日志
    Core\                   纯逻辑层（无 UI 依赖，可被测试直接引用）
      AppCore.cs            收益台账、会话时钟、配装成本、设置存取
      MarketClient.cs       AODP 接口 URL 构造、JSON 解析、HTTP 拉取
      Runtime.cs            会话运行时状态
    UI\
      Theme.cs              暗色主题、圆角绘制、金额格式化
      Widgets.cs            自绘控件：导航项、表格、条形图、指标卡、暗色菜单
      OverlayForm.cs        悬浮条
      PanelForm.cs          详细面板（左导航 + 右功能区）
      CompanionApp.cs       托盘、全局热键、悬浮条与面板联动
      Tabs\                 六个功能页
  tests\
    LogicTests.cs           逻辑层测试（控制台断言）
    VisualCheck.cs          离线渲染 + 点击/热键交互断言
  preview\                  离线渲染出的界面截图
  dist\AlbionCompanion.exe  产物
  build.ps1
```

## 刻意没有做的部分

- 不读取游戏进程内存，不注入 DLL，不 hook 图形接口
- 不模拟键盘鼠标，不做自动战斗 / 自动采集 / 连点
- 不做反作弊规避

这些行为违反《阿尔比恩 Online》的使用条款，也有封号风险，所以战斗数据需要你手工录入或从自己的记录粘贴。市场与配装页是纯查询与本地计算，可以放心使用。

## 已知限制

- 战斗页是「复盘工具」，不是实时抓取器：数据必须手工录入，或点「载入示例」看展示效果
- 市场查询依赖第三方社区接口，接口不可用时会在页面顶部给出错误提示，不会崩溃
- 界面按 100% DPI 设计；在 150% 缩放的显示器上会由系统整体放大，略糊但布局不走样
