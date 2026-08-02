<#
  Dev build + loose-layout registration.

  Uses Add-AppxPackage -Register, which grants package identity (and therefore the
  userNotificationListener capability) WITHOUT signing. Requires Developer Mode.
  Distribution builds are signed .msix instead - see tools/pack-release.ps1.
#>
[CmdletBinding()]
param(
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

$repo    = Split-Path -Parent $PSScriptRoot
$proj    = Join-Path $repo 'src\AmazonMusicSmtc\AmazonMusicSmtc.csproj'
$layout  = Join-Path $repo 'build\layout'
$dotnet  = 'C:\Program Files\dotnet\dotnet.exe'
$package = 'AmazonMusicSmtc'

Get-Process -Name $package -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

Write-Host "==> publishing" -ForegroundColor Cyan
& $dotnet publish $proj -c Debug -r win-x64 --self-contained false -o $layout --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Write-Host "==> staging package files" -ForegroundColor Cyan
Copy-Item (Join-Path $repo 'pkg\AppxManifest.xml') $layout -Force
Copy-Item (Join-Path $repo 'pkg\Assets') $layout -Recurse -Force

# Windows refuses to re-register the same version when the manifest changed, so
# always unregister first rather than requiring a version bump on every edit.
$existing = Get-AppxPackage -Name $package -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "==> unregistering $($existing.PackageFullName)" -ForegroundColor Cyan
    Remove-AppxPackage -Package $existing.PackageFullName
}

Write-Host "==> registering" -ForegroundColor Cyan
Add-AppxPackage -Register (Join-Path $layout 'AppxManifest.xml')

$pkg = Get-AppxPackage -Name $package
$aumid = "$($pkg.PackageFamilyName)!App"
Write-Host "registered: $($pkg.PackageFullName)" -ForegroundColor Green
Write-Host "AUMID     : $aumid" -ForegroundColor Green

if (-not $NoLaunch) {
    Start-Process 'explorer.exe' -ArgumentList "shell:appsFolder\$aumid"
    Write-Host "launched" -ForegroundColor Green
}
