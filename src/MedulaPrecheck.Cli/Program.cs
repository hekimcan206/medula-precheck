using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MedulaPrecheck.Domain;
using MedulaPrecheck.Engine;
using MedulaPrecheck.Reporting;
using MedulaPrecheck.Rules;
using MedulaPrecheck.Samples;
using MedulaPrecheck.Sut;
using MedulaPrecheck.Synthetic;

// Exit codes: 0 = nothing would be rejected, 2 = at least one invoice would be rejected, 1 = usage/input error.
// That makes `precheck check` usable as a gate in a billing pipeline.
Console.OutputEncoding = Encoding.UTF8;
var engine = new PrecheckEngine(SutCatalog.Default);

try
{
    return args.FirstOrDefault() switch
    {
        "check" => Check(Options.Parse(args[1..])),
        "generate" => Generate(Options.Parse(args[1..])),
        "samples" => Samples(args.Length > 1 ? args[1] : "docs/samples"),
        "rules" => Rules(Options.Parse(args[1..])),
        _ => Usage(),
    };
}
catch (Exception ex) when (ex is IOException or JsonException or FormatException or ArgumentException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

int Check(Options o)
{
    var input = o.Get("input") ?? throw new ArgumentException("--input <file> is required.");
    using var stream = File.OpenRead(input);
    var batch = JsonSerializer.Deserialize<InvoiceBatch>(stream, BatchReport.JsonOptions)
        ?? throw new FormatException("Empty input.");
    var format = Enum.Parse<ReportFormat>(o.Get("format") ?? "json", ignoreCase: true);

    var watch = Stopwatch.StartNew();
    var results = engine.CheckAll(batch.Invoices, parallel: o.Has("parallel"));
    watch.Stop();

    var report = BatchReport.Create(results, DateTimeOffset.UtcNow, "dotnet-engine");
    var rendered = ReportRenderer.Render(report, format);
    if (o.Get("out") is { } outFile)
    {
        File.WriteAllText(outFile, rendered);
    }
    else
    {
        Console.Out.Write(rendered);
    }

    var s = report.Summary;
    Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"checked {s.Invoices} invoices in {watch.Elapsed.TotalMilliseconds:F1} ms: {s.Clean} clean, {s.NeedsReview} need review, {s.Rejected} would be rejected; {s.AmountAtRisk:N2} of {s.TotalAmount:N2} TRY at risk"));
    foreach (var rule in report.ByRule.Take(5))
    {
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {rule.RuleId,-8} {rule.Count,5}  {rule.TitleEn}"));
    }

    return s.Rejected > 0 ? 2 : 0;
}

int Generate(Options o)
{
    var count = int.Parse(o.Get("count") ?? "100", CultureInfo.InvariantCulture);
    var seed = int.Parse(o.Get("seed") ?? "1", CultureInfo.InvariantCulture);
    var rate = double.Parse(o.Get("defect-rate") ?? "0.3", CultureInfo.InvariantCulture);
    var from = DateOnly.Parse(o.Get("from") ?? "2025-02-01", CultureInfo.InvariantCulture);
    var to = DateOnly.Parse(o.Get("to") ?? "2026-06-30", CultureInfo.InvariantCulture);

    var invoices = new InvoiceGenerator(SutCatalog.Default, seed).Generate(count, rate, from, to).Select(g => g.Invoice).ToList();
    var json = JsonSerializer.Serialize(new InvoiceBatch(invoices), BatchReport.JsonOptions);
    if (o.Get("out") is { } outFile)
    {
        File.WriteAllText(outFile, json);
        Console.Error.WriteLine($"wrote {invoices.Count} synthetic invoices to {outFile}");
    }
    else
    {
        Console.Out.Write(json);
    }

    return 0;
}

int Samples(string directory)
{
    Directory.CreateDirectory(directory);
    var report = SampleSet.BuildReport(engine);
    File.WriteAllText(Path.Combine(directory, "invoices.json"), SampleSet.InvoicesJson());
    File.WriteAllText(Path.Combine(directory, "report.json"), ReportRenderer.Render(report, ReportFormat.Json));
    File.WriteAllText(Path.Combine(directory, "report.md"), ReportRenderer.Render(report, ReportFormat.Markdown));
    File.WriteAllText(Path.Combine(directory, "report.html"), ReportRenderer.Render(report, ReportFormat.Html));
    Console.Error.WriteLine($"wrote sample input and reports to {directory}");
    return 0;
}

int Rules(Options o)
{
    var version = o.Get("version") is { } id
        ? SutCatalog.Default.Find(id) ?? throw new ArgumentException($"Unknown SUT version {id}.")
        : SutCatalog.Default.Versions[^1];

    Console.WriteLine($"{version.Id} (from {version.EffectiveFrom:yyyy-MM-dd}): {version.DescriptionEn}");
    foreach (var rule in RuleDefinitions.All)
    {
        var state = version.ActiveRules.TryGetValue(rule.Id, out var severity) ? severity.ToString() : "inactive";
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {rule.Id,-8} {state,-8} {(rule.OracleBulk ? "PL/SQL+.NET" : ".NET"),-11} {rule.TitleEn}"));
    }

    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("""
        precheck - MEDULA invoice pre-check (synthetic, illustrative SUT rules)

          precheck check --input <invoices.json> [--format json|markdown|html] [--out <file>] [--parallel]
          precheck generate [--count 100] [--seed 1] [--defect-rate 0.3] [--from 2025-02-01] [--to 2026-06-30] [--out <file>]
          precheck rules [--version SUT-2026.01]
          precheck samples [<directory>]

        Exit code 2 when at least one invoice would be rejected.
        """);
    return 1;
}

internal sealed record InvoiceBatch(IReadOnlyList<Invoice> Invoices);

internal sealed class Options
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{args[i]}'.");
            }

            var key = args[i][2..];
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            o._values[key] = hasValue ? args[++i] : null;
        }

        return o;
    }

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public bool Has(string key) => _values.ContainsKey(key);
}
