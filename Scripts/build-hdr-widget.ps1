[CmdletBinding()]
param(
    [string]$DotNet = 'dotnet',
    [string]$OutputDirectory = 'build\hdr-widget',
    [string]$MakeAppx = '',
    [string]$CertificateThumbprint = '',
    [string]$SignTool = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
# A fresh staging directory prevents old SDK payloads from leaking into new packages.
$stage = Join-Path $output ('stage-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $stage | Out-Null
& $DotNet publish (Join-Path $root 'Sources\Windows\HdrWidget\HdrWidget.csproj') -c Release -r win-x64 --self-contained true -o $stage
if ($LASTEXITCODE -ne 0) { throw 'HDR widget publish failed.' }
if (-not $MakeAppx) {
    $sdk = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools\10.0.26100.6584\bin\10.0.26100.0\x64'
    $MakeAppx = Join-Path $sdk 'makeappx.exe'
    if (-not $SignTool) { $SignTool = Join-Path $sdk 'signtool.exe' }
}
if (-not (Test-Path -LiteralPath $MakeAppx)) { throw 'Specify -MakeAppx with the Windows SDK makeappx.exe path.' }
Copy-Item -LiteralPath (Join-Path $root 'Sources\Windows\HdrWidget\Package.appxmanifest') -Destination (Join-Path $stage 'AppxManifest.xml')
# MakeAppx does not perform the packaging project's Windows App SDK manifest merge.
# Include the widget runtime activation/proxy entries from the exact restored SDK payload.
$runtimeManifest = Join-Path $root 'Sources\Windows\HdrWidget\obj\Release\net8.0-windows10.0.19041.0\win-x64\MsixContent\AppxManifest.xml'
[xml]$runtimeXml = Get-Content -LiteralPath $runtimeManifest -Raw
[xml]$packageXml = Get-Content -LiteralPath (Join-Path $stage 'AppxManifest.xml') -Raw
$extensions = $packageXml.CreateElement('Extensions', $packageXml.DocumentElement.NamespaceURI)
$runtimeEntries = @($runtimeXml.Package.Extensions.Extension | Where-Object { $_.InnerXml -match 'Microsoft.Windows.Widgets.dll' })
if ($runtimeEntries.Count -lt 2) { throw 'Missing Windows App SDK widget activation metadata.' }
foreach ($entry in $runtimeEntries) { [void]$extensions.AppendChild($packageXml.ImportNode($entry, $true)) }
[void]$packageXml.DocumentElement.AppendChild($extensions)
$packageXml.Save((Join-Path $stage 'AppxManifest.xml'))
New-Item -ItemType Directory -Force (Join-Path $stage 'Assets'),(Join-Path $stage 'Public') | Out-Null

Add-Type -AssemblyName System.Drawing
$source = [Drawing.Image]::FromFile((Join-Path $root 'Sources\Windows\WinUI\Assets\PersonalTools.png'))
try {
    foreach ($asset in @(@('StoreLogo.png',50), @('Logo.png',150), @('SmallLogo.png',44))) {
        $bitmap = [Drawing.Bitmap]::new([int]$asset[1], [int]$asset[1])
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($source, 0, 0, [int]$asset[1], [int]$asset[1])
            $bitmap.Save((Join-Path $stage ('Assets\' + $asset[0])), [Drawing.Imaging.ImageFormat]::Png)
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $source.Dispose() }
# A static illustrative preview for the widget picker, using the actual card labels.
$preview = [Drawing.Bitmap]::new(600,400)
$canvas = [Drawing.Graphics]::FromImage($preview)
$font = [Drawing.Font]::new('Segoe UI',20)
$small = [Drawing.Font]::new('Segoe UI',15)
try {
    $canvas.Clear([Drawing.Color]::FromArgb(32,32,32))
    $canvas.DrawString('Personal Tools HDR',$font,[Drawing.Brushes]::White,24,24)
    for ($monitorIndex = 0; $monitorIndex -lt 4; $monitorIndex++) {
        $rowTop = 85 + $monitorIndex * 64
        $label = if ($monitorIndex -eq 0) { 'Monitor 1 - Primary' } else { 'Monitor ' + ($monitorIndex + 1) }
        $canvas.DrawString($label,$small,[Drawing.Brushes]::White,24,$rowTop)
        $toggleName = if ($monitorIndex -eq 0) { 'ToggleOn.png' } else { 'ToggleOff.png' }
        $toggleImage = [Drawing.Image]::FromFile((Join-Path $root ('Sources\Windows\HdrWidget\Assets\' + $toggleName)))
        try { $canvas.DrawImage($toggleImage,490,$rowTop,66,36) } finally { $toggleImage.Dispose() }
    }
    $canvas.DrawString('Refresh',$small,[Drawing.Brushes]::LightGray,24,350)
    $preview.Save((Join-Path $stage 'Assets\Preview.png'),[Drawing.Imaging.ImageFormat]::Png)
} finally { $small.Dispose(); $font.Dispose(); $canvas.Dispose(); $preview.Dispose() }

# Separate picker preview for the display-power widget.
$powerPreview = [Drawing.Bitmap]::new(600,400)
$powerCanvas = [Drawing.Graphics]::FromImage($powerPreview)
$powerTitle = [Drawing.Font]::new('Malgun Gothic',24)
$powerText = [Drawing.Font]::new('Malgun Gothic',17)
try {
    $powerCanvas.Clear([Drawing.Color]::FromArgb(32,32,32))
    $powerCanvas.DrawString('화면 꺼짐 방지',$powerTitle,[Drawing.Brushes]::White,24,24)
    $powerCanvas.DrawString('화면 꺼짐 방지 요청 1개',$powerText,[Drawing.Brushes]::White,24,96)
    $powerCanvas.DrawString('[PROCESS] chrome.exe',$powerText,[Drawing.Brushes]::White,24,154)
    $powerCanvas.DrawString('Video Wake Lock',$powerText,[Drawing.Brushes]::LightGray,24,192)
    $powerCanvas.DrawString('DISPLAY 요청 기준 · 5초마다 갱신',$powerText,[Drawing.Brushes]::LightGray,24,272)
    $powerCanvas.DrawString('새로 고침',$powerText,[Drawing.Brushes]::White,24,340)
    $powerPreview.Save((Join-Path $stage 'Assets\PowerPreview.png'),[Drawing.Imaging.ImageFormat]::Png)
} finally { $powerText.Dispose(); $powerTitle.Dispose(); $powerCanvas.Dispose(); $powerPreview.Dispose() }

$vllmPreview = [Drawing.Bitmap]::new(600,400)
$vllmCanvas = [Drawing.Graphics]::FromImage($vllmPreview)
$vllmTitle = [Drawing.Font]::new('Malgun Gothic',24)
$vllmText = [Drawing.Font]::new('Malgun Gothic',17)
try {
    $vllmCanvas.Clear([Drawing.Color]::FromArgb(32,32,32))
    $vllmCanvas.DrawString('Qwen3.8 · 27B',$vllmTitle,[Drawing.Brushes]::White,24,24)
    $vllmCanvas.DrawString('모델 시작 · 단계별 진행 확인',$vllmText,[Drawing.Brushes]::LightGray,24,100)
    for ($vllmStep = 0; $vllmStep -lt 7; $vllmStep++) {
        $vllmX = 28 + $vllmStep * 78
        $vllmCanvas.FillEllipse([Drawing.Brushes]::DimGray,$vllmX,166,42,42)
        $vllmCanvas.DrawString(($vllmStep + 1).ToString(),$vllmText,[Drawing.Brushes]::White,($vllmX + 10),170)
    }
    $vllmCanvas.DrawString('가중치 로딩 → 엔진 준비 → 워밍업',$vllmText,[Drawing.Brushes]::LightGray,24,240)
    $vllmCanvas.DrawString('vLLM 시작       새로 고침',$vllmText,[Drawing.Brushes]::White,24,328)
    $vllmPreview.Save((Join-Path $stage 'Assets\VllmPreview.png'),[Drawing.Imaging.ImageFormat]::Png)
} finally { $vllmText.Dispose(); $vllmTitle.Dispose(); $vllmCanvas.Dispose(); $vllmPreview.Dispose() }

$package = Join-Path $output 'PersonalTools.HdrWidget.msix'
[xml]$verifiedManifest = Get-Content -LiteralPath (Join-Path $stage 'AppxManifest.xml') -Raw
$definitions = @($verifiedManifest.SelectNodes("//*[local-name()='Definition']"))
if ('PersonalToolsVllm' -notin $definitions.Id) { throw 'vLLM widget definition missing.' }
foreach ($assetNode in $verifiedManifest.SelectNodes("//*[local-name()='Screenshot' or local-name()='Icon']")) {
    if (-not (Test-Path -LiteralPath (Join-Path $stage $assetNode.Path))) { throw "Missing widget asset: $($assetNode.Path)" }
}
$packLog = Join-Path $output 'makeappx.log'
& $MakeAppx pack /d $stage /p $package /o *> $packLog
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $packLog -Tail 30; throw 'HDR widget MSIX validation/packaging failed.' }
if ($CertificateThumbprint) {
    if (-not (Test-Path -LiteralPath $SignTool)) { throw 'Specify -SignTool with the Windows SDK signtool.exe path.' }
    & $SignTool sign /fd SHA256 /sha1 $CertificateThumbprint $package
    if ($LASTEXITCODE -ne 0) { throw 'Widget signing failed. Certificate subject must match CN=PersonalTools Local.' }
}
Write-Output "Widget package: $package"
if (-not $CertificateThumbprint) { Write-Output 'Unsigned: sign with a trusted certificate before installation. See docs/windows-hdr.md.' }
