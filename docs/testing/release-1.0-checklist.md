# Release 1.0 checklist status (P8-T5)

The checklist from doc 07, with what has been verified on the development machine and what still needs a person, a certificate or hardware.

| Item | Status | Evidence or what is needed |
|------|--------|----------------------------|
| CHANGELOG `[Unreleased]` moved to `[1.0.0]` | Open | Do this when the version is tagged. Release notes draft: `docs/release-notes/1.0.0.md`. |
| All tests green; golden payroll fixtures unchanged | Done | Release build with warnings as errors, full test run passing. Golden fixtures unchanged since Phase 4. |
| Performance targets (doc 01, doc 08) | Done | `docs/testing/perf.md`: every timed target met on 500 employees and 100,000 punches. |
| Security pass | Done | `SecurityTests`: permissions on every request, no anonymous requests beyond sign-in and setup, no secrets in log calls, file paths kept inside the data folder. |
| Localization pass | Done | `LocalizationTests`: no literal text in XAML, translation keys and placeholders checked, fallback logged. Bengali scaffold needs review by a native speaker (doc 10 Q50). |
| Data export and diagnostics | Done | `DataExportTests`; export of the full benchmark data set in 21 s. |
| Fresh install on a clean Windows 11 VM | Open | Needs a person: first-run wizard, create employee, sync a device or import a file, run payroll, backup and restore, uninstall. |
| Upgrade test from the last beta (0.1.0-beta.4) | Open | Needs a person: install the beta, create data, install 1.0 over it, check the data and the "before update" backup. |
| Device test matrix (doc 06) | Open | Needs real terminals: `docs/testing/device-matrix.md`. |
| Signed binaries; SmartScreen check | Blocked | Needs a code-signing certificate (doc 10 Q49). |
| Update feed verified | Open | Needs the signed build published to the stable channel, then an update from the beta. |
| Rollback plan in the release notes | Done | `docs/release-notes/1.0.0.md`, "Upgrading from a beta". |
| Tag pushed, release notes published | Open | After the items above. |
| Real country tax pack | Blocked | Waits for doc 10 Q1 and Q43. 1.0 can ship with the sample pack and the rules editor. |
