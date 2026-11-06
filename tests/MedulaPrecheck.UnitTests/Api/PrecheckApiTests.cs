using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MedulaPrecheck.Golden;
using MedulaPrecheck.Reporting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MedulaPrecheck.UnitTests.Api;

/// <summary>In-process API tests for the .NET engine path (no database needed; the Oracle path has its own suite).</summary>
public sealed class PrecheckApiTests : IClassFixture<PrecheckApiTests.Factory>
{
    private readonly HttpClient _client;

    public PrecheckApiTests(Factory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Precheck_returns_the_rejection_report()
    {
        var invoices = new[]
        {
            GoldenCases.Outpatient(),
            GoldenCases.Outpatient() with { InvoiceNo = "GLD-BAD", Diagnoses = [new("I10", true)] },
        };

        var response = await _client.PostAsJsonAsync("/api/v1/precheck", new { invoices }, BatchReport.JsonOptions, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = await response.Content.ReadFromJsonAsync<BatchReport>(BatchReport.JsonOptions, TestContext.Current.CancellationToken);
        report!.Summary.Invoices.Should().Be(2);
        report.Summary.Rejected.Should().Be(1);
        report.Summary.AmountAtRisk.Should().Be(69.10m);
        report.Invoices[1].Findings.Should().ContainSingle(f => f.RuleId == "CLN-001");
    }

    [Theory]
    [InlineData("markdown", "text/markdown")]
    [InlineData("html", "text/html")]
    public async Task Precheck_can_render_markdown_and_html(string format, string mediaType)
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/precheck?format={format}", new { invoices = new[] { GoldenCases.Outpatient() } },
            BatchReport.JsonOptions, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be(mediaType);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("MEDULA");
    }

    [Fact]
    public async Task Empty_and_duplicate_batches_are_rejected_with_validation_problems()
    {
        var empty = await _client.PostAsJsonAsync("/api/v1/precheck", new { invoices = Array.Empty<object>() }, TestContext.Current.CancellationToken);
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var duplicate = await _client.PostAsJsonAsync("/api/v1/precheck", new { invoices = new[] { GoldenCases.Outpatient(), GoldenCases.Outpatient() } },
            BatchReport.JsonOptions, TestContext.Current.CancellationToken);
        duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await duplicate.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("appears more than once");

        var badFormat = await _client.PostAsJsonAsync("/api/v1/precheck?format=pdf", new { invoices = new[] { GoldenCases.Outpatient() } },
            BatchReport.JsonOptions, TestContext.Current.CancellationToken);
        badFormat.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Missing_nested_objects_are_a_validation_problem_not_a_server_error()
    {
        const string json = """{ "invoices": [ { "invoiceNo": "X1", "invoiceDate": "2025-09-20" } ] }""";
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/v1/precheck", content, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Oracle_path_reports_503_when_not_configured()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/precheck/oracle", new { invoices = new[] { GoldenCases.Outpatient() } },
            BatchReport.JsonOptions, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Versions_and_rules_are_discoverable()
    {
        using var versions = JsonDocument.Parse(await _client.GetStringAsync("/api/v1/sut/versions", TestContext.Current.CancellationToken));
        versions.RootElement.GetArrayLength().Should().Be(3);

        using var version = JsonDocument.Parse(await _client.GetStringAsync("/api/v1/sut/versions/SUT-2026.01", TestContext.Current.CancellationToken));
        version.RootElement.GetProperty("activeRules").GetProperty("PRV-001").GetString().Should().Be("Error");

        (await _client.GetAsync("/api/v1/sut/versions/SUT-1999.01", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var rules = JsonDocument.Parse(await _client.GetStringAsync("/api/v1/rules", TestContext.Current.CancellationToken));
        rules.RootElement.GetArrayLength().Should().Be(22);
    }

    [Fact]
    public async Task Liveness_and_readiness_respond()
    {
        (await _client.GetAsync("/health/live", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync("/health/ready", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");
    }
}
