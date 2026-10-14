# 03 — Domain Model and Business Rules

All rules here are implemented in `SuperOfc.Domain` as pure code with unit tests. Anything
marked **(policy)** is read from configuration tables, never hard-coded.

## Conventions
- Every entity: `Id (Guid v7)`, `CompanyId`, `CreatedAtUtc`, `CreatedBy`, `ModifiedAtUtc`,
  `ModifiedBy`, `RowVersion (long, incremented on save)`. Soft-delete via `DeletedAtUtc`
  only on master data (Employee, Department, etc.), never on financial records.
- Money: `Money { long MinorUnits; string Currency }`. Arithmetic in `long`; rounding policy
  applied once at the end of each payslip line (**policy**: `RoundingMode`, `RoundTo` minor units).
- Effective-dated records (`SalaryAssignment`, `ShiftAssignment`, `TaxRuleSet`, `Policy`):
  `EffectiveFrom (DateOnly)`, `EffectiveTo (DateOnly?)`. Lookups pick the record whose range
  contains the date; ranges for one subject must not overlap (validated).

## Entity map (by module)

### Organization
- `Company` — name, address, logo, currency, time zone, fiscal year start month, working week.
- `Department`, `Designation`, `Location`, `Grade` (optional salary band).
- `Holiday` — date, name, optional location filter, `IsPaid`.
- `WorkingWeek` — per weekday: `Off | Full | Half`.

### Employees
- `Employee` — code (unique, human readable), names, gender, DOB, contact, national id,
  join date, confirmation date, exit date, status (`Active | Probation | OnLeave | Left`),
  department/designation/location/grade, photo path, bank details, payment mode
  (`Bank | Cash | Cheque`), tax identifier, custom fields (JSON).
- `EmployeeDocument` — type, file path (stored under `%ProgramData%\SuperOfc\files\`), expiry.
- `EmploymentEvent` — history: join, promote, transfer, confirm, exit, with dates and notes.

### Attendance
- `Shift` — name, start, end, grace-in minutes, grace-out minutes, break minutes,
  `MinimumMinutesForHalfDay`, `MinimumMinutesForFullDay`, `OvertimeStartsAfterMinutes`,
  crosses-midnight flag.
- `ShiftAssignment` — employee, shift, effective range, optional rotation pattern.
- `Device` — name, model, IP, port, serial, comm key, time zone, last sync time, status.
- `DeviceUserMap` — device + device user id <-> employee. Enrollment lives on the device.
- `AttendancePunch` (raw, immutable) — employee, device (nullable for manual), `PunchAtUtc`,
  type (`In | Out | Unknown`), source (`Device | Manual | Import`), raw payload hash
  (dedup key: device serial + device user id + device timestamp).
- `AttendanceDay` (computed, re-computable) — employee, date, shift used, first in,
  last out, worked minutes, late minutes, early-leave minutes, overtime minutes, status
  (`Present | Late | HalfDay | Absent | Leave | Holiday | WeeklyOff | NotJoined | Exited`),
  `IsManuallyCorrected`, correction note, `ComputedAtUtc`.
- `AttendanceCorrection` — who, when, before/after snapshot, reason (audit of manual edits).

### Leave
- `LeaveType` — name, paid flag, yearly entitlement, accrual (`Yearly | Monthly`), carry-forward
  cap, allows half-day, requires attachment.
- `LeaveBalance` — employee, leave type, year, opening, accrued, used, adjusted, closing.
- `LeaveRequest` — employee, type, from/to, half-day flag, status
  (`Pending | Approved | Rejected | Cancelled`), approver, note.

### Salary and payroll
- `SalaryComponent` — code, name, kind (`Earning | Deduction | EmployerContribution`),
  calc (`Fixed | PercentOfBasic | PercentOfGross | Formula`), taxable flag, attendance-linked
  flag (pro-rated by paid days), display order.
- `SalaryStructure` — named template: list of components with default values.
- `SalaryAssignment` — employee, effective range, list of `(component, value)` lines. This is
  the source of truth for an employee's pay; the structure is only a template.
- `PayrollPeriod` — year, month, from/to dates, status (`Open | Computed | Locked | Paid`).
- `PayrollRun` — period, run number, computed by, computed at, policy snapshot (JSON),
  status (`Draft | Finalized | Voided`), totals.
- `Payslip` — run, employee, days summary (calendar, payable, present, paid leave, unpaid,
  holidays, weekly offs, overtime hours), lines, gross, total deductions, net, tax, loan
  deduction, payment mode, paid flag, paid date, reference.
- `PayslipLine` — component code, name, kind, basis, rate, quantity, amount, note.
- `PayrollAdjustment` — one-off bonus/deduction for an employee in a period, reason.

### Loans and advances
- `Loan` — employee, kind (`Loan | Advance`), principal, interest method
  (`None | FlatRate`) (**policy** default), annual rate, installment count, installment
  amount, start period, status (`Requested | Approved | Active | Closed | WrittenOff`),
  approver, notes.
- `LoanInstallment` — loan, sequence, due period, principal part, interest part, amount,
  status (`Due | Deducted | Paid | Skipped | Waived`), payslip link.
- `LoanTransaction` — disbursement, repayment via payroll, manual repayment, settlement,
  interest waiver, write-off; running balance derived, not stored.
- `LoanAllocation` — the part of one transaction applied to one installment, so a payment can be
  reversed exactly (payroll void). Added in Phase 5; status `Rejected` was added to `Loan` as well.

### Tax
- `TaxRuleSet` — country code, fiscal year range, name, exemption rules (JSON), slabs,
  rebate rules, minimum tax, rounding (**policy**).
- `TaxSlab` — rule set, lower bound, upper bound (null = infinite), rate percent.
- `EmployeeTaxProfile` — employee, rule set, declared exemptions/investments, tax category
  (e.g., general/female/senior where the law differentiates), prior employer income.
- `TaxComputation` — per payslip: annual projected taxable income, annual tax, tax paid so
  far, this-month tax, explanation lines (JSON) for the payslip breakdown.

### Security and system
- `User` — username, display name, password hash (PBKDF2/Argon2 via ASP.NET Core
  `PasswordHasher`), role, active flag, last login, failed attempts, lockout until.
- `Role` — fixed set in v1: `Admin`, `HR`, `Accounts`, `Viewer`. Permission matrix in code.
- `AuditEntry` — user, utc, action, entity type, entity id, summary, before/after JSON.
- `Setting` — key, value JSON, scope (`System | Company`).
- `BackupRecord` — path, size, created, type (`Auto | Manual | PreMigration`), app version.

## Calculation rules

### R1 — Attendance day computation
Input: employee, date, punches for the day window, shift, holidays, approved leave, working week.
1. Determine the day's shift via `ShiftAssignment` (rotation resolved by pattern index).
   Day window = shift start minus 4 h to shift end plus 8 h (handles night shifts).
2. If employee not joined yet -> `NotJoined`; if exited -> `Exited`. Stop.
3. If date is a holiday -> `Holiday`; weekly off -> `WeeklyOff` (half-day off handled as
   half expected minutes). If approved full-day leave -> `Leave`. Stop (but still record
   punches if any, for overtime on holiday **policy**).
4. Pair punches: first punch = in, last punch = out (**policy** `PunchPairing: FirstLast |
   Alternating`). Single punch -> `MissingPunch` flag; status per **policy**
   (`TreatAsHalfDay | TreatAsAbsent | TreatAsPresent`).
5. Worked minutes = out - in - break (if worked > break threshold).
6. Late minutes = max(0, in - (shift start + grace-in)). Early-leave = max(0,
   (shift end - grace-out) - out).
7. Status: no punches -> `Absent`; worked < half-day minimum -> `Absent`;
   < full-day minimum -> `HalfDay`; else `Present`, `Late` if late minutes > 0.
8. Overtime minutes = max(0, worked - shift minutes - `OvertimeStartsAfterMinutes`), rounded
   down to **policy** `OvertimeRoundingMinutes` (e.g., 30). Requires **policy** `OvertimeEnabled`.
9. Manual corrections override computed values and are flagged; recomputation never
   overwrites a corrected day unless the user chooses "reset correction".
Result is idempotent: recomputing the same inputs yields the same `AttendanceDay`.

### R2 — Payable days
For a period: `CalendarDays`, `WeeklyOffs`, `Holidays`, `Present` (Late counts as present),
`HalfDays`, `PaidLeave`, `UnpaidLeave`, `Absent`.
`PayableDays = Present + 0.5*HalfDays + PaidLeave + Holidays + WeeklyOffs` (**policy**:
whether offs/holidays are paid; whether late beyond N instances costs a half day
`LatePenalty: {Instances, DeductDays}`). Joiners/leavers: days before join or after exit are
not calendar days for that employee. `ProrateBasis` **policy**: `CalendarDays | WorkingDays | Fixed30`.

### R3 — Payslip computation (per employee, per run)
1. Resolve `SalaryAssignment` effective on the period's last day (if changed mid-month,
   **policy** `MidMonthChange: ProRateBySegments | UseLatest`).
2. For each component in order:
   - `Fixed` -> value. `PercentOfBasic` -> basic * pct. `PercentOfGross` -> gross-so-far * pct.
     `Formula` -> evaluated via a tiny expression evaluator (operators + - * / and component
     codes only; no arbitrary code).
   - If attendance-linked: amount = amount * PayableDays / ProrateBasisDays.
3. Overtime pay = overtime hours * hourly rate * **policy** `OvertimeMultiplier`, where
   hourly rate = (basic or gross per **policy**) / (ProrateBasisDays * shift hours).
4. Add `PayrollAdjustment`s.
5. Gross = sum(earnings). Deductions = sum(deductions) + loan installments due this period
   (R4) + tax (R5). Net = Gross - Deductions. Net must be >= 0; if a loan installment would
   take net below **policy** `MinimumNetPay`, skip the whole installment (never split it) and
   mark it `Skipped` with a note (see R7a).
6. Round each line per policy; Net is the sum of rounded lines (never round twice).
7. Store the policy snapshot and every basis in `PayslipLine` so a payslip can be explained
   later without re-running.

### R4 — Loans
- Flat interest: `TotalInterest = Principal * AnnualRate * Months / 12 / 100`;
  `Installment = ceil((Principal + TotalInterest) / Count)` rounded per policy; last
  installment absorbs the rounding difference.
- Schedule is generated at approval; due periods start at `StartPeriod`.
- Payroll pulls installments with `Due` status for the period. Deducted installments link
  to the payslip; voiding a run reverts them to `Due`.
- Early settlement: remaining principal (+ interest per **policy** `SettlementInterest:
  Full | ProRata | None`) creates a settlement transaction and closes the loan.
- Balance = Disbursed + Interest - Sum(repayments). Never stored; computed by a domain service.

### R4a — Loan decisions made in Phase 5 (implemented)
- **Lifecycle.** `Requested` (editable) -> `Approved` (schedule created; needs ApproveLoans) ->
  `Active` (paid out) -> `Closed` when the balance reaches zero, or `WrittenOff`. Requests can be
  `Rejected` with a reason; requests and rejected loans can be deleted.
- **Schedule.** Installment = total / count rounded up to the payroll rounding step, so payslips never
  round a loan line again; the last installment absorbs the difference. Interest is spread over the
  installments in proportion to their amounts. At most 120 installments.
- **Approval check.** Projected net = current salary net - other loans due that month - the new
  installment. Approval is refused when it is below `MinimumNetPay`; above `SoftLimitMultipleOfGross`
  months of gross only a warning is shown.
- **Payments.** Every payment fills open installments earliest first. Inside an installment, payment
  covers interest first. Partial payments stay on the installment (`PaidMinor`).
- **Payroll.** Each open installment due up to the payroll month is one deduction. A skipped installment
  (minimum net) becomes `Skipped` and a new installment with what was left is added after the last one.
  Void deletes the run's repayments, returns installments to `Due` and removes those added installments.
- **Leavers (Q26).** In the month an employee leaves, the whole balance is one deduction that may be
  partial down to the minimum net. What is left stays owed; the Loans page flags it as owed by a leaver.
- **Settlement.** Remaining principal plus interest per `SettlementInterest`: `Full`, `None`, or
  `ProRata` (interest earned for months up to and including the settlement month, minus interest paid).
  The rest is recorded as an interest waiver so the balance ends at zero.

### R5 — Income tax (slab engine, country-agnostic)
Inputs: `TaxRuleSet`, `EmployeeTaxProfile`, year-to-date taxable income, projected income
for remaining months of the fiscal year.
1. Annual taxable income = YTD taxable + projected (current month taxable * remaining months)
   - exemptions (rule-defined: fixed amounts, percentages of components, caps).
2. Apply slabs progressively. Apply rebates (e.g., investment rebate with cap). Apply
   minimum tax if the rule set defines one. Round per rule set.
3. This month's tax = max(0, (AnnualTax - TaxPaidYTD) / RemainingMonthsIncludingThis).
4. Record explanation lines. Final month of the fiscal year trues up the total.
The engine knows nothing about any specific country. Country packs are seed JSON files in
`Infrastructure/Seed/Tax/<country>-<fy>.json` (see 10-open-questions for which to ship).

### R5a — Tax decisions made in Phase 6 (implemented)
- **Rules are data.** A `TaxRuleSet` stores its `TaxRules` as JSON (slabs, category first slabs,
  exemptions, rebate, minimum tax, rounding step) instead of a separate slab table. `TaxRules.Validate`
  checks them on save: at least one slab, every slab but the last has a width, the last is open-ended,
  rates 0–100 %.
- **Fiscal year.** Named by its start year (2025 = July 2025 to June 2026 when the year starts in July).
  One rule set per fiscal year; the version in force on the first day of the payroll month is used.
- **Categories.** `FirstSlabByCategory` replaces the width of the first slab for a category such as
  "Female". The employee's category comes from their tax profile; "General" when there is none.
- **Exemptions** are a fixed amount, a share of income or a share of one component (by code), each
  with an optional cap, applied to the projected yearly figures.
- **Rebate** is a percentage of declared investment. The investment counted is capped by a share of
  income, and the rebate by an optional amount.
- **Minimum tax** applies only when taxable income is above the tax-free first slab.
- **Projection.** Yearly income = taxable pay already finalized this fiscal year + previous employer
  income + this month's regular taxable pay × remaining months + this month's one-off taxable pay
  (adjustments such as a bonus, counted once; doc 10 Q44). Remaining months run to the end of the fiscal
  year, or to the exit month for a leaver. Taxable pay is the sum of earnings marked taxable.
- **Monthly amount.** (Yearly tax - tax paid so far - previous employer tax) / remaining months, never
  below zero, rounded up to the rounding step. The last month of the year takes exactly what is left.
- **Exempt employees** get no tax line, but finalize still writes a `TaxComputation` with zero, so the
  statement shows every month.
- **Versioning.** Editing rules that finalized payroll used never changes them: saving adds a new
  version from the month after the last locked month (doc 02 ADR-21). Only an unused version is edited
  in place; used rule sets cannot be deleted.
- **Stale drafts.** Changing a tax profile or rules after a draft was computed makes finalize fail with
  `Concurrency.Conflict` (R7a).
- **Country packs** are JSON files embedded in Infrastructure (`Seed/Tax/*.json`) and offered as a
  starting point on the Tax page. Only the synthetic `xx-sample` pack ships until doc 10 Q1 is answered.

### R6 — Leave balances
- Opening + accrued (yearly at fiscal start or monthly on the 1st) - used + adjustments.
- Approved leave reduces balance on approval; cancelling restores it.
- Carry-forward applied at fiscal year rollover, capped per leave type.
- Unpaid leave types affect `PayableDays` (R2).

### R7 — Payroll run lifecycle
`Open` period -> `Compute` (creates Draft run; recomputes attendance days first) -> review
and edit adjustments -> `Finalize` (locks run and period; marks loan installments Deducted;
writes tax computations) -> `Mark paid` (per payslip or bulk) -> optional `Void` (only
latest finalized run; reverses installments and tax; creates audit trail; period returns
to `Open`). Finalized payslips are immutable; corrections go through a new run.

### R7a — Payroll decisions made in Phase 4 (implemented)
- **Formula units.** Formulas work in major currency units (`BASIC * 0.1`, not paisa). Allowed:
  numbers, component codes, `+ - * /`, unary minus and brackets. Division by zero gives 0. Unknown
  codes and cycles are rejected when the component is saved (`FormulaEvaluator.Parse`).
- **Calculation stages.** (0) basic; (1) fixed, percent-of-basic and formula components in
  dependency order; (2) percent-of-gross earnings, based on the earnings computed before them;
  (3) percent-of-gross deductions and employer contributions, based on the full gross.
- **Rounding.** Each line is rounded once with the policy. Totals are sums of rounded lines, so
  `Net == Gross - Deductions` exactly.
- **Mid-month salary change.** Each salary segment is pro-rated on its own (`PaySegment`); the late
  penalty is applied once per period, from the last segment back.
- **Loans are all or nothing.** An installment that would take net below the minimum is skipped
  whole and stays due. Splitting installments is not supported (simpler statements, fewer disputes).
- **Inactive or deleted components are not paid**, even if an old assignment still lists them.
- **Compute only after the month ends** (doc 10 Q36). Partial-month payroll is not supported in 1.0.
- **Stale check on finalize.** Finalize fails with `Concurrency.Conflict` if any input (attendance,
  leave, salary, adjustment) changed after the draft was computed. The user computes again.
- **Void** is allowed only for the latest finalized run and only when no payslip is marked paid
  (mark them unpaid first). The period returns to `Open`; the voided payslips stay for audit.
- **Locked periods.** While a finalized run covers a date, attendance punches and corrections, leave
  requests and decisions, salary assignments and adjustments for that date are rejected, and the
  attendance engine skips those days. The UI shows a banner on the Payroll, Attendance and Leave pages.
- **Loans and tax plug in** through `IPayrollParticipant` (ADR-18): prepare deductions and tax per
  employee before the calculator runs, then react to finalize and void.

## Invariants to test
- No two `SalaryAssignment`s for one employee overlap in date.
- `AttendancePunch` dedup key is unique.
- `Payslip.Net == Gross - Deductions` for all stored payslips.
- Loan installments sum == principal + interest.
- Recomputing a finalized run must not change any finalized payslip (recompute is blocked).
- Tax projection for a full year of identical months equals the annual slab calculation.
