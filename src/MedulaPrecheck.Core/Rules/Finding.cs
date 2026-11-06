namespace MedulaPrecheck.Rules;

/// <summary>
/// Error: SGK would reject or cut the line/invoice. Warning: likely to be questioned in review (inceleme).
/// Severity belongs to the SUT version, not to the rule, because amendments tighten rules over time.
/// </summary>
public enum Severity
{
    Info,
    Warning,
    Error,
}

public enum RuleCategory
{
    Structural,
    Pricing,
    Demographic,
    Clinical,
    Frequency,
    Package,
    Provision,
    Administrative,
}

/// <summary>Machine-readable correction, so a billing UI can offer a one-click fix rather than free text only.</summary>
public enum FixAction
{
    CorrectField,
    SetUnitPrice,
    ReduceQuantity,
    RemoveLine,
    AddDiagnosis,
    CorrectDiagnosis,
    ChangeDoctor,
    ChangeProvision,
    ChangeInvoiceDate,
    ReviewClinically,
}

public sealed record LocalizedText(string Tr, string En);

public sealed record SuggestedFix(FixAction Action, int? LineNo, string? Value);

/// <summary>One rule violation. <see cref="Expected"/> is the value the rule wanted; the PL/SQL path returns the same.</summary>
public sealed record Finding(
    string RuleId,
    RuleCategory Category,
    Severity Severity,
    int? LineNo,
    string? SutCode,
    string? Expected,
    string? Actual,
    LocalizedText Reason,
    LocalizedText Suggestion,
    SuggestedFix Fix);
