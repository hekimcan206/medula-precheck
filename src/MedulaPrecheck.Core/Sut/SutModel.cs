using MedulaPrecheck.Domain;
using MedulaPrecheck.Rules;

namespace MedulaPrecheck.Sut;

/// <summary>How a SUT code is billed. Packages follow the diagnosis-based list (EK-2/C) and absorb their contents.</summary>
public enum ProcedureKind
{
    Examination,
    Laboratory,
    Imaging,
    Procedure,
    Package,
    BedDay,
    Consumable,
}

/// <summary>At most <see cref="Count"/> units in any rolling window of <see cref="Days"/> days.</summary>
public sealed record FrequencyLimit(int Count, int Days);

/// <summary>One billable code in one SUT version, with every constraint the rules need.</summary>
public sealed record ProcedureDefinition(
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
    IReadOnlyList<string> RequiredIcdPrefixes,
    IReadOnlyList<string> AllowedBranches,
    bool InpatientOnly,
    IReadOnlyList<string> PackageContents);

/// <summary>Codes that may not be billed on the same day: the <see cref="Secondary"/> one is rejected.</summary>
public sealed record IncompatiblePair(string Primary, string Secondary);

/// <summary>ICD-10 three-character range [From, To] that is only valid for one sex (e.g. O00-O9A pregnancy).</summary>
public sealed record DiagnosisGenderRule(string From, string To, Gender Gender)
{
    public bool Matches(string icdCode)
    {
        if (icdCode.Length < 3)
        {
            return false;
        }

        var category = icdCode[..3];
        return string.CompareOrdinal(category, From) >= 0 && string.CompareOrdinal(category, To) <= 0;
    }
}

/// <summary>Numeric parameters that change between SUT versions.</summary>
public sealed record SutParameters(int ExaminationRepeatDays, int MaxInvoiceLagDays);

/// <summary>Specialty branch reference entry.</summary>
public sealed record Branch(string Code, string NameTr, string NameEn);

/// <summary>
/// A resolved, immutable SUT version: the rule set and reference data valid in [EffectiveFrom, EffectiveTo).
/// </summary>
public sealed class SutVersion
{
    public required string Id { get; init; }

    public required DateOnly EffectiveFrom { get; init; }

    /// <summary>Exclusive upper bound; null for the current version.</summary>
    public DateOnly? EffectiveTo { get; init; }

    public required string DescriptionEn { get; init; }

    public required SutParameters Parameters { get; init; }

    /// <summary>Active rules and the severity this version assigns them.</summary>
    public required IReadOnlyDictionary<string, Severity> ActiveRules { get; init; }

    public required IReadOnlyDictionary<string, ProcedureDefinition> Procedures { get; init; }

    public required IReadOnlyList<IncompatiblePair> IncompatiblePairs { get; init; }

    public required IReadOnlyList<DiagnosisGenderRule> DiagnosisGenderRules { get; init; }

    public bool IsValidOn(DateOnly date) => date >= EffectiveFrom && (EffectiveTo is null || date < EffectiveTo);
}
