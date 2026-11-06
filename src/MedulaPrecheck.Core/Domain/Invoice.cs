namespace MedulaPrecheck.Domain;

/// <summary>Patient sex as MEDULA records it (K = kadın / female, E = erkek / male).</summary>
public enum Gender
{
    Female,
    Male,
}

/// <summary>Treatment setting of the provision (ayaktan / yatarak / günübirlik).</summary>
public enum TreatmentType
{
    Outpatient,
    Inpatient,
    DayCase,
}

/// <summary>Provision reason; accident and forensic cases carry extra coding obligations.</summary>
public enum ProvisionType
{
    Normal,
    Emergency,
    WorkAccident,
    TrafficAccident,
    Forensic,
}

/// <summary>
/// Patient demographics needed by the rules. <see cref="PatientRef"/> is an opaque synthetic token rather than a
/// national id: the pre-check needs age and sex, not identity (data minimisation, KVKK art. 4).
/// </summary>
public sealed record Patient(string PatientRef, string FullName, DateOnly BirthDate, Gender Gender)
{
    /// <summary>Age in completed years on <paramref name="date"/>; identical to the PL/SQL YYYYMMDD arithmetic.</summary>
    public int AgeOn(DateOnly date)
    {
        var age = date.Year - BirthDate.Year;
        if (date.Month < BirthDate.Month || (date.Month == BirthDate.Month && date.Day < BirthDate.Day))
        {
            age--;
        }

        return age;
    }
}

/// <summary>
/// The MEDULA provision (provizyon) and its tracking number (takip numarası). Every billed service must belong to
/// a provision and fall inside its admission/discharge window.
/// </summary>
public sealed record Provision(
    string TrackingNo,
    DateOnly AdmissionDate,
    DateOnly? DischargeDate,
    TreatmentType TreatmentType,
    ProvisionType ProvisionType,
    string BranchCode);

/// <summary>A physician on the invoice, identified by a (synthetic) registration number and specialty branch.</summary>
public sealed record Doctor(string RegistrationNo, string BranchCode);

/// <summary>An ICD-10 diagnosis attached to the provision.</summary>
public sealed record Diagnosis(string IcdCode, bool IsPrimary);

/// <summary>A billed service line (hizmet kalemi).</summary>
public sealed record ServiceLine(
    int LineNo,
    string SutCode,
    DateOnly ServiceDate,
    int Quantity,
    decimal UnitPrice,
    string DoctorRegistrationNo)
{
    public decimal Amount => Quantity * UnitPrice;
}

/// <summary>
/// A service already billed for the same patient before this invoice (from the hospital's own history).
/// Frequency limits and the repeat-examination rule look across invoices, so the caller supplies this window.
/// </summary>
public sealed record PriorService(string SutCode, DateOnly ServiceDate, int Quantity, string BranchCode);

/// <summary>The invoice (fatura) as it would be sent to SGK through MEDULA.</summary>
public sealed record Invoice(
    string InvoiceNo,
    DateOnly InvoiceDate,
    string FacilityCode,
    Patient Patient,
    Provision Provision,
    IReadOnlyList<Doctor> Doctors,
    IReadOnlyList<Diagnosis> Diagnoses,
    IReadOnlyList<ServiceLine> Lines,
    IReadOnlyList<PriorService> PriorServices)
{
    public decimal Total => Lines.Sum(l => l.Amount);
}
