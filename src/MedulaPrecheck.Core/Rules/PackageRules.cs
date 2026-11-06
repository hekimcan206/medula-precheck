using MedulaPrecheck.Domain;
using MedulaPrecheck.Engine;
using MedulaPrecheck.Sut;

namespace MedulaPrecheck.Rules;

internal sealed class PackageContentRule : IRule
{
    public RuleDefinition Definition => RuleDefinitions.Pkg001;

    public void Evaluate(InvoiceContext context)
    {
        var lines = context.Invoice.Lines;
        List<string>? packages = null;
        foreach (var line in lines)
        {
            if (context.Procedure(line.SutCode) is { Kind: ProcedureKind.Package })
            {
                (packages ??= []).Add(line.SutCode);
            }
        }

        if (packages is null)
        {
            return;
        }

        packages.Sort(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            // Deterministic attribution: the lowest package code that contains the service (same as PL/SQL MIN()).
            foreach (var package in packages)
            {
                if (context.Procedure(package)!.PackageContents.Contains(line.SutCode, StringComparer.Ordinal))
                {
                    context.Report(Definition, line, package, null);
                    break;
                }
            }
        }
    }
}

internal sealed class IncompatibleCodesRule : IRule
{
    public RuleDefinition Definition => RuleDefinitions.Pkg002;

    public void Evaluate(InvoiceContext context)
    {
        var pairs = context.Version.IncompatiblePairs;
        if (pairs.Count == 0)
        {
            return;
        }

        var lines = context.Invoice.Lines;
        var onDay = new HashSet<(string, DateOnly)>();
        foreach (var line in lines)
        {
            onDay.Add((line.SutCode, line.ServiceDate));
        }

        foreach (var line in lines)
        {
            string? primary = null;
            foreach (var pair in pairs)
            {
                if (pair.Secondary == line.SutCode && onDay.Contains((pair.Primary, line.ServiceDate))
                    && (primary is null || string.CompareOrdinal(pair.Primary, primary) < 0))
                {
                    primary = pair.Primary;
                }
            }

            if (primary is not null)
            {
                context.Report(Definition, line, primary, null);
            }
        }
    }
}

internal sealed class InpatientOnlyRule : IRule
{
    public RuleDefinition Definition => RuleDefinitions.Pkg003;

    public void Evaluate(InvoiceContext context)
    {
        var treatment = context.Invoice.Provision.TreatmentType;
        if (treatment == TreatmentType.Inpatient)
        {
            return;
        }

        foreach (var line in context.Invoice.Lines)
        {
            if (context.Procedure(line.SutCode) is { InpatientOnly: true })
            {
                context.Report(Definition, line, nameof(TreatmentType.Inpatient), treatment.ToString());
            }
        }
    }
}

internal sealed class BedDayRule : IRule
{
    public RuleDefinition Definition => RuleDefinitions.Pkg004;

    public void Evaluate(InvoiceContext context)
    {
        var provision = context.Invoice.Provision;
        if (provision.TreatmentType != TreatmentType.Inpatient || provision.DischargeDate is not { } discharge)
        {
            return;
        }

        // Same-day admission and discharge still counts as one bed-day.
        var stay = Math.Max(1, discharge.DayNumber - provision.AdmissionDate.DayNumber);
        var total = 0;
        ServiceLine? last = null;
        foreach (var line in context.Invoice.Lines)
        {
            if (context.Procedure(line.SutCode) is { Kind: ProcedureKind.BedDay })
            {
                total += line.Quantity;
                if (last is null || line.LineNo > last.LineNo)
                {
                    last = line;
                }
            }
        }

        if (last is not null && total > stay)
        {
            context.Report(Definition, last, InvoiceContext.Number(stay), InvoiceContext.Number(total));
        }
    }
}

internal sealed class AccidentExternalCauseRule : IRule
{
    public RuleDefinition Definition => RuleDefinitions.Prv001;

    public void Evaluate(InvoiceContext context)
    {
        var type = context.Invoice.Provision.ProvisionType;
        if (type is not (ProvisionType.WorkAccident or ProvisionType.TrafficAccident))
        {
            return;
        }

        // ICD-10 chapter XX, external causes of morbidity: V01-Y98.
        var hasExternalCause = context.Invoice.Diagnoses.Any(d => d.IcdCode.Length > 0 && d.IcdCode[0] is 'V' or 'W' or 'X' or 'Y');
        if (!hasExternalCause)
        {
            context.Report(Definition, null, null, "V01-Y98", type.ToString());
        }
    }
}

internal sealed class LateInvoiceRule : IRule
{
    public RuleDefinition Definition => RuleDefinitions.Adm001;

    public void Evaluate(InvoiceContext context)
    {
        var invoice = context.Invoice;
        if (invoice.Lines.Count == 0)
        {
            return;
        }

        var lag = invoice.InvoiceDate.DayNumber - invoice.Lines.Max(l => l.ServiceDate).DayNumber;
        var limit = context.Version.Parameters.MaxInvoiceLagDays;
        if (lag > limit)
        {
            context.Report(Definition, null, null, InvoiceContext.Number(limit), InvoiceContext.Number(lag));
        }
    }
}
