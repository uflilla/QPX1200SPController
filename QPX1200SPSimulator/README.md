# QPX1200SP Simulator

A fake QPX1200SP that speaks the same SCPI-over-TCP protocol as the real supply (port 9221).
Use it to test QPX1200SPController without hardware, including fault cases that are hard to
produce on the real instrument. The app itself is described in `docs/USER_GUIDE.md`.

## Run it

1. Build the solution (the simulator is the `QPX1200SPSimulator` project).
2. Start `QPX1200SPSimulator\bin\Debug\QPX1200SPSimulator.exe`.
   In Visual Studio: right-click the project → **Debug → Start New Instance**.
3. Start the app, set the IP address to `127.0.0.1` and press **CONNECT**.

Options:

| Option | Meaning |
|---|---|
| `--port 9221` | TCP port (default 9221, same as the real supply) |
| `--any` | Listen on all network interfaces, so another PC can connect to this one |
| `--load 20` | Initial resistive load in ohms, or `open` (default 20 Ω) |

The app remembers the last IP, so switch it back to the real supply's address afterwards.

With `--port` set to something other than 9221, type the port into the app's IP box, e.g. `127.0.0.1:9231`.

`tools/simulator.ps1` runs the simulator with its console fed from a text file, so tests can send console commands
(`Add-Content sim-commands.txt 'trip ocp'`). `tools/remotecheck.ps1` uses it for the end-to-end check of the remote
control protocol (`docs/REMOTE_PROTOCOL.md`).
Note that test data is logged to the app's `powerlog.db` like real data.

## What it simulates

- Set-points `V1`, `I1`, `OVP1`, `OCP1`, output `OP1`, and their queries in the real reply formats
  (`12.000V`, `V1 12.000`, `VP1 61.000`, ...). `*IDN?`, `TRIPRST`, `LOCAL`, `*RST`, `*CLS`.
- `EER?` (Execution Error Register, cleared on read): a value outside the manual's range (V 0–60, I 0–50,
  OVP 2–65, OCP 2–55, OP1 0/1) is refused with error 100, and with `readonly` every set command gets error 200.
  Values are rounded to the setting resolution (1 mV, 10 mA, 0.1 V OVP, 0.1 A OCP), like the real supply.
- Other status queries for the SCPI console: `QER?`, `*ESR?`, `*STB?`, `LSE1?` (always `0`), `*OPC?` (`1`).
- A resistive load: CV/CC cross-over, the 1200 W power envelope (unregulated outside it),
  ±1 mV / ±1 mA measurement noise.
- OVP/OCP trips against the programmed levels: the output switches OFF and `OP1 1` is refused
  until `TRIPRST`, like the real supply. `LSR1?` reports mode and trip bits and clears on read.
- A command like `V1 12,000` (comma decimal) is rejected, as the real supply would.
- Unknown queries get no reply (the app should time out and reconnect).
- Replies are split into two TCP segments to exercise partial reads.

## Console commands

| Command | Effect |
|---|---|
| `status` / `s` | Show set-points, measurements, trips and fault switches |
| `load <ohms>` / `load open` | Change the load (e.g. `load 0.5` forces CC mode) |
| `trip ovp\|ocp\|sense\|fault` | Force a trip |
| `readonly` / `readonly off` | Refuse set commands with error 200, as when another interface has the lock |
| `vcap <volts>` / `vcap off` | Silently cap V1 so the read-back differs from the request |
| `mute` / `mute off` | Receive commands but never answer |
| `delay <ms>` | Delay every reply |
| `garble [n]` | Corrupt the next n replies |
| `drop` | Close the current connection |
| `outage [seconds]` | Go off the network for a while (default 15 s) |
| `fragment on\|off` | Split replies into two TCP segments (default on) |
| `noise on\|off` | Measurement noise (default on) |
| `verbose` / `v` | Toggle printing of every command and reply |
| `quit` / `q` | Exit |

## Test recipes

| Do this in the simulator | Expected in the app |
|---|---|
| `load 2` with V = 12 V, I = 1 A, output ON | Meters show ~2 V / 1 A, CC indicator lit |
| In the app set V above OVP (or `trip ovp`) | Output goes OFF, OVP indicator stays lit until **TRIP RESET** |
| `readonly`, then SET a voltage | Last-command line in red: `✗ Failed: error 200: no write access ...` |
| `vcap 5`, then SET 12 V | Last-command line in red: `⚠ Mismatch: read back 5, expected 12` |
| `garble 1` | One warning in App Logs, link stays up, no zero values in plots |
| `garble 3` | App reconnects automatically |
| `delay 4000` | Status reads time out, header turns amber "Reconnecting…"; `delay 0` → reconnects |
| `mute`, then `mute off` | Same as above |
| `drop` | App reconnects within a second or two |
| `outage 30` | App keeps retrying (1, 2, 5, then every 10 s) and reconnects when it ends |
| Close the app with output ON | Exit dialog; **TURN OUTPUT OFF AND EXIT** → simulator logs `OP1 0` |
| App Logs → Trace mode ON | Every command/reply in `Logs\Trace\trace_*.log` next to the app exe |
