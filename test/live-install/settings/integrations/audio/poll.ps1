param([int]$ParentPid = 0, [string]$ArtPath = "")

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
Add-Type -AssemblyName System.Runtime.WindowsRuntime | Out-Null

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
  $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
  $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, $ty) {
  $m = $asTaskGeneric.MakeGenericMethod($ty)
  $t = $m.Invoke($null, @($op))
  $t.Wait(-1) | Out-Null
  $t.Result
}

$mgrType = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType=WindowsRuntime]
$propsType = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties, Windows.Media.Control, ContentType=WindowsRuntime]
$streamType = [Windows.Storage.Streams.IRandomAccessStreamWithContentType, Windows.Storage.Streams, ContentType=WindowsRuntime]

$mgr = Await ($mgrType::RequestAsync()) ($mgrType)
$rev = 0
$lastKey = ''

while ($true) {
  if ($ParentPid -gt 0) {
    $parent = Get-Process -Id $ParentPid -ErrorAction SilentlyContinue
    if ($null -eq $parent) { exit }
  }

  $title = ''; $artist = ''; $status = 'none'
  try {
    $s = $mgr.GetCurrentSession()
    if ($s -ne $null) {
      $p = Await ($s.TryGetMediaPropertiesAsync()) ($propsType)
      $st = [int]$s.GetPlaybackInfo().PlaybackStatus
      if ($st -eq 4) { $status = 'playing' }
      elseif ($st -eq 5) { $status = 'paused' }
      else { $status = 'other' }
      $title = [string]$p.Title
      $artist = [string]$p.Artist

      $key = $title + '|' + $artist
      if ($ArtPath -ne '' -and $key -ne $lastKey) {
        $lastKey = $key
        $rev = $rev + 1
        $wrote = $false
        try {
          $thumb = $p.Thumbnail
          if ($thumb -ne $null) {
            $rs = Await ($thumb.OpenReadAsync()) ($streamType)
            if ($rs -ne $null -and $rs.Size -gt 0 -and $rs.Size -lt 8000000) {
              $net = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($rs)
              $ms = New-Object System.IO.MemoryStream
              $net.CopyTo($ms)
              $bytes = $ms.ToArray()
              $net.Dispose(); $ms.Dispose(); $rs.Dispose()
              $tmp = $ArtPath + '.tmp'
              [System.IO.File]::WriteAllBytes($tmp, $bytes)
              Move-Item -Force $tmp $ArtPath
              $wrote = $true
            }
          }
        } catch { }
        if (-not $wrote) { [System.IO.File]::Delete($ArtPath) }
      }
    }
  } catch { }

  $o = @{ title = $title; artist = $artist; status = $status; rev = $rev; art = $(if ($ArtPath -ne '' -and (Test-Path $ArtPath)) { $ArtPath } else { '' }) }
  [Console]::Out.WriteLine(($o | ConvertTo-Json -Compress))
  [Console]::Out.Flush()
  Start-Sleep -Milliseconds 1000
}