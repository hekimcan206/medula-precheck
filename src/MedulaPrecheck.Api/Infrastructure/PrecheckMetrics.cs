using System.Diagnostics.Metrics;
using MedulaPrecheck.Engine;

namespace MedulaPrecheck.Api.Infrastructure;

/// <summary>Business metrics: how many invoices were checked and which rules reject them most.</summary>
public sealed class PrecheckMetrics
{
    public const string MeterName = "MedulaPrecheck";

    private readonly Counter<long> _invoices;
    private readonly Counter<long> _findings;
    private readonly Histogram<double> _atRisk;

    public PrecheckMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _invoices = meter.CreateCounter<long>("precheck.invoices", unit: "{invoice}", description: "Invoices pre-checked, by outcome and path.");
        _findings = meter.CreateCounter<long>("precheck.findings", unit: "{finding}", description: "Findings, by rule and severity.");
        _atRisk = meter.CreateHistogram<double>("precheck.amount_at_risk", unit: "TRY", description: "Amount that would not be paid as submitted, per invoice.");
    }

    public void Record(IEnumerable<InvoiceResult> results, string path)
    {
        ArgumentNullException.ThrowIfNull(results);
        foreach (var result in results)
        {
            _invoices.Add(1, new("outcome", result.Outcome.ToString()), new("path", path));
            _atRisk.Record((double)result.AmountAtRisk);
            foreach (var finding in result.Findings)
            {
                _findings.Add(1, new("rule", finding.RuleId), new("severity", finding.Severity.ToString()));
            }
        }
    }
}
