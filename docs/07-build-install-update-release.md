# 07 — Build, Install, Update and Release

Principle: **every phase ends in an installable build.** Packaging is set up in Phase 0, not
at the end.

## Versioning
- Semantic versioning `MAJOR.MINOR.PATCH`. Pre-release builds `1.2.0-beta.3`.
- Source of truth: `<Version>` in `Directory.Build.props`; CI overrides with `-p:Version=`
  from the git tag `v1.2.3`. `InformationalVersion` adds the short commit SHA.
- Database schema version = applied EF migrations. The app records `AppVersion` in
  `BackupRecord` and in a `SchemaInfo` table on every successful migration.
- Compatibility rule: an older app refuses to open a database with migrations it does not
  know (message: "This data was created by a newer version. Please update.").

## Build outputs
```
# Main app (x64, self-contained, ReadyToRun for faster start; no trimming for WPF)
dotnet publish src/SuperOfc.Desktop -c Release -r win-x64 --self-contained true `
  -p:PublishReadyToRun=true -p:Version=%VERSION% -o artifacts/publish

# Device bridge (x86, self-contained) goes into a subfolder of the same publish dir
dotnet publish src/SuperOfc.DeviceBridge -c Release -r win-x86 --self-contained true `
  -p:Version=%VERSION% -o artifacts/publish/bridge
```
Include in `artifacts/publish/bridge`: `zkemkeeper.dll` + vendor dependencies + the
reg-free COM manifest. Do not use single-file publish (COM and native deps).

## Installer and updates: Velopack (ADR-05)
```
dotnet tool install -g vpk
vpk pack --packId SuperOfc --packVersion %VERSION% --packDir artifacts/publish `
  --mainExe SuperOfc.Desktop.exe --packTitle "SuperOfc" --packAuthors "<Company>" `
  --icon assets/app.ico --channel stable --outputDir artifacts/releases `
  --releaseNotes CHANGELOG-current.md
```
Produces `SuperOfc-win-Setup.exe`, full + delta `.nupkg`, `releases.stable.json`.
Install location: per-user `%LocalAppData%\SuperOfc\` (no admin needed). Shortcuts: Start menu + Desktop.

In code, the first line of `Program.Main` (Desktop uses an explicit `Program.cs`, not the
generated WPF entry point):
```csharp
VelopackApp.Build()
    .OnFirstRun(v => FirstRunFlag.Set())
    .Run();
```
Update manager (`Infrastructure/Updates/UpdateService`):
- Source: `SimpleWebSource("https://<updates-host>/superofc/<channel>/")` or
  `GithubSource(repoUrl, token: null, prerelease: channel == "beta")`. Host decided in doc 10.
- Check 10 s after startup and every 6 h. Download in background. Show InfoBar
  "Version X is ready — Restart to update" with release notes link. Apply with
  `ApplyUpdatesAndRestart` only when: no payroll run in progress, no unsaved changes,
  no device sync running.
- Settings > Updates: mode `Automatic (default) | Notify only | Manual`, channel
  `stable | beta`, "Check now", current version, last check.
- If running unpacked (developer `dotnet run`), `UpdateManager.IsInstalled` is false and
  updates are silently disabled.

## Code signing
- Sign `SuperOfc.Desktop.exe`, `SuperOfc.DeviceBridge.exe`, and the Setup via
  `vpk pack --signParams "/tr http://timestamp.digicert.com /td sha256 /fd sha256 /a"`
  (certificate in the machine store on the build machine or an HSM/cloud signing
  template via `--signTemplate`).
- Without a certificate Windows SmartScreen warns on first install. An OV or EV code-signing
  certificate is a business decision (doc 10). Never ship unsigned to customers.

## Data layout and permissions
```
%ProgramData%\SuperOfc\
  data\superofc.db (+ -wal, -shm)
  backups\auto\  backups\manual\  backups\premigration\
  files\employees\<EmployeeId>\...   (photos, documents)
  logs\app-YYYYMMDD.log  logs\bridge-YYYYMMDD.log
  settings.json  (machine-level: data path override, channel)
```
Create `%ProgramData%\SuperOfc` on first run with an ACL granting `Users: Modify` so any
Windows account on the PC can use the same data. Data path can be moved from Settings
(copies the folder, verifies, then switches).

## Database migrations at startup (safe sequence)
1. Open DB, `PRAGMA integrity_check` (quick); if it fails, stop and offer Restore.
2. If pending migrations exist: create `premigration` backup via SQLite online backup API
   (`SqliteConnection.BackupDatabase`), verify it, then run `Database.MigrateAsync()`
   (SQLite DDL is transactional per migration).
3. On failure: close, restore the premigration copy, log, show "Update could not migrate
   your data. Your data is unchanged. Send diagnostics." and keep the app closed.
4. Run idempotent seeders. Record `SchemaInfo {AppVersion, MigratedAtUtc}`.

## Backups
- Automatic: first launch of each day; before migration; before payroll finalize.
- Retention: 30 daily, 12 monthly, all premigration for 90 days; manual kept forever.
- Each backup: online backup API -> `superofc-YYYYMMDD-HHmm.db` -> `PRAGMA integrity_check`
  on the copy -> zip with `files\` optional (setting) -> `BackupRecord` row.
- Secondary location (external drive / OneDrive folder) optional in Settings.
- Restore: pick a file, verify, backup the current DB first, replace, restart.

## Continuous integration (GitHub Actions, `windows-latest`)

Two workflows in `.github/workflows/`:

| Workflow | Runs on | Does |
|----------|---------|------|
| `ci.yml` | every push to `main`, every pull request, manual | restore, build with `-warnaserror`, run all tests, upload the test results |
| `release.yml` | a pushed tag `v*` | build, test and package with `tools/release/publish.ps1`, then publish the installer and update feed |

Release steps in `release.yml`:

1. `VERSION` comes from the tag (`v1.2.3` gives `1.2.3`); `CHANNEL` is `beta` when the version has a
   pre-release part (`-beta.1`), otherwise `stable`.
2. `vpk download github` fetches the previous release so Velopack can build a small delta package.
3. `publish.ps1` builds, tests, publishes the app and the device bridge, and runs `vpk pack`.
4. `vpk upload github` publishes `Setup.exe`, the portable zip, the packages and `releases.<channel>.json`
   to the **public releases repository** `monirsaikat/superofc-releases` (beta releases are marked
   pre-release). The source repository stays private.
5. The installer is also attached to a release in the source repository, with the CHANGELOG notes.

The workflow needs the secret `RELEASES_TOKEN`: a fine-grained personal access token with
"Contents: read and write" on `monirsaikat/superofc-releases` only. Signing is added with
`-SignParams` when the certificate exists (Q49).

How to release: update `CHANGELOG.md`, commit, then

```
git tag v1.0.0-beta.3
git push origin v1.0.0-beta.3
```

## Where installed copies look for updates

`UpdateSettings.DefaultFeedUrl` is `https://github.com/monirsaikat/superofc-releases`. For a github.com
address the app uses Velopack's `GithubSource` (pre-releases included on the beta channel); any other
address is read as a plain web folder holding the feed files. The channel defaults to the one the copy
was installed from, and an administrator can change it under Settings › Updates.

## Release channels and process
- `beta`: internal/pilot customers. Every merged feature set gets a beta tag.
- `stable`: promoted beta with no open blockers. Same commit, retagged `vX.Y.Z`.
- Hotfix: branch from the stable tag, patch bump, both channels.

### Release checklist (copy into the release issue)
- [ ] `CHANGELOG.md`: move `[Unreleased]` to `[X.Y.Z] - YYYY-MM-DD`; user-facing wording.
- [ ] All tests green; golden payroll fixtures unchanged or approved.
- [ ] Fresh install on a clean Windows 11 VM: first-run wizard, create employee, sync fake
  device, run payroll, backup/restore, uninstall.
- [ ] Upgrade test: install previous stable, create data, install new build over it,
  migration succeeds, data intact, premigration backup exists.
- [ ] Device test matrix (doc 06) passed on real hardware for any device-related change.
- [ ] Signed binaries; SmartScreen check on a machine that never saw the app.
- [ ] Update feed verified: previous version detects and applies the update.
- [ ] Rollback plan noted: previous Setup.exe link kept in the release notes; data rollback
  = restore premigration backup.
- [ ] Tag pushed, release notes published, pinned "known issues" updated.

## Rollback
- App: install the previous `Setup.exe` (kept in the feed for the last 3 versions).
- Data: schema downgrades are not supported; restore the premigration backup created by
  the failed/unwanted version, then install the previous app version.

## Diagnostics
- Settings > About > "Export diagnostics": zip of last 7 days of logs, `settings.json`
  (secrets stripped), schema version, app version, OS version, device list (no keys).
- No automatic upload in v1 (doc 10).
