using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Tax;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.Services;
using SuperOfc.Desktop.ViewModels.Employees;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Security;
using SuperOfc.Domain.Tax;
using MoneyValue = SuperOfc.Domain.Common.Money;

namespace SuperOfc.Desktop.ViewModels.Tax;

/// <summary>Fiscal year helpers shared by the tax screens.</summary>
public static class TaxYears
{
    /// <summary>The fiscal year (named by its start year) that contains the date.</summary>
    public static int Containing(DateOnly date, int startMonth) => date.Month >= startMonth ? date.Year : date.Year - 1;

    public static string Label(int fiscalYear, int startMonth)
        => startMonth == 1 ? fiscalYear.ToString(CultureInfo.CurrentCulture) : $"{fiscalYear}-{(fiscalYear + 1) % 100:00}";

    public static IReadOnlyList<Choice<int>> Around(int current, int startMonth)
        => [.. Enumerable.Range(current - 3, 5).Reverse().Select(y => new Choice<int>(y, Label(y, startMonth)))];
}

/// <summary>Opens the rules editor: an existing rule set, or new rules for a fiscal year, optionally from a country pack.</summary>
public sealed record TaxRulesTarget(Guid? RuleSetId, int FiscalYear, TaxPack? Pack = null);

/// <summary>One employee's declarations for the fiscal year, edited in the profiles grid.</summary>
public sealed partial class TaxProfileItem : ObservableObject
{
    public TaxProfileItem(TaxProfileRow row)
    {
        EmployeeId = row.EmployeeId;
        EmployeeCode = row.EmployeeCode;
        EmployeeName = row.EmployeeName;
        Category = row.Category;
        Investment = (double)row.DeclaredInvestment.Major;
        PriorIncome = (double)row.PriorEmployerIncome.Major;
        PriorTax = (double)row.PriorEmployerTax.Major;
        IsExempt = row.IsExempt;
        Note = row.Note;
        IsDirty = false;
    }

    public Guid EmployeeId { get; }

    public string EmployeeCode { get; }

    public string EmployeeName { get; }

    [ObservableProperty]
    public partial string Category { get; set; }

    [ObservableProperty]
    public partial double Investment { get; set; }

    [ObservableProperty]
    public partial double PriorIncome { get; set; }

    [ObservableProperty]
    public partial double PriorTax { get; set; }

    [ObservableProperty]
    public partial bool IsExempt { get; set; }

    [ObservableProperty]
    public partial string? Note { get; set; }

    [ObservableProperty]
    public partial bool IsDirty { get; set; }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e?.PropertyName is not nameof(IsDirty))
        {
            IsDirty = true;
        }
    }

    public SaveTaxProfileCommand ToCommand(int fiscalYear) => new(EmployeeId, fiscalYear, Category, MoneyValue.FromMajor((decimal)Investment).Minor,
        MoneyValue.FromMajor((decimal)PriorIncome).Minor, MoneyValue.FromMajor((decimal)PriorTax).Minor, IsExempt, Note);
}

/// <summary>Tax: rules per fiscal year, employee declarations and the year's tax summary (P6-T1 to P6-T3).</summary>
public sealed partial class TaxViewModel(PageServices services, IFileDialogService files, IClock clock, ICompanyContext company) : PageViewModel(services), IRefreshable
{
    private bool _loading;

    public override string Title => Strings.Tax_Title;

    public bool CanManage => Can(Permission.ManageTax);

    private int StartMonth => company.Company?.FiscalYearStartMonth ?? 7;

    public ObservableCollection<Choice<int>> FiscalYears { get; } = [];

    public ObservableCollection<TaxRuleSetDto> RuleSets { get; } = [];

    public ObservableCollection<TaxPack> Packs { get; } = [];

    public ObservableCollection<TaxProfileItem> Profiles { get; } = [];

    public ObservableCollection<string> Categories { get; } = [];

    public ObservableCollection<TaxSummaryRow> SummaryRows { get; } = [];

    [ObservableProperty]
    public partial int FiscalYear { get; set; }

    [ObservableProperty]
    public partial TaxRuleSetDto? SelectedRuleSet { get; set; }

    [ObservableProperty]
    public partial TaxPack? SelectedPack { get; set; }

    [ObservableProperty]
    public partial TaxSummaryRow? SelectedSummaryRow { get; set; }

    [ObservableProperty]
    public partial string? SummaryText { get; set; }

    public bool HasRules => RuleSets.Count > 0;

    public bool NoRules => RuleSets.Count == 0;

    public bool HasPacks => Packs.Count > 0;

    public bool IsSummaryEmpty => SummaryRows.Count == 0;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        _loading = true;
        var current = TaxYears.Containing(clock.TodayIn(company.TimeZone), StartMonth);
        FiscalYears.Clear();
        foreach (var year in TaxYears.Around(current + 1, StartMonth))
        {
            FiscalYears.Add(year);
        }

        FiscalYear = parameter is int year2 ? year2 : current;
        var packs = await SendAsync(new GetTaxPacksQuery(), showErrors: false).ConfigureAwait(true);
        Packs.Clear();
        foreach (var pack in packs.IsSuccess ? packs.Value : [])
        {
            Packs.Add(pack);
        }

        SelectedPack = Packs.FirstOrDefault();
        OnPropertyChanged(nameof(HasPacks));
        _loading = false;
        await RefreshAsync().ConfigureAwait(true);
    }

    public async Task RefreshAsync()
    {
        var sets = await SendAsync(new GetTaxRuleSetsQuery()).ConfigureAwait(true);
        RuleSets.Clear();
        foreach (var set in (sets.IsSuccess ? sets.Value : []).Where(s => s.FiscalYear == FiscalYear).OrderBy(s => s.EffectiveFrom))
        {
            RuleSets.Add(set);
        }

        Categories.Clear();
        foreach (var category in RuleSets.LastOrDefault()?.Rules.Categories ?? ["General"])
        {
            Categories.Add(category);
        }

        var profiles = await SendAsync(new GetTaxProfilesQuery(FiscalYear)).ConfigureAwait(true);
        Profiles.Clear();
        foreach (var row in profiles.IsSuccess ? profiles.Value : [])
        {
            Profiles.Add(new TaxProfileItem(row));
        }

        var summary = await SendAsync(new GetTaxSummaryQuery(FiscalYear)).ConfigureAwait(true);
        SummaryRows.Clear();
        foreach (var row in summary.IsSuccess ? summary.Value.Rows : [])
        {
            SummaryRows.Add(row);
        }

        SummaryText = summary.IsSuccess
            ? string.Format(CultureInfo.CurrentCulture, Strings.Tax_SummaryTotals, summary.Value.Rows.Count, Format(summary.Value.TotalTaxable), Format(summary.Value.TotalDeducted))
            : null;
        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(NoRules));
        OnPropertyChanged(nameof(IsSummaryEmpty));
    }

    public override async Task<bool> CanNavigateAwayAsync()
    {
        if (!Profiles.Any(p => p.IsDirty))
        {
            return true;
        }

        return await Services.Dialogs.ConfirmAsync(Strings.App_UnsavedTitle, Strings.App_UnsavedMessage, Strings.App_Discard, danger: true).ConfigureAwait(true);
    }

    partial void OnFiscalYearChanged(int value)
    {
        if (!_loading)
        {
            _ = RefreshAsync();
        }
    }

    [RelayCommand]
    private Task NewRulesAsync() => Services.Navigation.NavigateToAsync<TaxRulesEditorViewModel>(new TaxRulesTarget(null, FiscalYear));

    [RelayCommand]
    private async Task NewFromPackAsync()
    {
        if (SelectedPack is { } pack)
        {
            await Services.Navigation.NavigateToAsync<TaxRulesEditorViewModel>(new TaxRulesTarget(null, FiscalYear, pack)).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task OpenRulesAsync(TaxRuleSetDto? set)
    {
        set ??= SelectedRuleSet;
        if (set is not null)
        {
            await Services.Navigation.NavigateToAsync<TaxRulesEditorViewModel>(new TaxRulesTarget(set.Id, set.FiscalYear)).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task DeleteRulesAsync(TaxRuleSetDto? set)
    {
        set ??= SelectedRuleSet;
        if (set is null || !await Services.Dialogs.ConfirmAsync(string.Format(CultureInfo.CurrentCulture, Strings.Tax_DeleteTitle, set.Name), Strings.Tax_DeleteMessage, Strings.App_Delete,
                danger: true).ConfigureAwait(true))
        {
            return;
        }

        if ((await SendAsync(new DeleteTaxRuleSetCommand(set.Id)).ConfigureAwait(true)).IsSuccess)
        {
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task SaveProfilesAsync()
    {
        ErrorMessage = null;
        var saved = 0;
        foreach (var profile in Profiles.Where(p => p.IsDirty).ToList())
        {
            var result = await SendAsync(profile.ToCommand(FiscalYear), showErrors: false).ConfigureAwait(true);
            if (result.IsFailure)
            {
                ErrorMessage = $"{profile.EmployeeCode}: {ErrorText.For(result.Errors)}";
                return;
            }

            profile.IsDirty = false;
            saved++;
        }

        if (saved > 0)
        {
            Toast(string.Format(CultureInfo.CurrentCulture, Strings.Tax_ProfilesSaved, saved));
        }
    }

    [RelayCommand]
    private async Task ExportSummaryAsync()
    {
        var path = files.SaveFile("Excel (*.xlsx)|*.xlsx", $"Tax summary {TaxYears.Label(FiscalYear, StartMonth)}.xlsx");
        if (path is not null && (await SendAsync(new ExportTaxSummaryCommand(FiscalYear, path)).ConfigureAwait(true)).IsSuccess)
        {
            files.ShellOpen(path);
        }
    }

    [RelayCommand]
    private async Task ExportStatementAsync(TaxSummaryRow? row)
    {
        row ??= SelectedSummaryRow;
        if (row is null)
        {
            return;
        }

        var path = files.SaveFile("PDF (*.pdf)|*.pdf", $"Tax statement {row.EmployeeCode} {TaxYears.Label(FiscalYear, StartMonth)}.pdf");
        if (path is not null && (await SendAsync(new ExportTaxStatementCommand(row.EmployeeId, FiscalYear, path)).ConfigureAwait(true)).IsSuccess)
        {
            files.ShellOpen(path);
        }
    }
}

/// <summary>One slab row in the rules editor. An empty width means "the rest of the income".</summary>
public sealed partial class TaxSlabRow : ObservableObject
{
    [ObservableProperty]
    public partial double? Width { get; set; }

    [ObservableProperty]
    public partial double Rate { get; set; }
}

/// <summary>A category with its own tax-free first slab, such as women or senior citizens.</summary>
public sealed partial class TaxCategoryRow : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double FirstSlab { get; set; }
}

public sealed partial class TaxExemptionRow : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ExemptionKind Kind { get; set; }

    [ObservableProperty]
    public partial double Amount { get; set; }

    [ObservableProperty]
    public partial double Percent { get; set; }

    [ObservableProperty]
    public partial string? ComponentCode { get; set; }

    [ObservableProperty]
    public partial double? Cap { get; set; }
}

/// <summary>Edits a fiscal year's tax rules as data (ADR-08), with a quick yearly check. Rules used by finalized payroll get a new version.</summary>
public sealed partial class TaxRulesEditorViewModel(PageServices services, ICompanyContext company) : EditableViewModel(services)
{
    private Guid? _id;

    public override string Title => _id is null ? Strings.TaxRules_NewTitle : Strings.TaxRules_EditTitle;

    public bool CanManage => Can(Permission.ManageTax);

    public ObservableCollection<TaxSlabRow> Slabs { get; } = [];

    public ObservableCollection<TaxCategoryRow> CategoryRows { get; } = [];

    public ObservableCollection<TaxExemptionRow> Exemptions { get; } = [];

    public ObservableCollection<string> TrialLines { get; } = [];

    public static IReadOnlyList<Choice<ExemptionKind>> ExemptionKinds { get; } =
    [
        new(ExemptionKind.Fixed, Strings.ExemptionKind_Fixed),
        new(ExemptionKind.PercentOfComponent, Strings.ExemptionKind_PercentOfComponent),
        new(ExemptionKind.PercentOfIncome, Strings.ExemptionKind_PercentOfIncome),
    ];

    [ObservableProperty]
    public partial int FiscalYear { get; set; }

    [ObservableProperty]
    public partial string FiscalYearText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CountryCode { get; set; } = "XX";

    [ObservableProperty]
    public partial string? Note { get; set; }

    [ObservableProperty]
    public partial bool HasRebate { get; set; }

    [ObservableProperty]
    public partial double RebatePercent { get; set; }

    [ObservableProperty]
    public partial double RebateMaxInvestmentPercent { get; set; } = 100;

    [ObservableProperty]
    public partial double? RebateMax { get; set; }

    [ObservableProperty]
    public partial double MinimumTax { get; set; }

    [ObservableProperty]
    public partial double RoundingStep { get; set; } = 1;

    [ObservableProperty]
    public partial double TrialIncome { get; set; } = 600_000;

    [ObservableProperty]
    public partial double TrialInvestment { get; set; }

    [ObservableProperty]
    public partial string? TrialCategory { get; set; } = "General";

    [ObservableProperty]
    public partial string? TrialResult { get; set; }

    [ObservableProperty]
    public partial string? VersionNotice { get; set; }

    [ObservableProperty]
    public partial string? EffectiveText { get; set; }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var target = parameter as TaxRulesTarget ?? new TaxRulesTarget(null, DateTime.Today.Year);
        _id = target.RuleSetId;
        var startMonth = company.Company?.FiscalYearStartMonth ?? 7;
        using (Loading())
        {
            FiscalYear = target.FiscalYear;
            TaxRules rules;
            if (_id is { } id && (await SendAsync(new GetTaxRuleSetsQuery()).ConfigureAwait(true)) is { IsSuccess: true } sets && sets.Value.FirstOrDefault(s => s.Id == id) is { } set)
            {
                Name = set.Name;
                CountryCode = set.CountryCode;
                Note = set.Note;
                FiscalYear = set.FiscalYear;
                rules = set.Rules;
                EffectiveText = string.Format(CultureInfo.CurrentCulture, Strings.TaxRules_EffectiveFrom, set.EffectiveFrom.ToString("MMMM yyyy", CultureInfo.CurrentCulture));
                VersionNotice = set.IsUsed ? Strings.TaxRules_UsedNotice : null;
            }
            else if (target.Pack is { } pack)
            {
                Name = $"{pack.Name} {TaxYears.Label(FiscalYear, startMonth)}";
                CountryCode = pack.CountryCode;
                Note = pack.Note;
                rules = pack.Rules;
            }
            else
            {
                Name = string.Format(CultureInfo.CurrentCulture, Strings.TaxRules_DefaultName, TaxYears.Label(FiscalYear, startMonth));
                rules = new TaxRules { Slabs = [new TaxSlab(null, 0)] };
            }

            FiscalYearText = TaxYears.Label(FiscalYear, startMonth);
            Load(rules);
        }

        OnPropertyChanged(nameof(Title));
        Try();
    }

    protected override bool IsTrackedProperty(string? propertyName)
        => propertyName is not (nameof(TrialIncome) or nameof(TrialInvestment) or nameof(TrialCategory) or nameof(TrialResult) or nameof(VersionNotice) or nameof(EffectiveText)
            or nameof(FiscalYearText) or nameof(Title));

    protected override string MapField(string field) => field == "Rules" ? nameof(ErrorMessage) : field;

    /// <summary>The rules as currently entered.</summary>
    public TaxRules Build() => new()
    {
        Slabs = [.. Slabs.Select(s => new TaxSlab(s.Width is { } w ? (decimal)w : null, (decimal)s.Rate))],
        FirstSlabByCategory = CategoryRows.Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => c.Name.Trim(), StringComparer.Ordinal).ToDictionary(g => g.Key, g => (decimal)g.Last().FirstSlab, StringComparer.Ordinal),
        Exemptions = [.. Exemptions.Select(e => new ExemptionRule(e.Name.Trim(), e.Kind, (decimal)e.Amount, (decimal)e.Percent,
            string.IsNullOrWhiteSpace(e.ComponentCode) ? null : e.ComponentCode.Trim(), e.Cap is { } c ? (decimal)c : null))],
        Rebate = HasRebate ? new RebateRule((decimal)RebatePercent, (decimal)RebateMaxInvestmentPercent, RebateMax is { } m ? (decimal)m : null) : null,
        MinimumTax = (decimal)MinimumTax,
        RoundingStep = (decimal)RoundingStep,
    };

    [RelayCommand]
    private void AddSlab()
    {
        // Keep the open-ended slab last.
        Slabs.Insert(Math.Max(0, Slabs.Count - 1), new TaxSlabRow { Width = 100_000, Rate = 0 });
        IsDirty = true;
    }

    [RelayCommand]
    private void RemoveSlab(TaxSlabRow? row) => Remove(Slabs, row);

    [RelayCommand]
    private void AddCategory()
    {
        CategoryRows.Add(new TaxCategoryRow { Name = Strings.TaxRules_NewCategory });
        IsDirty = true;
    }

    [RelayCommand]
    private void RemoveCategory(TaxCategoryRow? row) => Remove(CategoryRows, row);

    [RelayCommand]
    private void AddExemption()
    {
        Exemptions.Add(new TaxExemptionRow { Name = Strings.TaxRules_NewExemption, Kind = ExemptionKind.Fixed });
        IsDirty = true;
    }

    [RelayCommand]
    private void RemoveExemption(TaxExemptionRow? row) => Remove(Exemptions, row);

    /// <summary>Works out the yearly tax for the trial income with the rules as entered.</summary>
    [RelayCommand]
    private void Try()
    {
        TrialLines.Clear();
        var rules = Build();
        if (rules.Validate() is { } problem)
        {
            TrialResult = problem;
            return;
        }

        var none = new Dictionary<string, MoneyValue>();
        var result = TaxCalculator.Compute(new TaxInput(rules, TrialCategory ?? "General", MoneyValue.Zero, none, MoneyValue.FromMajor((decimal)TrialIncome / 12m), none, 12,
            MoneyValue.Zero) { DeclaredInvestment = MoneyValue.FromMajor((decimal)TrialInvestment) });
        TrialResult = string.Format(CultureInfo.CurrentCulture, Strings.TaxRules_TrialResult, Format(result.AnnualTax), Format(result.ThisMonth));
        foreach (var line in result.Explanation)
        {
            TrialLines.Add(line);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ClearErrors();
        ErrorMessage = null;
        var rules = Build();
        if (rules.Validate() is { } problem)
        {
            ErrorMessage = problem;
            return;
        }

        var saved = await SendAsync(new SaveTaxRuleSetCommand(_id, Name, CountryCode.Trim().ToUpperInvariant(), FiscalYear, rules, Note), showErrors: false).ConfigureAwait(true);
        if (saved.IsFailure)
        {
            ShowErrors(saved.Errors);
            return;
        }

        IsDirty = false;
        Toast(saved.Value == _id || _id is null ? Strings.App_Saved : Strings.TaxRules_VersionAdded);
        await Services.Navigation.NavigateToAsync<TaxViewModel>(FiscalYear).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task CancelAsync() => Services.Navigation.NavigateToAsync<TaxViewModel>(FiscalYear);

    private void Load(TaxRules rules)
    {
        Slabs.Clear();
        foreach (var slab in rules.Slabs)
        {
            Slabs.Add(new TaxSlabRow { Width = slab.Width is { } w ? (double)w : null, Rate = (double)slab.RatePercent });
        }

        CategoryRows.Clear();
        foreach (var (name, amount) in rules.FirstSlabByCategory)
        {
            CategoryRows.Add(new TaxCategoryRow { Name = name, FirstSlab = (double)amount });
        }

        Exemptions.Clear();
        foreach (var e in rules.Exemptions)
        {
            Exemptions.Add(new TaxExemptionRow { Name = e.Name, Kind = e.Kind, Amount = (double)e.Amount, Percent = (double)e.Percent, ComponentCode = e.ComponentCode, Cap = (double?)e.Cap });
        }

        HasRebate = rules.Rebate is not null;
        RebatePercent = (double)(rules.Rebate?.Percent ?? 0);
        RebateMaxInvestmentPercent = (double)(rules.Rebate?.MaxInvestmentPercentOfIncome ?? 100);
        RebateMax = rules.Rebate?.MaxRebate is { } max ? (double)max : null;
        MinimumTax = (double)rules.MinimumTax;
        RoundingStep = (double)rules.RoundingStep;
    }

    private void Remove<T>(ObservableCollection<T> rows, T? row)
        where T : class
    {
        if (row is not null && rows.Remove(row))
        {
            IsDirty = true;
        }
    }
}

/// <summary>Employee › Tax: this month's projection with its workings, and the months deducted so far this fiscal year.</summary>
public sealed partial class EmployeeTaxTabViewModel(PageServices services, IFileDialogService files, IClock clock, ICompanyContext company) : EmployeeTabViewModel(services)
{
    private Guid _employeeId;
    private DateOnly _month;

    public override string Header => Strings.EmpTab_Tax;

    public override string Title => Header;

    public override bool IsAllowed => Can(Permission.ViewSalaries);

    private int StartMonth => company.Company?.FiscalYearStartMonth ?? 7;

    public ObservableCollection<string> Explanation { get; } = [];

    public ObservableCollection<TaxStatementRow> Months { get; } = [];

    [ObservableProperty]
    public partial int FiscalYear { get; set; }

    [ObservableProperty]
    public partial string? ProjectionTitle { get; set; }

    [ObservableProperty]
    public partial string? ProjectionText { get; set; }

    [ObservableProperty]
    public partial string? NoRulesText { get; set; }

    [ObservableProperty]
    public partial string? TotalsText { get; set; }

    public bool HasMonths => Months.Count > 0;

    public override async Task LoadAsync(Guid employeeId)
    {
        _employeeId = employeeId;
        var today = clock.TodayIn(company.TimeZone);
        _month = new DateOnly(today.Year, today.Month, 1);
        FiscalYear = TaxYears.Containing(_month, StartMonth);
        ProjectionTitle = string.Format(CultureInfo.CurrentCulture, Strings.EmpTax_ProjectionTitle, _month.ToString("MMMM yyyy", CultureInfo.CurrentCulture));

        Explanation.Clear();
        var projection = await SendAsync(new GetTaxProjectionQuery(employeeId, _month.Year, _month.Month), showErrors: false).ConfigureAwait(true);
        if (projection.IsSuccess)
        {
            var p = projection.Value;
            NoRulesText = null;
            ProjectionText = p.IsExempt
                ? Strings.EmpTax_Exempt
                : string.Format(CultureInfo.CurrentCulture, Strings.EmpTax_Projection, Format(p.Result.ThisMonth), Format(p.Result.AnnualTax), p.Category, p.RuleSetName);
            foreach (var line in p.Result.Explanation)
            {
                Explanation.Add(line);
            }
        }
        else
        {
            ProjectionText = null;
            NoRulesText = ErrorText.For(projection.Errors);
        }

        var statement = await SendAsync(new GetTaxStatementQuery(employeeId, FiscalYear)).ConfigureAwait(true);
        Months.Clear();
        if (statement.IsSuccess)
        {
            foreach (var row in statement.Value.Rows)
            {
                Months.Add(row);
            }

            TotalsText = string.Format(CultureInfo.CurrentCulture, Strings.EmpTax_Totals, TaxYears.Label(FiscalYear, StartMonth), Format(statement.Value.TotalTaxable),
                Format(statement.Value.TotalDeducted));
        }

        OnPropertyChanged(nameof(HasMonths));
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var path = files.SaveFile("PDF (*.pdf)|*.pdf", $"Tax statement {TaxYears.Label(FiscalYear, StartMonth)}.pdf");
        if (path is not null && (await SendAsync(new ExportTaxStatementCommand(_employeeId, FiscalYear, path)).ConfigureAwait(true)).IsSuccess)
        {
            files.ShellOpen(path);
        }
    }
}
