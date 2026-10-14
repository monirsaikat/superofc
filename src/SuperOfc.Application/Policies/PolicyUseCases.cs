using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Platform;
using SuperOfc.Domain.Policies;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Policies;

public static class PolicyJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json)
        where T : new()
        => JsonSerializer.Deserialize<T>(json, Options) ?? new T();
}

public sealed record GetPolicySnapshotQuery(DateOnly Date) : IQuery<PolicySnapshot>;

public sealed class GetPolicySnapshotHandler(IPolicyProvider provider) : IRequestHandler<GetPolicySnapshotQuery, PolicySnapshot>
{
    public async Task<Result<PolicySnapshot>> HandleAsync(GetPolicySnapshotQuery request, CancellationToken cancellationToken)
        => await provider.GetSnapshotAsync(request.Date, cancellationToken).ConfigureAwait(false);
}

public sealed record PolicyVersionDto(Guid Id, string Section, DateOnly EffectiveFrom, string? Note, DateTime CreatedAtUtc, string CreatedBy);

[RequiresPermission(Permission.ManageSettings)]
public sealed record GetPolicyHistoryQuery(string Section) : IQuery<IReadOnlyList<PolicyVersionDto>>;

public sealed class GetPolicyHistoryHandler(IAppDbContext db, ICurrentUser current) : IRequestHandler<GetPolicyHistoryQuery, IReadOnlyList<PolicyVersionDto>>
{
    public async Task<Result<IReadOnlyList<PolicyVersionDto>>> HandleAsync(GetPolicyHistoryQuery request, CancellationToken cancellationToken)
    {
        var rows = await db.PolicyVersions.AsNoTracking()
            .Where(p => p.CompanyId == current.CompanyId && p.Section == request.Section)
            .OrderByDescending(p => p.EffectiveFrom)
            .Select(p => new PolicyVersionDto(p.Id, p.Section, p.EffectiveFrom, p.Note, p.CreatedAtUtc, p.CreatedBy))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }
}

/// <summary>Saves one policy section as a new version effective from a date (history is never edited).</summary>
[RequiresPermission(Permission.ManageSettings)]
public sealed record SavePolicyCommand(string Section, DateOnly EffectiveFrom, object Policy, string? Note) : ICommand<Unit>;

public sealed class SavePolicyHandler(IAppDbContext db, ICurrentUser current, IIdGenerator ids, IAuditWriter audit, IPolicyProvider provider)
    : IRequestHandler<SavePolicyCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(SavePolicyCommand request, CancellationToken cancellationToken)
    {
        var expectedType = request.Section switch
        {
            PolicySnapshot.AttendanceSection => typeof(AttendancePolicy),
            PolicySnapshot.PayrollSection => typeof(PayrollPolicy),
            PolicySnapshot.LoanSection => typeof(LoanPolicy),
            PolicySnapshot.GeneralSection => typeof(GeneralPolicy),
            _ => null,
        };
        if (expectedType is null || request.Policy.GetType() != expectedType)
        {
            return Error.Validation("Section", $"Unknown policy section '{request.Section}'.");
        }

        var json = JsonSerializer.Serialize(request.Policy, expectedType, PolicyJson.Options);
        var existing = await db.PolicyVersions.FirstOrDefaultAsync(
            p => p.CompanyId == current.CompanyId && p.Section == request.Section && p.EffectiveFrom == request.EffectiveFrom,
            cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            var before = existing.Json;
            existing.Json = json;
            existing.Note = request.Note;
            audit.Record("Policy.Updated", $"{request.Section} policy effective {request.EffectiveFrom:yyyy-MM-dd} updated.", nameof(PolicyVersion), existing.Id, before, json);
        }
        else
        {
            var version = new PolicyVersion
            {
                Id = ids.NewId(),
                CompanyId = current.CompanyId,
                Section = request.Section,
                EffectiveFrom = request.EffectiveFrom,
                Json = json,
                Note = request.Note,
            };
            db.PolicyVersions.Add(version);
            audit.Record("Policy.Created", $"{request.Section} policy effective {request.EffectiveFrom:yyyy-MM-dd} created.", nameof(PolicyVersion), version.Id, after: json);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        provider.Invalidate();
        return Unit.Value;
    }
}
