# 01 — Product Vision and Scope

## One-line vision
A fast, beautiful, offline-first Windows application that lets a small or mid-size office run
its people operations in one place: employee records, attendance (manual and fingerprint
devices), leave, salary and payroll, loans and advances, tax, and reports — with a path to
multi-PC/remote use and AI assistance later.

## Target user
- Office admin / HR / accounts person in a company with roughly 5–500 employees.
- Not a developer. Expects things to work like modern Windows apps (Settings, Teams, Outlook).
- Often a single PC runs the software. Internet may be unreliable. Data must never be lost.

## Product principles (use these to settle any design argument)
1. **Correct money first.** A wrong payslip is the worst bug. Rules are explicit, testable, auditable.
2. **Offline-first, local data.** The app works with no network. Backups are automatic.
3. **Fast.** Cold start under 2 s on a mid-range laptop. Lists of 10,000 rows scroll smoothly.
   Payroll for 500 employees computes in under 5 s.
4. **One obvious way.** Each task has one primary screen and one primary button.
5. **Nothing silent.** Every mutation is logged. Every device sync shows a result.
6. **Future-proof, not future-built.** Design seams for remote and AI, but ship local first.

## Scope by phase

### v1.0 (local, single PC) — must have
| Area | Capabilities |
|------|--------------|
| Organization | Company profile, departments, designations, locations, working-week settings, holidays. |
| Employees | Full profile, documents, employment history, status (active/left), photo, custom fields, bulk import (Excel/CSV). |
| Attendance | Shifts (fixed and rotating), punch records from devices or manual entry, auto day-status (present/late/absent/half-day/leave/holiday/weekend), overtime computation, corrections with reason, monthly attendance sheet. |
| Devices | ZKTeco fingerprint/face terminals over LAN (TCP). Pull logs, push/pull users, device health. USB `.dat` file import as fallback. |
| Leave | Leave types, yearly balances, accrual, requests and approvals, carry-forward rules. |
| Salary | Salary structures made of components (basic, allowances, deductions), per-employee assignment with effective dates, pro-rata for joiners/leavers, attendance-linked deductions and overtime pay. |
| Payroll | Monthly payroll run: compute -> review -> lock -> payslips (PDF) -> bank/cash sheet export. Re-run and void with audit trail. |
| Loans & advances | Loan with principal, optional flat interest, installment plan, auto-deduction in payroll, early settlement, statement. |
| Tax | Configurable slab-based income tax per fiscal year, exemptions, monthly projection, annual statement. Country-specific rules are data, not code. |
| Reports | Attendance summary, payroll register, department cost, loan ledger, headcount, tax summary. Export PDF/Excel. |
| Security | Local users with roles (Admin, HR, Accounts, Viewer), password hashing, lock screen, audit log. |
| Data | SQLite file, automatic daily backup, manual backup/restore, export all data. |
| Platform | Installer, auto-update, crash reporting to local log, in-app changelog. |

### v1.x — should have (after 1.0 is stable)
- Document expiry reminders (contracts, IDs), birthday/anniversary list.
- Email payslips (SMTP settings).
- Multi-company in one install.
- Shift scheduling calendar, overtime approval workflow.
- Attendance kiosk mode (PC webcam / RFID) — optional.
- Dashboard with charts.

### v2 — remote / multi-user
- Server edition: same `Application` layer hosted in ASP.NET Core; desktop app becomes a client.
- Central database (PostgreSQL) with migration from SQLite.
- Mobile/web self-service for employees (view payslip, apply leave).

### v3 — AI-powered
- Natural-language questions over data ("who was late more than 3 times this month").
- Anomaly detection (duplicate punches, unusual overtime).
- Draft HR letters, payroll explanations.
- Implementation seam: `IAssistantService` in `Application`, provider in `Infrastructure`.

## Explicitly out of scope for v1
- Accounting ledger / invoicing / inventory (a separate product line later).
- Cloud sync. Multi-PC concurrent editing on a shared SQLite file (unsafe; see 10-open-questions).
- Mac/Linux.

## Success metrics for v1.0
- One admin can onboard 100 employees and run a first payroll within one working day.
- Zero data-loss incidents; restore from backup works in under 5 minutes.
- Device sync of 10,000 punch logs completes in under 60 s on LAN.
