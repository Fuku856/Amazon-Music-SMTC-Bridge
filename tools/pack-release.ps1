<#
  Builds a signed .msix for GitHub Releases.

  Package identity is mandatory (the userNotificationListener capability cannot be
  declared without it) but the Store is not: this produces a self-signed package
  plus the .cer that users must trust before installing.

  Outputs build\release\<OutputName>.msix and <OutputName>.cer
#>
[CmdletBinding()]
param(
    # Must match Identity/Publisher in the produced manifest; the script keeps them in sync.
    # Ignored when -PfxPath is given: there the certificate's own subject wins.
    [string]$PublisherSubject = 'CN=AmazonMusicSmtc',
    [string]$Version          = '0.1.0.0',
    [string]$CertPassword     = 'amazonmusicsmtc',
    # Existing signing certificate. Supply one in CI so every release is signed by
    # the same identity - a fresh self-signed cert would force users to trust a new
    # certificate on every update.
    [string]$PfxPath          = '',
    # Base name of the produced files, e.g. 'AmazonMusicSmtc-v1.0.0'.
    [string]$OutputName       = 'AmazonMusicSmtc'
)

$ErrorActionPreference = 'Stop'

$repo    = Split-Path -Parent $PSScriptRoot
$proj    = Join-Path $repo 'src\AmazonMusicSmtc\AmazonMusicSmtc.csproj'
$staging = Join-Path $repo 'build\release\staging'
$outDir  = Join-Path $repo 'build\release'
$toolDir = Join-Path $repo 'build\sdk-tools'
$dotnet  = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

# MSIX identities are always four-part; accept the friendlier 1.0.0 form too.
$parts = @($Version.Split('.'))
while ($parts.Count -lt 4) { $parts += '0' }
$Version = ($parts[0..3] -join '.')

# A certificate supplied up front dictates the publisher the manifest must claim,
# because signtool rejects a package whose Identity/Publisher differs from it.
$suppliedCert = $null
if ($PfxPath) {
    if (-not (Test-Path $PfxPath)) { throw "certificate not found: $PfxPath" }
    $suppliedCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
        (Resolve-Path $PfxPath).Path, $CertPassword,
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    $PublisherSubject = $suppliedCert.Subject
    Write-Host "using supplied certificate: $PublisherSubject" -ForegroundColor DarkGray
}

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
    # The package ships every architecture; picking the wrong one fails with
    # "not a valid application for this OS platform".
    $existing = Get-ChildItem $toolDir -Recurse -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
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
$msix = Join-Path $outDir "$OutputName.msix"
if (Test-Path $msix) { Remove-Item $msix -Force }

Write-Host "==> packing" -ForegroundColor Cyan
& $makeappx pack /d $staging /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed ($LASTEXITCODE)" }

# ----------------------------------------------------------------- signing --

$cer = Join-Path $outDir "$OutputName.cer"

if ($suppliedCert) {
    $pfx = (Resolve-Path $PfxPath).Path
    $ownPfx = $false
    Write-Host "certificate thumbprint: $($suppliedCert.Thumbprint)" -ForegroundColor DarkGray
    # The .cer is what users import: the public half of whatever signed the package.
    [System.IO.File]::WriteAllBytes($cer, $suppliedCert.Export(
        [System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
}
else {
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
    $ownPfx = $true
    $securePassword = ConvertTo-SecureString -String $CertPassword -Force -AsPlainText
    Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $pfx -Password $securePassword | Out-Null

    Export-Certificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $cer -Type CERT | Out-Null
}

Write-Host "==> signing" -ForegroundColor Cyan
& $signtool sign /fd SHA256 /f $pfx /p $CertPassword $msix
if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }

# The .pfx holds the private key and must not ship.
if ($ownPfx) { Remove-Item $pfx -Force }

Write-Host ""
Write-Host "package : $msix" -ForegroundColor Green
Write-Host "cert    : $cer" -ForegroundColor Green
Write-Host "Publish BOTH files; see README for the install steps." -ForegroundColor Yellow
