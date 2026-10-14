using SuperOfc.Domain.Policies;

namespace SuperOfc.Desktop.ViewModels.Settings;

/// <summary>Module policy editors shown on Settings › Policies. Add one line per module.</summary>
public static partial class PolicyEditors
{
    private static partial IEnumerable<PolicyEditorBase> ModuleEditors(PolicySnapshot snapshot)
    {
        yield return new AttendancePolicyEditor(snapshot.Attendance);
        yield return new Payroll.PayrollPolicyEditor(snapshot.Payroll);
        yield return new Payroll.LoanPolicyEditor(snapshot.Loan);
    }
}
