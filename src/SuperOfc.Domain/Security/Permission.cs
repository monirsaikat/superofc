namespace SuperOfc.Domain.Security;

/// <summary>Coarse permissions checked by use-case handlers.</summary>
public enum Permission
{
    ViewData,
    ManageEmployees,
    ManageAttendance,
    ManageLeave,
    ApproveLeave,
    ManageDevices,
    ManagePayroll,
    FinalizePayroll,
    ManageLoans,
    ApproveLoans,
    ManageTax,
    ManageSettings,
    ManageUsers,
    ViewAuditLog,
    ManageBackups,
    ViewSalaries,
}

/// <summary>The fixed role-to-permission matrix for v1.</summary>
public static class PermissionMatrix
{
    private static readonly Dictionary<Role, HashSet<Permission>> Matrix = new()
    {
        [Role.Viewer] = [Permission.ViewData],
        [Role.Accounts] =
        [
            Permission.ViewData, Permission.ManagePayroll, Permission.FinalizePayroll, Permission.ManageLoans,
            Permission.ApproveLoans, Permission.ManageTax, Permission.ViewSalaries, Permission.ManageBackups,
        ],
        [Role.HR] =
        [
            Permission.ViewData, Permission.ManageEmployees, Permission.ManageAttendance, Permission.ManageLeave,
            Permission.ApproveLeave, Permission.ManageDevices, Permission.ManageBackups, Permission.ViewSalaries,
            Permission.ManagePayroll, Permission.ManageLoans,
        ],
        [Role.Admin] = [.. Enum.GetValues<Permission>()],
    };

    public static bool Has(Role role, Permission permission) => Matrix[role].Contains(permission);

    public static IReadOnlySet<Permission> For(Role role) => Matrix[role];
}
