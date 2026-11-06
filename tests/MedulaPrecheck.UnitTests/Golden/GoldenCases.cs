using MedulaPrecheck.Domain;

namespace MedulaPrecheck.Golden;

/// <summary>An expected finding: rule, line (null = invoice level) and the value the rule should demand.</summary>
public sealed record ExpectedFinding(string RuleId, int? LineNo, string? Expected)
{
    public override string ToString() => $"{RuleId}@{LineNo?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "inv"}={Expected}";
}

/// <summary>
/// A golden case targets one rule. The expected list is the COMPLETE engine output for the invoice, so a
/// fail case also proves the rule does not drag other rules along, and a pass case proves the boundary.
/// </summary>
public sealed record GoldenCase(string Name, string RuleId, bool ShouldFail, Invoice Invoice, IReadOnlyList<ExpectedFinding> Expected)
{
    public override string ToString() => Name;
}

/// <summary>
/// Hand-written golden set: at least one pass and one fail case per rule. Shared by the .NET unit tests and the
/// Oracle parity tests (for the rules the PL/SQL package implements).
/// </summary>
public static class GoldenCases
{
    public static readonly DateOnly Visit = new(2025, 9, 15);

    /// <summary>Clean outpatient internal-medicine visit under SUT-2025.07: exam, CBC, HbA1c for a diabetic.</summary>
    public static Invoice Outpatient() => new(
        "GLD-OUT",
        new DateOnly(2025, 9, 20),
        "SYN100",
        new Patient("P00000001", "Ayşe Yılmaz", new DateOnly(1980, 3, 15), Gender.Female),
        new Provision("A1B2C3D4", Visit, null, TreatmentType.Outpatient, ProvisionType.Normal, "1100"),
        [new Doctor("100001", "1100")],
        [new Diagnosis("E11.9", true)],
        [
            new ServiceLine(1, "900101", Visit, 1, 120.00m, "100001"),
            new ServiceLine(2, "901001", Visit, 1, 30.80m, "100001"),
            new ServiceLine(3, "901002", Visit, 1, 69.10m, "100001"),
        ],
        []);

    /// <summary>Clean appendectomy package stay (3 nights) under SUT-2025.07.</summary>
    public static Invoice Inpatient() => new(
        "GLD-INP",
        new DateOnly(2025, 9, 20),
        "SYN100",
        new Patient("P00000002", "Mehmet Demir", new DateOnly(1990, 6, 1), Gender.Male),
        new Provision("Z9Y8X7W6", Visit, Visit.AddDays(3), TreatmentType.Inpatient, ProvisionType.Normal, "1200"),
        [new Doctor("200001", "1200")],
        [new Diagnosis("K35.8", true)],
        [
            new ServiceLine(1, "903007", Visit, 1, 14200.00m, "200001"),
            new ServiceLine(2, "904001", Visit, 3, 650.00m, "200001"),
        ],
        []);

    /// <summary>The same stay one version later (SUT-2026.01), where the bed-day rule exists.</summary>
    public static Invoice Inpatient2026()
    {
        var admission = new DateOnly(2026, 1, 5);
        var inv = Inpatient();
        return inv with
        {
            InvoiceNo = "GLD-INP26",
            InvoiceDate = new DateOnly(2026, 1, 10),
            Provision = inv.Provision with { AdmissionDate = admission, DischargeDate = admission.AddDays(3) },
            Lines = [.. inv.Lines.Select(l => l with { ServiceDate = admission })],
        };
    }

    public static IReadOnlyList<GoldenCase> All { get; } = Build();

    private static ServiceLine Line(int no, string code, decimal price, string doctor = "100001", int qty = 1, DateOnly? date = null) =>
        new(no, code, date ?? Visit, qty, price, doctor);

    private static Invoice AddLines(this Invoice inv, params ServiceLine[] lines) => inv with { Lines = [.. inv.Lines, .. lines] };

    private static Invoice MapLine(this Invoice inv, int no, Func<ServiceLine, ServiceLine> map) =>
        inv with { Lines = [.. inv.Lines.Select(l => l.LineNo == no ? map(l) : l)] };

    private static List<GoldenCase> Build()
    {
        var o = Outpatient();
        var i = Inpatient();
        var cases = new List<GoldenCase>();

        void Pass(string rule, string name, Invoice invoice) => cases.Add(new($"{rule} pass: {name}", rule, false, invoice, []));

        void Fail(string rule, string name, Invoice invoice, params ExpectedFinding[] expected) =>
            cases.Add(new($"{rule} fail: {name}", rule, true, invoice, expected));

        ExpectedFinding F(string rule, int? line, string? expected) => new(rule, line, expected);

        // Structural
        Pass("MDL-001", "8-character tracking number", o);
        Fail("MDL-001", "lower-case short tracking number", o with { Provision = o.Provision with { TrackingNo = "ab-12" } },
            F("MDL-001", null, "[0-9A-Z]{7,10}"));

        Pass("MDL-002", "service on the discharge day", i.MapLine(2, l => l with { ServiceDate = Visit.AddDays(3), Quantity = 3 }));
        Fail("MDL-002", "service the day before admission", o.MapLine(2, l => l with { ServiceDate = Visit.AddDays(-1) }),
            F("MDL-002", 2, "2025-09-15.."));
        Fail("MDL-002", "service after discharge", i.MapLine(2, l => l with { ServiceDate = Visit.AddDays(4) }),
            F("MDL-002", 2, "2025-09-15..2025-09-18"));

        Pass("MDL-003", "invoice on the service day", o with { InvoiceDate = Visit });
        Fail("MDL-003", "invoice before the service", o with { InvoiceDate = Visit.AddDays(-1) },
            F("MDL-003", null, "2025-09-15"));
        Fail("MDL-003", "inpatient invoiced before discharge", i with { Provision = i.Provision with { DischargeDate = null } },
            F("MDL-003", null, "discharge"));

        Pass("MDL-004", "every code is in SUT-2025.07", o);
        Fail("MDL-004", "unknown code", o.AddLines(Line(4, "999999", 10m)),
            F("MDL-004", 4, "SUT-2025.07"));

        Pass("MDL-005", "same code on another day", o.AddLines(Line(4, "901001", 30.80m, date: Visit.AddDays(1))));
        Fail("MDL-005", "same code, day and physician twice", o.AddLines(Line(4, "901001", 30.80m)),
            F("MDL-005", 4, "2"));

        Pass("MDL-006", "physician on the invoice", o);
        Fail("MDL-006", "malformed registration", o.MapLine(2, l => l with { DoctorRegistrationNo = "123" }),
            F("MDL-006", 2, @"\d{6}"));
        Fail("MDL-006", "registration not on the invoice", o.MapLine(2, l => l with { DoctorRegistrationNo = "999999" }),
            F("MDL-006", 2, @"\d{6}"));

        Pass("MDL-007", "one primary ICD-10 diagnosis plus a secondary", o with { Diagnoses = [new("E11.9", true), new("I10", false)] });
        Fail("MDL-007", "malformed secondary diagnosis", o with { Diagnoses = [new("E11.9", true), new("XYZ", false)] },
            F("MDL-007", null, "A00.0"));
        Fail("MDL-007", "two primary diagnoses", o with { Diagnoses = [new("E11.9", true), new("I10", true)] },
            F("MDL-007", null, "1"));

        // Pricing
        Pass("PRC-001", "tariff price of the version in force", o);
        Fail("PRC-001", "previous version's price", o.MapLine(2, l => l with { UnitPrice = 28.50m }),
            F("PRC-001", 2, "30.80"));

        Pass("PRC-002", "exactly the daily limit", o.MapLine(2, l => l with { Quantity = 2 }));
        Fail("PRC-002", "one unit over the daily limit", o.MapLine(2, l => l with { Quantity = 3 }),
            F("PRC-002", 2, "2"));

        // Demographic
        Pass("DEM-001", "PSA for a male patient", (o with { Patient = o.Patient with { Gender = Gender.Male } }).AddLines(Line(4, "901003", 72.00m)));
        Fail("DEM-001", "PSA for a female patient", o.AddLines(Line(4, "901003", 72.00m)),
            F("DEM-001", 4, "Male"));

        Pass("DEM-002", "newborn screening at age 0", (o with { Patient = o.Patient with { BirthDate = new DateOnly(2025, 9, 1) } }).AddLines(Line(4, "901009", 140.00m)));
        Fail("DEM-002", "newborn screening for an adult", o.AddLines(Line(4, "901009", 140.00m)),
            F("DEM-002", 4, "-0"));
        Pass("DEM-002", "DEXA on the 40th birthday", (o with { Patient = o.Patient with { BirthDate = new DateOnly(1985, 9, 15) }, Doctors = [.. o.Doctors, new Doctor("100002", "1600")] })
            .AddLines(Line(4, "902006", 380.00m, "100002")));
        Fail("DEM-002", "DEXA the day before the 40th birthday", (o with { Patient = o.Patient with { BirthDate = new DateOnly(1985, 9, 16) }, Doctors = [.. o.Doctors, new Doctor("100002", "1600")] })
            .AddLines(Line(4, "902006", 380.00m, "100002")),
            F("DEM-002", 4, "40-"));

        Pass("DEM-003", "male-only diagnosis for a male", o with { Patient = o.Patient with { Gender = Gender.Male }, Diagnoses = [new("E11.9", true), new("N40", false)] });
        Fail("DEM-003", "male-only diagnosis for a female", o with { Diagnoses = [new("E11.9", true), new("N40", false)] },
            F("DEM-003", null, "Male"));

        // Clinical
        Pass("CLN-001", "HbA1c with a diabetes diagnosis", o);
        Fail("CLN-001", "HbA1c without an indication", o with { Diagnoses = [new("I10", true)] },
            F("CLN-001", 3, "E10, E11, E13, O24, R73"));

        Pass("CLN-002", "ultrasound by a radiologist", (o with { Doctors = [.. o.Doctors, new Doctor("100002", "1600")] }).AddLines(Line(4, "902003", 210.00m, "100002")));
        Fail("CLN-002", "ultrasound by an internist", o.AddLines(Line(4, "902003", 210.00m)),
            F("CLN-002", 4, "1600"));

        // Frequency
        Pass("FRQ-001", "second HbA1c in 180 days; third one fell out of the window", o with
        {
            PriorServices = [new("901002", new DateOnly(2025, 3, 19), 1, "1100"), new("901002", new DateOnly(2025, 8, 1), 1, "1100")],
        });
        Fail("FRQ-001", "third HbA1c inside 180 days", o with
        {
            PriorServices = [new("901002", new DateOnly(2025, 3, 20), 1, "1100"), new("901002", new DateOnly(2025, 8, 1), 1, "1100")],
        },
            F("FRQ-001", 3, "2/180"));

        Pass("FRQ-002", "previous same-branch exam 10 days earlier", o with { PriorServices = [new("900101", Visit.AddDays(-10), 1, "1100")] });
        Pass("FRQ-002", "emergency provision is exempt", o with
        {
            Provision = o.Provision with { ProvisionType = ProvisionType.Emergency },
            PriorServices = [new("900101", Visit.AddDays(-3), 1, "1100")],
        });
        Fail("FRQ-002", "same-branch exam 9 days earlier", o with { PriorServices = [new("900101", Visit.AddDays(-9), 1, "1100")] },
            F("FRQ-002", 1, "10"));

        // Package
        Pass("PKG-001", "package with bed-days only", i);
        Fail("PKG-001", "CBC billed inside the appendectomy package", i.AddLines(Line(3, "901001", 30.80m, "200001")),
            F("PKG-001", 3, "903007"));

        Pass("PKG-002", "emergency and specialist exams on different days", (o with { Doctors = [.. o.Doctors, new Doctor("100005", "1900")] })
            .AddLines(Line(4, "900102", 95.00m, "100005", date: Visit.AddDays(1))));
        Fail("PKG-002", "emergency and specialist exams on the same day", (o with { Doctors = [.. o.Doctors, new Doctor("100005", "1900")] })
            .AddLines(Line(4, "900102", 95.00m, "100005")),
            F("PKG-002", 1, "900102"));

        Pass("PKG-003", "bed-day on an inpatient provision", i);
        Fail("PKG-003", "bed-day on an outpatient provision", o.AddLines(Line(4, "904001", 650.00m)),
            F("PKG-003", 4, "Inpatient"));

        var i26 = Inpatient2026();
        Pass("PKG-004", "bed-days equal the length of stay", i26);
        Fail("PKG-004", "one bed-day too many", i26.MapLine(2, l => l with { Quantity = 4 }),
            F("PKG-004", 2, "3"));

        // Provision
        Pass("PRV-001", "traffic accident with an external-cause code", o with
        {
            Provision = o.Provision with { ProvisionType = ProvisionType.TrafficAccident },
            Diagnoses = [new("E11.9", true), new("V43.5", false)],
        });
        Fail("PRV-001", "traffic accident without an external-cause code", o with { Provision = o.Provision with { ProvisionType = ProvisionType.TrafficAccident } },
            F("PRV-001", null, "V01-Y98"));

        // Administrative
        Pass("ADM-001", "invoiced exactly 60 days after the service", o with { InvoiceDate = Visit.AddDays(60) });
        Fail("ADM-001", "invoiced 61 days after the service", o with { InvoiceDate = Visit.AddDays(61) },
            F("ADM-001", null, "60"));

        return cases;
    }
}
