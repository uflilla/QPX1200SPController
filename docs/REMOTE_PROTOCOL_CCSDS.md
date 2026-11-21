# QPX1200SP Controller: Remote Control Protocol (CCSDS)

Protocol version 1 · app v2.10 · 2026-11-21

The app has two remote control protocols on the same TCP port. The operator picks one in the connection bar:

- **JSON**: one JSON object per line ([REMOTE_PROTOCOL.md](REMOTE_PROTOCOL.md)). The same as the CPX400DP Controller's.
- **CCSDS**: this document. Binary CCSDS space packets with a PUS-A (ECSS-E-70-41A) data field header, for control software that already works with CCSDS telecommand (TC) and telemetry (TM) packets.

Both protocols run the same three commands: SET, OUTPUT and READ. Each command goes to the supply exactly as the buttons send it, checked with `EER?` and a read-back, and is logged in the database with the source `Remote`. The safety rules are the same too: soft limits, trips, the sequencer, and TURN OUTPUT OFF always working. CCSDS adds **periodic housekeeping**, which the app sends to the connected client without being asked.

A working C# client is in [examples/CcsdsClientExample.cs](examples/CcsdsClientExample.cs). How the operator switches remote control on is in the [user guide](USER_GUIDE.md#14-remote-control-by-test-software).

## Connection

- TCP, port **9501** (`RemotePort` in `settings.xml`), on all network cards. It is the same port as JSON; the protocol is chosen with **Protocol** next to the Remote control switch (`RemoteProtocol` in `settings.xml`).
- The port is open only while **Remote control** is on. The switch is off at every start. Switching it off closes the port and the connection.
- One client at a time. A second client gets an ERROR report with code 30 (another client connected), and its connection is closed.
- The status bar shows "Remote: off", "Remote: CCSDS, waiting on port 9501" or "Remote: CCSDS, client 192.168.1.20".
- The end-to-end test is `tools/ccsdscheck.ps1` (against the simulator).

## Framing

Every packet in both directions is sent between a sync header and a sync footer:

```
[sync header] [CCSDS packet, PEC included] [sync footer]
 2A 2A 23 23                                 23 23 2A 2A      (defaults)
```

- The markers are `SyncHeader` and `SyncFooter` in `settings.xml`, written in hex. Either one may be empty, which means no marker.
- The packet's own Packet Length field tells the receiver where the packet ends. The footer must follow straight after it.
- **Resync:** the app searches the stream for the sync header. Bytes before it are skipped. If the footer is wrong, or the length is impossible, the app sends an ERROR report and searches again from the byte after that header. Without a sync header the app cannot resync: it sends the ERROR report and closes the connection.
- The markers are not covered by the PEC.

## Byte order and field types

- All fields are **big-endian** (network order).
- `u8`, `u16`, `u32`: unsigned integers. `f32`: IEEE-754 single-precision float.
- Application data follows these rules:
  - decimal values are `f32`;
  - flags and small enumerations are one `u8` each (0 or 1 for flags);
  - values that need more than 8 bits are `u32`.
- The CCSDS and PUS header fields keep their standard sizes.

## TC packet (client → app)

| Offset | Size | Field | Value |
|---|---|---|---|
| 0 | 3 bits | Version number | 0 |
| | 1 bit | Type | 1 (TC) |
| | 1 bit | Data field header flag | 1 |
| | 11 bits | APID | `TcApid` (default 3) |
| 2 | 2 bits | Sequence flags | `11` (stand-alone packet) |
| | 14 bits | Sequence count | set by the client; echoed in the reply |
| 4 | u16 | Packet length | number of bytes after the primary header − 1 |
| 6 | 1 bit | CCSDS secondary header flag | 0 |
| | 3 bits | PUS version | 1 (not checked) |
| | 4 bits | Ack flags | ignored: each TC gets exactly one reply TM |
| 7 | u8 | Service type | see [Packet identification](#packet-identification) |
| 8 | u8 | Service subtype | |
| (9) | u8 | Source ID | only when `TcHasSourceId` is true (default false); ignored |
| 9 | u16 | SID | |
| 11 | … | Application data | see below |
| end − 2 | u16 | PEC | CRC-16-CCITT |

When `TcHasSourceId` is true, the SID and everything after it move one byte later.

## TM packet (app → client)

This is the layout of the sample "TM Packet Format.xlsx":

| Offset | Size | Field | Value |
|---|---|---|---|
| 0 | 3 bits | Version number | 0 |
| | 1 bit | Type | 0 (TM) |
| | 1 bit | Data field header flag | 1 |
| | 11 bits | APID | `TmApid` (default 3) |
| 2 | 2 bits | Grouping flags | `11` |
| | 14 bits | Source sequence count | +1 for every TM packet; starts at 0 for each connection |
| 4 | u16 | Packet length | number of bytes after the primary header − 1 |
| 6 | 1 bit | Spare | 0 |
| | 3 bits | PUS version | 1 |
| | 4 bits | Spare | 0 |
| 7 | u8 | Service type | |
| 8 | u8 | Service subtype | |
| 9 | u8 | Packet subcounter | +1 for every TM of the same service type and subtype; starts at 0 for each connection |
| 10 | u32 | Time, coarse | whole seconds since 1970-01-01 00:00:00 UTC (Unix time) |
| 14 | u32 | Time, fine | fraction of the second × 2³² |
| 18 | u16 | SID | |
| 20 | … | Application data | see below |
| end − 2 | u16 | PEC | CRC-16-CCITT |

- **Time** is CUC with 4 coarse and 4 fine octets and no P-field. The epoch is `TimeEpoch` in `settings.xml` (default `1970-01-01T00:00:00Z`).
- For HK and READ reports, the time is when the supply was measured. For the other reports, it is when the report was sent.

## PEC

CRC-16-CCITT, the PUS standard: polynomial 0x1021, initial value 0xFFFF, not reflected, no final XOR. It covers the packet from its first byte up to the PEC (the sync markers are not included). For a check, the ASCII string `123456789` gives 0x29B1.

The app checks the PEC of every TC (`CheckTcPec`, default true) and always sends a correct PEC on TM. With `CheckTcPec` set to false, the two PEC bytes must still be present.

## Packet identification

Each packet is identified by service type, subtype and SID. All three can be changed in `settings.xml`. The defaults are:

| Packet | Direction | Service | Subtype | SID |
|---|---|---|---|---|
| SET | TC | 128 | 1 | 1 |
| OUTPUT | TC | 128 | 1 | 2 |
| READ | TC | 128 | 1 | 3 |
| Housekeeping (HK) | TM | 3 | 25 | 100 |
| READ report | TM | 128 | 2 | 101 |
| SET report | TM | 128 | 2 | 102 |
| OUTPUT report | TM | 128 | 2 | 103 |
| ERROR report | TM | 128 | 2 | 199 |

No two TCs may have the same service, subtype and SID; the app refuses to start remote control if they do. A TC whose service and subtype match no TC is answered with error 5. A TC with a known service and subtype but an unknown SID is answered with error 6.

## Telecommands

### SET (SID 1): change settings

| Offset in application data | Type | Field |
|---|---|---|
| 0 | u8 | Voltage: apply (0 = leave, 1 = set) |
| 1 | f32 | Voltage, V |
| 5 | u8 | Current: apply |
| 6 | f32 | Current, A |
| 10 | u8 | OVP: apply |
| 11 | f32 | OVP, V |
| 15 | u8 | OCP: apply |
| 16 | f32 | OCP, A |

20 bytes. The whole TC is 33 bytes (Packet length 26).

- Give at least one value (apply = 1). A value with apply = 0 is ignored, but must still be present.
- All values are checked before anything is sent. They are then sent in the order OVP, OCP, current, voltage, and the app stops at the first one that is not OK.
- Ranges and resolution are the same as in JSON. Voltage and current use the app's soft limits (default 0–60 V, 0–50 A). OVP is 2–61 V, OCP 2–50 A. Resolution is 1 mV, 10 mA, 0.1 V and 0.1 A. Values are rounded to the resolution before they are sent.
- Refused while a sequence runs in the app's Sequencer (error 22).
- Reply: SET report.

### OUTPUT (SID 2): turn the output on or off

| Offset | Type | Field |
|---|---|---|
| 0 | u8 | 0 = off, 1 = on |

1 byte. The whole TC is 14 bytes (Packet length 7).

- Turning on is refused while a protection trip is latched (error 23). Remove the cause and press TRIP RESET in the app.
- Turning on is refused while a sequence runs (error 22). Turning off is accepted whenever the app is connected, also while a sequence runs.
- Reply: OUTPUT report.

### READ (SID 3): measurements and settings

No application data. The whole TC is 13 bytes (Packet length 6). The app reads the supply when the request arrives. Allowed while a sequence runs. Reply: READ report.

## Telemetry

### Reply block

Every reply to a TC starts its application data with:

| Offset | Type | Field |
|---|---|---|
| 0 | u32 | Sequence count of the TC this answers |
| 4 | u8 | Error code: 0 = OK, see [Error codes](#error-codes) |

Each TC gets exactly one reply. It is the report for its SID when the app could read the TC (header, PEC, service, subtype, SID and length all valid). Otherwise it is an ERROR report. HK packets can arrive between a TC and its reply, so tell packets apart by SID.

### Status block

The HK packet and the READ report both carry the same 33-byte status block:

| Offset | Type | Field |
|---|---|---|
| 0 | u8 | Connected to the supply (0/1) |
| 1 | u8 | Output on (0/1) |
| 2 | u8 | Mode: 0 OFF, 1 CV, 2 CC, 3 UNREG (outside the power envelope) |
| 3 | u8 | Trip: 0 none, 1 OVP, 2 OCP, 3 SENSE, 4 FAULT (the first latched, in that order) |
| 4 | u8 | Sequence running in the app (0/1) |
| 5 | f32 | Measured voltage, V |
| 9 | f32 | Measured current, A |
| 13 | f32 | Measured power, W |
| 17 | f32 | Set voltage, V |
| 21 | f32 | Set current, A |
| 25 | f32 | OVP, V |
| 29 | f32 | OCP, A |

- A trip stays latched until TRIP RESET. A FAULT trip needs the supply's mains switched off and on.
- Set voltage, set current, OVP and OCP are read from the supply. In HK they are the latest values the app read: the app reads them every 3 s, and after every command.
- When the app is not connected to the supply, Connected is 0 and every other field is 0.

### Housekeeping (SID 100): periodic, no request needed

Application data: the status block, 33 bytes. The whole TM is 55 bytes (Packet length 48).

- Sent to the connected client after **every poll**, at the app's sample rate (0.5, 1, 2 or 5 Hz, chosen in the connection bar), carrying that poll's readings. It does not add any traffic to the supply.
- While the app is not connected to the supply, it is sent once per second with Connected = 0.
- If the client reads too slowly and an HK packet is still being sent when the next one is due, the next one is skipped.

### READ report (SID 101)

Application data: the reply block, then the status block. 38 bytes. The whole TM is 60 bytes.

### SET report (SID 102)

| Offset | Type | Field |
|---|---|---|
| 0 | 5 B | Reply block |
| 5 | 9 B | Voltage result |
| 14 | 9 B | Current result |
| 23 | 9 B | OVP result |
| 32 | 9 B | OCP result |

41 bytes. The whole TM is 63 bytes. Each result is:

| Offset | Type | Field |
|---|---|---|
| 0 | u8 | Result: 0 not sent, 1 OK, 2 Mismatch (read-back differs), 3 Failed (the supply reported an error, or the link failed) |
| 1 | f32 | Requested: the value sent, after rounding (not sent: the value in the TC) |
| 5 | f32 | Read-back: what the supply reported afterwards (0 if there is none) |

If a value is not OK, the error code says why, and the values after it in the sending order (OVP, OCP, current, voltage) are not sent.

### OUTPUT report (SID 103)

| Offset | Type | Field |
|---|---|---|
| 0 | 5 B | Reply block |
| 5 | u8 | Output state the supply reports afterwards: 0 off, 1 on, 255 unknown |
| 6 | u8 | Result: 0 not sent, 1 OK, 2 Mismatch, 3 Failed |

7 bytes. The whole TM is 29 bytes.

### ERROR report (SID 199)

The reply to a TC the app could not read, and the message sent to a second client.

| Offset | Type | Field |
|---|---|---|
| 0 | 5 B | Reply block |
| 5 | u8 | Service type of the TC |
| 6 | u8 | Service subtype of the TC |
| 7 | u32 | SID of the TC |

11 bytes. The whole TM is 33 bytes. A field the app could not read is all ones (0xFF or 0xFFFFFFFF). That includes the sequence count in the reply block.

## Error codes

| Code | Meaning | JSON equivalent |
|---|---|---|
| 0 | OK | `"ok": true` |
| 1 | Malformed packet: version, type, flags or length wrong for the SID; too short or too long | `Invalid JSON` |
| 2 | Sync footer missing after the packet | |
| 3 | PEC wrong | |
| 4 | Wrong APID | |
| 5 | Unknown service type or subtype | `Missing "cmd"` |
| 6 | Unknown SID | `Unknown command` |
| 7 | Invalid parameter: apply or on not 0/1, value not a finite number | `must be a number`, `must be true or false` |
| 8 | SET without any value | `Give at least one of …` |
| 10 | Voltage outside the soft limits | `"voltage" must be …` |
| 11 | Current outside the soft limits | `"current" must be …` |
| 12 | OVP outside 2–61 V | `"ovp" must be 2–61` |
| 13 | OCP outside 2–50 A | `"ocp" must be 2–50` |
| 20 | Not connected to the supply | `Not connected to the supply` |
| 21 | No valid reply from the supply | `No valid reply from the supply` |
| 22 | A sequence is running in the app | `A sequence is running in the app` |
| 23 | Output tripped: remove the cause and press TRIP RESET | `The output is tripped (…)` |
| 24 | The supply did not accept a value (Failed: EER? error or link failure) | `voltage: error 200: …` |
| 25 | Read-back mismatch | `voltage: …` (Mismatch) |
| 26 | The supply kept the output off (tripped?) | `The supply kept the output off (tripped?)` |
| 30 | Another client is connected | `Another client is connected` |
| 31 | The app is closing | `The app is closing` |
| 32 | Internal error | `Internal error: …` |

The full text of each error goes into the App Logs and, when the trace is on, into the trace file.

## Settings (`settings.xml`)

```xml
<RemotePort>9501</RemotePort>
<RemoteProtocol>Ccsds</RemoteProtocol>          <!-- Json or Ccsds; also set in the connection bar -->
<Ccsds>
  <TcApid>3</TcApid>
  <TmApid>3</TmApid>
  <SyncHeader>2A2A2323</SyncHeader>             <!-- hex, empty = none -->
  <SyncFooter>23232A2A</SyncFooter>
  <TcHasSourceId>false</TcHasSourceId>
  <CheckTcPec>true</CheckTcPec>
  <TimeEpoch>1970-01-01T00:00:00Z</TimeEpoch>
  <TcSet Service="128" Subtype="1" Sid="1" />
  <TcOutput Service="128" Subtype="1" Sid="2" />
  <TcRead Service="128" Subtype="1" Sid="3" />
  <TmHousekeeping Service="3" Subtype="25" Sid="100" />
  <TmReadReport Service="128" Subtype="2" Sid="101" />
  <TmSetReport Service="128" Subtype="2" Sid="102" />
  <TmOutputReport Service="128" Subtype="2" Sid="103" />
  <TmErrorReport Service="128" Subtype="2" Sid="199" />
</Ccsds>
```

- The app writes this section with the defaults the first time it saves `settings.xml`. Change it while the app is closed; it is read at start.
- APIDs are 0–2046, service and subtype 0–255, SID 0–65535. An invalid value is replaced by its default, and a warning goes into the App Logs.
- Sync markers are up to 16 bytes each.

## Logging

- Every command goes into the database `CommandLog` like a button press, with `Source` = `Remote`, and shows as a marker on the live plots and in History.
- "Remote control on/off" and "Remote client connected/left" are logged as Connection records.
- With the trace on (App Logs), the trace file has every TC (`R<`) and reply (`R>`) in hex with a short decoded summary, and every HK packet (`RH`).

## Example exchange

The default settings, TC sequence count 5: READ.

```
→ 2A2A2323 1803 C005 0006 10 80 01 0003 xxxx 23232A2A
     sync  APID  seq  len  ^  ^  ^  SID  PEC  sync
                           |  |  subtype 1
                           |  service 128
                           PUS version 1, ack 0

← 2A2A2323 0803 C00C 0035 10 80 02 00 <time 8 B> 0065 00000005 00 <status block 33 B> xxxx 23232A2A
                 seq 12   len 53       subcounter     SID 101  TC seq 5  OK
```

A test step (HK packets keep arriving in between and are not shown):

```
→ SET      V=12.0 apply, I=1.0 apply, OVP=14 apply, OCP=2 apply
← SET report     error 0, OVP OK 14/14, OCP OK 2/2, I OK 1/1, V OK 12/12
→ OUTPUT   on=1
← OUTPUT report  error 0, state 1, result OK
→ READ
← READ report    error 0, connected 1, on 1, mode CV, trip 0, V 12.001, I 0.601, …
   … the test software checks the unit over serial …
→ OUTPUT   on=0
← OUTPUT report  error 0, state 0, result OK
```
