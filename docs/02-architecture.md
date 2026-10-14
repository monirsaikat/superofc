# 02 — Architecture

## Stack (fixed)

| Concern | Choice | Version |
|---------|--------|---------|
| Runtime | .NET (LTS) | 10.0 |
| Language | C# | 14 |
| UI | WPF (Windows Presentation Foundation) | built into .NET 10 |
| UI kit | WPF-UI (`WPF-UI` NuGet by lepoco) — Fluent/Windows 11 look | latest stable 4.x |
| MVVM | CommunityToolkit.Mvvm (source generators) | 8.x |
| DI / hosting | Microsoft.Extensions.Hosting + DependencyInjection | 10.x |
| Database | SQLite via Microsoft.EntityFrameworkCore.Sqlite | EF Core 10 |
| Logging | Serilog (File + Debug sinks) | latest |
| Validation | FluentValidation | latest |
| PDF | QuestPDF (Community license) | latest |
| Excel | ClosedXML | latest |
| Installer/updates | Velopack (`vpk` CLI + `Velopack` NuGet) | latest |
| Device bridge IPC | gRPC over named pipes (`Grpc.AspNetCore` + `GrpcDotNetNamedPipes`) or plain JSON over named pipes | see 06 |
| Tests | xUnit + AwesomeAssertions + NSubstitute | latest (see ADR-12) |
| Charts (v1.x) | LiveCharts2 (SkiaSharp WPF) | latest |

Pin versions in `Directory.Packages.props` (central package management). Do not add a NuGet
package not listed here without recording the reason in an ADR below.

## Solution layout

```
SuperOfc.sln
Directory.Build.props          # TargetFramework, Nullable, LangVersion, warnings-as-errors
Directory.Packages.props       # central package versions
src/
  SuperOfc.Domain/             # entities, value objects, domain services, rule engines. NO dependencies.
  SuperOfc.Application/        # use cases (commands/queries), interfaces (ports), DTOs, validators.
                               # depends on Domain only.
  SuperOfc.Infrastructure/     # EF Core DbContext, migrations, repositories, file/PDF/Excel,
                               # backup, device bridge client. depends on Application.
  SuperOfc.Desktop/            # WPF app: views, view models, navigation, DI composition root.
                               # depends on Application + Infrastructure (composition only).
  SuperOfc.DeviceBridge/       # x86 console/service process wrapping ZKTeco zkemkeeper COM SDK.
                               # depends on SuperOfc.DeviceBridge.Contracts only.
  SuperOfc.DeviceBridge.Contracts/  # IPC messages shared by Infrastructure and DeviceBridge.
tests/
  SuperOfc.Domain.Tests/
  SuperOfc.Application.Tests/
  SuperOfc.Infrastructure.Tests/   # uses SQLite in-memory / temp file
  SuperOfc.Desktop.Tests/          # view model tests only (no UI automation in v1)
tools/
  release/                     # scripts: build, sign, pack, publish (see 07)
docs/                          # these guidelines
CHANGELOG.md
```

Dependency direction is one-way: `Desktop -> Infrastructure -> Application -> Domain`.
Enforce with project references only; `Domain` has zero package references except
`CommunityToolkit.Diagnostics` (guards) if wanted.

## Runtime shape (v1)

```
+----------------------------------------------------------+
| SuperOfc.Desktop.exe (x64, WPF)                          |
|  Views <-> ViewModels <-> Application use cases          |
|                              |                           |
|                    Infrastructure (EF Core/SQLite, PDF)  |
|                              |                           |
|            DeviceBridgeClient (named pipe)               |
+------------------------------|---------------------------+
                               | IPC (named pipe, JSON/gRPC)
+------------------------------|---------------------------+
| SuperOfc.DeviceBridge.exe (x86) — zkemkeeper COM         |
|   TCP/IP  ----> ZKTeco terminal(s) on LAN                |
+----------------------------------------------------------+
Data: %ProgramData%\SuperOfc\data\superofc.db  (+ WAL)
Backups: %ProgramData%\SuperOfc\backups\
Logs: %ProgramData%\SuperOfc\logs\
Install: %LocalAppData%\SuperOfc\  (Velopack default)
```

## Application layer pattern

- One use case = one class: `XxxCommand` / `XxxQuery` + `XxxHandler`. Keep it plain
  (no MediatR); register handlers in DI and resolve them from view models via a small
  `IUseCaseDispatcher` or direct injection. Keep it simple: direct injection of the handler.
- Handlers return `Result<T>` (success/failure with error list), never throw for
  business-rule failures. Throw only for programmer errors or infrastructure faults.
- Ports (interfaces) live in `Application/Abstractions`: `IAppDbContext`, `IClock`,
  `ICurrentUser`, `IFileStorage`, `IPdfRenderer`, `IAttendanceDeviceGateway`,
  `IBackupService`, `IAssistantService` (stub for v3).
- Every mutating handler writes an `AuditEntry` through `IAuditWriter` in the same transaction.

## Decision records (ADRs)

### ADR-01 — WPF on .NET 10 instead of WinUI 3, Avalonia, MAUI or Electron
- **Decision:** WPF.
- **Why:** mature, fastest to build with, enormous example corpus (cheaper AI models produce
  correct WPF far more reliably than WinUI 3), stable tooling, no packaging surprises,
  DataGrid virtualization proven. Windows-only is acceptable (product is Windows-focused).
- **Trade-off:** no cross-platform desktop. Remote version will be web, not another desktop OS.
- **Rejected:** WinUI 3 (deployment and control gaps), Avalonia (smaller ecosystem for the
  model, no benefit without cross-platform need), Electron (performance, memory).

### ADR-02 — Fluent look via WPF-UI, MVVM via CommunityToolkit.Mvvm
- **Decision:** WPF-UI for controls/theme (NavigationView, Fluent window, dark/light), Toolkit
  for `[ObservableProperty]`, `[RelayCommand]`, messenger.
- **Why:** Windows 11-native feel with little custom styling; source generators remove
  boilerplate the cheaper model would otherwise get wrong.
- **Rule:** no third UI library mixed in. Custom controls go in `Desktop/Controls`.

### ADR-03 — EF Core + SQLite, integer money, GUID v7 ids, ISO dates
- **Decision:** EF Core 10 with SQLite provider; WAL journal mode; `PRAGMA foreign_keys=ON`.
- **Money:** `Money` value object in Domain (`long MinorUnits`, `string Currency`), stored
  as `INTEGER` column (minor units). Reason: SQLite has no decimal; EF stores `decimal` as
  TEXT which breaks `SUM`/`ORDER BY`. Display via a single formatter.
- **Ids:** `Guid` created with `Guid.CreateVersion7()` (time-ordered). Reason: future remote
  sync/merge without id collisions; still index-friendly.
- **Dates/times:** `DateOnly` for calendar days, `DateTime` (UTC) for instants, stored as
  ISO-8601 TEXT. Device punch times are local device time converted to UTC with the
  device's configured time zone.
- **Migrations:** always `dotnet ef migrations add`, never `EnsureCreated`. Applied at
  startup inside a backup-first step (see 07).
- **Future:** keep provider-agnostic SQL (no raw SQLite-only SQL); v2 swaps to PostgreSQL.

### ADR-04 — Modular monolith with clean layering
- **Decision:** four layers, one solution, one process (plus the device bridge).
- **Why:** lets the same `Application` layer be hosted in ASP.NET Core for v2 without rewrite.
- **Rule:** view models call use cases; use cases call ports; infrastructure implements ports.

### ADR-05 — Velopack for install and auto-update
- **Decision:** Velopack (`vpk pack`), delta updates, per-user install, release feed over HTTPS
  (GitHub Releases or a plain static file server).
- **Why:** single tool gives installer + updater + delta packages + rollback-safe layout;
  simpler than MSIX (certificate/Store friction) or Inno Setup + custom updater.
- **Rejected:** MSIX (sandboxing hurts COM bridge and ProgramData), ClickOnce (dated).

### ADR-06 — ZKTeco via a separate 32-bit bridge process
- **Decision:** `SuperOfc.DeviceBridge.exe` (x86) hosts the zkemkeeper COM SDK and exposes a
  small named-pipe API. Main app stays x64.
- **Why:** the vendor COM SDK is 32-bit only; loading it in-process would force the whole app
  to x86 and crash the UI on SDK faults. Isolation also lets us restart the bridge.
- **Fallback:** USB `.dat` attendance file import. Future: ADMS/push protocol (device pushes to
  our HTTP endpoint) once v2 server exists.
- **Amendment (Phase 3, 2026-10-14):** the bridge targets **.NET Framework 4.8, x86** instead of a
  self-contained .NET 10 win-x86 app. Windows 10 1903+ and Windows 11 already include 4.8, so the
  installer does not carry a second runtime (about 70 MB saved; the bridge folder is about 1.4 MB).
  `SuperOfc.DeviceBridge.Contracts` targets netstandard2.0 so both sides share it. Transport is
  length-prefixed JSON over a named pipe (4-byte little-endian length, camelCase JSON; no gRPC).
  The pipe name carries a random suffix per app session and its ACL allows only the current user.
  The bridge exits when the pipe closes or the parent process ends (`--parent <pid>`).

### ADR-07 — QuestPDF for payslips/reports, ClosedXML for Excel
- **Decision:** QuestPDF with fluent layouts in `Infrastructure/Reporting`. Templates are
  C# classes, one per document. Excel via ClosedXML.
- **Why:** deterministic layouts, fast, no external renderer. Community license is free below
  the revenue threshold; re-check license before commercial sale.

### ADR-08 — Rules as data
- **Decision:** tax slabs, overtime multipliers, late/early-leave penalties, rounding policy,
  leave accrual, loan interest method are rows in configuration tables with effective dates.
- **Why:** country/company differences must be changed by the admin, not by a release.
- **Rule:** the engine in `Domain` takes a `PolicySnapshot` object; it never reads the DB.

### ADR-09 — Serilog file logging, no telemetry in v1
- Logs to `%ProgramData%\SuperOfc\logs\app-.log` rolling daily, 14 days retained.
  Unhandled exceptions are logged and shown with a "copy details" dialog. No network
  telemetry until the owner decides (see 10).

### ADR-10 — Localization-ready from day one
- All UI strings in `Desktop/Resources/Strings.resx` (+ `Strings.bn.resx` etc. later).
  Currency, number and date formats come from company settings, not from the OS culture.

### ADR-11 — Single-writer data access
- v1 supports one running instance (named mutex). Multi-PC access to one SQLite file over a
  network share is forbidden by the product (corruption risk). Multi-user comes with v2 server.

### ADR-12 — AwesomeAssertions instead of FluentAssertions
- FluentAssertions 8+ requires a paid commercial license. AwesomeAssertions is the Apache-2.0 fork with the
  same API (`.Should()`). Do not add FluentAssertions.

### ADR-13 — Request dispatcher with one DI scope per request (implemented in Phase 0)
- Every use case is a record implementing `ICommand<T>` or `IQuery<T>` with an `IRequestHandler<TRequest,T>`.
  View models call `IDispatcher.SendAsync(request)`. The dispatcher creates a DI scope (fresh DbContext),
  checks permissions, runs FluentValidation validators, invokes the handler and logs timing.
- Commands must carry `[RequiresPermission(...)]` or `[AllowAnonymous]`; a test fails otherwise.
  Queries default to `Permission.ViewData`.
- Validation errors use codes `Validation.<Field>` so edit pages can show them under the field.

### ADR-14 — Own navigation, WPF-UI for chrome and controls only
- The shell uses its own `IAppNavigator` (view-model-first, back stack, unsaved-changes guard) and a
  convention `ViewLocator` (`FooViewModel` -> `FooPage` or `FooView`). WPF-UI `NavigationView`/`InfoBar`
  are not used: `InfoBar` cannot host an action button, and its navigation couples views to the library.
- `ui:Card` ignores `Width` when centered; entry screens use a `Border` with the `EntryCard` style.

### ADR-15 — Money has no currency field
- Currency is a company setting (one currency per company, doc 10 Q2). `Money` holds only `long Minor`.
  Formatting always goes through `MoneyFormatter.For(company)`.

### ADR-16 — Attendance days are written with a prepared SQL upsert (implemented in Phase 2)
- A month recompute writes about 15,000 rows for 500 employees. EF change tracking took 2.7 s of a 3.1 s
  recompute; one prepared `INSERT ... ON CONFLICT DO UPDATE` statement takes about 0.4 s.
- The SQL lives only in `Infrastructure/Persistence/AttendanceDayStore.cs` behind `IAttendanceDayStore`.
  It writes values in EF Core's SQLite text formats (upper-case GUIDs, ISO dates) so EF reads them unchanged.
- The `WHERE IsManuallyCorrected = 0` guard in the SQL is the last line of defence for corrections.
- Unchanged days are skipped, so a repeated recompute writes nothing. All other writes still go through EF.

### ADR-17 — Leave accrual is calculated, not posted by a job (implemented in Phase 2)
- `LeaveType.AccruedBy(yearStart, date, joinDate)` is pure. Balances are refreshed whenever they are read
  or changed, so there is no scheduled accrual job that can be missed while the PC is off.
- The leave year is the company fiscal year. Carry-forward is applied when the next year's balance row is
  first created, capped per leave type.

### ADR-18 — Payroll modules plug in as participants; payslips are snapshots (implemented in Phase 4)
- `ComputePayrollCommand` loads everything, calls the pure `PayslipCalculator`, and stores each payslip
  with copies of the employee's name, department, bank details and every line's basis. A payslip can be
  explained and printed later without re-running anything, even after the employee record changes.
- Loans (Phase 5) and tax (Phase 6) implement `IPayrollParticipant`: `PrepareAsync` returns per-employee
  deductions (all-or-nothing, R7a) and a tax function; `OnFinalizedAsync` and `OnVoidedAsync` post or
  revert their own records inside the same transaction. The payroll handlers do not reference those modules.
- The policy snapshot is stored as JSON on the run so a later policy change never alters a past month.

### ADR-19 — Payslip preview is the PDF page rendered to an image (implemented in Phase 4)
- The payroll page shows the selected payslip as a PNG produced by the same QuestPDF document as the PDF
  (`IPayslipPdfService.RenderPreview`). What users see is exactly what they print; there is no second
  XAML layout to keep in sync.

### ADR-20 — Loan payments are allocated to installments (implemented in Phase 5)
- A `LoanTransaction` has `LoanAllocation` rows saying which installments it paid and how much.
  Voiding a payroll run removes its repayment transactions and subtracts exactly those allocations,
  even when a leaver's final recovery covered several installments or a cash payment covered part of one.
- The balance is still derived (total minus repayments, doc 03 R4). A test checks that it equals the sum
  of what is left on open installments.
- Participants can refuse a finalize or void (`Result`) and take part in the stale-input check
  (`LastInputChangeAsync`), so a repayment recorded after compute forces a new compute.

### ADR-21 — Tax rules are versioned JSON, tax plugs into payroll as a participant (implemented in Phase 6)
- Tax rules are one JSON document per rule set (`TaxRules`), not tables. Countries differ in shape
  (categories, rebates, caps), and the engine only reads the document, so a new country is a new pack,
  not a migration.
- A rule set that finalized payroll used is never edited. Saving creates a new version with
  `EffectiveFrom` = the month after the last locked month; past `TaxComputation` rows keep pointing to
  the version they used, so statements and voids stay exact.
- `TaxPayrollParticipant` (ADR-18) gives each payslip a tax function of the month's taxable pay, writes
  one `TaxComputation` per payslip on finalize (with the explanation lines) and deletes them on void.
  Payroll does not reference the tax module.

### ADR-22 — Reports are classes that return a table; one renderer per format (implemented in Phase 7)
- A report implements `IReport` (id, name, category, description, permission, parameters,
  `RunAsync(ReportArgs)`) and returns a `ReportTable`: typed columns (text, integer, number, money,
  date), rows of plain values, a totals row for columns marked `Total`, and notes. Reports are found by
  assembly scan, so adding a report is adding one class; no page, PDF or Excel code changes.
- `ReportArgs` holds parameter values as invariant strings (`2026-02`, `2026-01-01..2026-01-31`, ids).
  They can be saved, logged and later sent to the v2 server unchanged.
- `IReportPdfService` renders any table with the shared header, footer and page numbers (`PdfStyles`);
  Excel export goes through the existing `ISpreadsheetService`. The Reports page builds its form from the
  parameter list and its grid columns from the table, so it never knows a specific report.
- Attendance reports use the attendance engine in preview mode (no writes), clipped to today, so they
  match the sheet and payroll exactly. Pay figures come from finalized payroll only.
- Permissions are per report (`ViewData` or `ViewSalaries`). Run and export check them again on the
  server side of the dispatcher, not only in the catalog.
- The dashboard asks for its module figures with one query (`GetDashboardQuery`) whose sections are
  null when the role may not see them.

### ADR-23 — Letters are templates with a field catalog; issued letters are snapshots (implemented after Phase 8)

- Templates are plain text with `{Group.Field}` placeholders, not Word files. The text engine
  (`LetterText` in Domain) only fills, escapes and splits paragraphs; the values come from one Application
  service (`LetterData`) that reads employee, salary and company data for many employees in a few queries.
- The field catalog (`LetterFields`) is code, so every field is tested and documented, and unknown fields
  are caught when the template is saved rather than when a letter is printed.
- Issued letters keep the rendered text. A letter is a legal record: a reprint must match the signed copy,
  so it never re-renders from the current data.
- One PDF renderer (`ILetterPdfService`) draws the letterhead from company settings, the reference number
  and date, the subject and paragraphs. Word output, signatures and letter approval are open (Q51–Q54).

## Seams for the future (build the interface now, the implementation later)

| Future need | Seam in v1 |
|-------------|------------|
| Remote/server (v2) | `Application` has no WPF or EF types; `IAppDbContext` is the only persistence port. Handlers are stateless. |
| Different DB | SQLite-specific SQL only in `AttendanceDayStore` (ADR-16); value converters isolated in `Infrastructure/Persistence/Converters`. |
| Multi-tenant | Every root entity has `CompanyId` from Phase 0. |
| AI assistant (v3) | `IAssistantService` port + `Application/Assistant` folder with read-only query helpers that return typed data, not raw SQL. |
| Push devices | `IAttendanceDeviceGateway` abstracts pull vs push; `AttendancePunch` has `Source` and `DeviceId`. |
| Email | `IEmailSender` port, no implementation in v1.0. |
