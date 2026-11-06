using System.Text;
using MedulaPrecheck.Domain;
using MedulaPrecheck.Engine;
using MedulaPrecheck.Golden;
using MedulaPrecheck.Rules;
using MedulaPrecheck.Sut;
using MedulaPrecheck.Synthetic;

namespace MedulaPrecheck.UnitTests.Engine;

public sealed class EngineAndCatalogTests
{
    private static readonly PrecheckEngine Engine = new(SutCatalog.Default);

    [Fact]
    public void Every_defined_rule_has_exactly_one_implementation()
    {
        Engine.Rules.Select(r => r.Definition.Id).Should().BeEquivalentTo(RuleDefinitions.All.Select(d => d.Id));
        Engine.Rules.Select(r => r.Definition.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Embedded_catalog_has_three_versions_and_a_realistic_rule_count()
    {
        var catalog = SutCatalog.Default;

        catalog.Versions.Select(v => v.Id).Should().Equal("SUT-2025.01", "SUT-2025.07", "SUT-2026.01");
        catalog.Versions[^1].ActiveRules.Should().HaveCount(22);
        catalog.Versions[0].Procedures.Should().HaveCountGreaterThan(30);
    }

    [Fact]
    public void Clean_golden_invoices_are_clean()
    {
        foreach (var invoice in new[] { GoldenCases.Outpatient(), GoldenCases.Inpatient(), GoldenCases.Inpatient2026() })
        {
            var result = Engine.Check(invoice);
            result.Findings.Should().BeEmpty(invoice.InvoiceNo);
            result.Outcome.Should().Be(InvoiceOutcome.Clean);
            result.AmountAtRisk.Should().Be(0m);
        }
    }

    [Fact]
    public void Amount_at_risk_counts_only_the_rejected_lines()
    {
        var invoice = GoldenCases.Outpatient() with { Diagnoses = [new Diagnosis("I10", true)] };

        var result = Engine.Check(invoice);

        result.Outcome.Should().Be(InvoiceOutcome.Rejected);
        result.AmountAtRisk.Should().Be(69.10m, "only the HbA1c line lacks its indication");
    }

    [Fact]
    public void Parallel_batch_gives_the_same_results_in_the_same_order()
    {
        var invoices = new InvoiceGenerator(SutCatalog.Default, seed: 7)
            .Generate(500, 0.5, new DateOnly(2025, 1, 15), new DateOnly(2026, 6, 30))
            .Select(g => g.Invoice)
            .ToList();

        var sequential = Engine.CheckAll(invoices);
        var parallel = Engine.CheckAll(invoices, parallel: true);

        parallel.Should().BeEquivalentTo(sequential, o => o.WithStrictOrdering());
    }

    [Fact]
    public void WithOnly_restricts_the_engine_to_the_given_rules()
    {
        var restricted = Engine.WithOnly(["PRC-001"]);

        restricted.Rules.Should().ContainSingle().Which.Definition.Id.Should().Be("PRC-001");
        restricted.Check(GoldenCases.Outpatient() with { Diagnoses = [new Diagnosis("I10", true)] }).Findings.Should().BeEmpty();
    }

    [Fact]
    public void Catalog_rejects_a_version_that_activates_an_unknown_rule()
    {
        const string json = """
            { "branches": [], "versions": [ { "id": "X", "effectiveFrom": "2025-01-01", "descriptionEn": "x",
              "parameters": { "examinationRepeatDays": 10, "maxInvoiceLagDays": 60 }, "rules": { "NOPE-1": "Error" } } ] }
            """;

        var act = () => SutCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        act.Should().Throw<InvalidDataException>().WithMessage("*NOPE-1*");
    }

    [Fact]
    public void Catalog_can_remove_a_rule_in_a_later_version()
    {
        const string json = """
            { "branches": [], "versions": [
              { "id": "A", "effectiveFrom": "2025-01-01", "descriptionEn": "a",
                "parameters": { "examinationRepeatDays": 10, "maxInvoiceLagDays": 60 }, "rules": { "MDL-001": "Error", "MDL-004": "Error" } },
              { "id": "B", "effectiveFrom": "2025-06-01", "inherits": "A", "descriptionEn": "b", "rules": { "MDL-001": null } } ] }
            """;

        var catalog = SutCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        catalog.Find("A")!.ActiveRules.Keys.Should().BeEquivalentTo(["MDL-001", "MDL-004"]);
        catalog.Find("B")!.ActiveRules.Keys.Should().BeEquivalentTo(["MDL-004"]);
        catalog.Find("B")!.Parameters.Should().Be(catalog.Find("A")!.Parameters, "parameters are inherited");
    }

    [Theory]
    [InlineData("2000-02-29", "2024-02-28", 23)]
    [InlineData("2000-02-29", "2024-02-29", 24)]
    [InlineData("2000-02-29", "2023-03-01", 23)]
    [InlineData("1985-09-16", "2025-09-15", 39)]
    [InlineData("1985-09-15", "2025-09-15", 40)]
    [InlineData("2025-09-01", "2025-09-15", 0)]
    public void Age_is_counted_in_completed_years(string birth, string on, int expected)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var patient = new Patient("P1", "x", DateOnly.Parse(birth, inv), Gender.Male);

        patient.AgeOn(DateOnly.Parse(on, inv)).Should().Be(expected);
    }
}
