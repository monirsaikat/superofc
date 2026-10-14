# 04 — UI / UX Guidelines

Goal: the app should feel like a first-party Windows 11 application (Settings, Outlook):
calm, fast, keyboard-friendly, no clutter. Use WPF-UI (Fluent) controls and themes; do not
hand-style basic controls.

## App shell
- `FluentWindow` with Mica/acrylic backdrop when supported, custom title bar with app name,
  company name, search box, user menu, and update indicator.
- Left `NavigationView` (collapsible). Top-level items, in this order:
  Dashboard, Employees, Attendance, Leave, Payroll, Loans, Tax, Reports, Devices, Settings.
  Footer: Backup status, current user, version.
- Content area hosts one page at a time; pages are `UserControl`s with a view model.
- Global `InfoBar` region under the title bar for non-blocking notices (update available,
  backup failed, device offline).
- Global busy indicator (thin progress bar under title bar) bound to a `IBusyService`.
- Keyboard: `Ctrl+K` global search, `Ctrl+N` new record on list pages, `F5` refresh,
  `Esc` closes dialogs/flyouts, `Ctrl+S` save on forms.

## Theme and style
- Light and dark theme, follow system by default; toggle in Settings.
- Accent color: system accent by default; company brand color override in Settings.
- Typography: Segoe UI Variable; sizes via WPF-UI resources (`TitleLarge`, `Title`,
  `Subtitle`, `Body`, `Caption`). No hard-coded font sizes.
- Spacing scale: 4, 8, 12, 16, 24, 32 px. Page padding 24 px. Card padding 16 px.
- Corners: 8 px for cards, 4 px for inputs (WPF-UI defaults).
- Colors only via theme resources; never hard-code hex in XAML except in the brand palette
  resource dictionary. Status colors: Success/Warning/Danger/Info from theme.
- Icons: Fluent System Icons via WPF-UI `SymbolIcon`. No PNG icons for UI chrome.

## Page patterns (reuse these, do not invent new ones)

### List page (Employees, Loans, Devices, Payroll runs)
```
[Page title]                                   [Primary action: + New]  [Secondary ...]
[Search box] [Filter chips: Department ▾ Status ▾]            [Column picker] [Export ▾]
+-----------------------------------------------------------------------------------+
| DataGrid (virtualized, sortable, row selection, context menu)                     |
+-----------------------------------------------------------------------------------+
[Footer: N records · selected M]                                   [Pager if > 500]
```
- Server-side (query-side) paging and filtering through the use case; never load whole tables.
- Double-click or Enter opens the detail page. Right-click gives contextual commands.
- Empty state: icon + one sentence + primary action button.

### Detail / edit page (Employee, Loan, Shift)
- Header: title, status badge, primary `Save`, `Cancel`, overflow menu (Delete, History).
- Body: sections as cards in a two-column responsive grid (single column below 1100 px).
- Validation: inline under field (red caption) on blur and on save; summary InfoBar on save
  failure. Save button disabled while invalid or busy.
- Unsaved-changes guard on navigation.

### Wizard (Payroll run, Device setup, Import, First-run)
- Steps shown as a horizontal step list; `Back`/`Next`/`Finish`; each step validates.
- The final step shows a review table and a big clear primary action ("Finalize payroll").
- Long operations run async with a progress panel (count, elapsed, cancel).

### Grid-editing page (Monthly attendance sheet, Bulk adjustments)
- Employees as rows, days as columns; cell colors by status; legend on top; click cell
  opens a small flyout for correction with reason field; keyboard navigation across cells.

### Dialogs
- Use `ContentDialog` for confirmations; title states the consequence ("Void payroll run
  #3 for March 2026?"), body states what is reversible, primary button is a verb.
- Destructive actions use the danger appearance and require typing nothing; voiding or
  deleting financial data requires a reason.

## Key screens (v1)
| Screen | Must show |
|--------|-----------|
| Dashboard | Headcount, today's attendance (present/late/absent), pending leave, current payroll status, devices status, last backup. Tiles are clickable. |
| Employee list/detail | As patterns above; detail tabs: Profile, Employment, Salary, Attendance, Leave, Loans, Documents, Tax. |
| Attendance sheet | Month grid, filter by department, recompute button, export. |
| Punch log | Raw punches with device, employee, time, source; manual add. |
| Devices | Cards per device: status, last sync, buttons Sync now / Test / Users. Sync log panel. |
| Payroll | Period list; run wizard; review grid (editable adjustments); payslip preview; mark paid; export bank sheet. |
| Loans | List; new loan form with live schedule preview; statement view. |
| Tax | Rule set editor (slabs table), employee profiles, annual statement. |
| Reports | Catalog with parameters; preview; export PDF/Excel. |
| Settings | Company, Working week & holidays, Shifts, Salary components, Policies, Users & roles, Backup, Updates, About. |
| First-run wizard | Company name, currency, working week, admin user, optional sample data. |

## Performance rules for the UI
- `VirtualizingStackPanel.IsVirtualizing="True"`, `VirtualizationMode="Recycling"`,
  `ScrollViewer.CanContentScroll="True"` on all DataGrids/ListViews.
- Never bind a `DataGrid` to an `ObservableCollection` filled one item at a time; build a
  list then replace the collection (or use `AddRange` on a range-observable collection).
- All I/O in view models is `async Task`, never `.Result`/`.Wait()`. Use `IProgress<T>`.
- Startup: show the shell within 500 ms; load dashboard data after the window is visible.
  Run migrations and backup checks on a background task with a status message.
- Images (photos) are loaded with `DecodePixelWidth` set; thumbnails cached on disk.
- Avoid `UpdateSourceTrigger=PropertyChanged` on large grids; use it on search boxes with
  a 300 ms debounce.
- Converters must be stateless and cheap; move logic to view models.
- Measure: `Stopwatch` logs for navigation > 200 ms and queries > 100 ms (Debug level).

## Accessibility and input
- Every control reachable by Tab in a sensible order; `AutomationProperties.Name` set on
  icon-only buttons.
- Minimum hit target 32 px. Text contrast per theme (do not lighten theme text).
- DPI-aware per monitor (set in manifest), tested at 100/125/150/200 %.
- Works at 1366×768 minimum; layouts reflow, no fixed-size windows beyond dialogs.

## Copywriting
- Sentence case for titles and buttons ("Run payroll", not "Run Payroll").
- Buttons are verbs. Errors say what happened and what to do next.
- Dates shown per company format; money shown with currency symbol and thousands separators
  via the single `MoneyFormatter`.
- No jargon from the codebase in UI text (no "entity", "null", "exception").
