# 05 — Coding Standards and Definition of Done

Read this before every task. It is short on purpose; follow it literally.

## Project settings (Directory.Build.props)
```xml
<TargetFramework>net10.0-windows</TargetFramework>   <!-- Domain/Application: net10.0 -->
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
<LangVersion>14</LangVersion>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<AnalysisLevel>latest-recommended</AnalysisLevel>
<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
<InvariantGlobalization>false</InvariantGlobalization>
```
`.editorconfig` at repo root: 4-space indent, `file_header` none, `dotnet_sort_system_directives_first = true`,
`csharp_style_namespace_declarations = file_scoped:error`, `csharp_prefer_braces = true:error`.

## Naming
- Namespaces mirror folders: `SuperOfc.Application.Payroll.Commands`.
- Use cases: `ComputePayrollCommand` + `ComputePayrollHandler`; queries: `GetEmployeeListQuery`.
- View models: `EmployeeListViewModel`; views: `EmployeeListPage.xaml`.
- Interfaces start with `I`; async methods end with `Async`; booleans read as questions
  (`IsLocked`, `HasPunches`).
- Private fields `_camelCase`; constants `PascalCase`; no Hungarian notation.
- Entities are nouns; events past tense (`PayrollFinalized`).

## Layer rules (hard)
| Layer | May reference | Must not contain |
|-------|---------------|------------------|
| Domain | nothing | EF attributes, DTOs, I/O, `DateTime.Now` (use `IClock`), WPF types |
| Application | Domain | EF Core API beyond `IAppDbContext` (which exposes `DbSet<T>` and `SaveChangesAsync`), WPF types, file paths |
| Infrastructure | Application, Domain | business rules, UI |
| Desktop | Application, Infrastructure (DI registration only) | LINQ-to-DB queries, business rules, direct `DbContext` use |
| DeviceBridge | Contracts | anything about employees/payroll — it only knows device users and punches |

If a task seems to need a rule broken, stop and add a question to `10-open-questions.md`.

## Patterns to use
- **Result type:** `Result` / `Result<T>` with `IReadOnlyList<Error>`; `Error(Code, Message)`.
  Codes are stable strings like `Payroll.PeriodLocked`. UI maps codes to localized text.
- **Validation:** FluentValidation validator per command; run by the handler before any
  write. Domain invariants additionally enforced inside entities (throw `DomainException`
  for programmer errors only).
- **Transactions:** one `SaveChangesAsync` per handler; multi-aggregate writes use
  `IAppDbContext.BeginTransactionAsync()`.
- **Audit:** `IAuditWriter.Record(action, entity, before, after)` in every mutating handler.
- **Time:** `IClock.UtcNow` and `IClock.Today(companyTimeZone)`; tests use `FakeClock`.
- **Ids:** `Guid.CreateVersion7()` via `IIdGenerator` (fakeable in tests).
- **Money:** only the `Money` struct; never `decimal`/`double` for amounts outside parsing
  and display.
- **Mapping:** hand-written `ToDto()` extension methods; no AutoMapper.
- **Queries:** project to DTOs in the query (`Select(new Dto{...})`), `AsNoTracking()`,
  paging with `Skip/Take`, total count only when the UI needs it.
- **Messaging in UI:** `WeakReferenceMessenger` for cross-view-model events
  (`EmployeeChangedMessage`), never static events.
- **Navigation:** `INavigationService.NavigateTo<TViewModel>(parameter)`; view models
  implement `INavigationAware` (`OnNavigatedTo/From`).
- **Dialogs:** through `IDialogService` so view models stay testable.
- **Settings/policies:** read through `IPolicyProvider.GetSnapshotAsync(date)`; never query
  `Setting` rows from handlers directly.

## Patterns to avoid
- No `static` mutable state, no singletons other than DI singletons.
- No code-behind logic except pure view concerns (focus, scroll). No `Window.Show()` from view models.
- No `async void` except WPF event handlers that immediately `await` a view-model command.
- No swallowing exceptions. Catch only to add context or to convert to `Result` at the boundary.
- No raw SQL unless a documented performance need exists; if used, must be provider-neutral.
- No `DateTime.Now`, `Guid.NewGuid()`, `Random` without the abstraction.
- No string concatenation for user-facing text; use resx with `string.Format` placeholders.
- No new NuGet packages without an ADR line in `02-architecture.md`.
- No TODO comments left in a finished task; create a question in doc 10 instead.

## Repository tooling (use these, do not hand-edit generated files)
- UI strings: add `Key=Value` lines to a file under `tools/dev/strings/` and run
  `python -I tools/dev/strings.py tools/dev/strings/<file>.txt`. This updates `Strings.resx` and regenerates
  `Strings.Designer.cs` (WPF markup compilation cannot see MSBuild-generated resource classes).
  Use strings in XAML as `{x:Static res:Strings.Key}`.
- New views: create the `.xaml` (UserControl, `x:Class` set) and run `python -I tools/dev/codebehind.py`.
- Migrations: `dotnet ef migrations add <Name> -p src/SuperOfc.Infrastructure -s src/SuperOfc.Infrastructure -o Persistence/Migrations`
  (EF's own timestamp prefix is used). Tables are singular (entity class name) by convention.
- A new page = `XxxViewModel : PageViewModel` (or `EditableViewModel`) + `XxxPage.xaml`. View models are
  registered automatically. Add top-level pages to `NavigationMap`, settings sections to `SettingsViewModel.AllSections`.
- Release: `tools/release/publish.ps1 -Version x.y.z[-beta.n] -Channel beta|stable`.
- Reports: a new report is one class implementing `IReport` in `Application/Reports` (ADR-22); it is found
  automatically. Add a test that checks its totals against the source data.
- Translations: put a `Strings.<culture>.resx` next to `Strings.resx` with the same keys. Missing keys fall back to
  English and are logged once. Try a language with the environment variable `SUPEROFC_UI_CULTURE=bn`.
- Performance: `dotnet run -c Release --project tools/SuperOfc.Bench -- 500 docs/testing/perf.md` rebuilds
  `docs/testing/perf.md`. Run it before a release and after changes to attendance, payroll, reports or export.

## Error handling and logging
- Unhandled exceptions: `App.DispatcherUnhandledException` + `TaskScheduler.UnobservedTaskException`
  + `AppDomain.UnhandledException` log with Serilog `Fatal`, show "Something went wrong"
  dialog with Copy details, keep app alive when safe.
- Log levels: `Information` for use-case start/finish with ids (not payloads), `Warning`
  for recoverable issues (device offline), `Error` for failures, `Debug` for timings.
- Never log passwords, national ids, bank numbers, salaries. Use `{EmployeeId}` not names.

## Database rules
- Migrations named `yyyyMMddHHmm_ShortDescription`. One migration per task if schema changes.
- Every FK indexed; unique indexes for natural keys (`Employee.Code`, punch dedup key).
- Table/column names PascalCase singular (`Employee`, `AttendancePunch`); EF default conventions.
- Seed data (roles, default components, sample policies) via `IDataSeeder` run at startup
  idempotently, not in migrations.
- Never drop or rename a column in a migration without a data-copy step and a note in CHANGELOG.

## Testing
- Domain: pure unit tests for every rule R1–R7, including edge cases listed in
  `03-domain-model.md` invariants. Use table-driven tests (`[Theory]`).
- Application: handler tests with in-memory SQLite (`DataSource=:memory:` with open
  connection) and fakes for `IClock`, `IIdGenerator`, `ICurrentUser`.
- Infrastructure: migration test (`Database.Migrate()` on empty DB, then model diff is empty).
- Desktop: view-model tests only; assert commands, state, and messages, with `IDialogService`
  and `INavigationService` mocked.
- Golden tests for payslip computation: JSON fixtures in `tests/Fixtures/Payroll/*.json`
  with inputs and expected lines; any change requires owner approval.
- Minimum: every public domain method has at least one test; every handler has a happy path
  and one failure path test.
- Guard tests that must stay green: `SecurityTests` (permissions, anonymous requests, secrets in log calls, file
  paths) and `LocalizationTests` (no literal text in XAML, translation keys and placeholders).

## Git and change hygiene
- Branch per task: `feat/<phase>-<task-id>-<slug>`; small commits; message
  `type(scope): summary` (feat, fix, refactor, test, docs, build, chore).
- `CHANGELOG.md` under `## [Unreleased]` gets a line for every user-visible change.
- Never commit `*.db`, `bin/`, `obj/`, secrets, signing certs. `.gitignore` from `dotnet new gitignore`.

## Definition of Done (tick all)
- [ ] Code compiles with zero warnings (`TreatWarningsAsErrors`).
- [ ] `dotnet test` passes locally; new rules have tests; golden fixtures unchanged or approved.
- [ ] Migration added and applied to an empty DB and to a DB from the previous version.
- [ ] UI reachable via navigation, follows patterns in doc 04, works in light and dark theme.
- [ ] Mutations write audit entries; errors surface as readable messages.
- [ ] Strings in resx; money via `MoneyFormatter`; dates via company format.
- [ ] Logs at Information for the use case; no sensitive data logged.
- [ ] CHANGELOG line added. Docs unchanged (or question added to doc 10).
- [ ] Acceptance criteria from `08-roadmap-milestones.md` verified one by one and reported.
