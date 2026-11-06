using System.Text.Json;
using System.Text.Json.Serialization;
using MedulaPrecheck.Domain;
using MedulaPrecheck.Rules;

namespace MedulaPrecheck.Sut;

/// <summary>
/// All SUT versions, ordered by effective date. A version is written as a delta over the one it inherits from,
/// the way SUT amendments (degisiklik tebligleri) are published, and resolved here into full snapshots.
/// </summary>
public sealed class SutCatalog
{
    private static readonly Lazy<SutCatalog> Embedded = new(LoadEmbedded);

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private SutCatalog(IReadOnlyList<SutVersion> versions, IReadOnlyList<Branch> branches)
    {
        Versions = versions;
        Branches = branches;
    }

    /// <summary>The synthetic catalog shipped with the engine.</summary>
    public static SutCatalog Default => Embedded.Value;

    public IReadOnlyList<SutVersion> Versions { get; }

    public IReadOnlyList<Branch> Branches { get; }

    /// <summary>The version valid on <paramref name="date"/>, or null when the date precedes every version.</summary>
    public SutVersion? ResolveFor(DateOnly date)
    {
        // Versions are few and ordered; a reverse scan is cheaper than any index.
        for (var i = Versions.Count - 1; i >= 0; i--)
        {
            if (Versions[i].IsValidOn(date))
            {
                return Versions[i];
            }
        }

        return null;
    }

    public SutVersion? Find(string id) => Versions.FirstOrDefault(v => v.Id == id);

    public static SutCatalog Load(Stream json)
    {
        var document = JsonSerializer.Deserialize<CatalogDocument>(json, JsonOptions)
            ?? throw new InvalidDataException("Empty SUT catalog.");
        return Resolve(document);
    }

    private static SutCatalog LoadEmbedded()
    {
        using var stream = typeof(SutCatalog).Assembly.GetManifestResourceStream("MedulaPrecheck.sut-catalog.json")
            ?? throw new InvalidOperationException("Embedded SUT catalog is missing.");
        return Load(stream);
    }

    private static SutCatalog Resolve(CatalogDocument document)
    {
        var ordered = document.Versions.OrderBy(v => v.EffectiveFrom).ToList();
        var resolved = new List<SutVersion>(ordered.Count);
        var byId = new Dictionary<string, SutVersion>(StringComparer.Ordinal);

        for (var i = 0; i < ordered.Count; i++)
        {
            var dto = ordered[i];
            SutVersion? parent = null;
            if (dto.Inherits is not null && !byId.TryGetValue(dto.Inherits, out parent))
            {
                throw new InvalidDataException($"Version {dto.Id} inherits unknown or later version {dto.Inherits}.");
            }

            var rules = parent is null
                ? new Dictionary<string, Severity>(StringComparer.Ordinal)
                : new Dictionary<string, Severity>(parent.ActiveRules, StringComparer.Ordinal);
            foreach (var (ruleId, severity) in dto.Rules ?? [])
            {
                if (severity is null)
                {
                    rules.Remove(ruleId);
                }
                else
                {
                    rules[ruleId] = severity.Value;
                }
            }

            var unknownRules = rules.Keys.Where(id => RuleDefinitions.Find(id) is null).ToList();
            if (unknownRules.Count > 0)
            {
                throw new InvalidDataException($"Version {dto.Id} activates unknown rules: {string.Join(", ", unknownRules)}.");
            }

            var procedures = parent is null
                ? new Dictionary<string, ProcedureDefinition>(StringComparer.Ordinal)
                : new Dictionary<string, ProcedureDefinition>(parent.Procedures, StringComparer.Ordinal);
            foreach (var code in dto.RemovedProcedures ?? [])
            {
                procedures.Remove(code);
            }

            foreach (var p in dto.Procedures ?? [])
            {
                procedures[p.Code] = p.ToDefinition();
            }

            foreach (var (code, change) in dto.ProcedureChanges ?? [])
            {
                if (!procedures.TryGetValue(code, out var current))
                {
                    throw new InvalidDataException($"Version {dto.Id} changes unknown procedure {code}.");
                }

                procedures[code] = change.ApplyTo(current);
            }

            var pairs = new List<IncompatiblePair>(parent?.IncompatiblePairs ?? []);
            pairs.AddRange(dto.IncompatiblePairs ?? []);

            var version = new SutVersion
            {
                Id = dto.Id,
                EffectiveFrom = dto.EffectiveFrom,
                EffectiveTo = i + 1 < ordered.Count ? ordered[i + 1].EffectiveFrom : null,
                DescriptionEn = dto.DescriptionEn,
                Parameters = dto.Parameters ?? parent?.Parameters
                    ?? throw new InvalidDataException($"Version {dto.Id} has no parameters."),
                ActiveRules = rules,
                Procedures = procedures,
                IncompatiblePairs = pairs,
                DiagnosisGenderRules = dto.DiagnosisGender ?? parent?.DiagnosisGenderRules ?? [],
            };

            resolved.Add(version);
            byId.Add(version.Id, version);
        }

        return new SutCatalog(resolved, document.Branches);
    }

    private sealed record CatalogDocument(IReadOnlyList<Branch> Branches, IReadOnlyList<VersionDocument> Versions);

    private sealed record VersionDocument(
        string Id,
        DateOnly EffectiveFrom,
        string? Inherits,
        string DescriptionEn,
        SutParameters? Parameters,
        Dictionary<string, Severity?>? Rules,
        IReadOnlyList<ProcedureDocument>? Procedures,
        Dictionary<string, ProcedureChange>? ProcedureChanges,
        IReadOnlyList<string>? RemovedProcedures,
        IReadOnlyList<IncompatiblePair>? IncompatiblePairs,
        IReadOnlyList<DiagnosisGenderRule>? DiagnosisGender);

    private sealed record ProcedureDocument(
        string Code,
        string NameTr,
        string NameEn,
        ProcedureKind Kind,
        decimal Price,
        Gender? Gender,
        int? MinAge,
        int? MaxAge,
        int? MaxPerDay,
        FrequencyLimit? Frequency,
        IReadOnlyList<string>? RequiredIcdPrefixes,
        IReadOnlyList<string>? AllowedBranches,
        bool InpatientOnly,
        IReadOnlyList<string>? PackageContents)
    {
        public ProcedureDefinition ToDefinition() => new(
            Code, NameTr, NameEn, Kind, Price, Gender, MinAge, MaxAge, MaxPerDay, Frequency,
            Sorted(RequiredIcdPrefixes), Sorted(AllowedBranches), InpatientOnly, Sorted(PackageContents));
    }

    /// <summary>A partial amendment of an inherited procedure; only the fields that are present change.</summary>
    private sealed record ProcedureChange(decimal? Price, int? MinAge, int? MaxAge, int? MaxPerDay, FrequencyLimit? Frequency)
    {
        public ProcedureDefinition ApplyTo(ProcedureDefinition p) => p with
        {
            Price = Price ?? p.Price,
            MinAge = MinAge ?? p.MinAge,
            MaxAge = MaxAge ?? p.MaxAge,
            MaxPerDay = MaxPerDay ?? p.MaxPerDay,
            Frequency = Frequency ?? p.Frequency,
        };
    }

    // Lists are kept in ordinal order so suggestion texts are stable and match the PL/SQL LISTAGG (BINARY sort).
    private static string[] Sorted(IReadOnlyList<string>? values) =>
        values is null ? [] : [.. values.Order(StringComparer.Ordinal)];
}
