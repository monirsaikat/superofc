using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SuperOfc.Application.Abstractions;
using SuperOfc.Infrastructure.Backups;
using SuperOfc.Infrastructure.Persistence;
using SuperOfc.Infrastructure.Seed;
using SuperOfc.Infrastructure.Services;

namespace SuperOfc.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers persistence and infrastructure services. Pass an environment to override paths (tests).</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, AppEnvironment? environment = null)
    {
        var env = environment ?? new AppEnvironment();
        services.AddSingleton(env);
        services.AddSingleton<IAppEnvironment>(env);

        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IIdGenerator, GuidV7Generator>();
        services.AddSingleton<UserSession>();
        services.AddSingleton<IUserSession>(sp => sp.GetRequiredService<UserSession>());
        services.AddSingleton<ICurrentUser>(sp => sp.GetRequiredService<UserSession>());
        services.AddSingleton<ICompanyContext, CompanyContext>();
        services.AddSingleton<IPolicyProvider, PolicyProvider>();
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<ISpreadsheetService, Reporting.SpreadsheetService>();
        services.AddSingleton<Application.Payroll.IPayslipPdfService, Reporting.PayslipPdfService>();
        services.AddSingleton<Application.Loans.ILoanStatementPdfService, Reporting.LoanStatementPdfService>();
        services.AddSingleton<Application.Tax.ITaxStatementPdfService, Reporting.TaxStatementPdfService>();
        services.AddSingleton<Application.Reports.IReportPdfService, Reporting.ReportPdfService>();
        services.AddSingleton<Application.Letters.ILetterPdfService, Reporting.LetterPdfService>();
        services.AddSingleton<Application.Letters.IWordLetterService, Reporting.WordLetterService>();
        services.AddSingleton<Application.Letters.IWordPdfConverter, Reporting.WordPdfConverter>();
        services.AddScoped<Application.Backups.IDataExportService, Services.DataExportService>();
        services.AddScoped<Application.Backups.IDiagnosticsService, Services.DiagnosticsService>();
        services.AddSingleton<Application.Tax.ITaxPackSource, Seed.TaxPackSource>();
        services.AddSingleton<IUpdateService, Updates.UpdateService>();
        services.AddSingleton<AuditFieldsInterceptor>();
        services.TryAddSingleton(new Devices.DeviceBridgeOptions());
        services.AddSingleton<IDeviceGateway, Devices.DeviceBridgeClient>();
        services.AddSingleton<IDeviceSyncGate, Devices.DeviceSyncGate>();
        services.AddSingleton<DatabaseInitializer>();

        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            options.UseSqlite(env.ConnectionString);
            options.AddInterceptors(sp.GetRequiredService<AuditFieldsInterceptor>());
        });
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IAttendanceDayStore, AttendanceDayStore>();
        services.AddScoped<ISampleDataSeeder, SampleDataSeeder>();
        services.AddScoped<IDataSeeder, Seed.AttendanceDefaultsSeeder>();
        services.AddScoped<IDataSeeder, Seed.PayrollDefaultsSeeder>();
        services.AddScoped<IDataSeeder, Seed.StandardLettersSeeder>();

        return services;
    }
}
