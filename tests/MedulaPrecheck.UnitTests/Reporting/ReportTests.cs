using System.Text.Json;
using MedulaPrecheck.Engine;
using MedulaPrecheck.Reporting;
using MedulaPrecheck.Samples;
using MedulaPrecheck.Sut;

namespace MedulaPrecheck.UnitTests.Reporting;

public sealed class ReportTests
{
    private static readonly PrecheckEngine Engine = new(SutCatalog.Default);

    [Theory]
    [InlineData("report.json", ReportFormat.Json)]
    [InlineData("report.md", ReportFormat.Markdown)]
    [InlineData("report.html", ReportFormat.Html)]
    public void Committed_sample_reports_match_the_current_engine_output(string file, ReportFormat format)
    {
        // Approval test: docs/samples is regenerated with `precheck samples docs/samples`; any rule or text change
        // shows up as a diff here instead of silently making the documentation stale.
        var report = SampleSet.BuildReport(Engine);
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", file)).ReplaceLineEndings("\n");

        ReportRenderer.Render(report, format).ReplaceLineEndings("\n").Should().Be(expected);
    }

    [Fact]
    public void Committed_sample_input_matches_the_generator()
    {
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "invoices.json")).ReplaceLineEndings("\n");

        SampleSet.InvoicesJson().ReplaceLineEndings("\n").Should().Be(expected);
    }

    [Fact]
    public void Json_report_round_trips()
    {
        var report = SampleSet.BuildReport(Engine);

        var back = JsonSerializer.Deserialize<BatchReport>(report.ToJson(), BatchReport.JsonOptions);

        back.Should().BeEquivalentTo(report);
    }

    [Fact]
    public void Summary_adds_up()
    {
        var s = SampleSet.BuildReport(Engine).Summary;

        (s.Clean + s.NeedsReview + s.Rejected).Should().Be(s.Invoices);
        s.AmountAtRisk.Should().BeLessThanOrEqualTo(s.TotalAmount);
        s.Rejected.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Html_report_escapes_content()
    {
        var invoice = MedulaPrecheck.Golden.GoldenCases.Outpatient() with { InvoiceNo = "<script>x</script>" };
        var report = BatchReport.Create([Engine.Check(invoice)], DateTimeOffset.UnixEpoch, "test");

        ReportRenderer.Html(report).Should().NotContain("<script>x").And.Contain("&lt;script&gt;");
    }
}
