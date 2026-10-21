[CmdletBinding()]
param(
    [string]$PackagePath = 'build\hdr-widget\PersonalTools.HdrWidget.msix',
    [string]$DevelopmentDirectory = '',
    [switch]$RefreshWidgetHost = $true
)
$ErrorActionPreference = 'Stop'
if ($RefreshWidgetHost) {
    # Keep pins/settings intact. Stop only the catalog/board processes before registration,
    # so the next board activation reads the new provider definitions.
    Get-Process HdrWidget,WidgetBoard,Widgets,WidgetService -ErrorAction SilentlyContinue | Stop-Process -Force
}
if ($DevelopmentDirectory) {
    $source = (Resolve-Path -LiteralPath $DevelopmentDirectory).Path
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $source 'AppxManifest.xml') -Raw
    if ($manifest.Package.Identity.Name -ne 'PersonalTools.HdrWidget') { throw 'Not a Personal Tools HDR widget package.' }
    $developerMode = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue
    if ($developerMode.AllowDevelopmentWithoutDevLicense -ne 1) { throw 'Enable Windows Developer Mode before using development registration.' }
    $destination = Join-Path $env:LOCALAPPDATA ('Programs\PersonalTools\HdrWidget\dev-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath $source | Copy-Item -Destination $destination -Recurse -Force
    Add-AppxPackage -Register (Join-Path $destination 'AppxManifest.xml') -ForceApplicationShutdown
    $registered = Get-AppxPackage -Name 'PersonalTools.HdrWidget'
    if (-not $registered -or -not [string]::Equals($registered.InstallLocation, $destination, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Windows retained a previous widget registration. Increase the manifest version, rebuild, and register again.'
    }
    Write-Output "Registered unsigned development widget: $destination"
    Write-Output 'Win+W > Add widgets: Personal Tools HDR, 화면 꺼짐 방지, or Personal Tools vLLM. Keep this installed folder.'
    return
}
$package = (Resolve-Path -LiteralPath $PackagePath).Path
if ([IO.Path]::GetExtension($package) -ne '.msix') { throw 'Select the signed HDR widget MSIX package.' }
Add-AppxPackage -Path $package
Write-Output 'Installed Personal Tools widgets. Run Personal Tools once, then Win+W > Add widgets: Personal Tools HDR, 화면 꺼짐 방지, or Personal Tools vLLM.'
