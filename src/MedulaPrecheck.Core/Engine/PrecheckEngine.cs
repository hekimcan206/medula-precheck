using MedulaPrecheck.Domain;
using MedulaPrecheck.Rules;
using MedulaPrecheck.Sut;

namespace MedulaPrecheck.Engine;

/// <summary>Rejected: at least one Error. NeedsReview: Warnings only. Clean: nothing found.</summary>
public enum InvoiceOutcome
{
    Clean,
    NeedsReview,
    Rejected,
}

public sealed record InvoiceResult(
    string InvoiceNo,
    DateOnly InvoiceDate,
    string? SutVersion,
    InvoiceOutcome Outcome,
    decimal Total,
    decimal AmountAtRisk,
    IReadOnlyList<Finding> Findings)
{
    /// <summary>Builds a result from findings produced by any evaluator (.NET rules or the PL/SQL package).</summary>
    public static InvoiceResult Create(Invoice invoice, string? sutVersion, IEnumerable<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var ordered = findings
            .OrderBy(f => f.LineNo ?? 0)
            .ThenBy(f => f.RuleId, StringComparer.Ordinal)
            .ToList();
        return new InvoiceResult(invoice.InvoiceNo, invoice.InvoiceDate, sutVersion, OutcomeOf(ordered), invoice.Total,
            AtRisk(invoice, ordered), ordered);
    }

    private static InvoiceOutcome OutcomeOf(List<Finding> findings) =>
        findings.Any(f => f.Severity == Severity.Error) ? InvoiceOutcome.Rejected
        : findings.Any(f => f.Severity == Severity.Warning) ? InvoiceOutcome.NeedsReview
        : InvoiceOutcome.Clean;

    /// <summary>
    /// Money SGK would not pay as submitted: the amount of every line with an Error, or the whole invoice when an
    /// Error is invoice-level (a bad provision blocks every line).
    /// </summary>
    private static decimal AtRisk(Invoice invoice, List<Finding> findings)
    {
        var errors = findings.Where(f => f.Severity == Severity.Error).ToList();
        if (errors.Count == 0)
        {
            return 0m;
        }

        if (errors.Any(f => f.LineNo is null))
        {
            return invoice.Total;
        }

        var lines = errors.Select(f => f.LineNo!.Value).ToHashSet();
        return invoice.Lines.Where(l => lines.Contains(l.LineNo)).Sum(l => l.Amount);
    }
}

/// <summary>
/// Applies the rule set of the SUT version valid on each invoice's date. The engine is immutable and
/// thread-safe, so one instance serves the API, the CLI and parallel batch runs.
/// </summary>
public sealed class PrecheckEngine
{
    private readonly IReadOnlyList<IRule> _rules;

    public PrecheckEngine(SutCatalog catalog, IEnumerable<IRule>? rules = null)
    {
        Catalog = catalog;
        _rules = [.. rules ?? AllRules()];
    }

    public SutCatalog Catalog { get; }

    public IReadOnlyList<IRule> Rules => _rules;

    public static IReadOnlyList<IRule> AllRules() =>
    [
        new TrackingNumberRule(),
        new ServiceWithinProvisionRule(),
        new InvoiceDateRule(),
        new KnownCodeRule(),
        new DuplicateLineRule(),
        new PhysicianRule(),
        new DiagnosisCodingRule(),
        new TariffPriceRule(),
        new DailyQuantityRule(),
        new ProcedureGenderRule(),
        new ProcedureAgeRule(),
        new DiagnosisGenderRuleCheck(),
        new RequiredDiagnosisRule(),
        new PhysicianBranchRule(),
        new FrequencyRule(),
        new RepeatExaminationRule(),
        new PackageContentRule(),
        new IncompatibleCodesRule(),
        new InpatientOnlyRule(),
        new BedDayRule(),
        new AccidentExternalCauseRule(),
        new LateInvoiceRule(),
    ];

    /// <summary>Restricts the engine to the given rule ids (e.g. the PL/SQL-overlap subset for parity runs).</summary>
    public PrecheckEngine WithOnly(IEnumerable<string> ruleIds)
    {
        var ids = ruleIds.ToHashSet(StringComparer.Ordinal);
        return new PrecheckEngine(Catalog, _rules.Where(r => ids.Contains(r.Definition.Id)));
    }

    public InvoiceResult Check(Invoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        var version = Catalog.ResolveFor(invoice.InvoiceDate);
        if (version is null)
        {
            return NoVersion(invoice);
        }

        var context = new InvoiceContext(invoice, version);
        foreach (var rule in _rules)
        {
            if (version.ActiveRules.ContainsKey(rule.Definition.Id))
            {
                rule.Evaluate(context);
            }
        }

        return InvoiceResult.Create(invoice, version.Id, context.Findings);
    }

    /// <summary>Result for an invoice dated before every SUT version: nothing can be checked, so it is rejected.</summary>
    public InvoiceResult NoVersion(Invoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var earliest = InvoiceContext.Date(Catalog.Versions[0].EffectiveFrom);
        var finding = RuleDefinitions.NoVersion.CreateFinding(
            Severity.Error, null, null, earliest, InvoiceContext.Date(invoice.InvoiceDate));
        return InvoiceResult.Create(invoice, null, [finding]);
    }

    public IReadOnlyList<InvoiceResult> CheckAll(IReadOnlyList<Invoice> invoices, bool parallel = false)
    {
        ArgumentNullException.ThrowIfNull(invoices);
        if (!parallel)
        {
            var results = new InvoiceResult[invoices.Count];
            for (var i = 0; i < invoices.Count; i++)
            {
                results[i] = Check(invoices[i]);
            }

            return results;
        }

        // Invoices are independent, so the batch is embarrassingly parallel; order is preserved.
        return invoices.AsParallel().AsOrdered().Select(Check).ToList();
    }
}
