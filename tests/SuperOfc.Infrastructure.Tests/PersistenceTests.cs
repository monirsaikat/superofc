using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Platform;
using SuperOfc.Domain.Security;
using SuperOfc.Infrastructure.Backups;
using SuperOfc.Infrastructure.Persistence;

namespace SuperOfc.Infrastructure.Tests;

public sealed class SqliteFixture : IDisposable
{
    private sealed class Clock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    }

    private sealed class User : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid? UserId => Guid.Empty;

        public string UserName => "tester";

        public string DisplayName => "Tester";

        public Role Role => Role.Admin;

        public Guid CompanyId { get; } = Guid.CreateVersion7();
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public SqliteFixture()
    {
        _connection.Open();
        using var db = CreateContext();
        db.Database.Migrate();
    }

    public AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new AuditFieldsInterceptor(new Clock(), new User()))
            .Options;
        return new AppDbContext(options);
    }

    public void Dispose() => _connection.Dispose();
}

public class PersistenceTests : IClassFixture<SqliteFixture>
{
    private readonly SqliteFixture _fixture;

    public PersistenceTests(SqliteFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Model_has_no_pending_changes_after_migrations()
    {
        using var db = _fixture.CreateContext();

        db.Database.HasPendingModelChanges().Should().BeFalse("every model change needs a migration");
        db.Database.GetPendingMigrations().Should().BeEmpty();
    }

    [Fact]
    public async Task Saving_sets_audit_fields_and_row_version()
    {
        var id = Guid.CreateVersion7();
        await using (var db = _fixture.CreateContext())
        {
            db.Companies.Add(new Company { Id = id, Name = "Audit Co" });
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            var company = await db.Companies.SingleAsync(c => c.Id == id);
            company.CreatedBy.Should().Be("tester");
            company.CreatedAtUtc.Kind.Should().Be(DateTimeKind.Utc);
            company.RowVersion.Should().Be(1);
            company.Name = "Changed";
            await db.SaveChangesAsync();
            company.RowVersion.Should().Be(2);
            company.ModifiedBy.Should().Be("tester");
        }
    }

    [Fact]
    public async Task Concurrent_edit_is_detected()
    {
        var id = Guid.CreateVersion7();
        await using (var db = _fixture.CreateContext())
        {
            db.Companies.Add(new Company { Id = id, Name = "Concurrency Co" });
            await db.SaveChangesAsync();
        }

        await using var first = _fixture.CreateContext();
        await using var second = _fixture.CreateContext();
        var a = await first.Companies.SingleAsync(c => c.Id == id);
        var b = await second.Companies.SingleAsync(c => c.Id == id);
        a.Name = "A";
        await first.SaveChangesAsync();
        b.Name = "B";

        var act = () => second.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task Enums_are_stored_as_text_and_complex_working_week_round_trips()
    {
        var id = Guid.CreateVersion7();
        await using (var db = _fixture.CreateContext())
        {
            db.Companies.Add(new Company { Id = id, Name = "Week Co", WorkingWeek = WorkingWeek.Default.With(DayOfWeek.Sunday, WorkDayType.Half) });
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateContext())
        {
            (await db.Companies.SingleAsync(c => c.Id == id)).WorkingWeek.Sunday.Should().Be(WorkDayType.Half);
            var raw = await db.Database.SqlQuery<string>($"SELECT WorkingWeek_Friday AS Value FROM Company WHERE Id = {id}").SingleAsync();
            raw.Should().Be("Off");
        }
    }

    [Fact]
    public void Money_round_trips_and_sums_as_integer_in_sql()
    {
        Persistence.Converters.MoneyConverter converter = new();
        var toProvider = converter.ConvertToProviderExpression.Compile();
        var fromProvider = converter.ConvertFromProviderExpression.Compile();

        toProvider(new Money(123456)).Should().Be(123456L);
        fromProvider(42L).Should().Be(new Money(42));
    }
}

public class BackupRetentionTests
{
    private static readonly DateTime Now = new(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Keeps_thirty_daily_and_twelve_monthly_auto_backups()
    {
        var backups = Enumerable.Range(0, 400)
            .Select(i => new BackupRetention.Candidate(Guid.CreateVersion7(), BackupType.Auto, Now.AddDays(-i)))
            .ToList();

        var deleted = BackupRetention.SelectForDeletion(backups, Now).ToHashSet();
        var kept = backups.Where(b => !deleted.Contains(b.Id)).ToList();

        kept.Should().HaveCountGreaterThanOrEqualTo(30).And.HaveCountLessThanOrEqualTo(42);
        kept.Where(b => b.CreatedAtUtc > Now.AddDays(-30)).Should().HaveCount(30);
        kept.Select(b => new { b.CreatedAtUtc.Year, b.CreatedAtUtc.Month }).Distinct().Should().HaveCount(12);
    }

    [Fact]
    public void Manual_backups_are_kept_forever_safety_backups_ninety_days()
    {
        var manual = new BackupRetention.Candidate(Guid.CreateVersion7(), BackupType.Manual, Now.AddYears(-5));
        var oldSafety = new BackupRetention.Candidate(Guid.CreateVersion7(), BackupType.PreMigration, Now.AddDays(-91));
        var newSafety = new BackupRetention.Candidate(Guid.CreateVersion7(), BackupType.PrePayroll, Now.AddDays(-10));

        var deleted = BackupRetention.SelectForDeletion([manual, oldSafety, newSafety], Now);

        deleted.Should().Equal(oldSafety.Id);
    }

    [Fact]
    public void Several_backups_on_one_day_keep_only_the_newest()
    {
        var older = new BackupRetention.Candidate(Guid.CreateVersion7(), BackupType.Auto, Now.Date.AddHours(1));
        var newer = new BackupRetention.Candidate(Guid.CreateVersion7(), BackupType.Auto, Now.Date.AddHours(9));

        var deleted = BackupRetention.SelectForDeletion([older, newer], Now);

        // The older one is still the first of the month, so it is kept as the monthly backup.
        deleted.Should().BeEmpty();
    }
}
