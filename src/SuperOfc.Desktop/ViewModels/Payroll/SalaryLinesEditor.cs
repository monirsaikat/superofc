using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SuperOfc.Application.Common;
using SuperOfc.Application.Payroll;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Payroll;

namespace SuperOfc.Desktop.ViewModels.Payroll;

/// <summary>One component in a structure or salary editor: include it and enter the amount or percentage.</summary>
public sealed partial class SalaryLineRow : ObservableObject
{
    public SalaryLineRow(SalaryComponentDto component)
    {
        ArgumentNullException.ThrowIfNull(component);
        Component = component;
        Amount = (double)component.DefaultAmount.Major;
        Rate = (double)component.DefaultRate;
        IsIncluded = component.IsBasic;
    }

    public event EventHandler? Changed;

    public SalaryComponentDto Component { get; }

    public string Name => Component.Name;

    public string Code => Component.Code;

    public bool IsBasic => Component.IsBasic;

    public bool UsesAmount => Component.UsesAmount;

    public bool UsesRate => Component.UsesRate;

    /// <summary>What the value means: "% of basic", "% of gross" or the formula.</summary>
    public string Hint => Component.Calc switch
    {
        ComponentCalc.PercentOfBasic => Strings.Salary_PercentOfBasic,
        ComponentCalc.PercentOfGross => Strings.Salary_PercentOfGross,
        ComponentCalc.Formula => Component.Formula ?? string.Empty,
        _ => Component.Kind switch
        {
            ComponentKind.Deduction => Strings.ComponentKind_Deduction,
            ComponentKind.EmployerContribution => Strings.ComponentKind_EmployerContribution,
            _ => string.Empty,
        },
    };

    [ObservableProperty]
    public partial bool IsIncluded { get; set; }

    [ObservableProperty]
    public partial double Amount { get; set; }

    [ObservableProperty]
    public partial double Rate { get; set; }

    public SalaryLineInput ToInput() => new(Component.Id, Money.FromMajor((decimal)Amount).Minor, (decimal)Rate);

    partial void OnIsIncludedChanged(bool value)
    {
        if (!value && IsBasic)
        {
            IsIncluded = true;
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnAmountChanged(double value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnRateChanged(double value) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// The component grid shared by Settings › Salary structures, Employee › Salary and the structure picker, with a live
/// preview of the monthly amounts (P4-T1 "live preview for a sample basic").
/// </summary>
public sealed partial class SalaryLinesEditor : ObservableObject
{
    private readonly IDispatcher _dispatcher;
    private int _version;
    private bool _loading;

    public SalaryLinesEditor(IDispatcher dispatcher, IEnumerable<SalaryComponentDto> components)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(components);
        _dispatcher = dispatcher;
        foreach (var component in components.Where(c => c.IsActive).OrderBy(c => c.Kind).ThenBy(c => c.DisplayOrder).ThenBy(c => c.Code, StringComparer.Ordinal))
        {
            var row = new SalaryLineRow(component);
            row.Changed += OnRowChanged;
            Rows.Add(row);
        }
    }

    /// <summary>Raised when the user changes a value (for dirty tracking).</summary>
    public event EventHandler? Changed;

    public ObservableCollection<SalaryLineRow> Rows { get; } = [];

    public ObservableCollection<SalaryPreviewLine> Preview { get; } = [];

    /// <summary>Delay before the preview recomputes after a change; zero in tests.</summary>
    public TimeSpan PreviewDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    [ObservableProperty]
    public partial Money Gross { get; set; }

    [ObservableProperty]
    public partial Money Deductions { get; set; }

    [ObservableProperty]
    public partial Money Net { get; set; }

    [ObservableProperty]
    public partial Money EmployerContributions { get; set; }

    [ObservableProperty]
    public partial string? Warnings { get; set; }

    public SalaryLineRow? Basic => Rows.FirstOrDefault(r => r.IsBasic);

    /// <summary>Fills the rows from saved lines; components not in the list are left out.</summary>
    public void Load(IEnumerable<SalaryLineDto> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        _loading = true;
        try
        {
            var byId = lines.ToDictionary(l => l.ComponentId);
            foreach (var row in Rows)
            {
                if (byId.TryGetValue(row.Component.Id, out var line))
                {
                    row.IsIncluded = true;
                    row.Amount = (double)line.Amount.Major;
                    row.Rate = (double)line.Rate;
                }
                else
                {
                    row.IsIncluded = row.IsBasic;
                    row.Amount = row.IsBasic ? 0 : (double)row.Component.DefaultAmount.Major;
                    row.Rate = (double)row.Component.DefaultRate;
                }
            }
        }
        finally
        {
            _loading = false;
        }
    }

    public IReadOnlyList<SalaryLineInput> ToInputs() => Rows.Where(r => r.IsIncluded).Select(r => r.ToInput()).ToList();

    /// <summary>Recomputes the preview now.</summary>
    public async Task RefreshPreviewAsync()
    {
        var result = await _dispatcher.SendAsync(new PreviewSalaryQuery(ToInputs())).ConfigureAwait(true);
        Preview.Clear();
        if (result.IsFailure)
        {
            Gross = Deductions = Net = EmployerContributions = Money.Zero;
            Warnings = ErrorText.For(result.Errors);
            return;
        }

        foreach (var line in result.Value.Lines)
        {
            Preview.Add(line);
        }

        Gross = result.Value.Gross;
        Deductions = result.Value.Deductions;
        Net = result.Value.Net;
        EmployerContributions = result.Value.EmployerContributions;
        Warnings = result.Value.Warnings.Count == 0 ? null : string.Join(Environment.NewLine, result.Value.Warnings);
    }

    private void OnRowChanged(object? sender, EventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        _ = DebouncedPreviewAsync();
    }

    private async Task DebouncedPreviewAsync()
    {
        // Only the latest change refreshes the preview; earlier ones give up after the delay.
        var version = ++_version;
        if (PreviewDelay > TimeSpan.Zero)
        {
            await Task.Delay(PreviewDelay).ConfigureAwait(true);
        }

        if (version == _version)
        {
            await RefreshPreviewAsync().ConfigureAwait(true);
        }
    }
}
