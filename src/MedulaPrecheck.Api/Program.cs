using System.Diagnostics;
using System.Text.Json.Serialization;
using MedulaPrecheck.Api.Features;
using MedulaPrecheck.Api.Infrastructure;
using MedulaPrecheck.Engine;
using MedulaPrecheck.Oracle;
using MedulaPrecheck.Sut;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(SutCatalog.Default);
builder.Services.AddSingleton(sp => new PrecheckEngine(sp.GetRequiredService<SutCatalog>()));
builder.Services.Configure<PrecheckOptions>(builder.Configuration.GetSection("Precheck"));

var health = builder.Services.AddHealthChecks();
var oracleApp = builder.Configuration.GetConnectionString("OracleApp");
var schemaOwner = builder.Configuration["Oracle:SchemaOwner"] ?? "PRECHECK_OWNER";
if (!string.IsNullOrWhiteSpace(oracleApp))
{
    // The Oracle bulk path is optional: without a connection string the API serves the .NET engine only.
    builder.Services.AddSingleton(new OracleBulkPrecheck(oracleApp, schemaOwner));
    health.AddCheck("oracle", new OracleHealthCheck(oracleApp), tags: [HealthEndpoints.ReadyTag]);
}

builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier));
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
// A 5000-invoice batch is a few MB of JSON; anything far beyond that is a mistake or abuse.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 32 * 1024 * 1024);

builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "MEDULA pre-check API";
    document.Info.Version = "v1";
    document.Info.Description = "Applies effective-dated, illustrative SUT rules to synthetic MEDULA invoices before submission.";
    return Task.CompletedTask;
}));

var app = builder.Build();

// One image, two roles: `--init-oracle` installs the schema and loads the SUT reference data with the
// schema-owner account and exits; the long-running API connects with the stage-and-execute-only account.
if (args.Contains("--init-oracle"))
{
    var owner = app.Configuration.GetConnectionString("OracleOwner");
    if (string.IsNullOrWhiteSpace(owner))
    {
        app.Logger.LogError("ConnectionStrings:OracleOwner is required for --init-oracle");
        return 1;
    }

    var installer = new OracleSchemaInstaller(owner);
    await installer.InstallAsync(app.Configuration["Oracle:AppUser"] ?? "PRECHECK_APP");
    await installer.LoadReferenceDataAsync(app.Services.GetRequiredService<SutCatalog>());
    app.Logger.LogInformation("Oracle schema installed and SUT reference data loaded");
    return 0;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging(options =>
    options.GetLevel = (context, _, exception) =>
        exception is null && context.Response.StatusCode < 400 && context.Request.Path.StartsWithSegments("/health")
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information);

if (app.Configuration.GetValue<bool>("OpenApi:Enabled"))
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.WithTitle("MEDULA pre-check API"));
}

app.MapHealthEndpoints();
app.MapGroup("/api/v1")
    .WithGroupName("v1")
    .MapPrecheckEndpoints();

await app.RunAsync();
return 0;

/// <summary>Entry point marker for WebApplicationFactory in tests.</summary>
public partial class Program;
