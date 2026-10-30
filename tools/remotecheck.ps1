# Checks the remote control protocol (docs/REMOTE_PROTOCOL.md) end to end: copies the built app to a
# temp folder with its own settings, starts the simulator and the app, connects the app to the
# simulator, switches Remote control on and then acts as the test software. Prints PASS/FAIL.
# Copied from CPX400DP tools/remotecheck.ps1 and adapted to the QPX1200SP (one output, soft limits,
# sequencer, no Local state). Leaves nothing running.
# Build first, preferably into a private folder so a running or debugged copy is not touched:
#   MSBuild QPX1200SPController.sln -v:minimal -nologo -p:OutDir=%TEMP%\qpx-test\app\
# Usage: pwsh -NoProfile -File tools/remotecheck.ps1 [-Build <folder with QPX1200SPController.exe>] [-SimPort 9231] [-RemotePort 9511]
param([string]$Build = "$env:TEMP\qpx-test\app", [int]$SimPort = 9231, [int]$RemotePort = 9511)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path "$Build\QPX1200SPController.exe")) { throw "Not found: $Build\QPX1200SPController.exe (build the solution first)" }
$simExe = if (Test-Path "$Build\QPX1200SPSimulator.exe") { "$Build\QPX1200SPSimulator.exe" } else { Join-Path (Split-Path $PSScriptRoot -Parent) 'QPX1200SPSimulator\bin\Debug\QPX1200SPSimulator.exe' }

# A private copy, so the test has its own settings.xml and database. Soft limit 30 V for the range check,
# one long sequence step for the sequencer checks.
$work = Join-Path $env:TEMP 'qpx1200sp-remotecheck'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force "$work\app" | Out-Null
Copy-Item "$Build\*" "$work\app" -Recurse -Exclude 'settings.xml', 'powerlog*.db*', 'Logs'
@"
<?xml version="1.0"?>
<Settings>
  <MinVoltage>0</MinVoltage>
  <MaxVoltage>30</MaxVoltage>
  <MinCurrent>0</MinCurrent>
  <MaxCurrent>50</MaxCurrent>
  <LastIpAddress>127.0.0.1:$SimPort</LastIpAddress>
  <RemotePort>$RemotePort</RemotePort>
  <SequenceSteps>
    <SequenceStepData><Voltage>5</Voltage><Current>1</Current><DurationSeconds>120</DurationSeconds><Ramp>false</Ramp></SequenceStepData>
  </SequenceSteps>
  <SequenceCycles>1</SequenceCycles>
  <SequenceOutputOnAtStart>true</SequenceOutputOnAtStart>
  <SequenceOutputOffAtEnd>true</SequenceOutputOffAtEnd>
</Settings>
"@ | Set-Content "$work\app\settings.xml"

$simCommands = "$work\sim-commands.txt"
# Start-Process does not quote arguments, and the repository path has a space ("PS App").
$sim = Start-Process pwsh -ArgumentList '-NoProfile', '-File', "`"$PSScriptRoot\simulator.ps1`"", '-Port', $SimPort,
  '-Commands', "`"$simCommands`"", '-Log', "`"$work\sim.log`"", '-Exe', "`"$simExe`"" -PassThru -WindowStyle Hidden
$app = Start-Process "$work\app\QPX1200SPController.exe" -PassThru
$env:QPX1200SP_PID = $app.Id
$uia = "$PSScriptRoot\uia.ps1"
function Ui([string]$name, [string]$action = 'invoke', [int]$index = 0) { pwsh -NoProfile -File $uia -Name $name -Action $action -Index $index | Out-Null }
function Sim([string]$line) { Add-Content $simCommands $line }

$script:fail = 0
function Check([string]$what, [bool]$ok, $detail = '') {
  if ($ok) { "PASS  $what" } else { $script:fail++; "FAIL  $what  $detail" }
}

# One line out, one line back.
function Open-Client {
  $c = [System.Net.Sockets.TcpClient]::new('127.0.0.1', $RemotePort)
  $c.ReceiveTimeout = 10000
  $s = $c.GetStream()
  $utf8 = [System.Text.UTF8Encoding]::new($false)
  [pscustomobject]@{ Tcp = $c; Reader = [System.IO.StreamReader]::new($s, $utf8); Writer = [System.IO.StreamWriter]::new($s, $utf8) }
}
function Send($client, [string]$line) {
  $client.Writer.NewLine = "`n"
  $client.Writer.WriteLine($line)
  $client.Writer.Flush()
  $reply = $client.Reader.ReadLine()
  $script:lastRaw = $reply
  if ($null -eq $reply) { return $null }
  $reply | ConvertFrom-Json
}
function Cmd($client, $request) { Send $client ($request | ConvertTo-Json -Compress) }
function J($r) { $r | ConvertTo-Json -Compress -Depth 4 }

try {
  Start-Sleep 6
  Ui 'CONNECT'
  Start-Sleep 5

  # Remote control is off at start: the port is closed.
  $refused = $false
  try { (Open-Client).Tcp.Close() } catch { $refused = $true }
  Check 'port closed while Remote control is off' $refused

  Ui 'Remote control' toggle
  Start-Sleep 1
  $c = Open-Client
  Start-Sleep 1
  $status = pwsh -NoProfile -File $uia -Action dump -Name "Remote:"
  Check 'status bar shows the client' ($status -match 'client 127.0.0.1') $status

  $second = Open-Client
  $r = $second.Reader.ReadLine() | ConvertFrom-Json
  Check 'second client refused' ($r.ok -eq $false -and $r.error -match 'Another client') (J $r)
  $second.Tcp.Close()

  # Request errors: nothing is sent to the supply.
  $r = Send $c 'not json'
  Check 'invalid JSON' ($r.ok -eq $false -and $r.error -match 'Invalid JSON') $r.error
  $r = Cmd $c @{ cmd = 'xyz' }
  Check 'unknown command' ($r.ok -eq $false -and $r.error -match 'Unknown command') $r.error
  $r = Cmd $c @{ cmd = 'read'; output = 2 }
  Check 'output 2 refused' ($r.ok -eq $false -and $r.error -match '"output" must be 1') $r.error
  $r = Cmd $c @{ cmd = 'set'; voltage = 40 }
  Check 'voltage above the soft limit' ($r.ok -eq $false -and $r.error -match '"voltage" must be 0–30 \(soft limits') $r.error
  $r = Cmd $c @{ cmd = 'set'; ovp = 1 }
  Check 'OVP below 2 V' ($r.ok -eq $false -and $r.error -match '"ovp" must be 2–61') $r.error
  $r = Cmd $c @{ cmd = 'set'; output = 1 }
  Check 'set without values' ($r.ok -eq $false -and $r.error -match 'at least one') $r.error
  $r = Cmd $c @{ cmd = 'output'; on = 'yes' }
  Check 'on must be true/false' ($r.ok -eq $false -and $r.error -match 'true or false') $r.error

  # set + read back
  $r = Cmd $c ([ordered]@{ cmd = 'set'; output = 1; voltage = 12.5; current = 1.2; ovp = 20; ocp = 2 })
  Check 'set V, I, OVP, OCP' ($r.ok -and $r.voltage.result -eq 'OK' -and $r.voltage.readback -eq 12.5 -and $r.current.readback -eq 1.2 -and $r.ovp.readback -eq 20 -and $r.ocp.readback -eq 2) (J $r)
  $r = Cmd $c ([ordered]@{ cmd = 'set'; voltage = 12.3456; current = 1.234 })
  Check 'values rounded to the resolution (1 mV, 10 mA)' ($r.ok -and $r.voltage.requested -eq 12.346 -and $r.voltage.readback -eq 12.346 -and $r.current.requested -eq 1.23 -and $r.current.readback -eq 1.23) (J $r)
  $r = Cmd $c ([ordered]@{ cmd = 'set'; voltage = 12.5; current = 1.2 })
  $r = Cmd $c @{ cmd = 'read'; output = 1 }
  Check 'read settings' ($r.ok -and $r.output -eq 1 -and $r.set_voltage -eq 12.5 -and $r.set_current -eq 1.2 -and $r.ovp -eq 20 -and $r.ocp -eq 2 -and $r.on -eq $false -and $r.mode -eq 'OFF' -and $null -eq $r.trip) (J $r)
  Check 'read has a timestamp' ($script:lastRaw -match '"time":"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}[+-]\d\d:\d\d"') $script:lastRaw
  $r = Cmd $c @{ cmd = 'read' }
  Check 'output may be left out' ($r.ok -and $r.output -eq 1) (J $r)

  # The supply refuses a setting (EER? error 200, as when another interface has the lock).
  Sim 'readonly'
  Start-Sleep 1
  $r = Cmd $c @{ cmd = 'set'; voltage = 10 }
  Check 'supply error reported (error 200)' ($r.ok -eq $false -and $r.voltage.result -eq 'Failed' -and $r.error -match 'voltage: error 200') (J $r)
  Sim 'readonly off'
  Start-Sleep 1

  # output on, measurements
  $r = Cmd $c @{ cmd = 'output'; on = $true }
  Check 'output on' ($r.ok -and $r.on -eq $true -and $r.result -eq 'OK') (J $r)
  $r = Cmd $c @{ cmd = 'read' }
  Check 'read on, CV, voltage' ($r.ok -and $r.on -and $r.mode -eq 'CV' -and [math]::Abs($r.voltage - 12.5) -lt 0.05 -and $r.current -gt 0.5) (J $r)
  Sim 'load 2'
  Start-Sleep 2
  $r = Cmd $c @{ cmd = 'read' }
  Check 'read CC at the current limit' ($r.ok -and $r.mode -eq 'CC' -and [math]::Abs($r.current - 1.2) -lt 0.05) (J $r)
  Sim 'load 20'

  # trip: reported, turning on refused until TRIP RESET in the app
  Sim 'trip ocp'
  Start-Sleep 3
  $r = Cmd $c @{ cmd = 'read' }
  Check 'read shows the OCP trip' ($r.ok -and $r.trip -eq 'OCP' -and $r.on -eq $false) (J $r)
  $r = Cmd $c @{ cmd = 'output'; on = $true }
  Check 'output on refused while tripped' ($r.ok -eq $false -and $r.error -match 'tripped \(OCP\)') $r.error
  Ui 'Enable Configuration' toggle
  Start-Sleep 1
  Ui 'TRIP RESET'
  Start-Sleep 2
  Ui 'Enable Configuration' toggle
  $r = Cmd $c @{ cmd = 'read' }
  Check 'trip cleared after TRIP RESET' ($r.ok -and $null -eq $r.trip) (J $r)
  $r = Cmd $c @{ cmd = 'output'; on = $true }
  Check 'output on after TRIP RESET' ($r.ok -and $r.on) (J $r)
  $r = Cmd $c @{ cmd = 'output'; on = $false }
  Check 'output off' ($r.ok -and $r.on -eq $false) (J $r)

  # Sequencer: set and output on refused while it runs; read and output off allowed (off ends the run).
  Ui 'Sequencer' select
  Start-Sleep 1
  Ui 'Start sequence'
  Start-Sleep 1
  Ui 'START'          # the confirmation dialog's button
  Ui 'Monitoring & Control' select
  Start-Sleep 3
  $r = Cmd $c @{ cmd = 'set'; voltage = 6 }
  Check 'set refused while a sequence runs' ($r.ok -eq $false -and $r.error -match 'A sequence is running') (J $r)
  $r = Cmd $c @{ cmd = 'output'; on = $true }
  Check 'output on refused while a sequence runs' ($r.ok -eq $false -and $r.error -match 'A sequence is running') (J $r)
  $r = Cmd $c @{ cmd = 'read' }
  Check 'read allowed while a sequence runs' ($r.ok -and $r.on -eq $true -and $r.set_voltage -eq 5) (J $r)
  $r = Cmd $c @{ cmd = 'output'; on = $false }
  Check 'output off allowed while a sequence runs' ($r.ok -and $r.on -eq $false) (J $r)
  Start-Sleep 2
  $r = Cmd $c @{ cmd = 'set'; voltage = 6 }
  Check 'sequence stopped by output off, set allowed again' ($r.ok -and $r.voltage.readback -eq 6) (J $r)

  # Switching Remote control off closes the connection and the port.
  Ui 'Remote control' toggle
  Start-Sleep 1
  $closed = $false
  try { $closed = $null -eq (Send $c '{"cmd":"read"}') } catch { $closed = $true }
  Check 'connection closed when Remote control is switched off' $closed
  $refused = $false
  try { (Open-Client).Tcp.Close() } catch { $refused = $true }
  Check 'port closed again' $refused
  $status = pwsh -NoProfile -File $uia -Action dump -Name "Remote:"
  Check 'status bar shows Remote: off' ($status -match 'Remote: off') $status
}
finally {
  Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
  Sim 'quit'
  Start-Sleep 1
  Stop-Process -Id $sim.Id -Force -ErrorAction SilentlyContinue
  Remove-Item Env:QPX1200SP_PID -ErrorAction SilentlyContinue
}

if ($script:fail -eq 0) { "ALL PASSED" } else { "$($script:fail) FAILED" }
exit $script:fail
