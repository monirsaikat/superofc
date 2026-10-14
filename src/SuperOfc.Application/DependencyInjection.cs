using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using SuperOfc.Application.Common;

namespace SuperOfc.Application;

public static class DependencyInjection
{
    /// <summary>Registers the dispatcher, every request handler and every validator in this assembly.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IDispatcher, Dispatcher>();
        services.AddScoped<Attendance.AttendanceEngine>();
        services.AddScoped<Leave.LeaveLedger>();
        services.AddScoped<Devices.DeviceAccess>();
        services.AddTransient<Devices.PunchIntake>();
        services.AddScoped<Payroll.PayrollLocks>();
        services.AddScoped<Payroll.SalaryRevision>();
        services.AddScoped<Payroll.IPayrollParticipant, Loans.LoanPayrollParticipant>();
        services.AddScoped<Payroll.IPayrollParticipant, Tax.TaxPayrollParticipant>();
        services.AddScoped<Tax.TaxData>();
        services.AddScoped<Reports.ReportData>();
        services.AddScoped<Letters.LetterData>();

        var assembly = typeof(DependencyInjection).Assembly;
        foreach (var type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }))
        {
            foreach (var iface in type.GetInterfaces().Where(i => i.IsGenericType))
            {
                var definition = iface.GetGenericTypeDefinition();
                if (definition == typeof(IRequestHandler<,>) || definition == typeof(IValidator<>))
                {
                    services.AddScoped(iface, type);
                }
            }

            // Reports (P7-T1): adding a report is adding one class.
            if (typeof(Reports.IReport).IsAssignableFrom(type))
            {
                services.AddScoped(typeof(Reports.IReport), type);
            }
        }

        return services;
    }

    /// <summary>All request types in the Application assembly (used by tests and diagnostics).</summary>
    public static IEnumerable<Type> RequestTypes() => typeof(DependencyInjection).Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>)));

    internal static Assembly Assembly => typeof(DependencyInjection).Assembly;
}
