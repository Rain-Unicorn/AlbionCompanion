param([string]$ImagePath, [string]$OutFile)
Add-Type -AssemblyName System.Runtime.WindowsRuntime

# Load WinRT types
[Windows.Storage.StorageFile, Windows.Storage, ContentType=WindowsRuntime] | Out-Null
[Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType=WindowsRuntime] | Out-Null
[Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime] | Out-Null
[Windows.Globalization.Language, Windows.Foundation, ContentType=WindowsRuntime] | Out-Null

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]

function Await($AsyncOp, $ResultType) {
    $asTask = $asTaskGeneric.MakeGenericMethod($ResultType)
    $netTask = $asTask.Invoke($null, @($AsyncOp))
    $netTask.Wait(-1) | Out-Null
    $netTask.Result
}

$file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($ImagePath)) ([Windows.Storage.StorageFile])
$stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
$decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
$bmp = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])

$lang = New-Object Windows.Globalization.Language("zh-Hans-CN")
$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($lang)
if (-not $engine) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages() }

$result = Await ($engine.RecognizeAsync($bmp)) ([Windows.Media.Ocr.OcrResult])
$all = New-Object System.Collections.Generic.List[string]
foreach ($line in $result.Lines) {
    if ($line.Words.Count -eq 0) { continue }
    $rect = $line.Words[0].BoundingRect
    $rs = "$rect"
    # Rect ToString() -> "X,Y,W,H"
    $parts = $rs -split '[, ]+'
    $x = 0; $y = 0
    if ($parts.Count -ge 2) { [int]::TryParse($parts[0], [ref]$x) | Out-Null; [int]::TryParse($parts[1], [ref]$y) | Out-Null }
    $all.Add("$x`t$y`t$($line.Text)")
}
[System.IO.File]::WriteAllLines($OutFile, $all, (New-Object System.Text.UTF8Encoding($false)))
$stream.Dispose()
