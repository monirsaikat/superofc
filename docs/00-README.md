# SuperOfc — Build Guidelines (Master Index)

Working name: **SuperOfc** (rename later; keep the root namespace `SuperOfc` until then).
Target: a Windows desktop office-management product (employees, attendance with ZKTeco
fingerprint devices, salary/payroll, loans, tax, reports), installable with auto-update,
built so it can later gain a remote/server version and AI-powered features.

These documents are the single source of truth for any developer or AI model implementing
the product. They are written so that a lower-cost AI model can execute each task without
re-deciding architecture.

## Reading order (first time)

| # | File | What it fixes |
|---|------|---------------|
| 01 | [01-product-vision-and-scope.md](01-product-vision-and-scope.md) | What we build, for whom, in what order. Feature list and phases. |
| 02 | [02-architecture.md](02-architecture.md) | Tech stack, solution layout, decision records (ADRs). |
| 03 | [03-domain-model.md](03-domain-model.md) | Entities, schema rules, payroll / loan / attendance / tax calculation rules. |
| 04 | [04-ui-ux-guidelines.md](04-ui-ux-guidelines.md) | Design system, navigation, screen patterns, performance rules for the UI. |
| 05 | [05-coding-standards.md](05-coding-standards.md) | Conventions, folder rules, error handling, testing, definition of done. |
| 06 | [06-device-integration-zkteco.md](06-device-integration-zkteco.md) | How fingerprint devices are connected, synced, and failure-handled. |
| 07 | [07-build-install-update-release.md](07-build-install-update-release.md) | Build, packaging, installer, auto-update, versioning, release checklist. |
| 08 | [08-roadmap-milestones.md](08-roadmap-milestones.md) | The work order: phases, tasks, acceptance criteria. Start here when coding. |
| 09 | [09-ai-model-task-protocol.md](09-ai-model-task-protocol.md) | How to brief the cheaper AI model per task, and how it must report back. |
| 10 | [10-open-questions.md](10-open-questions.md) | Decisions the product owner still has to make. Assumptions used meanwhile. |

## How to use these docs with an AI coding model

1. Give the model `00-README.md`, `05-coding-standards.md`, and the one task block from
   `08-roadmap-milestones.md` it must implement. Add the specific doc the task references.
2. Use the briefing template in `09-ai-model-task-protocol.md`. Never ask the model to
   "build the app"; ask it to complete one task with the stated acceptance criteria.
3. The model must not change anything in `docs/` except `10-open-questions.md`
   (to add a question) without the product owner's approval.
4. Every task ends with: build passes, tests pass, acceptance checklist ticked, short
   change summary.

## Non-negotiables (the short list)

- .NET 10 LTS, C# 14, WPF, MVVM. No other UI framework. See ADR-01/02.
- Layered solution: `Domain` -> `Application` -> `Infrastructure` -> `Desktop`.
  UI never talks to the database directly. See ADR-04.
- SQLite via EF Core with migrations. Money stored as integer minor units. IDs are GUID v7.
  Dates stored as ISO-8601 text. See ADR-03 and 03-domain-model.
- The app is installable and auto-updatable from Phase 0 onward (Velopack). Every phase
  ends in a shippable build. See 07.
- Device access (ZKTeco) runs in a separate 32-bit bridge process. The main app never loads
  the vendor COM DLL. See 06.
- All payroll math lives in `Domain` as pure, unit-tested code. No calculation in XAML
  code-behind, view models, or SQL.
- Everything that is a business rule (tax slabs, overtime rates, late-penalty rules,
  rounding) is configuration data, not hard-coded constants.
- No feature is "done" without: migration (if schema changed), unit tests for rules,
  UI wired through a view model, audit-log entry for the mutation, and an entry in CHANGELOG.
