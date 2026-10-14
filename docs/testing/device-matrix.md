# Device test matrix (ZKTeco)

Manual tests on real hardware before each release that touches devices (docs/06 Testing, P3-T2).
Automated tests cover the same paths with `FakeDeviceProtocol`; this file records what only real
devices can show: SDK behaviour, firmware quirks, timing and reg-free COM on customer PCs.

**Status: not run yet.** No hardware was available while Phase 3 was built. Fill one block per
device model and app version. Mark each cell Pass, Fail (with a note) or N/A.

## How to run
1. Put the ZKTeco Standalone SDK files in `src/SuperOfc.DeviceBridge/sdk` (see the README there)
   and build, or install a release build that includes them.
2. Use a PC on the same network as the device. Note the device IP, port (usually 4370) and
   communication key from the device menu (Comm. settings).
3. Back up the device first (USB export of users and logs) in case a test goes wrong.
4. Work through the cases below in order. Record firmware, platform and the app version.
5. Collect the log file from `%ProgramData%\SuperOfc\logs` for any failure.

## Cases
| # | Case | Steps | Expected |
|---|------|-------|----------|
| 1 | Connect | Devices > Add device, enter IP, Test connection | Serial, model and counts shown |
| 2 | Wrong key | Enter a wrong communication key, Test connection | "The device refused the communication key" |
| 3 | Wrong IP | Enter an address with no device, Test connection | Fails within about 10 seconds with a readable message |
| 4 | Full pull | Save, Sync now on a device with existing logs | All logs read; counts match the device log count |
| 5 | Incremental pull | Punch twice on the device, Sync now | Only the new punches are new; the overlap shows as duplicates |
| 6 | Large pull | Device with 10,000 or more logs | Completes; UI stays responsive; batches appear in the log |
| 7 | Power cycle mid-pull | Switch the device off during a large pull | Sync fails cleanly, device shows Offline; next sync completes without double punches |
| 8 | Clock drift | Set the device clock 5 minutes off, Sync now | Warning with the minutes; Fix now sets the clock |
| 9 | Users | Devices > Users | Device users listed with names and finger counts |
| 10 | Push employee | Send missing employees | Users appear on the device with ID and name |
| 11 | Fingerprint backup | Back up fingerprints for a user with enrolled fingers | Count matches enrolled fingers |
| 12 | Copy to second device | Copy linked users to another device | User can verify on the second device with the copied finger |
| 13 | Delete user | Delete from device | User and fingers removed from the device |
| 14 | USB export import | Import the `_attlog.dat` from the device, choosing the device | Counts match; a later network sync shows those punches as duplicates |
| 15 | Face device | Repeat 1, 4, 5 and 9 on a face terminal | Same results; face templates are not copied (finger only) |
| 16 | Reg-free COM | Install on a clean PC without registering the SDK | Test connection works without admin rights |
| 17 | Auto-sync | Leave the app open for two intervals | Two automatic entries in Sync history |

## Results

### Template (copy for each run)
- Date:
- App version:
- Tester:
- Device model / platform / firmware:
- Connection (LAN, Wi-Fi, port):

| # | Result | Notes |
|---|--------|-------|
| 1 | | |
| 2 | | |
| 3 | | |
| 4 | | |
| 5 | | |
| 6 | | |
| 7 | | |
| 8 | | |
| 9 | | |
| 10 | | |
| 11 | | |
| 12 | | |
| 13 | | |
| 14 | | |
| 15 | | |
| 16 | | |
| 17 | | |
