# 09 — AI Model Task Protocol

How to brief a lower-cost AI model so it produces correct, consistent work, and how to
review what it returns. The product owner (or a senior model) acts as "lead"; the cheaper
model acts as "implementer".

## Briefing template (copy, fill, send)

```
ROLE
You are implementing one task in the SuperOfc codebase (.NET 10, WPF, EF Core/SQLite).
Follow docs/05-coding-standards.md literally. Do not redesign architecture.

TASK
<Task id and title from docs/08-roadmap-milestones.md, pasted in full including AC>

READ FIRST (in this order)
- docs/00-README.md (Non-negotiables)
- docs/05-coding-standards.md
- <the Refs listed in the task, e.g. docs/03-domain-model.md section R1>
- Existing code to touch: <paths>

CONTEXT
- Current branch/version: <...>
- Decisions already made for this task: <...>
- Out of scope for this task: <...>

DELIVERABLES
1. Code + migration (if schema changed) + tests.
2. CHANGELOG.md line under [Unreleased].
3. A final report in the format below.

CONSTRAINTS
- No new NuGet packages. No changes under docs/ except adding a question to docs/10-open-questions.md.
- Build must pass with warnings as errors; `dotnet test` must pass.
- If a rule in the docs blocks you, stop, write the question in docs/10, and implement the
  rest with the documented default assumption.

REPORT FORMAT (mandatory, at the end)
## Summary        (3–6 lines: what was built)
## Files          (added/changed, one line each)
## Acceptance     (each AC from the task: PASS/FAIL + how verified)
## Tests          (command run, counts, any skipped and why)
## Migration      (name, tables/columns, upgrade tested from previous DB: yes/no)
## Questions      (anything added to docs/10)
## Risks          (what you are not sure about)
```

## Rules for the lead
- One task per session. Never "do Phase 2". Never combine a UI task with an engine task.
- Paste the relevant doc sections into the brief rather than relying on the model to find them.
- Ask for the report format every time; reject reports without per-AC verification.
- If the model proposes a new library or a shortcut that violates a layer rule, say no and
  point to the ADR. Do not negotiate architecture in a task session.
- When the model reports FAIL on an AC, decide: fix in a follow-up task or accept with a
  note in CHANGELOG. Never mark the task complete.

## Review checklist for the lead (10 minutes per task)
- [ ] `dotnet build -warnaserror` and `dotnet test` run by you, not just claimed.
- [ ] Layer violations: grep `Microsoft.EntityFrameworkCore` in Desktop and Application
  (only `IAppDbContext`/`DbSet` allowed in Application); grep `System.Windows` in
  Application/Domain/Infrastructure.
- [ ] `DateTime.Now`, `Guid.NewGuid()`, `decimal` for money, `EnsureCreated`, `.Result`,
  `.Wait()`, `async void`, `catch { }` — none added (grep).
- [ ] Migration exists if entities changed; `dotnet ef migrations has-pending-model-changes` is clean.
- [ ] New strings in resx, not literals in XAML/C#.
- [ ] Audit writer called in every new mutating handler.
- [ ] Tests cover the rule, not just the happy path; golden fixtures unchanged unless approved.
- [ ] UI follows the page pattern from doc 04; try light and dark.
- [ ] CHANGELOG line reads like something a user would understand.

## Common failure modes of cheaper models (watch for these)
| Symptom | Correct behaviour |
|---------|-------------------|
| Adds MediatR, AutoMapper, Prism, MaterialDesign, Dapper | Not allowed; ADR list is closed. |
| Puts queries or calculations in code-behind or view model | Move to Application handler / Domain service. |
| Uses `decimal`/`double` for amounts | Use `Money`. |
| Calls `EnsureCreated()` or edits a past migration | New migration only. |
| Hard-codes tax slabs, overtime rate, grace minutes | Policy snapshot / configuration tables. |
| Uses `DateTime.Now` or local time for storage | `IClock`, UTC storage, company time zone for display. |
| Loads whole tables into the UI | Paged query with projection. |
| Swallows exceptions, returns null on failure | `Result` with error code; log at boundary. |
| Writes the ZKTeco SDK calls inside the WPF app | Only in `SuperOfc.DeviceBridge`. |
| Skips tests "to save time" or writes tests that assert nothing | Reject; tests are part of the task. |
| Rewrites unrelated files "for consistency" | Reject; scope is the task. |
| Marks AC as passed without saying how it verified | Reject the report. |
| Leaves TODOs | Convert to a question in doc 10 or finish it. |

## Escalation rule
The implementer must stop and ask (via docs/10) rather than guess when:
- a business rule is undefined (e.g., what happens to a loan when an employee exits),
- two docs appear to conflict,
- a task AC cannot be met within the architecture,
- the task needs data (tax law, bank file format) that is not in the repo.
Everything else: pick the documented default and note it under "Risks" in the report.
