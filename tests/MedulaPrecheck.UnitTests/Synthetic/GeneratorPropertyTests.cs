using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using MedulaPrecheck.Engine;
using MedulaPrecheck.Sut;
using MedulaPrecheck.Synthetic;

namespace MedulaPrecheck.UnitTests.Synthetic;

/// <summary>
/// The generator and the engine check each other: a clean synthetic invoice must produce no finding, and every
/// injected defect must be caught by the rule it was labelled with, across random seeds and all SUT versions.
/// </summary>
public sealed class GeneratorPropertyTests
{
    private static readonly PrecheckEngine Engine = new(SutCatalog.Default);
    private static readonly DateOnly From = new(2025, 1, 25);
    private static readonly DateOnly To = new(2026, 9, 30);

    [Property(MaxTest = 60)]
    public Property Clean_invoices_produce_no_findings(PositiveInt seed)
    {
        var generated = new InvoiceGenerator(SutCatalog.Default, seed.Get).Generate(40, 0.0, From, To);

        var noisy = generated
            .Select(g => (g.Invoice.InvoiceNo, Findings: Engine.Check(g.Invoice).Findings))
            .Where(r => r.Findings.Count > 0)
            .Select(r => $"{r.InvoiceNo}: {string.Join(", ", r.Findings.Select(f => f.RuleId + "@" + f.LineNo))}")
            .ToList();

        return (noisy.Count == 0).Label(string.Join(" | ", noisy));
    }

    [Property(MaxTest = 60)]
    public Property Every_injected_defect_is_detected_by_its_rule(PositiveInt seed)
    {
        var generated = new InvoiceGenerator(SutCatalog.Default, seed.Get).Generate(40, 1.0, From, To);

        var missed = generated
            .Where(g => g.InjectedRuleId is not null)
            .Where(g => Engine.Check(g.Invoice).Findings.All(f => f.RuleId != g.InjectedRuleId))
            .Select(g => $"{g.Invoice.InvoiceNo} missed {g.InjectedRuleId}")
            .ToList();

        return (missed.Count == 0).Label(string.Join(" | ", missed));
    }

    [Fact]
    public void Generation_is_deterministic_for_a_seed()
    {
        var a = new InvoiceGenerator(SutCatalog.Default, 42).Generate(50, 0.3, From, To);
        var b = new InvoiceGenerator(SutCatalog.Default, 42).Generate(50, 0.3, From, To);

        a.Should().BeEquivalentTo(b, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Defect_injection_covers_every_rule_over_a_large_sample()
    {
        var injected = new InvoiceGenerator(SutCatalog.Default, 1).Generate(3000, 1.0, From, To)
            .Select(g => g.InjectedRuleId)
            .Distinct()
            .ToList();

        injected.Should().Contain(MedulaPrecheck.Rules.RuleDefinitions.All.Select(r => r.Id));
    }
}
