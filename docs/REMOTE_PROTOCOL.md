# QPX1200SP Controller: Remote Control Protocol

Protocol version 1 · app v2.9 · 2026-10-30

A test executing software (the "client") connects to this app over TCP. It can set the output, turn it on or off, and read it back. The app sends each command to the supply exactly like the buttons do, checking the result with `EER?` and a read-back, and logs it in the database. The reply tells the client what was sent and what the supply reported.

The protocol is the same as the CPX400DP Controller's, so one client can drive both apps. The QPX1200SP has one output; the differences are listed at the end.

A working C# client is in [examples/RemoteClientExample.cs](examples/RemoteClientExample.cs).

## Connection

- TCP, port **9501**. The port is set in `settings.xml` (`RemotePort`); the app listens on all network cards. (The CPX400DP Controller uses 9500, so both apps can run on the same PC.)
- The app opens the port only while **Remote control** is switched on in the connection bar. The switch is off at every start. Switching it off closes the port and the client's connection.
- One client at a time. A second connection gets `{"ok":false,"error":"Another client is connected"}` and is closed.
- The status bar shows "Remote: off", "Remote: waiting on port 9501" or "Remote: client 192.168.1.20".
- TURN OUTPUT OFF in the window always works.
- If the test software runs on another PC, Windows Firewall must allow `QPX1200SPController.exe` (Windows asks the first time the switch is turned on).

## Format

One JSON object per line, UTF-8, ending with LF. Each request gets exactly one reply, in order.

```
→ {"cmd": "...", ...}
← {"ok": true, ...}
← {"ok": false, "error": "text saying why"}
```

## Commands

`output` may be left out. If it is given, it must be 1 (the QPX1200SP has one output).

### `set`: change settings

Give any of `voltage`, `current`, `ovp` and `ocp`. All values are checked before anything is sent. They are then sent in the order OVP, OCP, current, voltage, and the app stops at the first one that is not OK.

```json
→ {"cmd": "set", "output": 1, "voltage": 28.0, "current": 2.0}
← {"ok": true,
   "current": {"requested": 2, "readback": 2, "result": "OK"},
   "voltage": {"requested": 28, "readback": 28, "result": "OK"}}
```

| Value | Range accepted by the app | Supply's range (manual) | Resolution |
|---|---|---|---|
| `voltage` | the app's soft limits (default 0–60 V) | 0–60 V | 0.001 V |
| `current` | the app's soft limits (default 0–50 A) | 0.01–50 A | 0.01 A |
| `ovp` | 2–61 V | 2–65 V | 0.1 V |
| `ocp` | 2–50 A | 2–55 A | 0.1 A |

- The soft limits are the ones under **Soft limits & storage** in the app, the same limits the SET buttons use. The error text gives the range in force, e.g. `"voltage" must be 12–13 (soft limits in the app)`.
- `requested` is the value after rounding to the resolution, as sent.
- `readback` is what the supply reported afterwards.
- `result` is one of:
  - `OK`: no error and the read-back matches.
  - `Mismatch`: the read-back differs.
  - `Failed`: the supply reported an error, or the link failed.
- If a value is not OK, the reply has `"ok": false` and `"error": "voltage: <why>"`, and the values after it are not sent.
- Refused while a sequence runs in the app's Sequencer.

### `output`: turn the output on or off

```json
→ {"cmd": "output", "output": 1, "on": true}
← {"ok": true, "on": true, "result": "OK"}
```

- `on` in the reply is what the supply reports afterwards.
- Turning on is refused while a protection trip is latched (OVP, OCP, sense or fault). Remove the cause and press TRIP RESET in the app.
- Turning on is refused while a sequence runs in the app's Sequencer.
- Turning off is accepted whenever the app is connected, also while a sequence runs.

### `read`: measurements and settings

```json
→ {"cmd": "read", "output": 1}
← {"ok": true, "time": "2026-10-30T10:15:03.410+05:00", "output": 1,
   "on": true, "voltage": 12.001, "current": 0.601, "mode": "CV", "trip": null,
   "set_voltage": 12, "set_current": 1, "ovp": 14, "ocp": 2}
```

- The app reads the supply when the request arrives, so the values are current. The reading also updates the window, the plots and the database like a normal poll.
- `time` is when the measurement was taken, in local time with the UTC offset.
- `voltage` and `current` are the measured values (meter resolution 1 mV and 10 mA).
- `mode` is `CV`, `CC`, `UNREG` (output outside the power envelope) or `OFF`.
- `trip` is null, `"OVP"`, `"OCP"`, `"SENSE"` or `"FAULT"` (the first one latched, in that order). A trip stays until TRIP RESET. A `FAULT` trip needs the supply's mains switched off and on.
- `set_voltage`, `set_current`, `ovp` and `ocp` are read from the supply, not from the app's memory.
- Allowed while a sequence runs.

## Errors

`"ok": false` with an `error` text, for example:

| When | `error` |
|---|---|
| Not valid JSON | `Invalid JSON: send one object per line, ...` |
| No or unknown `cmd` | `Missing "cmd": use set, output or read` / `Unknown command 'xyz': ...` |
| Bad parameter | `"output" must be 1 (the QPX1200SP has one output)`, `"voltage" must be 0–60 (soft limits in the app)`, `"ovp" must be 2–61`, `"on" must be true or false`, `Give at least one of voltage, current, ovp, ocp` |
| Link | `Not connected to the supply`, `No valid reply from the supply` |
| Busy | `A sequence is running in the app` |
| Safety | `The output is tripped (OVP): remove the cause and press TRIP RESET in the app` |
| The supply did not confirm | `voltage: <detail>` (set, e.g. `voltage: error 200: no write access ...`), `The supply kept the output off (tripped?)` (output) |

## Logging

- Every command goes into the database `CommandLog` like a button press, with `Source` = `Remote`, and shows as a marker on the live plots and in History.
- "Remote control on/off" and "Remote client connected/left" are logged as Connection records.
- With the trace switched on (App Logs), every request and reply line is in the trace file (`R<` and `R>`).

## Example test step

```json
→ {"cmd": "set", "voltage": 12.0, "current": 1.0, "ovp": 14.0, "ocp": 2.0}
← {"ok": true, "ovp": {...}, "ocp": {...}, "current": {...}, "voltage": {"requested": 12, "readback": 12, "result": "OK"}}
→ {"cmd": "output", "on": true}
← {"ok": true, "on": true, "result": "OK"}
→ {"cmd": "read"}
← {"ok": true, "voltage": 12.001, "current": 0.601, "mode": "CV", "trip": null, ...}
   ... the test software checks the unit over serial ...
→ {"cmd": "output", "on": false}
← {"ok": true, "on": false, "result": "OK"}
```

## Differences to the CPX400DP Controller

| | CPX400DP | QPX1200SP |
|---|---|---|
| Port | 9500 | 9501 |
| `output` | 1 or 2, required | optional, 1 only |
| Ranges | instrument ranges | the app's soft limits for voltage and current; OVP 2–61 V, OCP 2–50 A |
| Resolution | 0.01 V, 0.001 A, 0.1 V OVP, 0.01 A OCP | 0.001 V, 0.01 A, 0.1 V OVP, 0.1 A OCP |
| `trip` | `OVP`, `OCP`, `HARD`, `BACKUP` | `OVP`, `OCP`, `SENSE`, `FAULT` |
| Local | settings and `read` refused in Local | no Local state in the app: the LOCAL button hands the front panel back only until the next poll |
| OCP before turning on | the app restores its OCP level | not managed; `output on` does exactly what TURN OUTPUT ON does |
| Sequencer | none | `set` and `output on` refused while a sequence runs |
| Tracking mode | Output 2's voltage refused | none |
