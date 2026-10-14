# Performance results (P8-T1)

Measured 2026-10-14 with `dotnet run -c Release --project tools/SuperOfc.Bench -- 500`.

- Machine: 16 logical processors, Microsoft Windows 10.0.26100, .NET 10.0.9.
- Data: 500 employees, 100,000 punches, 12 finalized payroll months, database 152.7 MB.
- Times are wall-clock for one run, after the database was created. First-query compilation is included except for the dashboard.

| Area | Operation | Time | Target | Result |
|------|-----------|-----:|--------|--------|
| Startup | Create and migrate an empty database | 2,304 ms |  |  |
| Seed | Create 500 employees through the app | 6,578 ms |  |  |
| Seed | Give every employee a salary | 6,425 ms |  |  |
| Seed | Insert punches directly | 10.6 s |  |  |
| Lists | Employees page: first 200 sorted by name | 49 ms | < 300 ms | Met |
| Lists | Employees page: search 'Employee 4' | 57 ms | < 300 ms | Met |
| Attendance | Recompute one month for 500 employees | 1,123 ms | < 5 s | Met |
| Attendance | Monthly attendance sheet (all employees) | 314 ms | < 2 s | Met |
| Payroll | Compute payroll for 500 employees (slowest of 12 months) | 1,538 ms | < 5 s | Met |
| Payroll | Compute payroll (average of 12 months) | 1,098 ms | < 5 s | Met |
| Payroll | Payslip PDFs for 500 employees, one file | 1,618 ms | < 30 s | Met |
| Payroll | Payroll register (one month) | 27 ms | < 2 s | Met |
| Reports | Preview attendance-summary (one month) | 476 ms | < 2 s | Met |
| Reports | Preview late-absent (one month) | 422 ms | < 2 s | Met |
| Reports | Preview overtime (one month) | 429 ms | < 2 s | Met |
| Reports | Preview headcount (one month) | 5 ms | < 2 s | Met |
| Reports | Preview salary-cost (one month) | 13 ms | < 2 s | Met |
| Reports | Preview loan-ledger (one month) | 10 ms | < 2 s | Met |
| Reports | Preview leave-balance (one month) | 286 ms | < 2 s | Met |
| Reports | Attendance summary as PDF | 515 ms |  |  |
| Dashboard | All dashboard queries together | 14 ms | < 300 ms | Met |
| Data | Export all data (Excel per module + JSON) | 21.0 s | < 30 s | Met |
| Data | Manual backup | 5,563 ms |  |  |

Not measured here: WPF cold start to a visible window (the app logs "Shell visible after N ms" at every start; check it on the release test machine) and device sync over the LAN (needs a terminal, see `device-matrix.md`).
