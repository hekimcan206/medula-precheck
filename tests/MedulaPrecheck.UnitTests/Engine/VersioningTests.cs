using MedulaPrecheck.Domain;
using MedulaPrecheck.Engine;
using MedulaPrecheck.Golden;
using MedulaPrecheck.Rules;
using MedulaPrecheck.Sut;

namespace MedulaPrecheck.UnitTests.Engine;

/// <summary>"The SUT version valid on the invoice date" decides prices, limits, codes, active rules and severities.</summary>
public sealed class VersioningTests
{
    private static readonly SutCatalog Catalog = SutCatalog.Default;
    private static readonly PrecheckEngine Engine = new(Catalog);

    [Theory]
    [InlineData("2025-01-01", "SUT-2025.01")]
    [InlineData("2025-06-30", "SUT-2025.01")]
    [InlineData("2025-07-01", "SUT-2025.07")]
    [InlineData("2025-12-31", "SUT-2025.07")]
    [InlineData("2026-01-01", "SUT-2026.01")]
    [InlineData("2030-05-05", "SUT-2026.01")]
    public void Resolves_the_version_in_force_with_inclusive_start_and_exclusive_end(string date, string expected) =>
        Catalog.ResolveFor(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture))!.Id.Should().Be(expected);

    [Fact]
    public void Versions_chain_without_gaps_or_overlaps()
    {
        var versions = Catalog.Versions;
        for (var i = 0; i < versions.Count - 1; i++)
        {
            versions[i].EffectiveTo.Should().Be(versions[i + 1].EffectiveFrom);
        }

        versions[^1].EffectiveTo.Should().BeNull();
    }

    [Fact]
    public void Invoice_before_the_first_version_is_rejected_with_SYS_001()
    {
        var invoice = GoldenCases.Outpatient() with { InvoiceDate = new DateOnly(2024, 12, 31) };

        var result = Engine.Check(invoice);

        result.SutVersion.Should().BeNull();
        result.Outcome.Should().Be(InvoiceOutcome.Rejected);
        result.Findings.Should().ContainSingle().Which.RuleId.Should().Be("SYS-001");
        result.AmountAtRisk.Should().Be(invoice.Total);
    }

    [Fact]
    public void The_same_price_is_correct_in_one_version_and_wrong_in_the_next()
    {
        // CBC at the SUT-2025.01 price (28.50), invoiced on the last day of that version and the first day of the next.
        var invoice = GoldenCases.Outpatient() with
        {
            InvoiceDate = new DateOnly(2025, 6, 30),
            Provision = GoldenCases.Outpatient().Provision with { AdmissionDate = new DateOnly(2025, 6, 20) },
            Lines =
            [
                new ServiceLine(1, "900101", new DateOnly(2025, 6, 20), 1, 120.00m, "100001"),
                new ServiceLine(2, "901001", new DateOnly(2025, 6, 20), 1, 28.50m, "100001"),
            ],
        };

        Engine.Check(invoice).Findings.Should().BeEmpty();

        var next = Engine.Check(invoice with { InvoiceDate = new DateOnly(2025, 7, 1) });
        next.SutVersion.Should().Be("SUT-2025.07");
        next.Findings.Should().ContainSingle(f => f.RuleId == "PRC-001" && f.Expected == "30.80");
    }

    [Fact]
    public void A_code_introduced_by_an_amendment_is_unknown_before_it()
    {
        var invoice = GoldenCases.Outpatient() with
        {
            Diagnoses = [new Diagnosis("E55.9", true)],
            Lines = [new ServiceLine(1, "901011", GoldenCases.Visit, 1, 88.00m, "100001")],
        };

        Engine.Check(invoice).Findings.Should().BeEmpty("Vitamin D exists in SUT-2025.07");

        var earlier = invoice with
        {
            InvoiceDate = new DateOnly(2025, 6, 30),
            Provision = invoice.Provision with { AdmissionDate = new DateOnly(2025, 6, 25) },
            Lines = [new ServiceLine(1, "901011", new DateOnly(2025, 6, 25), 1, 88.00m, "100001")],
        };
        Engine.Check(earlier).Findings.Should().ContainSingle()
            .Which.Should().Match<Finding>(f => f.RuleId == "MDL-004" && f.Expected == "SUT-2025.01");
    }

    [Fact]
    public void A_withdrawn_code_is_unknown_after_the_amendment()
    {
        Catalog.Find("SUT-2025.07")!.Procedures.Should().ContainKey("901009");
        Catalog.Find("SUT-2026.01")!.Procedures.Should().NotContainKey("901009");
    }

    [Fact]
    public void Frequency_limit_tightens_with_the_amendment()
    {
        // Three HbA1c in 200 days: allowed by 4/365 (SUT-2025.01), rejected by 2/180 (SUT-2025.07).
        var baseInvoice = GoldenCases.Outpatient();
        Invoice Dated(DateOnly visit, DateOnly invoiceDate) => baseInvoice with
        {
            InvoiceDate = invoiceDate,
            Provision = baseInvoice.Provision with { AdmissionDate = visit },
            Lines = [new ServiceLine(1, "901002", visit, 1, Catalog.ResolveFor(invoiceDate)!.Procedures["901002"].Price, "100001")],
            PriorServices = [new PriorService("901002", visit.AddDays(-150), 1, "1100"), new PriorService("901002", visit.AddDays(-60), 1, "1100")],
        };

        Engine.Check(Dated(new DateOnly(2025, 6, 20), new DateOnly(2025, 6, 25))).Findings.Should().BeEmpty();
        Engine.Check(Dated(new DateOnly(2025, 7, 20), new DateOnly(2025, 7, 25))).Findings
            .Should().ContainSingle(f => f.RuleId == "FRQ-001" && f.Expected == "2/180" && f.Actual == "3");
    }

    [Fact]
    public void A_rule_is_only_applied_in_the_versions_that_activate_it()
    {
        var tooManyBedDays = GoldenCases.Inpatient().MapBedDays(4);
        Engine.Check(tooManyBedDays).Findings.Should().NotContain(f => f.RuleId == "PKG-004", "PKG-004 starts in SUT-2026.01");

        Engine.Check(GoldenCases.Inpatient2026().MapBedDays(4)).Findings
            .Should().ContainSingle(f => f.RuleId == "PKG-004");

        var late = GoldenCases.Outpatient() with
        {
            InvoiceDate = new DateOnly(2025, 6, 30),
            Provision = GoldenCases.Outpatient().Provision with { AdmissionDate = new DateOnly(2025, 3, 1) },
            Lines = [new ServiceLine(1, "900101", new DateOnly(2025, 3, 1), 1, 120.00m, "100001")],
        };
        Engine.Check(late).Findings.Should().BeEmpty("ADM-001 starts in SUT-2025.07");
    }

    [Fact]
    public void Severity_belongs_to_the_version()
    {
        var accident = GoldenCases.Outpatient() with
        {
            Provision = GoldenCases.Outpatient().Provision with { ProvisionType = ProvisionType.TrafficAccident },
        };
        var in2025 = Engine.Check(accident);
        in2025.Findings.Should().ContainSingle().Which.Severity.Should().Be(Severity.Warning);
        in2025.Outcome.Should().Be(InvoiceOutcome.NeedsReview);
        in2025.AmountAtRisk.Should().Be(0m);

        var visit = new DateOnly(2026, 1, 12);
        var in2026 = Engine.Check(accident with
        {
            InvoiceDate = new DateOnly(2026, 1, 15),
            Provision = accident.Provision with { AdmissionDate = visit },
            Lines = [new ServiceLine(1, "900101", visit, 1, 135.00m, "100001")],
        });
        in2026.Findings.Should().ContainSingle().Which.Severity.Should().Be(Severity.Error);
        in2026.Outcome.Should().Be(InvoiceOutcome.Rejected);
    }

    [Fact]
    public void Version_parameters_change_rule_thresholds()
    {
        // 50 days late: inside the 60-day limit of SUT-2025.07, outside the 45-day limit of SUT-2026.01.
        var visit2025 = new DateOnly(2025, 10, 1);
        var o = GoldenCases.Outpatient();
        var in2025 = o with
        {
            InvoiceDate = visit2025.AddDays(50),
            Provision = o.Provision with { AdmissionDate = visit2025 },
            Lines = [new ServiceLine(1, "900101", visit2025, 1, 120.00m, "100001")],
        };
        Engine.Check(in2025).Findings.Should().BeEmpty();

        var visit2026 = new DateOnly(2026, 2, 1);
        var in2026 = in2025 with
        {
            InvoiceDate = visit2026.AddDays(50),
            Provision = o.Provision with { AdmissionDate = visit2026 },
            Lines = [new ServiceLine(1, "900101", visit2026, 1, 135.00m, "100001")],
        };
        Engine.Check(in2026).Findings.Should().ContainSingle(f => f.RuleId == "ADM-001" && f.Expected == "45" && f.Actual == "50");
    }
}

internal static class InvoiceTestExtensions
{
    public static Invoice MapBedDays(this Invoice invoice, int quantity) => invoice with
    {
        Lines = [.. invoice.Lines.Select(l => l.SutCode == "904001" ? l with { Quantity = quantity } : l)],
    };
}
