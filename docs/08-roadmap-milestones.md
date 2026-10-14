# 08 — Roadmap and Work Order

Each task is sized for one focused session of a cheaper AI model (roughly 1–4 hours of work).
Do tasks in order inside a phase; phases are mostly sequential. A task is complete only when
every acceptance criterion is verified and the Definition of Done in doc 05 is ticked.

Legend: **Refs** = docs to read first. **AC** = acceptance criteria.

## Progress (update this table when a phase ships)

| Phase | State | Notes |
|-------|-------|-------|
| 0 Foundation | Done | Installer and auto-update verified up to 0.1.0-beta.4 on the beta channel. |
| 1 Organization and employees | Done | Employees, import/export, master data, holidays, audit log. |
| 2 Attendance and leave | Done | Manual punches, engine (ADR-16), sheet, corrections, leave (ADR-17). Questions Q29–Q32. |
| 3 Devices (ZKTeco) | Done, hardware test pending | Bridge on .NET Framework 4.8 x86 (ADR-06 amendment), sync, mapping, fingerprint backup, file import, auto-sync. P3-T2 acceptance needs a real device: `docs/testing/device-matrix.md`. Questions Q33–Q35. |
| 4 Salary and payroll | Done | Components with formulas, structures, effective-dated salaries, bulk revision, engine with 13 golden fixtures, run wizard, lock banner, payslip PDF and preview (ADR-19), bank and cash sheets, register. Participants seam for loans and tax (ADR-18). Questions Q36–Q39. |
| 5 Loans and advances | Done | Requests, approval with projected net check, pay-out, schedule with interest, payroll deductions with skip and void, cash repayments, settlement per policy, write-off, leaver recovery, statement PDF, Employee › Loans tab. Payroll and loan policy editors. ADR-20. Questions Q40–Q42. |
| 6 Tax | Done, real country pack pending | Rules as versioned JSON with categories, exemptions, rebate and minimum tax; synthetic sample pack; declarations per employee; projection with explanation; tax line and year-end true-up in payroll; statement PDF and summary Excel; Tax page, rules editor with quick check, Employee › Tax tab. ADR-21. The first real pack waits for Q1 (Q43). Questions Q43–Q45. |
| 7 Reports and dashboard | Done, timing check in Phase 8 | `IReport` framework (ADR-22) with catalog, generated choices, preview, PDF and Excel. Reports: attendance summary, late and absent list, overtime, headcount with joiners and leavers, salary cost by department, loan ledger, leave balances; each tested against its source data. Shared PDF header, footer and page numbers. Dashboard tiles for payroll, loans, tax, devices and expiring documents from one query, loaded after the shell with a progress ring. The 2 s preview and 300 ms dashboard targets are measured in P8-T1. Questions Q46–Q48. |
| 8 Hardening and v1.0 | Done except items that need a person, hardware or a certificate | Performance benchmark `tools/SuperOfc.Bench` with results in `docs/testing/perf.md` (all targets met; full export sped up to 21 s). Security tests (permissions, anonymous requests, secrets in logs, path traversal). Localization tests, missing-translation logging and a Bengali scaffold. Full data export (Excel per module + JSON, no secrets) and diagnostics zip under Settings › About. User guide in `docs/user-guide/`, release notes draft, and `docs/testing/release-1.0-checklist.md`. Open: clean-VM install, upgrade from beta, device matrix, code signing (Q49). Questions Q49–Q50. |

---

## Phase 0 — Foundation (ends with installable v0.1.0 "empty shell")

### P0-T1 Solution skeleton
Refs: 02, 05.
Deliver: `SuperOfc.sln`, all projects in `src/` and `tests/` with references per layer,
`Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `.gitignore`,
`CHANGELOG.md`, `README.md` (how to build/run), GitHub Actions build+test workflow.
AC: `dotnet build -warnaserror` and `dotnet test` succeed with one placeholder test per project;
`Domain` has no package references; dependency direction enforced by project references only.

### P0-T2 Domain primitives
Refs: 03 (Conventions), 05.
Deliver: `Entity` base (Id, CompanyId, audit fields, RowVersion), `Money` struct with
arithmetic, allocation (split without losing minor units), comparison, `Currency` validation;
`Result`/`Result<T>`/`Error`; `IClock`, `IIdGenerator`; `DateRange` with overlap check;
`PolicySnapshot` record (empty for now).
AC: unit tests for Money (add, multiply by percent, allocate 100 into 3 = 34/33/33, rounding
modes), DateRange overlap, Result composition.

### P0-T3 Persistence foundation
Refs: 02 ADR-03, 05 Database rules.
Deliver: `AppDbContext : IAppDbContext`, SQLite options (WAL, foreign keys, busy timeout 5 s),
value converters (Money->INTEGER, DateOnly->TEXT, DateTime UTC->TEXT), interceptor that fills
audit fields and increments RowVersion, entities `Company`, `User`, `Role`, `Setting`,
`AuditEntry`, `BackupRecord`, `SchemaInfo`; initial migration; `IDataSeeder` seeding roles;
`IAuditWriter`.
AC: migration applies to an empty DB; model-diff test shows no pending model changes;
saving an entity sets audit fields; Money round-trips; a `SUM` over Money column in SQL
returns the correct integer.

### P0-T4 Desktop shell
Refs: 02, 04 App shell, 05, 07 (Velopack entry point).
Deliver: `Program.cs` with `VelopackApp.Build().Run()`, Generic Host DI composition,
Serilog, global exception handlers, single-instance mutex, `FluentWindow` + `NavigationView`
with placeholder pages for every top-level item, light/dark theme, `INavigationService`,
`IDialogService`, `IBusyService`, resx strings, startup migration sequence from doc 07
(with premigration backup), `Settings > About` showing version.
AC: cold start to visible shell < 1 s on dev machine (log the timing); theme toggle works;
killing the DB file mid-run shows the friendly error, not a crash; second instance focuses the first.

### P0-T5 Users, login and roles
Refs: 03 Security, 04 Dialogs, 05.
Deliver: login page, `ICurrentUser`, password hashing (`PasswordHasher<User>`), lockout after
5 failures for 10 min, roles and a `[RequiresRole]`-style check in handlers via
`IAuthorizationService`, lock screen (`Ctrl+L` and after N idle minutes, policy), Users &
roles settings page (Admin only), audit entries for login/logout/failed login.
AC: handler tests prove a Viewer cannot execute a mutating command; lockout works; audit rows
exist; passwords never in logs.

### P0-T6 First-run wizard and company settings
Refs: 04 Wizard, 03 Organization.
Deliver: wizard (company name, currency, time zone, fiscal year start, working week, admin
user, "load sample data" option), Company settings page, `MoneyFormatter`, `IPolicyProvider`
with `PolicySnapshot` built from `Setting` rows.
AC: fresh DB -> wizard -> dashboard; currency symbol appears via formatter; sample data is
idempotent; reopening app skips wizard.

### P0-T7 Backup and restore
Refs: 07 Backups.
Deliver: `IBackupService` (online backup API, integrity check, retention, zip option),
daily auto backup on first launch, manual backup, restore flow with confirmation and
restart, Settings > Backup page with history and secondary location.
AC: backup file passes `integrity_check`; restore replaces DB and app restarts into the
restored data; retention deletes correctly in a test with fake clock; `BackupRecord` rows written.

### P0-T8 Packaging and auto-update
Refs: 07.
Deliver: `tools/release/publish.ps1` (publish both exes, vpk pack), `UpdateService`,
Settings > Updates page, InfoBar for ready updates, CI release job on tags, first
`v0.1.0-beta.1` artifact.
AC: Setup.exe installs per-user and launches; running a second build `v0.1.0-beta.2` through
the feed updates the installed app; updates disabled when not installed; bridge folder is
present in the install (even if empty of SDK yet).

---

## Phase 1 — Organization and Employees

### P1-T1 Master data CRUD
Refs: 03 Organization, 04 List/Detail patterns.
Deliver: Departments, Designations, Locations, Grades: entities, migration, handlers
(create/update/soft-delete/list), settings pages with inline editing, audit.
AC: unique names per company enforced with readable error; soft-deleted items hidden but
still resolvable on old records; tests for each handler happy/failure path.

### P1-T2 Working week and holidays
Refs: 03 Organization.
Deliver: `WorkingWeek` editor, `Holiday` CRUD with yearly list and import from previous year,
`ICalendarService.GetDayKind(date, location)` in Domain.
AC: tests: half-day weekday, holiday on weekly off, location-specific holiday.

### P1-T3 Employee entity and list
Refs: 03 Employees, 04 List page, 05 Queries.
Deliver: `Employee`, `EmploymentEvent`, migration, `GetEmployeeListQuery` (paging, search by
code/name/phone, filters department/status/location, sorting), Employees page.
AC: 10,000 generated employees: list loads first page < 150 ms, scroll smooth, search debounced;
`Employee.Code` unique; status badge colors from theme.

### P1-T4 Employee detail and edit
Refs: 04 Detail page, 03 Employees.
Deliver: tabs Profile, Employment (events timeline, join/confirm/transfer/promote/exit
actions), Documents (file storage under `files\`, expiry), photo with thumbnail cache,
custom fields (JSON schema defined in Settings), validation, unsaved-changes guard.
AC: all mutations audited with before/after; exit sets status `Left` and blocks future
attendance computation; validation messages localized; works at 125 % DPI.

### P1-T5 Bulk import
Refs: 04 Wizard.
Deliver: download template (Excel), upload, column mapping, preview with row-level errors,
commit in a transaction, result report (Excel of failed rows).
AC: 1,000-row import < 5 s; duplicates by code rejected with row number; partial import never happens.

### P1-T6 Audit log viewer
Deliver: Settings > Audit log: filter by user/entity/date, before/after diff view, export.
AC: paging works on 100k rows; no sensitive fields displayed for Viewer role.

---

## Phase 2 — Attendance and Leave (manual)

### P2-T1 Shifts and assignments
Refs: 03 Attendance, R1 step 1.
Deliver: `Shift`, `ShiftAssignment` (effective dated, optional rotation pattern), Settings >
Shifts, Employee > Attendance tab shows current shift; `IShiftResolver.Resolve(employee, date)`.
AC: overlap validation; night shift crossing midnight resolved correctly; rotation of 3 shifts
over a week tested.

### P2-T2 Punches and manual entry
Refs: 03 AttendancePunch.
Deliver: `AttendancePunch`, dedup unique index, Punch log page (filters, manual add with
reason, delete manual only), `AddManualPunchCommand`.
AC: duplicate manual punch (same minute) rejected; device punches cannot be deleted from UI;
audit present.

### P2-T3 Attendance day engine
Refs: 03 R1, R2; ADR-08.
Deliver: `AttendanceCalculator` (pure), `AttendancePolicy` in `PolicySnapshot` (grace,
pairing, missing punch rule, half/full thresholds, overtime rounding, late penalty),
`RecomputeAttendanceCommand(employeeIds, dateRange)`, `AttendanceDay` table.
AC: table-driven tests covering every status, missing punch options, holiday with punches,
night shift, joiner/leaver edges; recompute is idempotent; 500 employees x 31 days < 3 s.

### P2-T4 Monthly attendance sheet
Refs: 04 Grid-editing page.
Deliver: month grid, legend, department filter, cell flyout correction with reason
(`AttendanceCorrection`), reset correction, recompute button, Excel export.
AC: keyboard navigation; corrected cells visibly flagged; recompute does not overwrite corrections.

### P2-T5 Leave
Refs: 03 Leave, R6.
Deliver: `LeaveType`, `LeaveBalance`, `LeaveRequest`, accrual job (monthly/yearly with fake
clock tests), request/approve/reject/cancel handlers, Leave page (pending list, calendar),
integration: approved leave -> `AttendanceDay.Status = Leave`.
AC: balance never negative unless type allows; cancellation restores balance; carry-forward
capped; year rollover tested.

### P2-T6 Attendance policies settings page
Deliver: UI for all attendance policies with effective date, preview of effect on a sample day.
AC: changing policy and recomputing a month changes results as expected in a test.

---

## Phase 3 — Devices (ZKTeco)

### P3-T1 Bridge skeleton and contracts
Refs: 06, ADR-06.
Deliver: `SuperOfc.DeviceBridge.Contracts` (messages), `SuperOfc.DeviceBridge` (x86 console,
named-pipe server, job queue per device, `IDeviceProtocol`, `FakeDeviceProtocol` replaying
JSON), `DeviceBridgeClient` in Infrastructure (starts the bridge, timeouts, restart on next call).
AC: integration test starts bridge, pings, pulls 10,000 fake logs in batches < 10 s, kills
bridge mid-pull and verifies restart + clean failure.

### P3-T2 zkemkeeper adapter
Refs: 06 SDK calls.
Deliver: `ZkemkeeperProtocol` implementing connect/info/time/pull/users/templates with
reg-free COM manifest; `EnableDevice(false)` around bulk reads; error mapping to readable
messages.
AC: manual test on real device recorded in `docs/testing/device-matrix.md` (connect, pull
full, pull incremental, wrong key, offline); no SDK types leak outside the bridge.

### P3-T3 Devices UI and mapping
Refs: 06 Mapping UI, 04.
Deliver: `Device` CRUD with DPAPI-protected comm key, Devices page cards, Test connection,
Sync now with progress, sync log panel, Users mapping page, Unmapped punches page.
AC: sync inserts punches and recomputes only affected days; unmapped shown with count; clock
drift warning shown when fake device returns skewed time.

### P3-T4 Auto-sync scheduler
Deliver: hosted service with policy interval, pause during payroll finalize, backoff on failure.
AC: tests with fake clock verify scheduling and backoff; app shutdown cancels cleanly.

### P3-T5 Attendance file import
Refs: 06 File import.
Deliver: `.dat` and Excel/CSV import wizard reusing dedup/mapping path.
AC: sample files in `tests/Fixtures/Attendance/` import with correct counts.

### P3-T6 User push/pull and template backup
Deliver: push employee to device, pull users, backup/restore fingerprint templates to DB
(encrypted), copy user to another device.
AC: round-trip templates through fake protocol byte-identical.

---

## Phase 4 — Salary and Payroll

### P4-T1 Salary components and structures
Refs: 03 Salary, R3 step 2.
Deliver: `SalaryComponent` (kinds, calc types, formula with safe evaluator), `SalaryStructure`,
seed defaults (Basic, House rent %, Medical, Conveyance, Provident fund deduction placeholder),
Settings > Salary components, structure editor with live preview for a sample basic.
AC: formula evaluator rejects anything but `+ - * / ( )`, numbers and component codes;
percent-of-gross ordering respected; tests.

### P4-T2 Salary assignments
Deliver: `SalaryAssignment` effective-dated with lines; Employee > Salary tab (history,
new assignment from structure, edit future-dated), bulk revision wizard (percent increase).
AC: overlap prevented; current assignment resolved by date; audit; bulk revision creates
new assignments, never edits history.

### P4-T3 Payroll engine
Refs: 03 R2, R3, R7; 05 golden tests.
Deliver: `PayableDaysCalculator`, `PayslipCalculator` (pure), `PayrollPolicy` in snapshot
(prorate basis, mid-month change, overtime multiplier, rounding, minimum net), `PayrollPeriod`,
`PayrollRun`, `Payslip`, `PayslipLine`, `PayrollAdjustment`, `ComputePayrollCommand`.
AC: golden fixtures (at least: full month, joiner mid-month, leaver, unpaid leave, overtime,
half days, late penalty, mid-month raise) pass; invariants in doc 03 tested; 500 employees < 5 s.

### P4-T4 Payroll run UI
Refs: 04 Wizard, R7.
Deliver: Payroll page (periods), run wizard (select period -> recompute attendance -> compute
-> review grid with inline adjustments -> finalize), payslip preview panel, void with reason,
mark paid (single/bulk), period lock indicator across the app (attendance edits for locked
periods blocked with message).
AC: finalize is transactional; void reverses everything (verified by tests); UI blocks editing
locked data; Viewer can view but not act.

### P4-T5 Payslip PDF and payment sheets
Refs: ADR-07.
Deliver: QuestPDF payslip template (company header/logo, employee block, earnings/deductions,
days summary, loan and tax lines, net in words), bulk PDF export (one file per employee or
merged), bank transfer sheet and cash sheet Excel.
AC: 500 payslips rendered < 20 s; PDF text searchable; amounts formatted via `MoneyFormatter`;
sample PDF checked visually in light theme only (PDF is theme-independent).

### P4-T6 Payroll register report
Deliver: period register (per employee all components), department totals, export.
AC: totals equal sum of payslips exactly (integer math).

---

## Phase 5 — Loans and Advances

### P5-T1 Loan domain
Refs: 03 R4.
Deliver: `Loan`, `LoanInstallment`, `LoanTransaction`, `LoanScheduleGenerator`,
`LoanBalanceCalculator`, settlement rules.
AC: schedule sums exactly; last installment absorbs rounding; settlement variants tested.

### P5-T2 Loan UI
Deliver: Loans page, new loan form with live schedule preview, approve/disburse, statement
view with running balance, Employee > Loans tab, PDF statement.
AC: cannot approve if net pay would fall below minimum (warn with projected net); audit.

### P5-T3 Payroll integration
Deliver: due installments pulled into payslip (R3 step 5), skip rule, void reversal,
manual repayment outside payroll.
AC: golden fixture with loan; void returns installments to `Due`; statement matches payslips.

---

## Phase 6 — Tax

### P6-T1 Tax engine
Refs: 03 R5, ADR-08.
Deliver: `TaxRuleSet`, `TaxSlab`, `EmployeeTaxProfile`, `TaxComputation`, `TaxCalculator`
(pure), exemption rule DSL as JSON (fixed, percent-of-component with cap, per-category
threshold), rebate rules, minimum tax, country pack loader from `Infrastructure/Seed/Tax`.
AC: tests with a synthetic rule set; full-year projection equals annual slab result; first
real country pack per doc 10 answer, validated against a published example.

### P6-T2 Tax UI
Deliver: Tax page: rule set list and editor (slabs grid, exemptions, rebates), employee tax
profiles (declarations), monthly projection view with explanation lines.
AC: editing a rule set with finalized payroll creates a new effective-dated version, never edits history.

### P6-T3 Payroll and reports integration
Deliver: tax line in payslip (R3), year-end true-up, annual tax statement per employee (PDF),
tax summary report.
AC: golden fixture with tax; statement totals equal sum of monthly deductions.

---

## Phase 7 — Reports and Dashboard

### P7-T1 Report infrastructure
Deliver: `IReport` with parameters schema, Reports page (catalog, parameter form, preview,
export PDF/Excel), shared QuestPDF styles (header/footer/page numbers).
AC: adding a report requires only one class; preview under 2 s for monthly data.

### P7-T2 Core reports
Deliver: attendance summary (by employee/department), late/absent list, overtime report,
headcount and joiners/leavers, department salary cost, loan ledger, leave balance report.
AC: each report has a test asserting totals against the underlying data.

### P7-T3 Dashboard
Refs: 04 Key screens.
Deliver: tiles with live data, clickable navigation, loads after shell is visible.
AC: dashboard queries combined < 300 ms on 500 employees / 100k punches.

---

## Phase 8 — Hardening and v1.0

### P8-T1 Performance pass
Deliver: benchmark script generating 500 employees, 100k punches, 12 payroll runs; measure
startup, list paging, attendance recompute, payroll compute, PDF export; fix regressions;
add indexes as needed.
AC: all numbers in doc 01 "Product principles" met and recorded in `docs/testing/perf.md`.

### P8-T2 Security pass
Deliver: role checks on every mutating handler verified by a reflection test; DPAPI for all
secrets; lock screen idle; no sensitive data in logs (grep test); file storage path traversal checks.
AC: reflection test fails if a mutating handler lacks a role requirement.

### P8-T3 Data export and diagnostics
Deliver: export all data (Excel workbook per module + JSON), diagnostics zip (doc 07).
AC: export of 500 employees full dataset < 30 s; diagnostics contains no secrets.

### P8-T4 Localization pass
Deliver: all strings in resx verified by a test that scans XAML for literal text; number/date
formats from company settings everywhere; second language scaffold file.
AC: switching language resource shows no missing keys at runtime (fallback logged).

### P8-T5 Release 1.0
Refs: 07 Release checklist.
Deliver: signed build, user guide (`docs/user-guide/`), release notes, upgrade test from the
last beta, device matrix executed.
AC: checklist fully ticked and attached to the release.

---

## Later phases (outline only; detail when reached)
- **v1.x**: document expiry reminders, SMTP payslips (`IEmailSender`), multi-company,
  shift calendar, overtime approvals, dashboard charts (LiveCharts2), kiosk mode.
- **v2 Remote**: `SuperOfc.Server` (ASP.NET Core Minimal API hosting the same handlers),
  PostgreSQL provider, SQLite->PostgreSQL migration tool, desktop in client mode via
  `IAppDbContext` replaced by HTTP-backed repositories or a sync engine, employee self-service web.
- **v3 AI**: `IAssistantService` with provider in Infrastructure; natural-language questions
  translated to typed queries (never raw SQL); anomaly detection on punches and overtime;
  payslip explanations; HR letter drafts. Data access through read-only query helpers with
  role checks.
