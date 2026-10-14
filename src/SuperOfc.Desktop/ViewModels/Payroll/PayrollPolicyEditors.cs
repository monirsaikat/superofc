using CommunityToolkit.Mvvm.ComponentModel;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.ViewModels.Settings;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Policies;

namespace SuperOfc.Desktop.ViewModels.Payroll;

/// <summary>Payroll section on Settings › Policies (doc 03 R2, R3).</summary>
public sealed partial class PayrollPolicyEditor : PolicyEditorBase
{
    private readonly decimal _standardDayHours;

    public PayrollPolicyEditor(PayrollPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ProrateBasis = policy.ProrateBasis;
        MidMonthChange = policy.MidMonthChange;
        OvertimeRateBase = policy.OvertimeRateBase;
        OvertimeMultiplier = (double)policy.OvertimeMultiplier;
        HolidayOvertimeMultiplier = (double)policy.HolidayOvertimeMultiplier;
        RoundingMode = policy.LineRoundingMode;
        RoundingStep = policy.LineRoundingStepMinor;
        MinimumNetPay = (double)policy.MinimumNetPay.Major;
        _standardDayHours = policy.StandardDayHours;
    }

    public override string Section => PolicySnapshot.PayrollSection;

    public override string Header => Strings.PolicyPay_Header;

    public static IReadOnlyList<Choice<ProrateBasis>> Bases { get; } =
    [
        new(ProrateBasis.CalendarDays, Strings.PolicyPay_BasisCalendar),
        new(ProrateBasis.WorkingDays, Strings.PolicyPay_BasisWorking),
        new(ProrateBasis.Fixed30, Strings.PolicyPay_BasisFixed30),
    ];

    public static IReadOnlyList<Choice<MidMonthChange>> MidMonthChoices { get; } =
    [
        new(MidMonthChange.ProRateBySegments, Strings.PolicyPay_MidMonthSplit),
        new(MidMonthChange.UseLatest, Strings.PolicyPay_MidMonthLatest),
    ];

    public static IReadOnlyList<Choice<OvertimeRateBase>> OvertimeBases { get; } =
    [
        new(OvertimeRateBase.Basic, Strings.PolicyPay_OtBasic),
        new(OvertimeRateBase.Gross, Strings.PolicyPay_OtGross),
    ];

    public static IReadOnlyList<Choice<RoundingMode>> RoundingModes { get; } =
    [
        new(RoundingMode.HalfUp, Strings.PolicyPay_RoundHalfUp),
        new(RoundingMode.HalfEven, Strings.PolicyPay_RoundHalfEven),
        new(RoundingMode.Up, Strings.PolicyPay_RoundUp),
        new(RoundingMode.Down, Strings.PolicyPay_RoundDown),
    ];

    public static IReadOnlyList<Choice<long>> RoundingSteps { get; } =
    [
        new(1, Strings.PolicyPay_StepCents),
        new(100, Strings.PolicyPay_StepWhole),
        new(1000, Strings.PolicyPay_StepTen),
    ];

    [ObservableProperty]
    public partial ProrateBasis ProrateBasis { get; set; }

    [ObservableProperty]
    public partial MidMonthChange MidMonthChange { get; set; }

    [ObservableProperty]
    public partial OvertimeRateBase OvertimeRateBase { get; set; }

    [ObservableProperty]
    public partial double OvertimeMultiplier { get; set; }

    [ObservableProperty]
    public partial double HolidayOvertimeMultiplier { get; set; }

    [ObservableProperty]
    public partial RoundingMode RoundingMode { get; set; }

    [ObservableProperty]
    public partial long RoundingStep { get; set; }

    [ObservableProperty]
    public partial double MinimumNetPay { get; set; }

    public override object Build() => new PayrollPolicy
    {
        ProrateBasis = ProrateBasis,
        MidMonthChange = MidMonthChange,
        OvertimeRateBase = OvertimeRateBase,
        OvertimeMultiplier = (decimal)Math.Clamp(OvertimeMultiplier, 0, 10),
        HolidayOvertimeMultiplier = (decimal)Math.Clamp(HolidayOvertimeMultiplier, 0, 10),
        StandardDayHours = _standardDayHours,
        LineRoundingMode = RoundingMode,
        LineRoundingStepMinor = RoundingStep is 1 or 100 or 1000 ? RoundingStep : 100,
        MinimumNetPayMinor = Money.FromMajor((decimal)Math.Max(0, MinimumNetPay)).Minor,
    };
}

/// <summary>Loans section on Settings › Policies (doc 03 R4, doc 10 Q12).</summary>
public sealed partial class LoanPolicyEditor : PolicyEditorBase
{
    public LoanPolicyEditor(LoanPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        DefaultInterestMethod = policy.DefaultInterestMethod;
        SettlementInterest = policy.SettlementInterest;
        SoftLimit = (double)policy.SoftLimitMultipleOfGross;
    }

    public override string Section => PolicySnapshot.LoanSection;

    public override string Header => Strings.PolicyLoan_Header;

    public static IReadOnlyList<Choice<InterestMethod>> InterestMethods { get; } =
    [
        new(InterestMethod.None, Strings.InterestMethod_None),
        new(InterestMethod.FlatRate, Strings.InterestMethod_FlatRate),
    ];

    public static IReadOnlyList<Choice<SettlementInterest>> SettlementChoices { get; } =
    [
        new(SettlementInterest.None, Strings.PolicyLoan_SettleNone),
        new(SettlementInterest.ProRata, Strings.PolicyLoan_SettleProRata),
        new(SettlementInterest.Full, Strings.PolicyLoan_SettleFull),
    ];

    [ObservableProperty]
    public partial InterestMethod DefaultInterestMethod { get; set; }

    [ObservableProperty]
    public partial SettlementInterest SettlementInterest { get; set; }

    [ObservableProperty]
    public partial double SoftLimit { get; set; }

    public override object Build() => new LoanPolicy
    {
        DefaultInterestMethod = DefaultInterestMethod,
        SettlementInterest = SettlementInterest,
        SoftLimitMultipleOfGross = (decimal)Math.Clamp(SoftLimit, 0, 100),
    };
}
