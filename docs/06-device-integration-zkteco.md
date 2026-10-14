# 06 — Device Integration (ZKTeco fingerprint / face terminals)

## Goal
Pull attendance punches from ZKTeco terminals on the LAN reliably, map device users to
employees, keep device clocks correct, and never block or crash the main app because of a
device. Manual entry and file import always remain available.

## Approach (ADR-06)
- Official ZKTeco **Standalone SDK** (`zkemkeeper.dll`, COM, 32-bit) hosted in a separate
  process `SuperOfc.DeviceBridge.exe` built for **.NET Framework 4.8, x86** (ADR-06 amendment),
  installed in the `bridge` folder next to the app.
- Main app (x64) talks to the bridge over a **named pipe** (`\\.\pipe\SuperOfc.DeviceBridge.<sessionId>`)
  with length-prefixed JSON messages (decided: 4-byte little-endian length + camelCase JSON, no
  gRPC). Contracts live in `SuperOfc.DeviceBridge.Contracts` (netstandard2.0). Each message has an
  `id`; replies carry the same id, so requests for different devices run concurrently. `PullLogs`
  sends one reply per batch of 500 with `final = false`, then a final reply.
- Inside the bridge, the SDK sits behind `IDeviceProtocol` so a managed TCP implementation
  (port of the open-source ZK protocol) can be added later without touching the app.
- Registration-free COM: ship `zkemkeeper.dll` and its dependencies in `bridge\sdk` with
  `zkemkeeper.sxs.manifest` that declares the COM classes. The bridge activates that manifest at
  run time (`CreateActCtx`), so no `regsvr32` and no admin rights. Without the manifest it falls
  back to a registered SDK. The SDK files are not in source control (`sdk/README.md`); without
  them `Ping` reports `sdkAvailable = false` and devices fail with `Device.SdkMissing`, while file
  import keeps working. A "Repair device driver" button (elevated `regsvr32`) is deferred until
  the hardware matrix shows reg-free COM failing somewhere (`docs/testing/device-matrix.md`).
- Fake mode for tests and demos: `--fake <session.json>` replaces the SDK with
  `FakeDeviceProtocol`. The app passes it when the `SUPEROFC_FAKE_DEVICES` environment variable
  points at a session file; `tests/Fixtures/Attendance/fake-devices.json` is a ready demo.
- Check the ZKTeco SDK redistribution terms before commercial release (question in doc 10).

## Bridge API (one request = one response; the bridge serializes work per device)
| Operation | Input | Output |
|-----------|-------|--------|
| `Ping` | — | bridge version, SDK loaded ok |
| `TestConnection` | ip, port, commKey | ok/error, serial, firmware, device name, user count, log count |
| `GetDeviceInfo` | device | serial, firmware, platform, face/finger capability, capacity, counts |
| `GetTime` / `SetTime` | device (+ utc time) | device local time |
| `PullLogs` | device, `fromLocal?`, `toLocal?` | list of `{deviceUserId, timestampLocal, verifyMode, inOutMode, workCode}` streamed in batches of 500 |
| `GetUsers` | device | list of `{deviceUserId, name, privilege, cardNo, enabled}` |
| `SetUser` / `DeleteUser` | device, user | ok |
| `GetTemplates` / `SetTemplates` | device, deviceUserId | finger templates (base64) for backup / copy to another device |
| `ClearLogs` | device, confirmToken | ok — admin only, never automatic |
| `Cancel` | jobId | ok |

SDK calls used (for the implementer): `Connect_Net`, `SetCommPassword`, `GetSerialNumber`,
`GetFirmwareVersion`, `GetDeviceTime`/`SetDeviceTime`, `EnableDevice(false)` around bulk
reads then `EnableDevice(true)`, `ReadGeneralLogData` + `SSR_GetGeneralLogData` loop (full
pull), `ReadTimeGLogData(start,end)` where supported (incremental), `SSR_GetAllUserInfo`,
`SSR_SetUserInfo`, `SSR_DeleteEnrollData`, `GetUserTmpExStr`/`SetUserTmpExStr`,
`GetLastError`, `RefreshData`, `Disconnect`. Always `Disconnect` in `finally`.

## Sync algorithm (Application layer: `SyncDevicePunchesHandler`)
1. Load device; if `LastSyncLocal` is null -> full pull; else pull from `LastSyncLocal - 24h`
   (overlap protects against clock changes and partial reads).
2. For each batch: convert `timestampLocal` to UTC using the device's configured time zone
   (company zone when empty) and store `LocalTime` in company time; dedup key
   `D:{serial}:{deviceUserId}:{yyyyMMddHHmmss}` (`AttendancePunch.DeviceKey`, readable on purpose
   so support can search it); skip if it exists in punches or unmapped punches.
3. Resolve employee via `DeviceUserMap`; if missing, auto-map by `Employee.Code == deviceUserId`,
   then by the number in the code when it is unique (`E0012` matches device user `12`). Automatic
   matches are saved as maps. Otherwise store in `UnmappedPunch` (same fields plus device) and
   count it. Shared code: `PunchIntake` (also used by file import).
4. Insert `AttendancePunch` rows in batches of 500 inside one transaction per batch.
5. After the pull: mark the affected (employee, date) pairs dirty and recompute
   `AttendanceDay` (rule R1) for them only.
6. Update `Device.LastSyncLocal` to the max punch timestamp seen (not "now"), `LastSyncAtUtc`,
   `Status = Online`. Write `DeviceSyncLog {device, started, finished, pulled, inserted,
   duplicates, unmapped, errors}`.
7. Clock check: if |device time - PC time| > 2 min, write a Warning to the sync log and show
   an InfoBar "Device clock is off by N minutes — Fix now".
8. Any failure: log, set `Status = Offline/Error` with message, keep what was inserted
   (idempotent), schedule retry with backoff 1, 5, 15 min.

## Bridge lifecycle (Infrastructure: `DeviceBridgeClient`)
- Started on demand (first device operation), stopped on app exit (pipe closes, and the bridge
  also watches the parent process). No periodic health ping: every call detects a dead bridge.
  If the process dies or a call exceeds its timeout (connect 10 s, pull 5 min of silence,
  others 30 s) the bridge is killed and the operation reports failure; the next call starts a
  fresh bridge. Cancelling a call sends `Cancel` for its id and leaves the bridge running.
- One operation at a time per device; different devices in parallel up to 4.
- The bridge never touches the database or employee data.

## Auto-sync scheduler (Desktop background service)
- `DeviceAutoSync` (hosted service) runs `SyncDeviceCommand` for each enabled device every
  `AutoSyncMinutes` (**policy**, default 15) while someone is signed in, plus "Sync all" on demand.
  Scheduling lives in `DeviceSyncSchedule` (pure, tested with a fake clock).
- Failures retry after 1, 5, then every 15 minutes (never later than the interval). After 3
  failures in a row the user sees an InfoBar; a clock drift above 2 minutes shows an InfoBar with
  "Fix now" (`SetDeviceTimeCommand`).
- `IDeviceSyncGate.Pause(reason)` stops automatic sync; payroll finalize (Phase 4) holds it.

## Mapping UI
- Devices page -> Users: table of device users with mapped employee (or "Unmapped"),
  bulk auto-map by code, manual pick, push employee as device user (name + id), delete.
- Unmapped punches page: group by device user id, assign employee, which re-processes those
  punches into `AttendancePunch` and recomputes days. "Discard" deletes them (test users,
  visitor cards); a later sync may read the last 24 hours again because of the overlap.
- Users page also backs up fingerprints (`FingerprintBackup`, template text encrypted with DPAPI)
  and copies linked users with their fingerprints to another device (backup, then push).
- Pushed users get the existing map id or the number in the employee code as device user id,
  and at most 24 characters of the name.

## File import fallback (`ImportAttendanceFileHandler`)
- ZKTeco USB export `*_attlog.dat` (tab-separated: userId, `yyyy-MM-dd HH:mm:ss`, verify,
  status, workcode) and Excel/CSV with columns `EmployeeCode, DateTime, Type`.
- Preview with counts (valid, duplicate, unmapped, unreadable lines), then commit using the same
  dedup and mapping path with `Source = Import`.
- For a `.dat` file the user may pick the device it came from: its maps are used and the dedup
  keys use its serial, so a later network sync does not count those punches twice. Spreadsheet
  rows use keys `F:{code}:{yyyyMMddHHmmss}`. Each import writes a `DeviceSyncLog` with trigger File.

## Security
- `CommKey` stored encrypted with Windows DPAPI (machine scope, so every Windows account on the PC can sync; see `ISecretProtector`) in the `Device` row.
- Device credentials never logged. Bridge pipe ACL restricted to the current user.

## Testing
- `FakeDeviceProtocol` in the bridge replays a JSON session file (users + logs) so the whole
  sync path runs in CI without hardware.
  - `SuperOfc.Infrastructure.Tests/DeviceBridgeTests` runs the real bridge: ping, 10,000 logs in
    batches (well under 10 s), killed bridge mid-pull then restart, cancel, users and templates.
  - `SuperOfc.Application.Tests/DeviceTests` uses an in-memory gateway: overlap, duplicates,
    unmapped users, failure mid-pull, night shift crossing midnight, device time zone, mapping,
    fingerprint backup and restore, file import, and the scheduler backoff.
  - `SuperOfc.Desktop.Tests/DevicesFlowTests` drives the pages against the real bridge in fake mode.
- Manual test matrix before release: one fingerprint model (e.g., K40/U160 class), one
  face model (e.g., MB/SpeedFace class), wrong IP, wrong comm key, device power-cycled
  mid-pull, 10,000+ logs. Results go in `docs/testing/device-matrix.md`.
