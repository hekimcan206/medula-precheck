using System.Text.Json;
using System.Text.Json.Serialization;
using MedulaPrecheck.Engine;
using MedulaPrecheck.Rules;

namespace MedulaPrecheck.Reporting;

public sealed record ReportSummary(
    int Invoices,
    int Clean,
    int NeedsReview,
    int Rejected,
    int Findings,
    decimal TotalAmount,
    decimal AmountAtRisk);

public sealed record RuleCount(string RuleId, string TitleTr, string TitleEn, int Count);

/// <summary>
/// The rejection-reason report. It carries invoice numbers and codes only, never patient identity, so it can be
/// shared with the billing team without widening access to health data.
/// </summary>
public sealed record BatchReport(
    DateTimeOffset GeneratedAt,
    string Engine,
    ReportSummary Summary,
    IReadOnlyList<RuleCount> ByRule,
    IReadOnlyList<InvoiceResult> Invoices)
{
    private const string Lf = "\n";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = Lf,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Keep Turkish characters readable in the JSON file instead of \u escapes.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static BatchReport Create(IReadOnlyList<InvoiceResult> results, DateTimeOffset generatedAt, string engine)
    {
        ArgumentNullException.ThrowIfNull(results);

        var findings = results.SelectMany(r => r.Findings).ToList();
        var byRule = findings
            .GroupBy(f => f.RuleId, StringComparer.Ordinal)
            .Select(g =>
            {
                var def = RuleDefinitions.Find(g.Key) ?? RuleDefinitions.NoVersion;
                return new RuleCount(g.Key, def.TitleTr, def.TitleEn, g.Count());
            })
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.RuleId, StringComparer.Ordinal)
            .ToList();

        var summary = new ReportSummary(
            results.Count,
            results.Count(r => r.Outcome == InvoiceOutcome.Clean),
            results.Count(r => r.Outcome == InvoiceOutcome.NeedsReview),
            results.Count(r => r.Outcome == InvoiceOutcome.Rejected),
            findings.Count,
            results.Sum(r => r.Total),
            results.Sum(r => r.AmountAtRisk));

        return new BatchReport(generatedAt, engine, summary, byRule, results);
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
