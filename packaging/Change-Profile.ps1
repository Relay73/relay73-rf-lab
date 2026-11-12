# This menu always talks only to the locally running simulator.
$ErrorActionPreference = 'Stop'
$folder = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $folder 'FakeRadio.exe'
$configFile = Join-Path $folder 'fakeradio.json'
if (-not (Test-Path $configFile)) { throw 'Run Start-FakeRadio.cmd and finish setup first.' }
$cfg = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
$port = [int]$cfg.ControlPort
if ($port -le 0 -or $port -gt 65535) { throw 'Invalid configured control port' }
Write-Output 'Available channel profiles:'
$profiles = @(& $exe --profiles)
$profiles | ForEach-Object { Write-Output ('  ' + $_) }
$selection = (Read-Host 'Enter one profile name').Trim().ToUpperInvariant()
if ($selection -cnotin $profiles) { throw "Not a known profile: $selection" }
$client = [Net.Sockets.TcpClient]::new()
try {
    $client.Connect([Net.IPAddress]::Loopback, $port)
    $stream = $client.GetStream()
    $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII)
    $writer = [IO.StreamWriter]::new($stream, [Text.Encoding]::ASCII)
    $writer.AutoFlush = $true
    $banner = $reader.ReadLine()
    if ($banner -notlike 'OK VAFM-AUDIO-NODE*') { throw 'Unexpected local FakeRadio control banner' }
    $writer.WriteLine("PROFILE $selection")
    $reply = $reader.ReadLine()
    if ($reply -ne 'OK') { throw "FakeRadio profile change failed: $reply" }
    $writer.WriteLine('STATUS')
    Write-Output ('ACTIVE CHANNEL: ' + $reader.ReadLine())
} finally {
    $client.Dispose()
}
