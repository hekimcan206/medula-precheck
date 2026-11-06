using MedulaPrecheck.Domain;
using MedulaPrecheck.Engine;
using MedulaPrecheck.Golden;
using MedulaPrecheck.Oracle;
using MedulaPrecheck.Rules;
using MedulaPrecheck.Sut;
using MedulaPrecheck.Synthetic;
using Oracle.ManagedDataAccess.Client;

namespace MedulaPrecheck.OracleTests;

public sealed class OracleBulkTests(OracleFixture db)
{
    private static readonly PrecheckEngine FullEngine = new(SutCatalog.Default);
    private static readonly PrecheckEngine OverlapEngine = FullEngine.WithOnly(OracleBulkPrecheck.ImplementedRules);

    private OracleBulkPrecheck Bulk => new(db.AppConnectionString);

    [Fact]
    public async Task Package_rule_list_matches_the_rules_flagged_for_the_bulk_path()
    {
        await using var connection = new OracleConnection(db.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT precheck_pkg.implemented_rules FROM dual";

        var rules = ((string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).Split(',');

        rules.Should().BeEquivalentTo(OracleBulkPrecheck.ImplementedRules);
        rules.Should().HaveCount(13);
    }

    public static TheoryData<string> BulkGoldenCases() =>
        [.. GoldenCases.All.Where(c => RuleDefinitions.Find(c.RuleId)!.OracleBulk).Select(c => c.Name)];

    [Theory]
    [MemberData(nameof(BulkGoldenCases))]
    public async Task PLSQL_matches_the_golden_case(string name)
    {
        var golden = GoldenCases.All.Single(c => c.Name == name);

        var result = await Bulk.RunAsync([golden.Invoice], TestContext.Current.CancellationToken);

        var bulkRules = OracleBulkPrecheck.ImplementedRules.ToHashSet();
        result.Findings.Select(f => new ExpectedFinding(f.RuleId, f.LineNo, f.Expected))
            .Should().BeEquivalentTo(golden.Expected.Where(e => bulkRules.Contains(e.RuleId)), golden.Name);
    }

    [Fact]
    public async Task PLSQL_and_NET_agree_on_every_overlapping_rule_for_a_synthetic_batch()
    {
        // Mixed versions, every injectable defect type, clean invoices in between.
        var invoices = new InvoiceGenerator(SutCatalog.Default, seed: 2026)
            .Generate(3000, 0.7, new DateOnly(2025, 1, 25), new DateOnly(2026, 9, 30))
            .Select(g => g.Invoice)
            .ToList();

        var oracle = await Bulk.RunAsync(invoices, TestContext.Current.CancellationToken);
        var dotnet = OverlapEngine.CheckAll(invoices);

        var fromOracle = oracle.Findings.Select(f => Key(f.InvoiceNo, f.LineNo, f.RuleId, f.Severity, f.Expected, f.Actual)).ToList();
        var fromDotnet = dotnet.SelectMany(r => r.Findings.Select(f => Key(r.InvoiceNo, f.LineNo, f.RuleId, f.Severity, f.Expected, f.Actual))).ToList();

        fromDotnet.Should().HaveCountGreaterThan(1000, "the batch must exercise the rules, not just clean invoices");
        fromOracle.Should().BeEquivalentTo(fromDotnet);
        fromOracle.Select(f => f.Split('|')[2]).Distinct().Should().BeEquivalentTo(OracleBulkPrecheck.ImplementedRules,
            "every PL/SQL rule must fire at least once in the parity batch");
    }

    [Fact]
    public async Task Bulk_results_render_into_the_same_report_shape_as_the_engine()
    {
        var invoices = GoldenCases.All.Where(c => RuleDefinitions.Find(c.RuleId)!.OracleBulk).Select((c, i) => c.Invoice with { InvoiceNo = $"R{i:D3}" }).ToList();

        var bulk = await Bulk.RunAsync(invoices, TestContext.Current.CancellationToken);
        var fromOracle = OracleBulkPrecheck.ToResults(OverlapEngine, invoices, bulk);
        var fromEngine = OverlapEngine.CheckAll(invoices);

        fromOracle.Should().BeEquivalentTo(fromEngine, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task Concurrent_sessions_see_only_their_own_staged_batch()
    {
        var generator = new InvoiceGenerator(SutCatalog.Default, seed: 99);
        var batches = Enumerable.Range(0, 4)
            .Select(_ => generator.Generate(300, 0.7, new DateOnly(2025, 2, 1), new DateOnly(2026, 6, 30)).Select(g => g.Invoice).ToList())
            .ToList();

        var results = await Task.WhenAll(batches.Select(b => Bulk.RunAsync(b, TestContext.Current.CancellationToken)));

        for (var i = 0; i < batches.Count; i++)
        {
            var own = batches[i].Select(inv => inv.InvoiceNo).ToHashSet();
            results[i].Findings.Should().OnlyContain(f => own.Contains(f.InvoiceNo));
            results[i].Findings.Should().HaveCount(OverlapEngine.CheckAll(batches[i]).Sum(r => r.Findings.Count));
        }
    }

    [Fact]
    public async Task Each_run_is_audited_with_the_database_user()
    {
        var result = await Bulk.RunAsync([GoldenCases.Outpatient() with { Diagnoses = [new Diagnosis("I10", true)] }], TestContext.Current.CancellationToken);

        await using var connection = new OracleConnection(db.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT executed_by, invoice_count, finding_count, finished_at FROM precheck_run WHERE run_id = :id";
        command.Parameters.Add(new OracleParameter("id", result.RunId));
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        (await reader.ReadAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
        reader.GetString(0).Should().Be("PRECHECK_APP");
        reader.GetInt32(1).Should().Be(1);
        reader.GetInt32(2).Should().Be(1, "HbA1c without an indication");
        reader.IsDBNull(3).Should().BeFalse();
    }

    [Theory]
    [InlineData("SELECT COUNT(*) FROM precheck_owner.sut_procedure")]
    [InlineData("SELECT COUNT(*) FROM precheck_owner.precheck_finding")]
    [InlineData("DELETE FROM precheck_owner.precheck_run")]
    [InlineData("UPDATE precheck_owner.sut_procedure SET price = 0")]
    public async Task Application_role_cannot_touch_reference_or_audit_data(string sql)
    {
        await using var connection = new OracleConnection(db.AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var act = () => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        // ORA-00942: table or view does not exist (no privilege means the object is invisible).
        (await act.Should().ThrowAsync<OracleException>()).Which.Number.Should().Be(942);
    }

    [Fact]
    public async Task Reference_data_reload_is_idempotent()
    {
        var installer = new OracleSchemaInstaller(db.OwnerConnectionString);
        await installer.InstallAsync("precheck_app", TestContext.Current.CancellationToken);
        await installer.LoadReferenceDataAsync(SutCatalog.Default, TestContext.Current.CancellationToken);

        await using var connection = new OracleConnection(db.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sut_procedure";
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);

        count.Should().Be(SutCatalog.Default.Versions.Sum(v => v.Procedures.Count));
    }

    private static string Key(string invoice, int? line, string rule, Severity severity, string? expected, string? actual) =>
        $"{invoice}|{line}|{rule}|{severity}|{expected}|{actual}";
}
