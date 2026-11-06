using System.Diagnostics;
using MedulaPrecheck.Api.Infrastructure;
using MedulaPrecheck.Domain;
using MedulaPrecheck.Engine;
using MedulaPrecheck.Oracle;
using MedulaPrecheck.Reporting;
using MedulaPrecheck.Rules;
using MedulaPrecheck.Sut;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace MedulaPrecheck.Api.Features;

public sealed record PrecheckRequest(IReadOnlyList<Invoice>? Invoices);

public sealed class PrecheckOptions
{
    public int MaxInvoicesPerRequest { get; set; } = 5000;
}

public sealed record SutVersionSummary(
    string Id,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    string Description,
    int ProcedureCount,
    IReadOnlyDictionary<string, Severity> ActiveRules);

public sealed record RuleSummary(
    string Id,
    RuleCategory Category,
    string TitleTr,
    string TitleEn,
    FixAction FixAction,
    bool RunsInPlSql,
    string Basis);

internal static class PrecheckEndpoints
{
    private static readonly ActivitySource Activity = new(ObservabilityExtensions.ActivitySourceName);

    public static RouteGroupBuilder MapPrecheckEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/precheck", CheckAsync)
            .WithName("Precheck")
            .WithSummary("Pre-check invoices with the .NET rule engine")
            .WithDescription("Applies every rule of the SUT version valid on each invoice date. format=json (default), markdown or html.");

        group.MapPost("/precheck/oracle", CheckWithOracleAsync)
            .WithName("PrecheckOracle")
            .WithSummary("Pre-check invoices with the PL/SQL bulk path")
            .WithDescription("Runs the set-based PL/SQL subset (see ADR 0002). Returns 503 when Oracle is not configured.");

        group.MapGet("/sut/versions", (SutCatalog catalog) => TypedResults.Ok(catalog.Versions.Select(Summarise).ToList()))
            .WithName("ListSutVersions")
            .WithSummary("List effective-dated SUT rule-set versions");

        group.MapGet("/sut/versions/{id}", Results<Ok<SutVersionSummary>, NotFound> (string id, SutCatalog catalog) =>
                catalog.Find(id) is { } v ? TypedResults.Ok(Summarise(v)) : TypedResults.NotFound())
            .WithName("GetSutVersion")
            .WithSummary("Get one SUT version and the severity of each active rule");

        group.MapGet("/rules", () => TypedResults.Ok(RuleDefinitions.All.Select(r =>
                new RuleSummary(r.Id, r.Category, r.TitleTr, r.TitleEn, r.FixAction, r.OracleBulk, r.Basis)).ToList()))
            .WithName("ListRules")
            .WithSummary("List all rules with their bilingual titles");

        return group;
    }

    private static Results<Ok<BatchReport>, ContentHttpResult, ValidationProblem> CheckAsync(
        PrecheckRequest request,
        string? format,
        PrecheckEngine engine,
        PrecheckMetrics metrics,
        TimeProvider clock,
        IOptions<PrecheckOptions> options)
    {
        if (Validate(request, options.Value, format, out var reportFormat) is { } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        using var activity = Activity.StartActivity("precheck.dotnet");
        activity?.SetTag("precheck.invoices", request.Invoices!.Count);
        var results = engine.CheckAll(request.Invoices!, parallel: request.Invoices!.Count > 200);
        metrics.Record(results, "dotnet");
        return Render(BatchReport.Create(results, clock.GetUtcNow(), "dotnet-engine"), reportFormat);
    }

    private static async Task<Results<Ok<BatchReport>, ContentHttpResult, ValidationProblem, ProblemHttpResult>> CheckWithOracleAsync(
        PrecheckRequest request,
        string? format,
        PrecheckEngine engine,
        PrecheckMetrics metrics,
        TimeProvider clock,
        IOptions<PrecheckOptions> options,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (services.GetService<OracleBulkPrecheck>() is not { } bulk)
        {
            return TypedResults.Problem(
                "The Oracle bulk path is not configured (ConnectionStrings:OracleApp).",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Oracle not configured");
        }

        if (Validate(request, options.Value, format, out var reportFormat) is { } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        using var activity = Activity.StartActivity("precheck.oracle");
        activity?.SetTag("precheck.invoices", request.Invoices!.Count);
        var raw = await bulk.RunAsync(request.Invoices!, cancellationToken);
        activity?.SetTag("precheck.run_id", raw.RunId);
        var results = OracleBulkPrecheck.ToResults(engine, request.Invoices!, raw);
        metrics.Record(results, "oracle");
        var report = BatchReport.Create(results, clock.GetUtcNow(), "oracle-plsql-bulk");
        return reportFormat switch
        {
            ReportFormat.Markdown => TypedResults.Text(ReportRenderer.Markdown(report), "text/markdown; charset=utf-8"),
            ReportFormat.Html => TypedResults.Text(ReportRenderer.Html(report), "text/html; charset=utf-8"),
            _ => TypedResults.Ok(report),
        };
    }

    private static Results<Ok<BatchReport>, ContentHttpResult, ValidationProblem> Render(BatchReport report, ReportFormat format) =>
        format switch
        {
            ReportFormat.Markdown => TypedResults.Text(ReportRenderer.Markdown(report), "text/markdown; charset=utf-8"),
            ReportFormat.Html => TypedResults.Text(ReportRenderer.Html(report), "text/html; charset=utf-8"),
            _ => TypedResults.Ok(report),
        };

    private static SutVersionSummary Summarise(SutVersion v) =>
        new(v.Id, v.EffectiveFrom, v.EffectiveTo, v.DescriptionEn, v.Procedures.Count, v.ActiveRules);

    /// <summary>
    /// Shape validation only: the engine reports business problems as findings, but it must not be fed nulls,
    /// duplicate invoice numbers (the PL/SQL staging key) or unbounded batches.
    /// </summary>
    internal static Dictionary<string, string[]>? Validate(PrecheckRequest request, PrecheckOptions options, string? format, out ReportFormat reportFormat)
    {
        var errors = new Dictionary<string, string[]>();
        reportFormat = ReportFormat.Json;
        if (format is not null && !Enum.TryParse(format, ignoreCase: true, out reportFormat))
        {
            errors["format"] = ["Use json, markdown or html."];
        }

        var invoices = request?.Invoices;
        if (invoices is null || invoices.Count == 0)
        {
            errors["invoices"] = ["At least one invoice is required."];
            return errors;
        }

        if (invoices.Count > options.MaxInvoicesPerRequest)
        {
            errors["invoices"] = [$"At most {options.MaxInvoicesPerRequest} invoices per request."];
            return errors;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < invoices.Count; i++)
        {
            var invoice = invoices[i];
            var path = $"invoices[{i}]";
            if (invoice is null)
            {
                errors[path] = ["Invoice is required."];
                continue;
            }

            var problems = new List<string>();
            if (string.IsNullOrWhiteSpace(invoice.InvoiceNo) || invoice.InvoiceNo.Length > 30)
            {
                problems.Add("invoiceNo is required (max 30 characters).");
            }
            else if (!seen.Add(invoice.InvoiceNo))
            {
                problems.Add($"invoiceNo '{invoice.InvoiceNo}' appears more than once.");
            }

            if (invoice.Patient is null)
            {
                problems.Add("patient is required.");
            }

            if (invoice.Provision is null)
            {
                problems.Add("provision is required.");
            }

            if (invoice.Lines is null || invoice.Lines.Count == 0 || invoice.Lines.Any(l => l is null || l.SutCode is null || l.DoctorRegistrationNo is null))
            {
                problems.Add("lines are required and each needs sutCode and doctorRegistrationNo.");
            }
            else if (invoice.Lines.Select(l => l.LineNo).Distinct().Count() != invoice.Lines.Count)
            {
                problems.Add("lineNo must be unique within the invoice.");
            }

            if (invoice.Doctors is null || invoice.Diagnoses is null || invoice.PriorServices is null
                || invoice.Doctors.Any(d => d is null) || invoice.Diagnoses.Any(d => d?.IcdCode is null) || invoice.PriorServices.Any(p => p is null))
            {
                problems.Add("doctors, diagnoses and priorServices must be present (use [] when empty).");
            }

            if (problems.Count > 0)
            {
                errors[path] = [.. problems];
            }
        }

        return errors.Count > 0 ? errors : null;
    }
}
