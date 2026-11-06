using System.Data;
using System.Diagnostics;
using MedulaPrecheck.Domain;
using MedulaPrecheck.Engine;
using MedulaPrecheck.Rules;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace MedulaPrecheck.Oracle;

/// <summary>One finding row as returned by PRECHECK_PKG.RUN_BATCH.</summary>
public sealed record OracleFindingRow(
    string InvoiceNo,
    int? LineNo,
    string RuleId,
    Severity Severity,
    string? SutCode,
    string? Expected,
    string? Actual);

/// <summary>Findings plus the time split between staging (array insert) and the set-based check.</summary>
public sealed record OracleBulkResult(long RunId, IReadOnlyList<OracleFindingRow> Findings, TimeSpan StageTime, TimeSpan CheckTime);

/// <summary>
/// Bulk path: stage a whole batch into session-private temporary tables with array binding, run the PL/SQL
/// package once, read the findings back, commit (which also empties the staging tables). Runs as the
/// least-privileged application account.
/// </summary>
public sealed class OracleBulkPrecheck
{
    /// <summary>Rules PRECHECK_PKG implements; must match <c>precheck_pkg.c_rules</c> (asserted by a test).</summary>
    public static readonly IReadOnlyList<string> ImplementedRules = RuleDefinitions.All.Where(r => r.OracleBulk).Select(r => r.Id).ToList();

    private readonly string _connectionString;
    private readonly string _schema;

    public OracleBulkPrecheck(string appConnectionString, string schemaOwner = "PRECHECK_OWNER")
    {
        _connectionString = appConnectionString;
        _schema = OracleNames.Identifier(schemaOwner);
    }

    public async Task<OracleBulkResult> RunAsync(IReadOnlyList<Invoice> invoices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invoices);
        await using var connection = new OracleConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        var stage = Stopwatch.StartNew();
        await StageAsync(connection, invoices, cancellationToken);
        stage.Stop();

        var check = Stopwatch.StartNew();
        await using var command = connection.CreateCommand();
        command.CommandText = _schema + ".precheck_pkg.run_batch";
        command.CommandType = CommandType.StoredProcedure;
        var runId = command.Parameters.Add("p_run_id", OracleDbType.Int64, ParameterDirection.Output);
        var cursor = command.Parameters.Add("p_findings", OracleDbType.RefCursor, ParameterDirection.Output);
        await command.ExecuteNonQueryAsync(cancellationToken);

        var findings = new List<OracleFindingRow>();
        using (var refCursor = (OracleRefCursor)cursor.Value)
        await using (var reader = refCursor.GetDataReader())
        {
            reader.FetchSize = reader.RowSize * 10_000;
            while (await reader.ReadAsync(cancellationToken))
            {
                findings.Add(new OracleFindingRow(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    reader.GetString(2),
                    Enum.Parse<Severity>(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
            }
        }

        await tx.CommitAsync(cancellationToken);
        check.Stop();

        return new OracleBulkResult(((OracleDecimal)runId.Value).ToInt64(), findings, stage.Elapsed, check.Elapsed);
    }

    /// <summary>
    /// Turns database rows into the same <see cref="InvoiceResult"/> shape as the .NET engine, with messages and
    /// fix suggestions rendered from the shared rule metadata. Invoices with no SUT version get SYS-001, as in .NET.
    /// </summary>
    public static IReadOnlyList<InvoiceResult> ToResults(PrecheckEngine engine, IReadOnlyList<Invoice> invoices, OracleBulkResult result)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(invoices);
        ArgumentNullException.ThrowIfNull(result);

        var byInvoice = result.Findings.ToLookup(f => f.InvoiceNo, StringComparer.Ordinal);
        return invoices.Select(invoice =>
        {
            var version = engine.Catalog.ResolveFor(invoice.InvoiceDate);
            if (version is null)
            {
                return engine.NoVersion(invoice);
            }

            var findings = byInvoice[invoice.InvoiceNo].Select(row =>
                (RuleDefinitions.Find(row.RuleId) ?? throw new InvalidOperationException($"Unknown rule {row.RuleId} from PL/SQL."))
                .CreateFinding(row.Severity, row.LineNo, row.SutCode, row.Expected, row.Actual));
            return InvoiceResult.Create(invoice, version.Id, findings);
        }).ToList();
    }

    private async Task StageAsync(OracleConnection connection, IReadOnlyList<Invoice> invoices, CancellationToken ct)
    {
        await ArrayInsert.Of(invoices)
            .Text(i => i.InvoiceNo).Date(i => i.InvoiceDate).Text(i => i.Provision.TreatmentType.ToString())
            .Date(i => i.Patient.BirthDate).Text(i => i.Patient.Gender.ToString())
            .Date(i => i.Provision.AdmissionDate).Date(i => i.Provision.DischargeDate)
            .ExecuteAsync(connection,
                $"INSERT INTO {_schema}.stg_invoice (invoice_no, invoice_date, treatment_type, birth_date, gender, admission_date, discharge_date) VALUES (:1, :2, :3, :4, :5, :6, :7)",
                ct);

        var lines = invoices.SelectMany(i => i.Lines.Select(l => (i.InvoiceNo, Line: l))).ToList();
        await ArrayInsert.Of(lines)
            .Text(x => x.InvoiceNo).Int(x => x.Line.LineNo).Text(x => x.Line.SutCode).Date(x => x.Line.ServiceDate)
            .Int(x => x.Line.Quantity).Money(x => x.Line.UnitPrice).Text(x => x.Line.DoctorRegistrationNo)
            .ExecuteAsync(connection,
                $"INSERT INTO {_schema}.stg_line (invoice_no, line_no, sut_code, service_date, quantity, unit_price, doctor_reg_no) VALUES (:1, :2, :3, :4, :5, :6, :7)",
                ct);

        var diagnoses = invoices.SelectMany(i => i.Diagnoses.Select(d => (i.InvoiceNo, Diagnosis: d))).ToList();
        await ArrayInsert.Of(diagnoses)
            .Text(x => x.InvoiceNo).Text(x => x.Diagnosis.IcdCode).Text(x => x.Diagnosis.IsPrimary ? "Y" : "N")
            .ExecuteAsync(connection, $"INSERT INTO {_schema}.stg_diagnosis (invoice_no, icd_code, is_primary) VALUES (:1, :2, :3)", ct);

        // Same "last registration wins" semantics as the .NET context, so duplicates cannot break parity.
        var doctors = invoices
            .SelectMany(i => i.Doctors.GroupBy(d => d.RegistrationNo, StringComparer.Ordinal).Select(g => (i.InvoiceNo, Doctor: g.Last())))
            .ToList();
        await ArrayInsert.Of(doctors)
            .Text(x => x.InvoiceNo).Text(x => x.Doctor.RegistrationNo).Text(x => x.Doctor.BranchCode)
            .ExecuteAsync(connection, $"INSERT INTO {_schema}.stg_doctor (invoice_no, reg_no, branch_code) VALUES (:1, :2, :3)", ct);

        var priors = invoices.SelectMany(i => i.PriorServices.Select(p => (i.InvoiceNo, Prior: p))).ToList();
        await ArrayInsert.Of(priors)
            .Text(x => x.InvoiceNo).Text(x => x.Prior.SutCode).Date(x => x.Prior.ServiceDate).Int(x => x.Prior.Quantity).Text(x => x.Prior.BranchCode)
            .ExecuteAsync(connection,
                $"INSERT INTO {_schema}.stg_prior (invoice_no, sut_code, service_date, quantity, branch_code) VALUES (:1, :2, :3, :4, :5)",
                ct);
    }
}
