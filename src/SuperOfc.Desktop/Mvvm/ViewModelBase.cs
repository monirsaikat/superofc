using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.Services;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Security;

namespace SuperOfc.Desktop.Mvvm;

/// <summary>Services nearly every page needs, bundled to keep constructors short.</summary>
public sealed class PageServices(
    IDispatcher dispatcher,
    IAppNavigator navigation,
    IDialogService dialogs,
    INotificationService notify,
    ICurrentUser user,
    ICompanyContext company,
    IBusyService busy,
    ILoggerFactory loggerFactory)
{
    public IDispatcher Dispatcher { get; } = dispatcher;

    public IAppNavigator Navigation { get; } = navigation;

    public IDialogService Dialogs { get; } = dialogs;

    public INotificationService Notify { get; } = notify;

    public ICurrentUser User { get; } = user;

    public ICompanyContext Company { get; } = company;

    public IBusyService Busy { get; } = busy;

    public ILoggerFactory LoggerFactory { get; } = loggerFactory;

    public MoneyFormatter Money => MoneyFormatter.For(Company.Company);

    public string DateFormat => Company.Company?.DateFormat ?? "dd MMM yyyy";
}

public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }
}

/// <summary>A page shown in the shell content area.</summary>
public abstract partial class PageViewModel(PageServices services) : ViewModelBase, INavigationAware
{
    protected PageServices Services { get; } = services;

    protected IDispatcher Dispatcher => Services.Dispatcher;

    public abstract string Title { get; }

    public MoneyFormatter Money => Services.Money;

    public string CompanyDateFormat => Services.DateFormat;

    public bool Can(Permission permission) => PermissionMatrix.Has(Services.User.Role, permission);

    public virtual Task OnNavigatedToAsync(object? parameter) => Task.CompletedTask;

    public virtual Task<bool> CanNavigateAwayAsync() => Task.FromResult(true);

    /// <summary>Sends a request; on failure shows the error inline and returns default.</summary>
    protected async Task<Result<T>> SendAsync<T>(IRequest<T> request, bool showErrors = true)
    {
        IsBusy = true;
        ErrorMessage = null;
        using var busy = Services.Busy.Begin();
        try
        {
            var result = await Dispatcher.SendAsync(request).ConfigureAwait(true);
            if (result.IsFailure && showErrors)
            {
                ErrorMessage = ErrorText.For(result.Errors);
            }

            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected void Toast(string message) => Services.Notify.Toast(message);

    /// <summary>The last date covered by finalized payroll (P4-T4 lock indicator), or null.</summary>
    protected async Task<DateOnly?> PayrollLockedUntilAsync()
    {
        var result = await Dispatcher.SendAsync(new Application.Payroll.GetPayrollLockQuery()).ConfigureAwait(true);
        return result.IsSuccess ? result.Value : null;
    }

    protected string Format(DateOnly? date) => date?.ToString(CompanyDateFormat, System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;

    protected string Format(Money money) => Money.Format(money);
}

public interface INavigationAware
{
    Task OnNavigatedToAsync(object? parameter);

    Task<bool> CanNavigateAwayAsync();
}

/// <summary>Pages with a refresh action (F5).</summary>
public interface IRefreshable
{
    Task RefreshAsync();
}

/// <summary>Pages with a "new" action (Ctrl+N).</summary>
public interface ICreatesNew
{
    Task CreateNewAsync();
}

/// <summary>Turns error codes into localized text; falls back to the handler message.</summary>
public static class ErrorText
{
    public static string For(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var key = "Error_" + error.Code.Replace('.', '_');
        return Strings.ResourceManager.GetString(key, Strings.Culture) ?? error.Message;
    }

    public static string For(IEnumerable<Error> errors) => string.Join(Environment.NewLine, errors.Select(For).Distinct());
}
