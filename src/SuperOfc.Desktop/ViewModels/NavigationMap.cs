using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.ViewModels.Settings;
using SuperOfc.Domain.Security;
using Wpf.Ui.Controls;

namespace SuperOfc.Desktop.ViewModels;

/// <summary>Top-level navigation. Replace a ComingSoon entry with the real page view model when a module ships.</summary>
public static class NavigationMap
{
    public static IReadOnlyList<NavItem> Items { get; } =
    [
        new(Strings.Nav_Dashboard, SymbolRegular.Home24, typeof(DashboardViewModel), Permission.ViewData),
        new(Strings.Nav_Employees, SymbolRegular.People24, typeof(Employees.EmployeesViewModel), Permission.ViewData),
        new(Strings.Nav_Attendance, SymbolRegular.CalendarClock24, typeof(Attendance.AttendanceViewModel), Permission.ViewData),
        new(Strings.Nav_Leave, SymbolRegular.CalendarPerson20, typeof(Leave.LeaveViewModel), Permission.ViewData),
        new(Strings.Nav_Payroll, SymbolRegular.Money24, typeof(Payroll.PayrollViewModel), Permission.ViewSalaries),
        new(Strings.Nav_Loans, SymbolRegular.Wallet24, typeof(Loans.LoansViewModel), Permission.ViewSalaries),
        new(Strings.Nav_Tax, SymbolRegular.ReceiptMoney24, typeof(Tax.TaxViewModel), Permission.ViewSalaries),
        new(Strings.Nav_Letters, SymbolRegular.DocumentText24, typeof(Letters.LettersViewModel), Permission.ViewData),
        new(Strings.Nav_Reports, SymbolRegular.DocumentTable24, typeof(Reports.ReportsViewModel), Permission.ViewData),
        new(Strings.Nav_Devices, SymbolRegular.Fingerprint24, typeof(Devices.DevicesViewModel), Permission.ManageDevices),
        new(Strings.Nav_Settings, SymbolRegular.Settings24, typeof(SettingsViewModel), Permission.ViewData),
    ];
}
