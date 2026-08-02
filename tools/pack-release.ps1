<#
  Builds a signed .msix for GitHub Releases.

  Package identity is mandatory (the userNotificationListener capability cannot be
  declared without it) but the Store is not: this produces a self-signed package
  plus the .cer that users must trust before installing.

  Outputs build\release\AmazonMusicSmtc.msix and AmazonMusicSmtc.cer
#>
[CmdletBinding()]
param(
    # Must match Identity/Publisher in the produced manifest; the script keeps them in sync.
    [string]$PublisherSubject = 'CN=AmazonMusicSmtc',
    [string]$Version          = '0.1.0.0',
    [string]$CertPassword     = 'amazonmusicsmtc'
)

$ErrorActionPreference = 'Stop'

$repo    = Split-Path -Parent $PSScriptRoot
$proj    = Join-Path $repo 'src\AmazonMusicSmtc\AmazonMusicSmtc.csproj'
$staging = Join-Path $repo 'build\release\staging'
$outDir  = Join-Path $repo 'build\release'
$toolDir = Join-Path $repo 'build\sdk-tools'
$dotnet  = 'C:\Program Files\dotnet\dotnet.exe'

# ---------------------------------------------------------------- SDK tools --

function Get-SdkTools {
    # Prefer an installed Windows SDK.
    $kit = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (Test-Path $kit) {
        $found = Get-ChildItem $kit -Directory |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64' } |
            Where-Object { (Test-Path (Join-Path $_ 'makeappx.exe')) -and (Test-Path (Join-Path $_ 'signtool.exe')) } |
            Select-Object -First 1
        if ($found) { return $found }
    }

    # Otherwise pull them from the SDK build tools NuGet package - much smaller
    # than installing the whole Windows SDK just for two executables.
    $existing = Get-ChildItem $toolDir -Recurse -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($existing) { return $existing.Directory.FullName }

    Write-Host "==> fetching Windows SDK build tools from nuget.org" -ForegroundColor Cyan
    $index = Invoke-RestMethod 'https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/index.json'
    $version = ($index.versions | Where-Object { $_ -notmatch '-' } | Select-Object -Last 1)
    if (-not $version) { throw "could not resolve a stable Microsoft.Windows.SDK.BuildTools version" }

    New-Item -ItemType Directory -Force -Path $toolDir | Out-Null
    $nupkg = Join-Path $toolDir "buildtools.$version.zip"
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$version/microsoft.windows.sdk.buildtools.$version.nupkg" -OutFile $nupkg
    Expand-Archive $nupkg -DestinationPath (Join-Path $toolDir $version) -Force

    $found = Get-ChildItem (Join-Path $toolDir $version) -Recurse -Filter 'makeappx.exe' |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Select-Object -First 1
    if (-not $found) { throw "makeappx.exe not found in the build tools package" }
    return $found.Directory.FullName
}

$tools    = Get-SdkTools
$makeappx = Join-Path $tools 'makeappx.exe'
$signtool = Join-Path $tools 'signtool.exe'
Write-Host "using SDK tools: $tools" -ForegroundColor DarkGray

# ------------------------------------------------------------------- build --

Write-Host "==> publishing (self-contained, so users need no .NET runtime)" -ForegroundColor Cyan
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
& $dotnet publish $proj -c Release -r win-x64 --self-contained true -o $staging --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Write-Host "==> staging manifest" -ForegroundColor Cyan
Copy-Item (Join-Path $repo 'pkg\Assets') $staging -Recurse -Force

# Publisher and Version must be written into the manifest that gets packed,
# because signing fails if Identity/Publisher differs from the cert subject.
[xml]$manifest = Get-Content (Join-Path $repo 'pkg\AppxManifest.xml')
$manifest.Package.Identity.Publisher = $PublisherSubject
$manifest.Package.Identity.Version   = $Version
$manifest.Save((Join-Path $staging 'AppxManifest.xml'))

# Logs from dev runs must never ship.
Get-ChildItem $staging -Filter '*.log' -ErrorAction SilentlyContinue | Remove-Item -Force

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$msix = Join-Path $outDir 'AmazonMusicSmtc.msix'
if (Test-Path $msix) { Remove-Item $msix -Force }

Write-Host "==> packing" -ForegroundColor Cyan
& $makeappx pack /d $staging /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed ($LASTEXITCODE)" }

# ----------------------------------------------------------------- signing --

$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $PublisherSubject } | Select-Object -First 1
if (-not $cert) {
    Write-Host "==> creating self-signed certificate $PublisherSubject" -ForegroundColor Cyan
    # Long validity: an expired cert makes the package uninstallable for new users.
    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $PublisherSubject `
        -KeyUsage DigitalSignature `
        -FriendlyName 'Amazon Music SMTC Bridge signing' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears(10) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}
Write-Host "certificate thumbprint: $($cert.Thumbprint)" -ForegroundColor DarkGray

$pfx = Join-Path $outDir 'signing.pfx'
$securePassword = ConvertTo-SecureString -String $CertPassword -Force -AsPlainText
Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $pfx -Password $securePassword | Out-Null

Write-Host "==> signing" -ForegroundColor Cyan
& $signtool sign /fd SHA256 /a /f $pfx /p $CertPassword $msix
if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }

# The .cer is what users import; the .pfx holds the private key and must not ship.
$cer = Join-Path $outDir 'AmazonMusicSmtc.cer'
Export-Certificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $cer -Type CERT | Out-Null
Remove-Item $pfx -Force

Write-Host ""
Write-Host "package : $msix" -ForegroundColor Green
Write-Host "cert    : $cer" -ForegroundColor Green
Write-Host "Publish BOTH files; see README for the install steps." -ForegroundColor Yellow
