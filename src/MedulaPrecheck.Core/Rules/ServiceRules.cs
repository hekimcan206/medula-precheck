using MedulaPrecheck.Domain;
using MedulaPrecheck.Engine;
using MedulaPrecheck.Sut;

namespace MedulaPrecheck.Rules;

internal sealed class TariffPriceRule : IRule
{
    public RuleDefinition Definition => RuleDefinitions.Prc001;

    public void Evaluate(InvoiceContext context)
    {
        foreach (var line in context.Invoice.Lines)
        {
            if (context.Procedure(line.SutCode) is { } p && line.UnitPrice != p.Price)
            {
                context.Report(Definition, line, InvoiceContext.Money(p.Price), InvoiceContext.Money(line.UnitPrice));
            }
        }
    }
}

internal sealed class DailyQuantityRule : IRule
{
    public RuleDefinition Definition => RuleDefinitions.Prc002;

    public void Evaluate(InvoiceContext context)
    {
        var totals = new Dictionary<(string, DateOnly), int>();
        foreach (var line in context.Invoice.Lines)
        {
            var key = (line.SutCode, line.ServiceDate);
            totals[key] = totals.GetValueOrDefault(key) + line.Quantity;
        }

        foreach (var line in context.Invoice.Lines)
        {
            if (context.Procedure(line.SutCode) is { MaxPerDay: { } max }
                && totals[(line.SutCode, line.ServiceDate)] is var total && total > max)
            {
                context.Report(Definition, line, InvoiceContext.Number(max), InvoiceContext.Number(total));
            }
        }
    }
}

internal sealed class ProcedureGenderRule : IRule
{
    public RuleDefinition Definition => RuleDefinitions.Dem001;

    public void Evaluate(InvoiceContext context)
    {
        var gender = context.Invoice.Patient.Gender;
        foreach (var line in context.Invoice.Lines)
        {
            if (context.Procedure(line.SutCode) is { Gender: { } required } && required != gender)
            {
                context.Report(Definition, line, required.ToString(), gender.ToString());
            }
        }
    }
}

internal sealed class ProcedureAgeRule : IRule
{
    public RuleDefinition Definition => RuleDefinitions.Dem002;

    public void Evaluate(InvoiceContext context)
    {
        var patient = context.Invoice.Patient;
        foreach (var line in context.Invoice.Lines)
        {
            if (context.Procedure(line.SutCode) is not { } p || (p.MinAge is null && p.MaxAge is null))
            {
                continue;
            }

            var age = patient.AgeOn(line.ServiceDate);
            if (age < p.MinAge || age > p.MaxAge)
            {
                var range = (p.MinAge is { } min ? InvoiceContext.Number(min) : string.Empty) + "-"
                    + (p.MaxAge is { } max ? InvoiceContext.Number(max) : string.Empty);
                context.Report(Definition, line, range, InvoiceContext.Number(age));
            }
        }
    }
}

internal sealed class DiagnosisGenderRuleCheck : IRule
{
    public RuleDefinition Definition => RuleDefinitions.Dem003;

    public void Evaluate(InvoiceContext context)
    {
        var gender = context.Invoice.Patient.Gender;
        foreach (var diagnosis in context.Invoice.Diagnoses)
        {
            foreach (var rule in context.Version.DiagnosisGenderRules)
            {
                if (rule.Gender != gender && rule.Matches(diagnosis.IcdCode))
                {
                    context.Report(Definition, null, null, rule.Gender.ToString(), diagnosis.IcdCode);
                    break;
                }
            }
        }
    }
}

internal sealed class RequiredDiagnosisRule : IRule
{
    public RuleDefinition Definition => RuleDefinitions.Cln001;

    public void Evaluate(InvoiceContext context)
    {
        var diagnoses = context.Invoice.Diagnoses;
        foreach (var line in context.Invoice.Lines)
        {
            if (context.Procedure(line.SutCode) is not { RequiredIcdPrefixes.Count: > 0 } p)
            {
                continue;
            }

            var supported = diagnoses.Any(d =>
                p.RequiredIcdPrefixes.Any(prefix => d.IcdCode.StartsWith(prefix, StringComparison.Ordinal)));
            if (!supported)
            {
                context.Report(Definition, line, string.Join(", ", p.RequiredIcdPrefixes), null);
            }
        }
    }
}

internal sealed class PhysicianBranchRule : IRule
{
    public RuleDefinition Definition => RuleDefinitions.Cln002;

    public void Evaluate(InvoiceContext context)
    {
        foreach (var line in context.Invoice.Lines)
        {
            // A missing physician is MDL-006's finding; reporting it twice would double-count one defect.
            if (context.Procedure(line.SutCode) is { AllowedBranches.Count: > 0 } p
                && context.Doctor(line.DoctorRegistrationNo) is { } doctor
                && !p.AllowedBranches.Contains(doctor.BranchCode, StringComparer.Ordinal))
            {
                context.Report(Definition, line, string.Join(", ", p.AllowedBranches), doctor.BranchCode);
            }
        }
    }
}

internal sealed class FrequencyRule : IRule
{
    public RuleDefinition Definition => RuleDefinitions.Frq001;

    public void Evaluate(InvoiceContext context)
    {
        var invoice = context.Invoice;
        foreach (var line in invoice.Lines)
        {
            if (context.Procedure(line.SutCode) is not { Frequency: { } limit })
            {
                continue;
            }

            // Rolling window of `Days` days ending on the service date, inclusive on both ends.
            var start = line.ServiceDate.AddDays(-(limit.Days - 1));
            var count = 0;
            foreach (var prior in invoice.PriorServices)
            {
                if (prior.SutCode == line.SutCode && prior.ServiceDate >= start && prior.ServiceDate <= line.ServiceDate)
                {
                    count += prior.Quantity;
                }
            }

            foreach (var other in invoice.Lines)
            {
                if (other.SutCode == line.SutCode && other.ServiceDate >= start
                    && (other.ServiceDate < line.ServiceDate
                        || (other.ServiceDate == line.ServiceDate && other.LineNo <= line.LineNo)))
                {
                    count += other.Quantity;
                }
            }

            if (count > limit.Count)
            {
                context.Report(
                    Definition,
                    line,
                    InvoiceContext.Number(limit.Count) + "/" + InvoiceContext.Number(limit.Days),
                    InvoiceContext.Number(count));
            }
        }
    }
}

internal sealed class RepeatExaminationRule : IRule
{
    public RuleDefinition Definition => RuleDefinitions.Frq002;

    public void Evaluate(InvoiceContext context)
    {
        var invoice = context.Invoice;
        if (invoice.Provision.ProvisionType == ProvisionType.Emergency)
        {
            return; // Emergency visits are outside the repeat-examination window.
        }

        var days = context.Version.Parameters.ExaminationRepeatDays;
        foreach (var line in invoice.Lines)
        {
            if (context.Procedure(line.SutCode) is not { Kind: ProcedureKind.Examination }
                || context.Doctor(line.DoctorRegistrationNo) is not { } doctor)
            {
                continue;
            }

            var start = line.ServiceDate.AddDays(-(days - 1));
            DateOnly? earlier = null;
            foreach (var prior in invoice.PriorServices)
            {
                if (prior.BranchCode == doctor.BranchCode && prior.ServiceDate >= start && prior.ServiceDate <= line.ServiceDate
                    && context.Procedure(prior.SutCode) is { Kind: ProcedureKind.Examination })
                {
                    earlier = Min(earlier, prior.ServiceDate);
                }
            }

            foreach (var other in invoice.Lines)
            {
                if (other.LineNo != line.LineNo && other.ServiceDate >= start
                    && (other.ServiceDate < line.ServiceDate || (other.ServiceDate == line.ServiceDate && other.LineNo < line.LineNo))
                    && context.Procedure(other.SutCode) is { Kind: ProcedureKind.Examination }
                    && context.Doctor(other.DoctorRegistrationNo)?.BranchCode == doctor.BranchCode)
                {
                    earlier = Min(earlier, other.ServiceDate);
                }
            }

            if (earlier is { } date)
            {
                context.Report(Definition, line, InvoiceContext.Number(days), InvoiceContext.Date(date));
            }
        }
    }

    private static DateOnly Min(DateOnly? a, DateOnly b) => a is { } x && x < b ? x : b;
}
