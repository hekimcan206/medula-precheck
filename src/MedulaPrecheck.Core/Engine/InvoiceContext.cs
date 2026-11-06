using System.Globalization;
using MedulaPrecheck.Domain;
using MedulaPrecheck.Rules;
using MedulaPrecheck.Sut;

namespace MedulaPrecheck.Engine;

/// <summary>
/// Everything a rule needs to evaluate one invoice against one SUT version, with lookups built once per invoice.
/// </summary>
public sealed class InvoiceContext
{
    private readonly List<Finding> _findings = [];
    private Dictionary<string, Doctor>? _doctors;

    internal InvoiceContext(Invoice invoice, SutVersion version)
    {
        Invoice = invoice;
        Version = version;
    }

    public Invoice Invoice { get; }

    public SutVersion Version { get; }

    public IReadOnlyList<Finding> Findings => _findings;

    public ProcedureDefinition? Procedure(string code) =>
        Version.Procedures.TryGetValue(code, out var p) ? p : null;

    public Doctor? Doctor(string registrationNo)
    {
        if (_doctors is null)
        {
            // Last one wins on duplicates; a duplicate registration is a data-entry issue, not a billing rule.
            _doctors = new Dictionary<string, Doctor>(StringComparer.Ordinal);
            foreach (var d in Invoice.Doctors)
            {
                _doctors[d.RegistrationNo] = d;
            }
        }

        return _doctors.GetValueOrDefault(registrationNo);
    }

    internal void Report(RuleDefinition rule, ServiceLine? line, string? expected, string? actual) =>
        Report(rule, line?.LineNo, line?.SutCode, expected, actual);

    internal void Report(RuleDefinition rule, int? lineNo, string? code, string? expected, string? actual) =>
        _findings.Add(rule.CreateFinding(Version.ActiveRules[rule.Id], lineNo, code, expected, actual));

    // Formatting helpers shared by rules; the PL/SQL package produces byte-identical strings (parity tests).
    internal static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    internal static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
