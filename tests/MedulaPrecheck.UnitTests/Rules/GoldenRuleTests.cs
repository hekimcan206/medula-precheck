using MedulaPrecheck.Engine;
using MedulaPrecheck.Golden;
using MedulaPrecheck.Rules;
using MedulaPrecheck.Sut;

namespace MedulaPrecheck.UnitTests.Rules;

public sealed class GoldenRuleTests
{
    private static readonly PrecheckEngine Engine = new(SutCatalog.Default);
    private static readonly string[] Placeholders = ["{line}", "{code}", "{expected}", "{actual}"];

    // Case names (strings) keep each case a separately reported, serialisable test.
    public static TheoryData<string> Cases() => [.. GoldenCases.All.Select(c => c.Name)];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Engine_output_matches_the_golden_case(string name)
    {
        var golden = GoldenCases.All.Single(c => c.Name == name);
        var result = Engine.Check(golden.Invoice);

        result.Findings.Select(f => new ExpectedFinding(f.RuleId, f.LineNo, f.Expected))
            .Should().BeEquivalentTo(golden.Expected, o => o.WithoutStrictOrdering(), golden.Name);
    }

    [Fact]
    public void Every_rule_has_at_least_one_pass_and_one_fail_case()
    {
        foreach (var rule in RuleDefinitions.All)
        {
            GoldenCases.All.Should().Contain(c => c.RuleId == rule.Id && c.ShouldFail, $"{rule.Id} needs a failing golden case");
            GoldenCases.All.Should().Contain(c => c.RuleId == rule.Id && !c.ShouldFail, $"{rule.Id} needs a passing golden case");
        }
    }

    [Fact]
    public void Fail_cases_only_expect_the_rule_they_target()
    {
        foreach (var c in GoldenCases.All.Where(c => c.ShouldFail))
        {
            c.Expected.Should().NotBeEmpty(c.Name).And.OnlyContain(e => e.RuleId == c.RuleId, c.Name);
        }
    }

    [Fact]
    public void Findings_carry_bilingual_reason_and_a_concrete_suggestion()
    {
        var failing = GoldenCases.All.Where(c => c.ShouldFail).SelectMany(c => Engine.Check(c.Invoice).Findings).ToList();

        failing.Should().NotBeEmpty();
        foreach (var f in failing)
        {
            foreach (var text in new[] { f.Reason.Tr, f.Reason.En, f.Suggestion.Tr, f.Suggestion.En })
            {
                text.Should().NotBeNullOrWhiteSpace().And.NotContainAny(Placeholders);
            }
            f.Fix.Action.Should().Be(RuleDefinitions.Find(f.RuleId)!.FixAction);
        }
    }

    [Fact]
    public void Price_finding_suggests_the_exact_tariff_value()
    {
        var invoice = GoldenCases.All.Single(c => c.Name == "PRC-001 fail: previous version's price").Invoice;

        var finding = Engine.Check(invoice).Findings.Single();

        finding.Fix.Should().Be(new SuggestedFix(FixAction.SetUnitPrice, 2, "30.80"));
        finding.Suggestion.En.Should().Be("Set the unit price to 30.80 TRY.");
        finding.Suggestion.Tr.Should().Be("Birim fiyatı 30.80 TL olarak düzeltin.");
        finding.Actual.Should().Be("28.50");
    }
}
