# Runs QPX1200SPSimulator so that console commands can come from another shell (for unattended
# tests of the app): every line appended to the command file is typed into the simulator, and its
# output goes to the log file. "quit" ends both. Copied from CPX400DP tools/simulator.ps1.
# Usage: pwsh -NoProfile -File tools/simulator.ps1 [-Port 9221] [-Commands sim-commands.txt] [-Log sim.log] [-SimArgs "--load 20"] [-Exe <path>]
#   then e.g.: Add-Content sim-commands.txt 'trip ocp'
param([int]$Port = 9221, [string]$Commands = "$env:TEMP\qpx1200sp-sim-commands.txt", [string]$Log = "$env:TEMP\qpx1200sp-sim.log",
      [string]$SimArgs = "", [string]$Exe)
$ErrorActionPreference = 'Stop'
$sim = if ($Exe) { $Exe } else { Join-Path (Split-Path $PSScriptRoot -Parent) 'QPX1200SPSimulator\bin\Debug\QPX1200SPSimulator.exe' }
if (-not (Test-Path $sim)) { throw "Not found: $sim (build the solution first)" }
Set-Content $Commands '' -NoNewline
Set-Content $Log ''

$psi = New-Object System.Diagnostics.ProcessStartInfo $sim, "--port $Port $SimArgs"
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$proc = [System.Diagnostics.Process]::Start($psi)
$writer = [System.IO.StreamWriter]::new($Log, $true)
$writer.AutoFlush = $true
$out = Register-ObjectEvent $proc OutputDataReceived -MessageData $writer -Action { if ($EventArgs.Data -ne $null) { $Event.MessageData.WriteLine($EventArgs.Data) } }
$err = Register-ObjectEvent $proc ErrorDataReceived -MessageData $writer -Action { if ($EventArgs.Data -ne $null) { $Event.MessageData.WriteLine($EventArgs.Data) } }
$proc.BeginOutputReadLine()
$proc.BeginErrorReadLine()
"simulator pid $($proc.Id) on port $Port; commands: $Commands; log: $Log"

$done = 0
try {
  while (-not $proc.HasExited) {
    $lines = @(Get-Content $Commands -ErrorAction SilentlyContinue)
    for (; $done -lt $lines.Count; $done++) {
      $line = $lines[$done]
      if (-not $line) { continue }
      $writer.WriteLine("> $line")
      $proc.StandardInput.WriteLine($line)
    }
    Start-Sleep -Milliseconds 200
  }
}
finally {
  if (-not $proc.HasExited) { $proc.Kill() }
  Unregister-Event -SourceIdentifier $out.Name
  Unregister-Event -SourceIdentifier $err.Name
  $writer.Dispose()
}
"simulator exited"
