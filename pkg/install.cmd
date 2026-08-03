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
rem closing quote and hand PowerShell a mangled path.
powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" "%~dp0."
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
$appName  = 'Amazon Music SMTC Bridge'
$pkgName  = 'AmazonMusicSmtc'
$download = $null

function Write-Step($text) { Write-Host "  $text" -ForegroundColor Cyan }
function Write-Done()      { Write-Host "        完了" -ForegroundColor Green }
function Fail($text) {
    Write-Host ""
    Write-Host "  [エラー] $text" -ForegroundColor Red
    Write-Host ""
    exit 1
}

Write-Host ""
Write-Host "  $appName のインストール"
Write-Host "  ======================================"
Write-Host ""

try {
    # ------------------------------------------------------------- パッケージ --

    # A package sitting next to the installer wins: it lets people who downloaded
    # the .msix manually - or who are offline - use the same script. It has to be
    # THIS release's file though: a Downloads folder holding an older .msix must
    # not quietly turn this into an install of that older version.
    $msix = $null
    if ($here) {
        $filter = if ($MsixName -like '@*@') { '*.msix' } else { $MsixName }
        $local = @(Get-ChildItem -LiteralPath $here -Filter $filter -ErrorAction SilentlyContinue)
        if ($local.Count -gt 1) {
            Fail ".msix が複数見つかりました。使いたいバージョンだけを残して、もう一度実行してください。"
        }
        if ($local.Count -eq 1) {
            $msix = $local[0].FullName
            Write-Host "  同じフォルダーのパッケージを使用します:"
            Write-Host "  $msix"
            Write-Host ""
        }
    }

    if (-not $msix) {
        if ($MsixUrl -like '@*@') {
            Fail ".msix が同じフォルダーに見つかりません。`n           Release からダウンロードした .msix と同じ場所に置いて、もう一度実行してください。"
        }
        Write-Step "[1/3] パッケージをダウンロードしています..."
        Write-Host  "        $MsixUrl" -ForegroundColor DarkGray
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $download = Join-Path $env:TEMP $MsixName
        try {
            Invoke-WebRequest -Uri $MsixUrl -OutFile $download -UseBasicParsing
        }
        catch {
            Fail "ダウンロードに失敗しました。`n           $($_.Exception.Message)"
        }
        $msix = $download
        Write-Done
        Write-Host ""
    }

    # ----------------------------------------------------------------- 検証 --

    Write-Step "[2/3] 署名を確認し、証明書を信頼済みに登録します..."

    $sig = Get-AuthenticodeSignature -LiteralPath $msix
    if (-not $sig.SignerCertificate) {
        Fail "パッケージが署名されていません。ダウンロードし直してください。"
    }
    # A pinned thumbprint turns "trust whatever signed this file" into "trust only
    # the certificate this release was built with".
    if ($Thumbprint -notlike '@*@' -and $sig.SignerCertificate.Thumbprint -ne $Thumbprint) {
        Fail "パッケージの署名が想定と一致しません。ファイルが壊れているか、改ざんされている可能性があります。`n           想定: $Thumbprint`n           実際: $($sig.SignerCertificate.Thumbprint)"
    }

    $signer = $sig.SignerCertificate
    $trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $signer.Thumbprint }

    if ($trusted) {
        Write-Host "        登録済みのため省略します" -ForegroundColor DarkGray
    }
    else {
        # The certificate comes out of the package itself, so no separate .cer
        # needs to be downloaded or kept next to this script.
        $cer = Join-Path $env:TEMP "$pkgName-signer.cer"
        [IO.File]::WriteAllBytes($cer, $signer.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))

        Write-Host "        管理者の確認画面で「はい」を選んでください" -ForegroundColor Yellow
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
            Fail "証明書を信頼済みに登録できませんでした（終了コード $($elevated.ExitCode)）。`n           管理者の確認画面で「はい」を選んで、もう一度実行してください。"
        }
    }
    Write-Done
    Write-Host ""

    # ------------------------------------------------------- インストール --

    Write-Step "[3/3] インストールしています..."
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
            Fail ("同じバージョンが既にインストールされています。`n" +
                  "           入れ直すには先にアンインストールしてください（設定とキャッシュも消えます）:`n`n" +
                  "             Get-AppxPackage -Name $pkgName | Remove-AppxPackage")
        }
        # Reinstalling an older release over a newer one needs an explicit opt-in.
        try {
            Add-AppxPackage -Path $msix -ForceUpdateFromAnyVersion -ErrorAction Stop
        }
        catch {
            Fail "インストールに失敗しました。`n           $($_.Exception.Message)"
        }
    }
    Write-Done
    Write-Host ""

    $installed = Get-AppxPackage -Name $pkgName | Sort-Object Version | Select-Object -Last 1
    Write-Host "  インストールが完了しました（バージョン $($installed.Version)）。" -ForegroundColor Green
    Write-Host "  アプリを起動します。設定はタスクトレイのアイコンから行えます。"
    if ($installed) {
        Start-Process explorer.exe ('shell:appsFolder\' + $installed.PackageFamilyName + '!App')
    }
    Write-Host ""
    exit 0
}
finally {
    if ($download -and (Test-Path $download)) { Remove-Item $download -Force -ErrorAction SilentlyContinue }
}
