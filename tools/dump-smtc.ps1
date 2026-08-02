<#
  Dumps every SMTC session on the system with full metadata.

  Used to verify the bridge: Amazon Music's own session should show an empty Title
  and no thumbnail, while the bridge's session shows everything populated.
#>
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -AssemblyName System.Runtime.WindowsRuntime

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and
    $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
})[0]

function Await($operation, $resultType) {
    $method = $asTaskGeneric.MakeGenericMethod($resultType)
    $task = $method.Invoke($null, @($operation))
    $task.Wait(-1) | Out-Null
    $task.Result
}

[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime] | Out-Null

$manager = Await ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])
# Wrap in @() - PowerShell member-enumerates .Count over WinRT vector views.
$sessions = @($manager.GetSessions())

Write-Host "sessions: $($sessions.Count)" -ForegroundColor Cyan
$current = $manager.GetCurrentSession()
if ($current) { Write-Host "current : $($current.SourceAppUserModelId)" -ForegroundColor Cyan }

foreach ($session in $sessions) {
    Write-Host ("=" * 78)
    Write-Host "AUMID       : $($session.SourceAppUserModelId)" -ForegroundColor Yellow

    $info = $session.GetPlaybackInfo()
    Write-Host "Status      : $($info.PlaybackStatus)"
    Write-Host ("Controls    : play={0} pause={1} next={2} prev={3}" -f `
        $info.Controls.IsPlayEnabled, $info.Controls.IsPauseEnabled,
        $info.Controls.IsNextEnabled, $info.Controls.IsPreviousEnabled)

    $timeline = $session.GetTimelineProperties()
    Write-Host ("Timeline    : pos={0} end={1}" -f $timeline.Position, $timeline.EndTime)

    try {
        $props = Await ($session.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
        Write-Host "Title       : [$($props.Title)]"
        Write-Host "Artist      : [$($props.Artist)]"
        Write-Host "AlbumTitle  : [$($props.AlbumTitle)]"
        Write-Host "AlbumArtist : [$($props.AlbumArtist)]"
        Write-Host "Thumbnail   : $($props.Thumbnail -ne $null)"
    }
    catch {
        Write-Host "MediaProperties THREW: $($_.Exception.Message)" -ForegroundColor Red
    }
}
