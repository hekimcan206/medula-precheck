using System.Text.RegularExpressions;
using MedulaPrecheck.Domain;
using MedulaPrecheck.Engine;

namespace MedulaPrecheck.Rules;

/// <summary>A pre-check rule. Implementations are stateless and thread-safe; all state lives in the context.</summary>
public interface IRule
{
    RuleDefinition Definition { get; }

    void Evaluate(InvoiceContext context);
}

internal sealed partial class TrackingNumberRule : IRule
{
    public RuleDefinition Definition => RuleDefinitions.Mdl001;

    public void Evaluate(InvoiceContext context)
    {
        var trackingNo = context.Invoice.Provision.TrackingNo;
        if (!TrackingNo().IsMatch(trackingNo ?? string.Empty))
        {
            context.Report(Definition, null, null, "[0-9A-Z]{7,10}", trackingNo);
        }
    }

    [GeneratedRegex("^[0-9A-Z]{7,10}$", RegexOptions.CultureInvariant)]
    private static partial Regex TrackingNo();
}

internal sealed class ServiceWithinProvisionRule : IRule
{
    public RuleDefinition Definition => RuleDefinitions.Mdl002;

    public void Evaluate(InvoiceContext context)
    {
        var provision = context.Invoice.Provision;
        var window = InvoiceContext.Date(provision.AdmissionDate) + ".."
            + (provision.DischargeDate is { } d ? InvoiceContext.Date(d) : string.Empty);

        foreach (var line in context.Invoice.Lines)
        {
            if (line.ServiceDate < provision.AdmissionDate
                || (provision.DischargeDate is { } discharge && line.ServiceDate > discharge))
            {
                context.Report(Definition, line, window, InvoiceContext.Date(line.ServiceDate));
            }
        }
    }
}

internal sealed class InvoiceDateRule : IRule
{
    public RuleDefinition Definition => RuleDefinitions.Mdl003;

    public void Evaluate(InvoiceContext context)
    {
        var invoice = context.Invoice;
        var provision = invoice.Provision;
        if (provision.TreatmentType == TreatmentType.Inpatient && provision.DischargeDate is null)
        {
            context.Report(Definition, null, null, "discharge", InvoiceContext.Date(invoice.InvoiceDate));
            return;
        }

        var latest = invoice.Lines.Count == 0 ? provision.AdmissionDate : invoice.Lines.Max(l => l.ServiceDate);
        if (provision.DischargeDate is { } discharge && discharge > latest)
        {
            latest = discharge;
        }

        if (invoice.InvoiceDate < latest)
        {
            context.Report(Definition, null, null, InvoiceContext.Date(latest), InvoiceContext.Date(invoice.InvoiceDate));
        }
    }
}

internal sealed class KnownCodeRule : IRule
{
    public RuleDefinition Definition => RuleDefinitions.Mdl004;

    public void Evaluate(InvoiceContext context)
    {
        foreach (var line in context.Invoice.Lines)
        {
            if (context.Procedure(line.SutCode) is null)
            {
                context.Report(Definition, line, context.Version.Id, line.SutCode);
            }
        }
    }
}

internal sealed class DuplicateLineRule : IRule
{
    public RuleDefinition Definition => RuleDefinitions.Mdl005;

    public void Evaluate(InvoiceContext context)
    {
        var first = new Dictionary<(string, DateOnly, string), int>();
        foreach (var line in context.Invoice.Lines.OrderBy(l => l.LineNo))
        {
            var key = (line.SutCode, line.ServiceDate, line.DoctorRegistrationNo);
            if (first.TryGetValue(key, out var firstLine))
            {
                context.Report(Definition, line, InvoiceContext.Number(firstLine), null);
            }
            else
            {
                first.Add(key, line.LineNo);
            }
        }
    }
}

internal sealed partial class PhysicianRule : IRule
{
    public RuleDefinition Definition => RuleDefinitions.Mdl006;

    public void Evaluate(InvoiceContext context)
    {
        foreach (var line in context.Invoice.Lines)
        {
            var reg = line.DoctorRegistrationNo ?? string.Empty;
            if (!RegistrationNo().IsMatch(reg) || context.Doctor(reg) is null)
            {
                context.Report(Definition, line, @"\d{6}", reg);
            }
        }
    }

    [GeneratedRegex(@"^\d{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex RegistrationNo();
}

internal sealed partial class DiagnosisCodingRule : IRule
{
    public RuleDefinition Definition => RuleDefinitions.Mdl007;

    public void Evaluate(InvoiceContext context)
    {
        var diagnoses = context.Invoice.Diagnoses;
        foreach (var diagnosis in diagnoses)
        {
            if (!Icd10().IsMatch(diagnosis.IcdCode ?? string.Empty))
            {
                context.Report(Definition, null, null, "A00.0", diagnosis.IcdCode);
            }
        }

        var primaries = diagnoses.Count(d => d.IsPrimary);
        if (primaries != 1)
        {
            context.Report(Definition, null, null, "1", "primary=" + InvoiceContext.Number(primaries));
        }
    }

    // Category letter, two digits, optional dot and up to four characters (ICD-10 / ICD-10-TR style).
    [GeneratedRegex(@"^[A-Z][0-9][0-9A-Z](\.[0-9A-Z]{1,4})?$", RegexOptions.CultureInvariant)]
    private static partial Regex Icd10();
}
