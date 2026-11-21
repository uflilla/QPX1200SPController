# Checks the CCSDS remote control protocol (docs/REMOTE_PROTOCOL_CCSDS.md) end to end, like
# remotecheck.ps1 does for JSON: copies the built app to a temp folder with its own settings
# (RemoteProtocol = Ccsds, default Ccsds section), starts the simulator and the app, connects the app
# to the simulator, switches Remote control on and then acts as the CCSDS control software.
# Prints PASS/FAIL. Leaves nothing running.
# Build first, preferably into a private folder so a running or debugged copy is not touched:
#   MSBuild QPX1200SPController.sln -v:minimal -nologo -p:OutDir=%TEMP%\qpx-test\app\
# Usage: pwsh -NoProfile -File tools/ccsdscheck.ps1 [-Build <folder with QPX1200SPController.exe>] [-SimPort 9232] [-RemotePort 9512]
param([string]$Build = "$env:TEMP\qpx-test\app", [int]$SimPort = 9232, [int]$RemotePort = 9512)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path "$Build\QPX1200SPController.exe")) { throw "Not found: $Build\QPX1200SPController.exe (build the solution first)" }
$simExe = if (Test-Path "$Build\QPX1200SPSimulator.exe") { "$Build\QPX1200SPSimulator.exe" } else { Join-Path (Split-Path $PSScriptRoot -Parent) 'QPX1200SPSimulator\bin\Debug\QPX1200SPSimulator.exe' }

$work = Join-Path $env:TEMP 'qpx1200sp-ccsdscheck'
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
  <SampleRateHz>2</SampleRateHz>
  <LastIpAddress>127.0.0.1:$SimPort</LastIpAddress>
  <RemotePort>$RemotePort</RemotePort>
  <RemoteProtocol>Ccsds</RemoteProtocol>
  <SequenceSteps>
    <SequenceStepData><Voltage>5</Voltage><Current>1</Current><DurationSeconds>120</DurationSeconds><Ramp>false</Ramp></SequenceStepData>
  </SequenceSteps>
  <SequenceCycles>1</SequenceCycles>
  <SequenceOutputOnAtStart>true</SequenceOutputOnAtStart>
  <SequenceOutputOffAtEnd>true</SequenceOutputOffAtEnd>
</Settings>
"@ | Set-Content "$work\app\settings.xml"

$simCommands = "$work\sim-commands.txt"
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

#region CCSDS client

$Header = [byte[]](0x2A, 0x2A, 0x23, 0x23)
$Footer = [byte[]](0x23, 0x23, 0x2A, 0x2A)
$SidHk = 100; $SidRead = 101; $SidSet = 102; $SidOutput = 103; $SidError = 199

function Crc([byte[]]$b, [int]$n) {
  $crc = 0xFFFF
  for ($i = 0; $i -lt $n; $i++) {
    $crc = $crc -bxor ([int]$b[$i] -shl 8)
    for ($k = 0; $k -lt 8; $k++) {
      if ($crc -band 0x8000) { $crc = (($crc -shl 1) -bxor 0x1021) -band 0xFFFF } else { $crc = ($crc -shl 1) -band 0xFFFF }
    }
  }
  $crc
}
function U16([byte[]]$b, [int]$o) { ([int]$b[$o] -shl 8) -bor $b[$o + 1] }
function U32([byte[]]$b, [int]$o) { ([uint32]$b[$o] -shl 24) -bor ([uint32]$b[$o + 1] -shl 16) -bor ([uint32]$b[$o + 2] -shl 8) -bor $b[$o + 3] }
function RF32([byte[]]$b, [int]$o) { [BitConverter]::ToSingle([byte[]]($b[$o + 3], $b[$o + 2], $b[$o + 1], $b[$o]), 0) }
function WF32([double]$v) { $x = [BitConverter]::GetBytes([single]$v); [Array]::Reverse($x); , $x }
function Hex([byte[]]$b) { ($b | ForEach-Object { $_.ToString('X2') }) -join '' }

# A TC packet (PEC included, no sync markers).
function New-Tc([int]$sid, [byte[]]$data = @(), [int]$seq = 1, [int]$service = 128, [int]$subtype = 1, [int]$apid = 3, [switch]$BadPec) {
  $total = 6 + 3 + 2 + $data.Length + 2
  $p = New-Object byte[] $total
  $p[0] = 0x18 -bor (($apid -shr 8) -band 7); $p[1] = $apid -band 0xFF
  $p[2] = 0xC0 -bor (($seq -shr 8) -band 0x3F); $p[3] = $seq -band 0xFF
  $len = $total - 7; $p[4] = $len -shr 8; $p[5] = $len -band 0xFF
  $p[6] = 0x10; $p[7] = $service; $p[8] = $subtype
  $p[9] = $sid -shr 8; $p[10] = $sid -band 0xFF
  if ($data.Length) { [Array]::Copy($data, 0, $p, 11, $data.Length) }
  $crc = Crc $p ($total - 2)
  if ($BadPec) { $crc = $crc -bxor 1 }
  $p[$total - 2] = $crc -shr 8; $p[$total - 1] = $crc -band 0xFF
  , $p
}
function SetData($v = $null, $i = $null, $ovp = $null, $ocp = $null, [int]$applyOverride = -1) {
  $d = New-Object System.Collections.Generic.List[byte]
  foreach ($x in @($v, $i, $ovp, $ocp)) {
    $apply = if ($applyOverride -ge 0) { $applyOverride } elseif ($null -ne $x) { 1 } else { 0 }
    $d.Add([byte]$apply)
    $d.AddRange([byte[]](WF32 ($(if ($null -ne $x) { $x } else { 0 }))))
  }
  , $d.ToArray()
}

function Open-Client {
  $t = [System.Net.Sockets.TcpClient]::new('127.0.0.1', $RemotePort)
  $t.ReceiveTimeout = 10000
  [pscustomobject]@{ Tcp = $t; Stream = $t.GetStream(); Buf = [System.Collections.Generic.List[byte]]::new(); Hk = 0; LastHk = $null }
}
function Send-Raw($c, [byte[]]$bytes) { $c.Stream.Write($bytes, 0, $bytes.Length); $c.Stream.Flush() }
function Send-Tc($c, [byte[]]$packet) { Send-Raw $c ([byte[]]($Header + $packet + $Footer)) }
function Fill($c, [int]$n) {
  while ($c.Buf.Count -lt $n) {
    $tmp = New-Object byte[] 4096
    $r = $c.Stream.Read($tmp, 0, 4096)
    if ($r -le 0) { throw 'connection closed' }
    $c.Buf.AddRange([byte[]]$tmp[0..($r - 1)])
  }
}
# The next TM frame from the stream, decoded.
function Read-Tm($c) {
  Fill $c 4
  while (-not ($c.Buf[0] -eq 0x2A -and $c.Buf[1] -eq 0x2A -and $c.Buf[2] -eq 0x23 -and $c.Buf[3] -eq 0x23)) { $c.Buf.RemoveAt(0); Fill $c 4 }
  Fill $c 10
  $total = (U16 ([byte[]]$c.Buf.GetRange(4, 6).ToArray()) 4) + 7
  Fill $c (4 + $total + 4)
  $f = $c.Buf.GetRange(0, 4 + $total + 4).ToArray()
  $c.Buf.RemoveRange(0, 4 + $total + 4)
  $p = [byte[]]$f[4..(4 + $total - 1)]
  [pscustomobject]@{
    Packet = $p; Total = $total
    FooterOk = (Hex ([byte[]]$f[(4 + $total)..(4 + $total + 3)])) -eq (Hex $Footer)
    Version = $p[0] -shr 5; Type = ($p[0] -shr 4) -band 1; Dfh = ($p[0] -shr 3) -band 1
    Apid = (($p[0] -band 7) -shl 8) -bor $p[1]; Flags = $p[2] -shr 6; Seq = (($p[2] -band 0x3F) -shl 8) -bor $p[3]
    Pus = $p[6]; Service = $p[7]; Subtype = $p[8]; Sub = $p[9]
    Coarse = U32 $p 10; Fine = U32 $p 14; Sid = U16 $p 18
    Data = [byte[]]$p[20..($p.Length - 3)]
    PecOk = (Crc $p ($p.Length - 2)) -eq (U16 $p ($p.Length - 2))
  }
}
# The next reply: housekeeping packets in between are counted and skipped.
function Read-Reply($c) {
  while ($true) {
    $tm = Read-Tm $c
    if ($tm.Sid -ne $SidHk) { return $tm }
    $c.Hk++; $c.LastHk = $tm
  }
}
function Status([byte[]]$d, [int]$o) {
  [pscustomobject]@{
    Connected = $d[$o]; On = $d[$o + 1]; Mode = $d[$o + 2]; Trip = $d[$o + 3]; Seq = $d[$o + 4]
    V = RF32 $d ($o + 5); I = RF32 $d ($o + 9); P = RF32 $d ($o + 13)
    SetV = RF32 $d ($o + 17); SetI = RF32 $d ($o + 21); Ovp = RF32 $d ($o + 25); Ocp = RF32 $d ($o + 29)
  }
}
function Reply($tm) {
  $d = $tm.Data
  $r = [ordered]@{ Sid = $tm.Sid; TcSeq = U32 $d 0; Error = $d[4]; Len = $tm.Total }
  switch ($tm.Sid) {
    $SidRead { $r.Status = Status $d 5 }
    $SidSet { $names = 'V', 'I', 'Ovp', 'Ocp'; for ($k = 0; $k -lt 4; $k++) { $o = 5 + 9 * $k; $r[$names[$k]] = [pscustomobject]@{ Result = $d[$o]; Req = RF32 $d ($o + 1); Rb = RF32 $d ($o + 5) } } }
    $SidOutput { $r.State = $d[5]; $r.Result = $d[6] }
    $SidError { $r.Service = $d[5]; $r.Subtype = $d[6]; $r.TcSid = U32 $d 7 }
  }
  [pscustomobject]$r
}
$script:seq = 0
function Tc($c, [int]$sid, [byte[]]$data = @(), [hashtable]$opt = @{}) {
  $script:seq = ($script:seq + 1) -band 0x3FFF
  Send-Tc $c (New-Tc $sid $data $script:seq @opt)
  Reply (Read-Reply $c)
}
function Near([double]$a, [double]$b, [double]$tol = 0.0005) { [math]::Abs($a - $b) -lt $tol }
function J($r) { $r | ConvertTo-Json -Compress -Depth 4 }

#endregion

try {
  Start-Sleep 6
  Ui 'CONNECT'
  Start-Sleep 5

  $refused = $false
  try { (Open-Client).Tcp.Close() } catch { $refused = $true }
  Check 'port closed while Remote control is off' $refused

  Ui 'Remote control' toggle
  Start-Sleep 1
  $c = Open-Client
  Start-Sleep 1
  $status = pwsh -NoProfile -File $uia -Action dump -Name "Remote:"
  Check 'status bar shows CCSDS and the client' ($status -match 'CCSDS, client 127.0.0.1') $status

  # Housekeeping arrives without a request (sample rate 2 Hz).
  $hk = @(); $t0 = Get-Date
  while ($hk.Count -lt 4) { $tm = Read-Tm $c; if ($tm.Sid -eq $SidHk) { $hk += $tm } }
  $elapsed = ((Get-Date) - $t0).TotalSeconds
  $h = $hk[-1]
  $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
  Check 'HK sent unrequested, about every poll' ($elapsed -lt 3.5) "4 HK in $elapsed s"
  Check 'HK header: version 0, TM, DFH 1, APID 3, flags 11' ($h.Version -eq 0 -and $h.Type -eq 0 -and $h.Dfh -eq 1 -and $h.Apid -eq 3 -and $h.Flags -eq 3) (J $h)
  Check 'HK data field header: PUS 1, service 3/25, SID 100' ($h.Pus -eq 0x10 -and $h.Service -eq 3 -and $h.Subtype -eq 25 -and $h.Sid -eq 100)
  Check 'HK length 55 bytes (Packet length 48), PEC and footer OK' ($h.Total -eq 55 -and (U16 $h.Packet 4) -eq 48 -and $h.PecOk -and $h.FooterOk)
  Check 'source sequence count and subcounter count up' (($hk[1].Seq - $hk[0].Seq) -eq 1 -and (($hk[1].Sub - $hk[0].Sub) -band 0xFF) -eq 1) "$($hk.Seq) / $($hk.Sub)"
  Check 'HK time is CUC Unix seconds, close to now' ([math]::Abs($now - $h.Coarse) -le 3) "$($h.Coarse) vs $now"
  $s = Status $h.Data 0
  Check 'HK status: connected, off, mode OFF' ($s.Connected -eq 1 -and $s.On -eq 0 -and $s.Mode -eq 0 -and $s.Trip -eq 0) (J $s)

  # Second client: ERROR report 30, then closed.
  $second = Open-Client
  $r = Reply (Read-Reply $second)
  Check 'second client refused with error 30' ($r.Sid -eq $SidError -and $r.Error -eq 30 -and $r.TcSeq -eq [uint32]::MaxValue) (J $r)
  $second.Tcp.Close()

  # READ
  $r = Tc $c 3
  Check 'READ report: SID 101, echoes the TC sequence count, 60 bytes' ($r.Sid -eq $SidRead -and $r.TcSeq -eq $script:seq -and $r.Error -eq 0 -and $r.Len -eq 60) (J $r)

  # Packet errors: nothing is sent to the supply.
  $r = Tc $c 3 -opt @{ BadPec = $true }
  Check 'wrong PEC: error 3' ($r.Sid -eq $SidError -and $r.Error -eq 3 -and $r.TcSeq -eq $script:seq) (J $r)
  $r = Tc $c 3 -opt @{ apid = 5 }
  Check 'wrong APID: error 4' ($r.Sid -eq $SidError -and $r.Error -eq 4) (J $r)
  $r = Tc $c 3 -opt @{ service = 77 }
  Check 'unknown service: error 5, service echoed' ($r.Sid -eq $SidError -and $r.Error -eq 5 -and $r.Service -eq 77 -and $r.Subtype -eq 1 -and $r.TcSid -eq 3) (J $r)
  $r = Tc $c 9
  Check 'unknown SID: error 6' ($r.Sid -eq $SidError -and $r.Error -eq 6 -and $r.TcSid -eq 9) (J $r)
  $r = Tc $c 3 ([byte[]](1))
  Check 'READ with application data: error 1' ($r.Sid -eq $SidError -and $r.Error -eq 1) (J $r)
  $r = Tc $c 1 (SetData)
  Check 'SET without values: error 8' ($r.Sid -eq $SidSet -and $r.Error -eq 8 -and $r.V.Result -eq 0) (J $r)
  $r = Tc $c 1 (SetData 5 -applyOverride 2)
  Check 'SET apply 2: error 7' ($r.Sid -eq $SidSet -and $r.Error -eq 7) (J $r)
  $r = Tc $c 2 ([byte[]](5))
  Check 'OUTPUT on 5: error 7' ($r.Sid -eq $SidOutput -and $r.Error -eq 7 -and $r.Result -eq 0) (J $r)
  $r = Tc $c 1 (SetData 40)
  Check 'voltage above the soft limit: error 10, not sent' ($r.Error -eq 10 -and $r.V.Result -eq 0 -and (Near $r.V.Req 40)) (J $r)
  $r = Tc $c 1 (SetData -ovp 1)
  Check 'OVP below 2 V: error 12' ($r.Error -eq 12) (J $r)

  # Framing: garbage before the header is skipped; a wrong footer gets error 2 and the next packet works.
  $script:seq++
  Send-Raw $c ([byte[]](0x00, 0x2A, 0x99, 0x2A, 0x2A) + $Header + (New-Tc 3 @() $script:seq) + $Footer)
  $r = Reply (Read-Reply $c)
  Check 'bytes before the sync header skipped' ($r.Sid -eq $SidRead -and $r.Error -eq 0 -and $r.TcSeq -eq $script:seq) (J $r)
  $script:seq++
  Send-Raw $c ([byte[]]($Header + (New-Tc 3 @() $script:seq) + [byte[]](1, 2, 3, 4)))
  $r = Reply (Read-Reply $c)
  Check 'wrong footer: error 2' ($r.Sid -eq $SidError -and $r.Error -eq 2 -and $r.TcSeq -eq $script:seq) (J $r)
  $r = Tc $c 3
  Check 'next packet after a wrong footer works' ($r.Sid -eq $SidRead -and $r.Error -eq 0 -and $r.TcSeq -eq $script:seq) (J $r)

  # SET with read-back
  $r = Tc $c 1 (SetData 12.5 1.2 20 2)
  Check 'SET V, I, OVP, OCP' ($r.Error -eq 0 -and $r.V.Result -eq 1 -and (Near $r.V.Rb 12.5) -and (Near $r.I.Rb 1.2) -and (Near $r.Ovp.Rb 20) -and (Near $r.Ocp.Rb 2) -and $r.Len -eq 63) (J $r)
  $r = Tc $c 1 (SetData 12.3456 1.234)
  Check 'values rounded to the resolution (1 mV, 10 mA)' ($r.Error -eq 0 -and (Near $r.V.Req 12.346) -and (Near $r.V.Rb 12.346) -and (Near $r.I.Req 1.23) -and $r.Ovp.Result -eq 0) (J $r)
  $r = Tc $c 1 (SetData 12.5 1.2)
  $r = Tc $c 3
  $s = $r.Status
  Check 'READ settings' ($r.Error -eq 0 -and $s.Connected -eq 1 -and (Near $s.SetV 12.5) -and (Near $s.SetI 1.2) -and (Near $s.Ovp 20) -and (Near $s.Ocp 2) -and $s.On -eq 0 -and $s.Mode -eq 0 -and $s.Trip -eq 0) (J $s)

  Sim 'readonly'
  Start-Sleep 1
  $r = Tc $c 1 (SetData 10)
  Check 'supply error reported: error 24, voltage Failed' ($r.Error -eq 24 -and $r.V.Result -eq 3) (J $r)
  Sim 'readonly off'
  Start-Sleep 1

  # output on, measurements
  $r = Tc $c 2 ([byte[]](1))
  Check 'OUTPUT on' ($r.Sid -eq $SidOutput -and $r.Error -eq 0 -and $r.State -eq 1 -and $r.Result -eq 1 -and $r.Len -eq 29) (J $r)
  $r = Tc $c 3
  Check 'READ on, CV, voltage' ($r.Error -eq 0 -and $r.Status.On -eq 1 -and $r.Status.Mode -eq 1 -and (Near $r.Status.V 12.5 0.05) -and $r.Status.I -gt 0.5 -and $r.Status.P -gt 5) (J $r.Status)
  Sim 'load 2'
  Start-Sleep 2
  $r = Tc $c 3
  Check 'READ CC at the current limit' ($r.Status.Mode -eq 2 -and (Near $r.Status.I 1.2 0.05)) (J $r.Status)
  Sim 'load 20'

  # trip
  Sim 'trip ocp'
  Start-Sleep 3
  $r = Tc $c 3
  Check 'READ shows the OCP trip' ($r.Status.Trip -eq 2 -and $r.Status.On -eq 0) (J $r.Status)
  $hkSeen = $c.Hk
  $r = Tc $c 2 ([byte[]](1))
  Check 'OUTPUT on refused while tripped: error 23' ($r.Error -eq 23 -and $r.Result -eq 0) (J $r)
  $tm = Read-Tm $c; while ($tm.Sid -ne $SidHk) { $tm = Read-Tm $c }
  Check 'HK shows the trip too' ((Status $tm.Data 0).Trip -eq 2) (J (Status $tm.Data 0))
  Ui 'Enable Configuration' toggle
  Start-Sleep 1
  Ui 'TRIP RESET'
  Start-Sleep 2
  Ui 'Enable Configuration' toggle
  $r = Tc $c 3
  Check 'trip cleared after TRIP RESET' ($r.Status.Trip -eq 0) (J $r.Status)
  $r = Tc $c 2 ([byte[]](1))
  Check 'OUTPUT on after TRIP RESET' ($r.Error -eq 0 -and $r.State -eq 1) (J $r)
  $r = Tc $c 2 ([byte[]](0))
  Check 'OUTPUT off' ($r.Error -eq 0 -and $r.State -eq 0) (J $r)

  # Sequencer
  Ui 'Sequencer' select
  Start-Sleep 1
  Ui 'Start sequence'
  Start-Sleep 1
  Ui 'START'
  Ui 'Monitoring & Control' select
  Start-Sleep 3
  $r = Tc $c 1 (SetData 6)
  Check 'SET refused while a sequence runs: error 22' ($r.Error -eq 22) (J $r)
  $r = Tc $c 2 ([byte[]](1))
  Check 'OUTPUT on refused while a sequence runs: error 22' ($r.Error -eq 22) (J $r)
  $r = Tc $c 3
  Check 'READ allowed while a sequence runs, sequence flag set' ($r.Error -eq 0 -and $r.Status.On -eq 1 -and $r.Status.Seq -eq 1 -and (Near $r.Status.SetV 5)) (J $r.Status)
  $r = Tc $c 2 ([byte[]](0))
  Check 'OUTPUT off allowed while a sequence runs' ($r.Error -eq 0 -and $r.State -eq 0) (J $r)
  Start-Sleep 2
  $r = Tc $c 1 (SetData 6)
  Check 'sequence stopped by output off, SET allowed again' ($r.Error -eq 0 -and (Near $r.V.Rb 6)) (J $r)

  Check 'HK kept arriving between replies' ($c.Hk -gt 10) "$($c.Hk) HK"

  # Not connected to the supply: HK once per second with Connected = 0, READ refused.
  Ui 'Enable Configuration' toggle
  Start-Sleep 1
  Ui 'DISCONNECT'
  Start-Sleep 2
  # Spacing from the packets' own CUC times (packets may already be waiting in the socket).
  $tm = Read-Tm $c; while ($tm.Sid -ne $SidHk -or (Status $tm.Data 0).Connected -ne 0) { $tm = Read-Tm $c }
  $first = $tm
  $tm = Read-Tm $c; while ($tm.Sid -ne $SidHk) { $tm = Read-Tm $c }
  $gap = ($tm.Coarse + $tm.Fine / 4294967296.0) - ($first.Coarse + $first.Fine / 4294967296.0)
  Check 'HK while disconnected: Connected 0, about 1 s apart' ((Status $tm.Data 0).Connected -eq 0 -and $gap -gt 0.5 -and $gap -lt 1.6) "gap $gap s"
  $r = Tc $c 3
  Check 'READ while disconnected: error 20' ($r.Sid -eq $SidRead -and $r.Error -eq 20 -and $r.Status.Connected -eq 0) (J $r)

  # Switching Remote control off closes the connection and the port.
  Ui 'Remote control' toggle
  Start-Sleep 1
  $closed = $false
  try { while ($true) { Read-Tm $c | Out-Null } } catch { $closed = $true }
  Check 'connection closed when Remote control is switched off' $closed
  $refused = $false
  try { (Open-Client).Tcp.Close() } catch { $refused = $true }
  Check 'port closed again' $refused
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
