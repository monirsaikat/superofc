using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Policies;
using SuperOfc.Domain.Platform;

namespace SuperOfc.Infrastructure.Services;

public sealed class SettingsStore(IServiceScopeFactory scopeFactory, IIdGenerator ids) : ISettingsStore
{
    public async Task<T> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : new()
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var row = await db.Settings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == key && s.CompanyId == null, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return new T();
        }

        try
        {
            return JsonSerializer.Deserialize<T>(row.ValueJson, PolicyJson.Options) ?? new T();
        }
        catch (JsonException)
        {
            return new T();
        }
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key && s.CompanyId == null, cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(value, PolicyJson.Options);
        if (row is null)
        {
            db.Settings.Add(new Setting { Id = ids.NewId(), Key = key, ValueJson = json, Scope = SettingScope.System });
        }
        else
        {
            row.ValueJson = json;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
