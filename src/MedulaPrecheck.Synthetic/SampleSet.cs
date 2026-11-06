using System.Text.Json;
using MedulaPrecheck.Domain;
using MedulaPrecheck.Engine;
using MedulaPrecheck.Reporting;
using MedulaPrecheck.Sut;
using MedulaPrecheck.Synthetic;

namespace MedulaPrecheck.Samples;

/// <summary>
/// The fixed synthetic batch behind docs/samples. Everything is deterministic (seed, dates, timestamp), so the
/// committed sample files double as approval files for the engine and the renderers.
/// </summary>
public static class SampleSet
{
    public const int Seed = 20251120;
    public const int Count = 40;
    public const double DefectRate = 0.5;
    public static readonly DateOnly From = new(2025, 7, 15);
    public static readonly DateOnly To = new(2026, 3, 31);
    public static readonly DateTimeOffset GeneratedAt = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<Invoice> Invoices() =>
        new InvoiceGenerator(SutCatalog.Default, Seed).Generate(Count, DefectRate, From, To).Select(g => g.Invoice).ToList();

    public static string InvoicesJson() => JsonSerializer.Serialize(new { invoices = Invoices() }, BatchReport.JsonOptions) + "\n";

    public static BatchReport BuildReport(PrecheckEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return BatchReport.Create(engine.CheckAll(Invoices()), GeneratedAt, "dotnet-engine");
    }
}
