using System.Globalization;
using Bogus;
using MedulaPrecheck.Domain;
using MedulaPrecheck.Sut;

namespace MedulaPrecheck.Synthetic;

/// <summary>A generated invoice and the rule whose defect was injected into it (null for a clean invoice).</summary>
public sealed record GeneratedInvoice(Invoice Invoice, string? InjectedRuleId);

/// <summary>
/// Deterministic synthetic invoice generator (Bogus, Turkish locale). It first builds an invoice that is clean
/// under the SUT version valid on its date, then optionally injects exactly one labelled defect. The labels let
/// tests prove that every injected defect is caught, and give the benchmark a realistic rejection mix.
/// No output corresponds to a real person, physician, facility or claim.
/// </summary>
public sealed class InvoiceGenerator
{
    private const string TrackingAlphabet = "0123456789ABCDEFGHJKLMNPRSTUVYZ";

    private static readonly string[] OutpatientBranches = ["1100", "1100", "2000", "1400", "1300", "1500", "2200"];

    private static readonly string[] InjectableRules =
    [
        "MDL-001", "MDL-002", "MDL-003", "MDL-004", "MDL-005", "MDL-006", "MDL-007",
        "PRC-001", "PRC-002", "DEM-001", "DEM-002", "DEM-003", "CLN-001", "CLN-002",
        "FRQ-001", "FRQ-002", "PKG-001", "PKG-002", "PKG-003", "PKG-004", "PRV-001", "ADM-001",
    ];

    private readonly SutCatalog _catalog;
    private readonly Faker _faker;
    private int _sequence;

    public InvoiceGenerator(SutCatalog catalog, int seed)
    {
        _catalog = catalog;
        _faker = new Faker("tr") { Random = new Randomizer(seed) };
    }

    public IReadOnlyList<GeneratedInvoice> Generate(int count, double defectRate, DateOnly from, DateOnly to)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var result = new List<GeneratedInvoice>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(GenerateOne(defectRate, from, to));
        }

        return result;
    }

    public GeneratedInvoice GenerateOne(double defectRate, DateOnly from, DateOnly to)
    {
        var invoiceDate = DateOnly.FromDateTime(_faker.Date.Between(
            from.ToDateTime(TimeOnly.MinValue), to.ToDateTime(TimeOnly.MinValue)));
        var version = _catalog.ResolveFor(invoiceDate)
            ?? throw new ArgumentException("The date range starts before the first SUT version.", nameof(from));

        var builder = _faker.Random.Int(0, 9) switch
        {
            < 7 => Outpatient(version, invoiceDate),
            < 9 => Surgery(version, invoiceDate),
            _ => Delivery(version, invoiceDate),
        };

        var invoice = builder.Build();
        if (!_faker.Random.Bool((float)defectRate))
        {
            return new GeneratedInvoice(invoice, null);
        }

        // Pick defects until one applies to this invoice shape (e.g. PKG-001 needs a package invoice).
        foreach (var ruleId in _faker.Random.Shuffle(InjectableRules))
        {
            if (!version.ActiveRules.ContainsKey(ruleId))
            {
                continue;
            }

            if (Inject(ruleId, invoice, version) is { } defective)
            {
                return new GeneratedInvoice(defective, ruleId);
            }
        }

        return new GeneratedInvoice(invoice, null);
    }

    private InvoiceBuilder NewBuilder(SutVersion version, DateOnly invoiceDate, TreatmentType treatment, string branch)
    {
        _sequence++;
        return new InvoiceBuilder(version)
        {
            InvoiceNo = string.Create(CultureInfo.InvariantCulture, $"FT{invoiceDate:yyMM}-{_sequence:D6}"),
            FacilityCode = "SYN" + _faker.Random.Int(100, 999).ToString(CultureInfo.InvariantCulture),
            TrackingNo = _faker.Random.String2(8, TrackingAlphabet),
            Treatment = treatment,
            Branch = branch,
        };
    }

    private Patient NewPatient(DateOnly onDate, int minAge, int maxAge, Gender? gender = null)
    {
        var sex = gender ?? (_faker.Random.Bool() ? Gender.Female : Gender.Male);
        var age = _faker.Random.Int(minAge, maxAge);
        var birth = onDate.AddYears(-age).AddDays(-_faker.Random.Int(1, 360));
        var name = _faker.Name.FullName(sex == Gender.Female ? Bogus.DataSets.Name.Gender.Female : Bogus.DataSets.Name.Gender.Male);
        return new Patient("P" + _faker.Random.Int(10_000_000, 99_999_999).ToString(CultureInfo.InvariantCulture), name, birth, sex);
    }

    private string NewDoctorNo() => _faker.Random.Int(100_000, 999_999).ToString(CultureInfo.InvariantCulture);

    private InvoiceBuilder Outpatient(SutVersion version, DateOnly invoiceDate)
    {
        var emergency = _faker.Random.Int(0, 9) == 0;
        var branch = emergency ? "1900" : _faker.PickRandom(OutpatientBranches);
        var serviceDate = invoiceDate.AddDays(-_faker.Random.Int(0, 20));
        var b = NewBuilder(version, invoiceDate, TreatmentType.Outpatient, branch);
        b.Admission = serviceDate;
        b.InvoiceDate = invoiceDate;
        b.ProvisionType = emergency ? ProvisionType.Emergency : ProvisionType.Normal;
        b.Patient = branch switch
        {
            "1500" => NewPatient(serviceDate, 1, 17),
            "1300" => NewPatient(serviceDate, 18, 70, Gender.Female),
            "1400" => NewPatient(serviceDate, 30, 85, Gender.Male),
            _ => NewPatient(serviceDate, 18, 90),
        };

        var doctor = b.AddDoctor(NewDoctorNo(), branch);
        b.AddDiagnosis(PrimaryDiagnosisFor(branch, b.Patient.Gender), primary: true);
        var exam = emergency ? "900102" : branch == "1500" ? "900103" : "900101";
        b.AddLine(exam, serviceDate, 1, doctor);

        // A few additional tests/procedures, each valid for this patient, physician and version.
        var candidates = version.Procedures.Values
            .Where(p => p.Kind is ProcedureKind.Laboratory or ProcedureKind.Imaging or ProcedureKind.Procedure)
            .Where(p => !p.InpatientOnly && p.Kind != ProcedureKind.Package)
            .Where(p => p.Gender is null || p.Gender == b.Patient.Gender)
            .Where(p => Fits(p, b.Patient.AgeOn(serviceDate)))
            .Where(p => p.Code is not ("903002" or "903003")) // anaesthesia only in surgical scenarios
            .OrderBy(p => p.Code, StringComparer.Ordinal)
            .ToList();

        foreach (var p in _faker.Random.ListItems(candidates, _faker.Random.Int(0, 3)))
        {
            if (b.HasIncompatibility(p.Code, serviceDate))
            {
                continue;
            }

            var performer = p.AllowedBranches.Count == 0 || p.AllowedBranches.Contains(branch)
                ? doctor
                : b.AddDoctor(NewDoctorNo(), p.AllowedBranches[0]);
            foreach (var prefix in p.RequiredIcdPrefixes.Take(1))
            {
                if (!b.HasDiagnosisWithPrefix(p.RequiredIcdPrefixes))
                {
                    b.AddDiagnosis(IcdFromPrefix(prefix), primary: false);
                }
            }

            b.AddLine(p.Code, serviceDate, 1, performer);
        }

        // History far outside every frequency window: realistic, and keeps the clean invoice clean.
        if (_faker.Random.Bool(0.3f))
        {
            b.Priors.Add(new PriorService(exam, serviceDate.AddDays(-_faker.Random.Int(800, 1200)), 1, branch));
        }

        return b;
    }

    private InvoiceBuilder Surgery(SutVersion version, DateOnly invoiceDate)
    {
        var appendectomy = _faker.Random.Bool();
        var stay = _faker.Random.Int(1, 5);
        var discharge = invoiceDate.AddDays(-_faker.Random.Int(0, 15));
        var admission = discharge.AddDays(-stay);
        var b = NewBuilder(version, invoiceDate, TreatmentType.Inpatient, "1200");
        b.Admission = admission;
        b.Discharge = discharge;
        b.InvoiceDate = invoiceDate;
        b.Patient = NewPatient(admission, 18, 85);
        var surgeon = b.AddDoctor(NewDoctorNo(), "1200");
        b.AddDiagnosis(appendectomy ? "K35.8" : "K80.2", primary: true);
        b.AddLine(appendectomy ? "903007" : "903008", admission, 1, surgeon);
        b.AddLine("904001", admission, stay, surgeon);
        if (_faker.Random.Bool())
        {
            b.AddLine("903009", admission, 1, surgeon); // ECG is outside both packages
        }

        return b;
    }

    private InvoiceBuilder Delivery(SutVersion version, DateOnly invoiceDate)
    {
        var caesarean = _faker.Random.Bool(0.4f);
        var stay = _faker.Random.Int(1, 3);
        var discharge = invoiceDate.AddDays(-_faker.Random.Int(0, 15));
        var admission = discharge.AddDays(-stay);
        var b = NewBuilder(version, invoiceDate, TreatmentType.Inpatient, "1300");
        b.Admission = admission;
        b.Discharge = discharge;
        b.InvoiceDate = invoiceDate;
        b.Patient = NewPatient(admission, 18, 44, Gender.Female);
        var obstetrician = b.AddDoctor(NewDoctorNo(), "1300");
        b.AddDiagnosis(caesarean ? "O82.0" : "O80.0", primary: true);
        b.AddLine(caesarean ? "903005" : "903006", admission, 1, obstetrician);
        b.AddLine("904001", admission, stay, obstetrician);
        b.AddLine("901001", admission, 1, obstetrician);
        return b;
    }

    private string PrimaryDiagnosisFor(string branch, Gender gender) => branch switch
    {
        "1100" => _faker.PickRandom("E11.9", "I10", "K21.0", "J06.9"),
        "2000" => _faker.PickRandom("I10", "I25.1", "R07.4"),
        "1400" => _faker.PickRandom("N40", "N20.0", "R31"),
        "1300" => _faker.PickRandom("N92.0", "Z34.0", "N76.0"),
        "1500" => _faker.PickRandom("J06.9", "A09", "J20.9"),
        "2200" => _faker.PickRandom("M54.5", "S93.4", "M17.1"),
        "1900" => _faker.PickRandom("R10.4", "S01.8", "R50.9"),
        _ => gender == Gender.Female ? "R51" : "R51",
    };

    private static bool Fits(ProcedureDefinition p, int age) =>
        (p.MinAge is null || age >= p.MinAge) && (p.MaxAge is null || age <= p.MaxAge);

    private static string IcdFromPrefix(string prefix) => prefix.Length == 1 ? prefix + "26.8" : prefix + ".9";

    private Invoice? Inject(string ruleId, Invoice inv, SutVersion version)
    {
        var lines = inv.Lines;
        var first = lines[0];
        var lastDate = lines.Max(l => l.ServiceDate);
        var doctor = inv.Doctors[0];
        var nextLine = lines.Max(l => l.LineNo) + 1;
        var age = inv.Patient.AgeOn(first.ServiceDate);

        Invoice AddLine(string code, int qty = 1, string? doctorNo = null, DateOnly? date = null) => inv with
        {
            Lines = [.. lines, new ServiceLine(nextLine, code, date ?? first.ServiceDate, qty,
                version.Procedures.TryGetValue(code, out var p) ? p.Price : 100m, doctorNo ?? doctor.RegistrationNo)],
        };

        Invoice ReplaceLine(ServiceLine line, ServiceLine replacement) => inv with
        {
            Lines = [.. lines.Select(l => l.LineNo == line.LineNo ? replacement : l)],
        };

        switch (ruleId)
        {
            case "MDL-001":
                return inv with { Provision = inv.Provision with { TrackingNo = "tk-" + _faker.Random.Int(10, 99).ToString(CultureInfo.InvariantCulture) } };
            case "MDL-002":
                return ReplaceLine(first, first with { ServiceDate = inv.Provision.AdmissionDate.AddDays(-1) });
            case "MDL-003":
                return inv.Provision.TreatmentType == TreatmentType.Inpatient
                    ? inv with { Provision = inv.Provision with { DischargeDate = null } }
                    : inv with { InvoiceDate = lastDate.AddDays(-1) };
            case "MDL-004":
                return AddLine("999999");
            case "MDL-005":
                return inv with { Lines = [.. lines, first with { LineNo = nextLine }] };
            case "MDL-006":
                return ReplaceLine(first, first with { DoctorRegistrationNo = "000000" });
            case "MDL-007":
                return inv with { Diagnoses = [.. inv.Diagnoses, new Diagnosis("INVALID", false)] };
            case "PRC-001":
                return ReplaceLine(first, first with { UnitPrice = first.UnitPrice + 10m });
            case "PRC-002":
                var limited = lines.FirstOrDefault(l => version.Procedures.TryGetValue(l.SutCode, out var p) && p.MaxPerDay is not null);
                return limited is null ? null
                    : ReplaceLine(limited, limited with { Quantity = version.Procedures[limited.SutCode].MaxPerDay!.Value + 1 });
            case "DEM-001":
                return inv.Patient.Gender == Gender.Female ? AddLine("901003") : AddLine("901004");
            case "DEM-002":
                var newborn = version.Procedures.ContainsKey("901012") ? "901012" : "901009";
                return age > 0 ? AddLine(newborn) : null;
            case "DEM-003":
                return inv with
                {
                    Diagnoses = [.. inv.Diagnoses, new Diagnosis(inv.Patient.Gender == Gender.Female ? "N40" : "O80.0", false)],
                };
            case "CLN-001":
                return lines.Any(l => l.SutCode == "901010") || inv.Diagnoses.Any(d => d.IcdCode[0] is 'D' or 'E' or 'G' or 'K')
                    ? null
                    : AddLine("901010");
            case "CLN-002":
                return inv.Doctors.Any(d => d.BranchCode == "1600") ? null : AddLine("902005");
            case "FRQ-001":
                if (lines.Any(l => l.SutCode == "901005"))
                {
                    return null;
                }

                var withTsh = AddLine("901005");
                return withTsh with
                {
                    PriorServices = [.. inv.PriorServices, new PriorService("901005", first.ServiceDate.AddDays(-30), 2, doctor.BranchCode)],
                };
            case "FRQ-002":
                if (inv.Provision.ProvisionType == ProvisionType.Emergency
                    || !version.Procedures.TryGetValue(first.SutCode, out var exam) || exam.Kind != ProcedureKind.Examination)
                {
                    return null;
                }

                return inv with
                {
                    PriorServices = [.. inv.PriorServices, new PriorService(first.SutCode, first.ServiceDate.AddDays(-3), 1, doctor.BranchCode)],
                };
            case "PKG-001":
                return version.Procedures.TryGetValue(first.SutCode, out var package) && package.Kind == ProcedureKind.Package
                    ? AddLine(package.PackageContents.First(c => c is not "903003" and not "902003"))
                    : null;
            case "PKG-002":
                if (first.SutCode != "900101" || lines.Any(l => l.SutCode == "900102") || inv.Doctors.Any(d => d.RegistrationNo == "190190"))
                {
                    return null;
                }

                // An emergency examination on the same day absorbs the specialist examination.
                var er = new Doctor("190190", "1900");
                return inv with
                {
                    Doctors = [.. inv.Doctors, er],
                    Lines = [.. lines, new ServiceLine(nextLine, "900102", first.ServiceDate, 1, version.Procedures["900102"].Price, er.RegistrationNo)],
                };
            case "PKG-003":
                return inv.Provision.TreatmentType == TreatmentType.Inpatient ? null : AddLine("904001");
            case "PKG-004":
                var bed = lines.FirstOrDefault(l => l.SutCode == "904001");
                return bed is null || inv.Provision.TreatmentType != TreatmentType.Inpatient
                    ? null
                    : ReplaceLine(bed, bed with { Quantity = bed.Quantity + 1 });
            case "PRV-001":
                return inv with { Provision = inv.Provision with { ProvisionType = ProvisionType.TrafficAccident } };
            case "ADM-001":
                return inv with { InvoiceDate = lastDate.AddDays(version.Parameters.MaxInvoiceLagDays + 5) };
            default:
                return null;
        }
    }

    /// <summary>Mutable helper used only while composing one clean invoice.</summary>
    private sealed class InvoiceBuilder(SutVersion version)
    {
        private readonly List<Doctor> _doctors = [];
        private readonly List<Diagnosis> _diagnoses = [];
        private readonly List<ServiceLine> _lines = [];

        public required string InvoiceNo { get; init; }

        public required string FacilityCode { get; init; }

        public required string TrackingNo { get; init; }

        public required TreatmentType Treatment { get; init; }

        public required string Branch { get; init; }

        public ProvisionType ProvisionType { get; set; } = ProvisionType.Normal;

        public DateOnly Admission { get; set; }

        public DateOnly? Discharge { get; set; }

        public DateOnly InvoiceDate { get; set; }

        public Patient Patient { get; set; } = null!;

        public List<PriorService> Priors { get; } = [];

        public Doctor AddDoctor(string registrationNo, string branch)
        {
            var existing = _doctors.FirstOrDefault(d => d.BranchCode == branch);
            while (_doctors.Any(d => d.RegistrationNo == registrationNo))
            {
                // Registration numbers are random; keep them unique within the invoice.
                registrationNo = (int.Parse(registrationNo, CultureInfo.InvariantCulture) % 899_999 + 100_001).ToString(CultureInfo.InvariantCulture);
            }

            if (existing is not null)
            {
                return existing;
            }

            var doctor = new Doctor(registrationNo, branch);
            _doctors.Add(doctor);
            return doctor;
        }

        public void AddDiagnosis(string icd, bool primary) => _diagnoses.Add(new Diagnosis(icd, primary));

        public bool HasDiagnosisWithPrefix(IReadOnlyList<string> prefixes) =>
            _diagnoses.Any(d => prefixes.Any(p => d.IcdCode.StartsWith(p, StringComparison.Ordinal)));

        public bool HasIncompatibility(string code, DateOnly date) =>
            version.IncompatiblePairs.Any(pair =>
                (pair.Primary == code && _lines.Any(l => l.SutCode == pair.Secondary && l.ServiceDate == date))
                || (pair.Secondary == code && _lines.Any(l => l.SutCode == pair.Primary && l.ServiceDate == date)));

        public void AddLine(string code, DateOnly date, int quantity, Doctor doctor) =>
            _lines.Add(new ServiceLine(_lines.Count + 1, code, date, quantity, version.Procedures[code].Price, doctor.RegistrationNo));

        public Invoice Build() => new(
            InvoiceNo,
            InvoiceDate,
            FacilityCode,
            Patient,
            new Provision(TrackingNo, Admission, Discharge, Treatment, ProvisionType, Branch),
            [.. _doctors],
            [.. _diagnoses],
            [.. _lines],
            [.. Priors]);
    }
}
