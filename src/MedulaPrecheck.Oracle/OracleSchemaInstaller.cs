using System.Text;
using System.Text.RegularExpressions;
using MedulaPrecheck.Sut;
using Oracle.ManagedDataAccess.Client;

namespace MedulaPrecheck.Oracle;

/// <summary>
/// Applies the embedded schema scripts and loads the SUT reference data. Runs with the schema-owner account only;
/// the application account never needs DDL rights.
/// </summary>
public sealed partial class OracleSchemaInstaller(string ownerConnectionString)
{
    private static readonly string[] Scripts = ["01-tables.sql", "02-precheck-pkg.sql", "03-grants.sql"];

    public async Task InstallAsync(string appUser, CancellationToken cancellationToken = default)
    {
        var grantee = OracleNames.Identifier(appUser);
        await using var connection = new OracleConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var script in Scripts)
        {
            foreach (var statement in Split(ReadScript(script)))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = statement.Replace("${APP_USER}", grantee, StringComparison.Ordinal);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await AssertPackageValidAsync(connection, cancellationToken);
    }

    /// <summary>
    /// Replaces all reference data with the resolved snapshots of <paramref name="catalog"/>, in one transaction,
    /// using array binding (one round trip per table).
    /// </summary>
    public async Task LoadReferenceDataAsync(SutCatalog catalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        await using var connection = new OracleConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var table in new[]
                 {
                     "sut_package_content", "sut_procedure_branch", "sut_procedure_icd", "sut_incompatible_pair",
                     "sut_procedure", "sut_rule_activation", "sut_version",
                 })
        {
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM " + table;
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        var versions = catalog.Versions;
        await ArrayInsert.Of(versions)
            .Text(v => v.Id).Date(v => v.EffectiveFrom).Date(v => v.EffectiveTo)
            .Int(v => v.Parameters.ExaminationRepeatDays).Int(v => v.Parameters.MaxInvoiceLagDays)
            .ExecuteAsync(connection,
                "INSERT INTO sut_version (version_id, effective_from, effective_to, exam_repeat_days, max_invoice_lag_days) VALUES (:1, :2, :3, :4, :5)",
                cancellationToken);

        var activations = versions.SelectMany(v => v.ActiveRules.Select(r => (Version: v.Id, Rule: r.Key, Severity: r.Value.ToString()))).ToList();
        await ArrayInsert.Of(activations)
            .Text(a => a.Version).Text(a => a.Rule).Text(a => a.Severity)
            .ExecuteAsync(connection, "INSERT INTO sut_rule_activation (version_id, rule_id, severity) VALUES (:1, :2, :3)", cancellationToken);

        var procedures = versions.SelectMany(v => v.Procedures.Values.Select(p => (Version: v.Id, P: p))).ToList();
        await ArrayInsert.Of(procedures)
            .Text(x => x.Version).Text(x => x.P.Code).Text(x => x.P.Kind.ToString()).Money(x => x.P.Price)
            .Text(x => x.P.Gender?.ToString()).Int(x => x.P.MinAge).Int(x => x.P.MaxAge).Int(x => x.P.MaxPerDay)
            .Int(x => x.P.Frequency?.Count).Int(x => x.P.Frequency?.Days).Text(x => x.P.InpatientOnly ? "Y" : "N")
            .ExecuteAsync(connection,
                "INSERT INTO sut_procedure (version_id, code, kind, price, gender, min_age, max_age, max_per_day, freq_count, freq_days, inpatient_only) VALUES (:1, :2, :3, :4, :5, :6, :7, :8, :9, :10, :11)",
                cancellationToken);

        await InsertTriplesAsync(connection, "INSERT INTO sut_procedure_icd (version_id, code, icd_prefix) VALUES (:1, :2, :3)",
            procedures.SelectMany(x => x.P.RequiredIcdPrefixes.Select(i => (x.Version, x.P.Code, i))), cancellationToken);
        await InsertTriplesAsync(connection, "INSERT INTO sut_procedure_branch (version_id, code, branch_code) VALUES (:1, :2, :3)",
            procedures.SelectMany(x => x.P.AllowedBranches.Select(b => (x.Version, x.P.Code, b))), cancellationToken);
        await InsertTriplesAsync(connection, "INSERT INTO sut_package_content (version_id, package_code, included_code) VALUES (:1, :2, :3)",
            procedures.SelectMany(x => x.P.PackageContents.Select(c => (x.Version, x.P.Code, c))), cancellationToken);
        await InsertTriplesAsync(connection, "INSERT INTO sut_incompatible_pair (version_id, primary_code, secondary_code) VALUES (:1, :2, :3)",
            versions.SelectMany(v => v.IncompatiblePairs.Distinct().Select(p => (v.Id, p.Primary, p.Secondary))), cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    private static Task<int> InsertTriplesAsync(OracleConnection connection, string sql, IEnumerable<(string, string, string)> rows, CancellationToken ct) =>
        ArrayInsert.Of(rows.ToList()).Text(r => r.Item1).Text(r => r.Item2).Text(r => r.Item3).ExecuteAsync(connection, sql, ct);

    private static async Task AssertPackageValidAsync(OracleConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM user_objects WHERE object_name = 'PRECHECK_PKG' AND status <> 'VALID'";
        var invalid = Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        if (invalid > 0)
        {
            command.CommandText = "SELECT LISTAGG(line || ': ' || text, '; ') WITHIN GROUP (ORDER BY sequence) FROM user_errors WHERE name = 'PRECHECK_PKG'";
            throw new InvalidOperationException("PRECHECK_PKG did not compile: " + await command.ExecuteScalarAsync(ct));
        }
    }

    private static string ReadScript(string name)
    {
        using var stream = typeof(OracleSchemaInstaller).Assembly.GetManifestResourceStream("MedulaPrecheck.Oracle.Schema." + name)
            ?? throw new InvalidOperationException($"Embedded script {name} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Splits a script on lines that contain only "/" (SQL*Plus convention), dropping empty chunks.</summary>
    internal static IEnumerable<string> Split(string script) =>
        SlashLine().Split(script.Replace("\r\n", "\n", StringComparison.Ordinal))
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && !IsOnlyComments(s));

    private static bool IsOnlyComments(string chunk) =>
        chunk.Split('\n').All(l => l.TrimStart().StartsWith("--", StringComparison.Ordinal) || l.Trim().Length == 0);

    [GeneratedRegex(@"^\s*/\s*$", RegexOptions.Multiline)]
    private static partial Regex SlashLine();
}
