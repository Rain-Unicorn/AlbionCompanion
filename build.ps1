param(
    [switch]$Test
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'

if (-not (Test-Path -LiteralPath $csc)) {
    Write-Host "未找到 .NET Framework 编译器：$csc" -ForegroundColor Red
    exit 1
}

$coreSources = @(
    "$root\src\Core\AppCore.cs",
    "$root\src\Core\MarketClient.cs",
    "$root\src\Core\ItemCatalog.cs",
    "$root\src\Core\MarketDb.cs",
    "$root\src\Core\Market\ItemIndex.cs",
    "$root\src\Core\Market\MarketDataService.cs",
    "$root\src\Core\Arbitrage.cs",
    "$root\src\Core\Runtime.cs",
    "$root\src\Core\OcrHelper.cs",
    "$root\src\Core\PacketMonitor.cs",
    "$root\src\Core\ChatSniffer.cs",
    "$root\src\Core\PhotonParser.cs",
    "$root\src\Core\Protocol18.cs",
    "$root\src\Core\GatherCategories.cs",
    "$root\src\Core\ItemDatabase.cs",
    "$root\src\Core\MobDatabase.cs",
    "$root\src\Core\GameEvents.cs"
)

$uiSources = @(
    "$root\src\Program.cs",
    "$root\src\UI\Theme.cs",
    "$root\src\UI\Widgets.cs",
    "$root\src\UI\OverlayForm.cs",
    "$root\src\UI\PanelForm.cs",
    "$root\src\UI\CompanionApp.cs",
    "$root\src\UI\Tabs\TabBase.cs",
    "$root\src\UI\Tabs\OverviewTab.cs",
    "$root\src\UI\Tabs\IncomeTab.cs",
    "$root\src\UI\Tabs\CombatTab.cs",
    "$root\src\UI\Tabs\MarketTab.cs",
    "$root\src\UI\Tabs\TradeTab.cs",
    "$root\src\UI\Tabs\HarvestTab.cs",
    "$root\src\UI\Tabs\LocalDbTab.cs",
    "$root\src\UI\Tabs\AutoUpdateTab.cs",
    "$root\src\UI\Tabs\BuildTab.cs",
    "$root\src\UI\Tabs\LiveTab.cs",
    "$root\src\UI\Tabs\SettingsTab.cs"
)

$common = @(
    '/nologo',
    '/codepage:65001',
    "/r:$fw\System.dll",
    "/r:$fw\System.Windows.Forms.dll",
    "/r:$fw\System.Drawing.dll",
    "/r:$fw\System.Web.Extensions.dll",
    "/r:$fw\System.Net.Http.dll"
)

if ($Test) {
    & $csc @common /target:exe /out:"$root\tests\LogicTests.exe" "$root\tests\LogicTests.cs" @coreSources
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & "$root\tests\LogicTests.exe"
    exit $LASTEXITCODE
}

if (-not (Test-Path -LiteralPath "$root\dist")) {
    New-Item -ItemType Directory -Path "$root\dist" | Out-Null
}

& $csc @common /target:winexe /out:"$root\dist\AlbionCompanion.exe" @uiSources @coreSources
if ($LASTEXITCODE -ne 0) {
    Write-Host "编译失败，退出码 $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "已生成：$root\dist\AlbionCompanion.exe" -ForegroundColor Green
exit 0
