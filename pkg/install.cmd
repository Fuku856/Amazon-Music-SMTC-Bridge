@echo off
rem Single-file installer shipped as a release asset.
rem
rem Two problems make a plain .cer useless on its own: double-clicking one imports
rem into the CurrentUser store while MSIX deployment only reads the LocalMachine
rem store, and users have no reason to guess that. So this does the whole thing.
rem
rem Everything below the marker line at the bottom is a PowerShell script: it is
rem written out to a temp .ps1 and run. Keeping the batch half ASCII-only avoids
rem cmd's codepage handling entirely - the Japanese text is read explicitly as
rem UTF-8. The batch half exits before reaching it. The split takes the LAST
rem occurrence of the marker, because the line performing the split names it too.

chcp 65001 >nul
setlocal
set "PS1=%TEMP%\AmazonMusicSmtc-install-%RANDOM%.ps1"

rem -Encoding UTF8 writes a BOM, which is what makes Windows PowerShell 5.1 read
rem the Japanese text back correctly. The .cmd itself must stay BOM-less.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$raw = Get-Content -LiteralPath '%~f0' -Raw -Encoding UTF8; Set-Content -LiteralPath '%PS1%' -Value $raw.Substring($raw.LastIndexOf(':POWERSHELL:') + 12) -Encoding UTF8"
if not exist "%PS1%" (
    echo Failed to prepare the installer script.
    pause
    exit /b 1
)

rem The trailing "." matters: %~dp0 ends in a backslash, which would escape the
rem closing quote and hand PowerShell a mangled path. Extra arguments are passed
rem through so the installer language can be forced with -Lang en or -Lang ja.
powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" "%~dp0." %*
set "RC=%ERRORLEVEL%"
del "%PS1%" >nul 2>&1
pause
exit /b %RC%

:POWERSHELL:
<#
  Downloads (or picks up) the package, trusts its signing certificate, installs it.
  Receives the folder the .cmd was launched from as $args[0].
#>
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
# Invoke-WebRequest spends most of its time drawing the progress bar.
$ProgressPreference = 'SilentlyContinue'

# Substituted at packaging time by tools\pack-release.ps1. Left as placeholders in
# the repo copy, which then only installs a package sitting next to it.
$MsixName   = '@MSIX_NAME@'
$MsixUrl    = '@MSIX_URL@'
$Thumbprint = '@THUMBPRINT@'

$here     = $args[0]
$appName  = 'AmazonMusic SMTC Bridge'
$pkgName  = 'AmazonMusicSmtc'
$download = $null

# English is the default. Japanese is selected automatically when the OS UI is
# Japanese; -Lang en / -Lang ja overrides that from the command line.
$lang = $null
for ($i = 1; $i -lt $args.Count; $i++) {
    if ($args[$i] -in @('-Lang', '--lang') -and ($i + 1) -lt $args.Count) {
        $lang = "$($args[$i + 1])".ToLowerInvariant()
        $i++
    }
}
if ($lang -notin @('en', 'ja')) {
    $lang = if ([Globalization.CultureInfo]::CurrentUICulture.TwoLetterISOLanguageName -eq 'ja') { 'ja' } else { 'en' }
}

$strings = @{
    en = @{
        Done                = 'Done'
        Error               = '[error]'
        InstallBanner       = 'Installing {0}'
        MultipleMsix        = 'Multiple .msix files were found. Leave only the version you want to install and run this again.'
        UsingLocalPackage   = 'Using the package in the same folder:'
        MsixNotFound        = ".msix was not found in the same folder.`n           Place it in the same folder as the .msix downloaded from the release and run this again."
        Downloading         = '[1/3] Downloading the package...'
        DownloadFailed      = "Download failed.`n           {0}"
        Verifying           = '[2/3] Checking the signature and trusting the certificate...'
        NotSigned           = 'The package is not signed. Download it again.'
        SignatureMismatch   = "The package signature does not match the expected one. The file may be corrupted or tampered with.`n           Expected: {0}`n           Actual: {1}"
        AlreadyTrusted      = 'Already registered, skipping'
        ConfirmAdmin        = 'Choose "Yes" in the administrator prompt'
        TrustFailed         = "Could not trust the certificate (exit code {0}).`n           Choose `"Yes`" in the administrator prompt and run this again."
        Installing          = '[3/3] Installing...'
        SameVersion         = "This version is already installed.`n            Uninstall it first to reinstall (settings and cache will be lost):`n`n             Get-AppxPackage -Name {0} | Remove-AppxPackage"
        InstallFailed       = "Installation failed.`n           {0}"
        InstallComplete     = 'Installation completed (version {0}).'
        StartingApp         = 'Starting the app. Settings can be changed from the taskbar tray icon.'
    }
    ja = @{
        Done                = '完了'
        Error               = '[エラー]'
        InstallBanner       = '{0} のインストール'
        MultipleMsix        = '.msix が複数見つかりました。使いたいバージョンだけを残して、もう一度実行してください。'
        UsingLocalPackage   = '同じフォルダーのパッケージを使用します:'
        MsixNotFound        = ".msix が同じフォルダーに見つかりません。`n           Release からダウンロードした .msix と同じ場所に置いて、もう一度実行してください。"
        Downloading         = '[1/3] パッケージをダウンロードしています...'
        DownloadFailed      = "ダウンロードに失敗しました。`n           {0}"
        Verifying           = '[2/3] 署名を確認し、証明書を信頼済みに登録します...'
        NotSigned           = 'パッケージが署名されていません。ダウンロードし直してください。'
        SignatureMismatch   = "パッケージの署名が想定と一致しません。ファイルが壊れているか、改ざんされている可能性があります。`n           想定: {0}`n           実際: {1}"
        AlreadyTrusted      = '登録済みのため省略します'
        ConfirmAdmin        = '管理者の確認画面で「はい」を選んでください'
        TrustFailed         = "証明書を信頼済みに登録できませんでした（終了コード {0}）。`n           管理者の確認画面で「はい」を選んで、もう一度実行してください。"
        Installing          = '[3/3] インストールしています...'
        SameVersion         = "同じバージョンが既にインストールされています。`n           入れ直すには先にアンインストールしてください（設定とキャッシュも消えます）:`n`n             Get-AppxPackage -Name {0} | Remove-AppxPackage"
        InstallFailed       = "インストールに失敗しました。`n           {0}"
        InstallComplete     = 'インストールが完了しました（バージョン {0}）。'
        StartingApp         = 'アプリを起動します。設定はタスクトレイのアイコンから行えます。'
    }
}
$t = $strings[$lang]

function Write-Step($text) { Write-Host "  $text" -ForegroundColor Cyan }
function Write-Done()      { Write-Host "        $($t.Done)" -ForegroundColor Green }
function Fail($text) {
    Write-Host ""
    Write-Host "  $($t.Error) $text" -ForegroundColor Red
    Write-Host ""
    exit 1
}

Write-Host ""
Write-Host "  $($t.InstallBanner -f $appName)"
Write-Host "  ======================================"
Write-Host ""

try {
    # --------------------------------------------------------------- Package --

    # A package sitting next to the installer wins: it lets people who downloaded
    # the .msix manually - or who are offline - use the same script. It has to be
    # THIS release's file though: a Downloads folder holding an older .msix must
    # not quietly turn this into an install of that older version.
    $msix = $null
    if ($here) {
        $filter = if ($MsixName -like '@*@') { '*.msix' } else { $MsixName }
        $local = @(Get-ChildItem -LiteralPath $here -Filter $filter -ErrorAction SilentlyContinue)
        if ($local.Count -gt 1) {
            Fail $t.MultipleMsix
        }
        if ($local.Count -eq 1) {
            $msix = $local[0].FullName
            Write-Host "  $($t.UsingLocalPackage)"
            Write-Host "  $msix"
            Write-Host ""
        }
    }

    if (-not $msix) {
        if ($MsixUrl -like '@*@') {
            Fail $t.MsixNotFound
        }
        Write-Step $t.Downloading
        Write-Host  "        $MsixUrl" -ForegroundColor DarkGray
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $download = Join-Path $env:TEMP $MsixName
        try {
            Invoke-WebRequest -Uri $MsixUrl -OutFile $download -UseBasicParsing
        }
        catch {
            Fail ($t.DownloadFailed -f $($_.Exception.Message))
        }
        $msix = $download
        Write-Done
        Write-Host ""
    }

    # ---------------------------------------------------------- Verification --

    Write-Step $t.Verifying

    $sig = Get-AuthenticodeSignature -LiteralPath $msix
    if (-not $sig.SignerCertificate) {
        Fail $t.NotSigned
    }
    # A pinned thumbprint turns "trust whatever signed this file" into "trust only
    # the certificate this release was built with".
    if ($Thumbprint -notlike '@*@' -and $sig.SignerCertificate.Thumbprint -ne $Thumbprint) {
        Fail ($t.SignatureMismatch -f $Thumbprint, $sig.SignerCertificate.Thumbprint)
    }

    $signer = $sig.SignerCertificate
    $trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $signer.Thumbprint }

    if ($trusted) {
        Write-Host "        $($t.AlreadyTrusted)" -ForegroundColor DarkGray
    }
    else {
        # The certificate comes out of the package itself, so no separate .cer
        # needs to be downloaded or kept next to this script.
        $cer = Join-Path $env:TEMP "$pkgName-signer.cer"
        [IO.File]::WriteAllBytes($cer, $signer.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))

        Write-Host "        $($t.ConfirmAdmin)" -ForegroundColor Yellow
        # Only this step is elevated. Installing as an administrator would put the
        # app on the administrator's account instead of the user's.
        $elevated = Start-Process -FilePath 'powershell' -Verb RunAs -Wait -PassThru -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command',
            "Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
        )
        Remove-Item $cer -Force -ErrorAction SilentlyContinue

        $trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $signer.Thumbprint }
        if (-not $trusted) {
            Fail ($t.TrustFailed -f $elevated.ExitCode)
        }
    }
    Write-Done
    Write-Host ""

    # ---------------------------------------------------------- Installation --

    Write-Step $t.Installing
    # An older build left running holds files the deployment needs to replace.
    Get-Process -Name $pkgName -ErrorAction SilentlyContinue | Stop-Process -Force
    try {
        Add-AppxPackage -Path $msix -ErrorAction Stop
    }
    catch {
        # Windows refuses to replace a package with different bits carrying the same
        # version, and no switch overrides it - the old one has to go first. That
        # takes the app's settings and cache with it, so it is the user's call.
        if ("$($_.Exception.Message)" -match '0x80073CFB') {
            Fail ($t.SameVersion -f $pkgName)
        }
        # Reinstalling an older release over a newer one needs an explicit opt-in.
        try {
            Add-AppxPackage -Path $msix -ForceUpdateFromAnyVersion -ErrorAction Stop
        }
        catch {
            Fail ($t.InstallFailed -f $($_.Exception.Message))
        }
    }
    Write-Done
    Write-Host ""

    $installed = Get-AppxPackage -Name $pkgName | Sort-Object Version | Select-Object -Last 1
    Write-Host "  $($t.InstallComplete -f $installed.Version)" -ForegroundColor Green
    Write-Host "  $($t.StartingApp)"
    if ($installed) {
        Start-Process explorer.exe ('shell:appsFolder\' + $installed.PackageFamilyName + '!App')
    }
    Write-Host ""
    exit 0
}
finally {
    if ($download -and (Test-Path $download)) { Remove-Item $download -Force -ErrorAction SilentlyContinue }
}
