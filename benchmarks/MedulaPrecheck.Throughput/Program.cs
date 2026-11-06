using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using DotNet.Testcontainers.Configurations;
using MedulaPrecheck.Engine;
using MedulaPrecheck.Oracle;
using MedulaPrecheck.Sut;
using MedulaPrecheck.Synthetic;
using Oracle.ManagedDataAccess.Client;
using Testcontainers.Oracle;

// Throughput harness: invoices/second for the .NET engine and for the Oracle PL/SQL bulk path, on the same
// synthetic batch. Usage: dotnet run -c Release --project benchmarks/MedulaPrecheck.Throughput -- [count] [runs] [--no-oracle]
var inv = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentCulture = inv;
CultureInfo.CurrentCulture = inv;
var count = args.Length > 0 && int.TryParse(args[0], inv, out var c) ? c : 20_000;
var runs = args.Length > 1 && int.TryParse(args[1], inv, out var r) ? r : 5;
var withOracle = !args.Contains("--no-oracle");

Console.WriteLine($"# medula-precheck throughput ({DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC)");
Console.WriteLine();
Console.WriteLine($"- OS: {RuntimeInformation.OSDescription}; {RuntimeInformation.OSArchitecture}");
Console.WriteLine($"- .NET: {RuntimeInformation.FrameworkDescription}; logical CPUs: {Environment.ProcessorCount}; server GC: {System.Runtime.GCSettings.IsServerGC}");
Console.WriteLine($"- Batch: {count:N0} synthetic invoices (seed 1, 30% injected defects, invoice dates 2025-02-01..2026-06-30), {runs} measured runs after warm-up");

var invoices = new InvoiceGenerator(SutCatalog.Default, 1)
    .Generate(count, 0.3, new DateOnly(2025, 2, 1), new DateOnly(2026, 6, 30))
    .Select(g => g.Invoice)
    .ToList();
var lineCount = invoices.Sum(i => i.Lines.Count);
Console.WriteLine($"- Lines: {lineCount:N0} ({(double)lineCount / count:F2} per invoice)");
Console.WriteLine();
Console.WriteLine("| Path | Rules | Median inv/s | Min inv/s | Max inv/s | Median ms per batch |");
Console.WriteLine("|---|---:|---:|---:|---:|---:|");

var full = new PrecheckEngine(SutCatalog.Default);
var overlap = full.WithOnly(OracleBulkPrecheck.ImplementedRules);

Measure(".NET engine, sequential", 22, () => full.CheckAll(invoices));
Measure(".NET engine, parallel (PLINQ)", 22, () => full.CheckAll(invoices, parallel: true));
Measure(".NET engine, sequential, PL/SQL subset", 13, () => overlap.CheckAll(invoices));

if (withOracle)
{
    await using var container = new OracleBuilder("gvenzl/oracle-free:23-slim-faststart")
        .WithEnvironment("OWNER_PASSWORD", "OwnerBench1")
        .WithEnvironment("APP_PASSWORD", "AppBench1")
        .WithResourceMapping(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "oracle-init", "01-users.sh")),
            "/container-entrypoint-initdb.d/01-users.sh",
            fileMode: UnixFileModes.UserRead | UnixFileModes.UserWrite | UnixFileModes.UserExecute | UnixFileModes.GroupRead | UnixFileModes.OtherRead)
        .WithLabel("project", "medula-precheck-bench")
        .Build();
    await container.StartAsync();

    string Conn(string user, string password) =>
        new OracleConnectionStringBuilder(container.GetConnectionString()) { UserID = user, Password = password }.ConnectionString;

    var installer = new OracleSchemaInstaller(Conn("precheck_owner", "OwnerBench1"));
    await installer.InstallAsync("precheck_app");
    await installer.LoadReferenceDataAsync(SutCatalog.Default);
    var bulk = new OracleBulkPrecheck(Conn("precheck_app", "AppBench1"));

    await bulk.RunAsync(invoices); // warm-up: connection pool, cursor cache, hard parse, temp segments
    var totals = new List<double>();
    var stages = new List<double>();
    var checks = new List<double>();
    var findings = 0;
    for (var i = 0; i < runs; i++)
    {
        var watch = Stopwatch.StartNew();
        var result = await bulk.RunAsync(invoices);
        watch.Stop();
        totals.Add(watch.Elapsed.TotalMilliseconds);
        stages.Add(result.StageTime.TotalMilliseconds);
        checks.Add(result.CheckTime.TotalMilliseconds);
        findings = result.Findings.Count;
    }

    Row("Oracle PL/SQL bulk, end to end (stage + check + fetch + commit)", 13, totals);
    Row("Oracle PL/SQL bulk, check + fetch only", 13, checks);
    Console.WriteLine();
    Console.WriteLine(string.Create(inv,
        $"Oracle split (median): stage {Median(stages):F0} ms, check+fetch {Median(checks):F0} ms; {findings:N0} findings per run; container image gvenzl/oracle-free:23-slim-faststart."));
}

void Measure(string name, int rules, Func<IReadOnlyList<InvoiceResult>> action)
{
    for (var w = 0; w < 3; w++)
    {
        action(); // warm-up: JIT and tiered compilation reach steady state
    }

    var times = new List<double>();
    for (var i = 0; i < runs; i++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var watch = Stopwatch.StartNew();
        action();
        watch.Stop();
        times.Add(watch.Elapsed.TotalMilliseconds);
    }

    Row(name, rules, times);
}

void Row(string name, int rules, List<double> millis)
{
    var perSecond = millis.Select(ms => count / (ms / 1000.0)).ToList();
    Console.WriteLine(string.Create(inv,
        $"| {name} | {rules} | {Median(perSecond):N0} | {perSecond.Min():N0} | {perSecond.Max():N0} | {Median(millis):F1} |"));
}

static double Median(List<double> values)
{
    var sorted = values.Order().ToList();
    return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
}
