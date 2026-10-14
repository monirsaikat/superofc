using System.Text.Json;
using SuperOfc.Application.Tax;

namespace SuperOfc.Infrastructure.Seed;

/// <summary>Reads the tax country packs embedded from Seed/Tax/*.json (doc 03 R5).</summary>
public sealed class TaxPackSource : ITaxPackSource
{
    private static readonly Lazy<IReadOnlyList<TaxPack>> Packs = new(Load);

    public IReadOnlyList<TaxPack> List() => Packs.Value;

    private static IReadOnlyList<TaxPack> Load()
    {
        var assembly = typeof(TaxPackSource).Assembly;
        var packs = new List<TaxPack>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("TaxPack.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var pack = JsonSerializer.Deserialize<TaxPack>(stream, TaxJson.Options) ?? throw new InvalidOperationException($"Tax pack {name} is empty.");
            var problem = pack.Rules.Validate();
            if (problem is not null)
            {
                throw new InvalidOperationException($"Tax pack {pack.Id} is invalid: {problem}");
            }

            packs.Add(pack);
        }

        return packs;
    }
}
