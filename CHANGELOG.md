# Changelog

All notable user-visible changes to SuperOfc are recorded here.
Format follows Keep a Changelog; versions follow Semantic Versioning.

## [Unreleased]

### Added
- First-run setup wizard: company name, currency, number grouping, time zone, fiscal year, date format, working week and the administrator account.
- Sign-in with lockout after repeated failed attempts, lock screen (Ctrl+L and after idle time), sign-out.
- Users and roles (Admin, HR, Accounts, Viewer) with password reset; every change is recorded in the audit log.
- Company settings, appearance (light, dark or system theme) and change-my-password.
- Policies with effective dates (security and device sync for now).
- Automatic daily backup, backups before data updates, manual backup and restore, backup retention.
- Automatic updates (download in the background, restart when convenient), with update channel and mode settings.
- Safe start-up: data checks, automatic backup before data upgrades and recovery screen if the data file is damaged.
- Employees: searchable list with department and status filters, paging and sorting across all records; Excel and CSV export.
- Employee profile with photo, personal, contact, job and payment details; bank and tax details are hidden from roles that cannot see salaries.
- Employment history: confirm, promote, transfer, mark as left, rejoin and notes, each with an effective date.
- Employee documents with expiry dates.
- Import employees from Excel or CSV: download a template, check the file, see every problem by row, then import all rows at once.
- Settings for departments, designations, locations and grades, the holiday calendar (with copy from last year) and a searchable audit log.
- Dashboard tiles for headcount and joiners and leavers this month.
- Shifts with grace times, break, half-day and full-day minimums, overtime threshold and a default shift; night shifts past midnight.
- Shift assignments per employee with optional rotation, and assigning a shift to a whole department.
- Attendance punches with manual entry (reason required), a punch log, and protection against duplicate and future punches.
- Attendance calculation for every day: present, late, half day, absent, leave, holiday, weekly off, overtime, missing punches.
- Monthly attendance sheet with colours, totals, a day panel showing punches, manual corrections with reason and undo, and Excel export.
- Leave types, balances with yearly or monthly crediting and capped carry-forward, requests with approval, rejection and cancellation.
- Attendance rules on the policies page, and attendance and leave tabs on the employee page.
- Dashboard tiles for today's attendance and leave waiting for approval.
- Devices page for ZKTeco fingerprint and face terminals: add by IP address with an encrypted communication key, test the connection, sync now or sync all, status and last sync on each card, and sync history.
- Automatic device sync every few minutes (set on the policies page) with retries when a device is offline, and a warning with "Fix now" when a device clock is wrong.
- Device users: see who is on each device and which employee they belong to, link by employee code in one click, send missing employees to a device, delete users from a device.
- Fingerprint backup in the database (encrypted) and copying users with their fingerprints to another device.
- Unlinked punches page: assign punches from unknown device users to an employee, or discard them.
- Import punches from a device USB export (`_attlog.dat`) or a CSV or Excel file, with a preview of new, duplicate and unlinked punches.
- Salary components (fixed, percent of basic, percent of gross or a formula such as `BASIC * 0.1`) and salary structures with a live monthly preview, under Settings.
- Salary tab on the employee page: current salary, history, new salary from a structure, and editing or deleting salaries not yet in a finalized payroll.
- Salary revision wizard: choose employees, set a percentage and start date, review the new amounts, apply.
- Payroll page: months with status and totals, and a run page to compute, review payslips with warnings, add one-off bonuses or deductions, finalize, void with a reason, and mark payslips paid or unpaid one by one or in bulk.
- Payslip preview next to the payroll grid, and payslip PDFs (one file for everyone or one per employee) with net pay in words.
- Bank transfer and cash payment sheets, and a payroll register with department totals, all exportable to Excel.
- Finalized months are locked: attendance, leave and salary changes for those dates are blocked, with a banner on the Payroll, Attendance and Leave pages.
- Payroll and loan rules on the policies page: pay per day basis, salary changes during a month, overtime rates, rounding, minimum net pay, loan interest and early settlement.
- Loans and salary advances: request with a live repayment schedule and the employee's net pay after the installment, approval (refused when net pay would fall below the minimum), pay-out, and a Loans tab on the employee page.
- Loan installments are taken from salary automatically. An installment that would push net pay below the minimum moves to the end of the loan; voiding a payroll puts installments back.
- Cash repayments, early settlement with interest per policy, write-off, and recovery from the final salary of an employee who leaves.
- Loan statement with running balance, as a PDF.
- Income tax: rules per fiscal year with slabs, a larger tax-free slab for categories such as women, exemptions, an investment rebate and a minimum tax, on a new Tax page with a quick check of the yearly tax.
- Start tax rules from a sample pack. Changing rules after payroll used them adds a new version from the next open month, so past payslips never change.
- Employee tax declarations for the year: category, investment, previous employer income and tax, and exemption.
- Tax is deducted in payroll from a yearly projection and the last month of the year evens out the total; a Tax tab on the employee page shows this month's tax and how it is worked out.
- Tax statement per employee as a PDF and a tax summary for the year in Excel.
- Reports page: choose a report, set the month, period, department or other choices, preview it and save it as PDF or Excel.
- Reports for attendance summary, late and absent list, overtime, headcount with joiners and leavers, salary cost by department, loan ledger and leave balances.
- PDFs now share one header and footer with page numbers.
- Dashboard tiles for last month's payroll, loans outstanding, missing tax rules, device status and documents that are expiring; each opens its page.
- Export all data from Settings › About: every table as Excel (one workbook per area) and JSON, without passwords or device keys.
- Export diagnostics from Settings › About: recent logs and version details for support, without salaries or secrets.
- Backup types in the backup list now read "Daily", "Before update" and so on.
- User guide.
- Project guidelines in `docs/` (vision, architecture, domain rules, UI, standards, devices, release, roadmap).
