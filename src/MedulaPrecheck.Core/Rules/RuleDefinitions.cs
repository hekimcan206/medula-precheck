using System.Globalization;

namespace MedulaPrecheck.Rules;

/// <summary>
/// Static metadata of a rule: identity, bilingual rejection reason and fix suggestion. Templates use the
/// placeholders {line}, {code}, {expected} and {actual}. <c>OracleBulk</c> is true when the rule is also implemented
/// set-based in PL/SQL (see ADR 0002).
/// </summary>
public sealed record RuleDefinition(
    string Id,
    RuleCategory Category,
    string TitleTr,
    string TitleEn,
    string ReasonTr,
    string ReasonEn,
    string FixTr,
    string FixEn,
    FixAction FixAction,
    bool OracleBulk,
    string Basis)
{
    public Finding CreateFinding(Severity severity, int? lineNo, string? code, string? expected, string? actual)
    {
        var fixValue = FixAction switch
        {
            FixAction.SetUnitPrice or FixAction.ReduceQuantity or FixAction.AddDiagnosis
                or FixAction.ChangeProvision or FixAction.ChangeDoctor => expected,
            _ => null,
        };

        return new Finding(
            Id,
            Category,
            severity,
            lineNo,
            code,
            expected,
            actual,
            FormatReason(lineNo, code, expected, actual),
            FormatFix(lineNo, code, expected, actual),
            new SuggestedFix(FixAction, lineNo, fixValue));
    }

    public LocalizedText FormatReason(int? line, string? code, string? expected, string? actual) =>
        new(Format(ReasonTr, line, code, expected, actual), Format(ReasonEn, line, code, expected, actual));

    public LocalizedText FormatFix(int? line, string? code, string? expected, string? actual) =>
        new(Format(FixTr, line, code, expected, actual), Format(FixEn, line, code, expected, actual));

    private static string Format(string template, int? line, string? code, string? expected, string? actual) =>
        template
            .Replace("{line}", line?.ToString(CultureInfo.InvariantCulture) ?? "-", StringComparison.Ordinal)
            .Replace("{code}", code ?? "-", StringComparison.Ordinal)
            .Replace("{expected}", expected ?? "-", StringComparison.Ordinal)
            .Replace("{actual}", actual ?? "-", StringComparison.Ordinal);
}

/// <summary>
/// The rule inventory. Each rule models a category of the public SUT / MEDULA structure; the concrete codes,
/// limits and texts are illustrative, not quotations of the official communique.
/// </summary>
public static class RuleDefinitions
{
    public static readonly RuleDefinition NoVersion = new(
        "SYS-001", RuleCategory.Administrative,
        "Fatura tarihinde geçerli SUT sürümü yok", "No SUT version valid on the invoice date",
        "Fatura tarihi ({actual}) için yürürlükte olan bir SUT sürümü bulunamadı; hiçbir kural uygulanamadı.",
        "No SUT version is in force on the invoice date ({actual}); no rule could be applied.",
        "Fatura tarihini kontrol edin; en erken desteklenen sürüm {expected} tarihinde başlar.",
        "Check the invoice date; the earliest supported version starts on {expected}.",
        FixAction.ChangeInvoiceDate, false, "Engine: effective-dated rule sets");

    public static readonly RuleDefinition Mdl001 = new(
        "MDL-001", RuleCategory.Structural,
        "Takip numarası geçersiz", "Invalid provision tracking number",
        "Takip numarası '{actual}' biçim olarak geçersiz; MEDULA hizmet kaydını kabul etmez.",
        "Tracking number '{actual}' is malformed; MEDULA will not accept the service record.",
        "Provizyonu MEDULA'dan yeniden sorgulayıp {expected} biçimindeki takip numarasını faturaya yazın.",
        "Re-query the provision in MEDULA and put the tracking number ({expected}) on the invoice.",
        FixAction.ChangeProvision, false, "MEDULA provision (takip) is the key of every service record");

    public static readonly RuleDefinition Mdl002 = new(
        "MDL-002", RuleCategory.Structural,
        "Hizmet tarihi provizyon dönemi dışında", "Service date outside the provision period",
        "Satır {line} ({code}) hizmet tarihi {actual}, provizyon dönemi {expected} dışında.",
        "Line {line} ({code}) is dated {actual}, outside the provision period {expected}.",
        "Hizmet tarihini {expected} aralığına düzeltin ya da hizmeti ait olduğu provizyona taşıyın.",
        "Correct the service date into {expected} or move the service to the provision it belongs to.",
        FixAction.CorrectField, true, "Services are billed under the provision they were delivered in");

    public static readonly RuleDefinition Mdl003 = new(
        "MDL-003", RuleCategory.Structural,
        "Fatura tarihi hizmetten önce", "Invoice dated before the service or discharge",
        "Fatura tarihi {actual}, son hizmet/taburcu tarihi {expected} öncesinde.",
        "Invoice date {actual} precedes the last service / discharge date {expected}.",
        "Faturayı {expected} veya sonrasına tarihleyin; yatan hastada taburcu olmadan fatura kesmeyin.",
        "Date the invoice on or after {expected}; do not invoice an inpatient stay before discharge.",
        FixAction.ChangeInvoiceDate, false, "Invoices follow completed episodes");

    public static readonly RuleDefinition Mdl004 = new(
        "MDL-004", RuleCategory.Structural,
        "SUT kodu bu sürümde yok", "SUT code not in the version in force",
        "Satır {line}: {code} kodu {expected} sürümünde tanımlı değil.",
        "Line {line}: code {code} is not defined in SUT version {expected}.",
        "Kodu {expected} listesindeki güncel karşılığıyla değiştirin; kaldırılmış kodlar faturalandırılamaz.",
        "Replace the code with its current equivalent in {expected}; withdrawn codes cannot be billed.",
        FixAction.CorrectField, true, "SUT annex lists (EK-2/B, EK-2/C) define billable codes");

    public static readonly RuleDefinition Mdl005 = new(
        "MDL-005", RuleCategory.Structural,
        "Mükerrer hizmet satırı", "Duplicate service line",
        "Satır {line} ({code}), satır {expected} ile aynı gün, aynı hekim ve aynı kodla tekrar girilmiş.",
        "Line {line} ({code}) repeats line {expected}: same code, same day, same physician.",
        "Satır {line}'i silin; birden fazla uygulama yapıldıysa satır {expected}'in adedini artırın.",
        "Remove line {line}; if the service was really repeated, increase the quantity on line {expected}.",
        FixAction.RemoveLine, true, "Duplicate records are a common rejection reason");

    public static readonly RuleDefinition Mdl006 = new(
        "MDL-006", RuleCategory.Structural,
        "Hekim bilgisi eksik/geçersiz", "Physician missing or invalid",
        "Satır {line} ({code}) hekim tescil no '{actual}' faturadaki hekim listesinde yok veya geçersiz.",
        "Line {line} ({code}): physician registration '{actual}' is not on the invoice or is malformed.",
        "Hizmeti yapan hekimi ({expected} biçiminde tescil no) faturaya ekleyin veya satırı doğru hekime bağlayın.",
        "Add the performing physician (registration {expected}) to the invoice or link the line to the right one.",
        FixAction.ChangeDoctor, false, "Each service is attributed to a registered physician");

    public static readonly RuleDefinition Mdl007 = new(
        "MDL-007", RuleCategory.Structural,
        "Tanı kodlaması hatalı", "Diagnosis coding invalid",
        "Tanı '{actual}' ICD-10 biçimine uymuyor veya ana tanı sayısı hatalı ({expected}).",
        "Diagnosis '{actual}' is not valid ICD-10 or the primary-diagnosis count is wrong ({expected}).",
        "Tanıyı ICD-10 biçiminde (ör. E11.9) yazın ve tam olarak bir ana tanı işaretleyin.",
        "Write the diagnosis in ICD-10 form (e.g. E11.9) and mark exactly one primary diagnosis.",
        FixAction.CorrectDiagnosis, false, "MEDULA requires ICD-10 coded diagnoses");

    public static readonly RuleDefinition Prc001 = new(
        "PRC-001", RuleCategory.Pricing,
        "Birim fiyat tarifeden farklı", "Unit price differs from the tariff",
        "Satır {line} ({code}) birim fiyatı {actual} TL, geçerli tarife {expected} TL.",
        "Line {line} ({code}) is priced {actual} TRY; the tariff in force is {expected} TRY.",
        "Birim fiyatı {expected} TL olarak düzeltin.",
        "Set the unit price to {expected} TRY.",
        FixAction.SetUnitPrice, true, "SUT annex point/price lists, valid on the version date");

    public static readonly RuleDefinition Prc002 = new(
        "PRC-002", RuleCategory.Pricing,
        "Günlük adet sınırı aşıldı", "Daily quantity limit exceeded",
        "Satır {line} ({code}) aynı gün toplam {actual} adet; günlük sınır {expected}.",
        "Line {line} ({code}): {actual} units on one day; the daily limit is {expected}.",
        "Aynı gündeki toplam adedi {expected} adede indirin.",
        "Reduce the total for that day to {expected} unit(s).",
        FixAction.ReduceQuantity, true, "Per-day billing limits in the SUT annex notes");

    public static readonly RuleDefinition Dem001 = new(
        "DEM-001", RuleCategory.Demographic,
        "İşlem hasta cinsiyetine uygun değil", "Procedure not valid for the patient's sex",
        "Satır {line} ({code}) yalnızca {expected} hastalarda faturalandırılır; hasta {actual}.",
        "Line {line} ({code}) is billable for {expected} patients only; the patient is {actual}.",
        "Hasta cinsiyetini kimlik kaydıyla doğrulayın; doğruysa kodu cinsiyete uygun karşılığıyla değiştirin.",
        "Verify the patient's sex against the identity record; if it is right, use the sex-appropriate code.",
        FixAction.ReviewClinically, true, "Sex-specific procedures");

    public static readonly RuleDefinition Dem002 = new(
        "DEM-002", RuleCategory.Demographic,
        "İşlem hasta yaşına uygun değil", "Procedure not valid for the patient's age",
        "Satır {line} ({code}) {expected} yaş aralığında faturalandırılır; hasta {actual} yaşında.",
        "Line {line} ({code}) is billable for ages {expected}; the patient is {actual}.",
        "Doğum tarihini doğrulayın; yaş aralığı dışındaki uygulama için tıbbi gerekçe raporu ekleyin veya satırı çıkarın.",
        "Verify the birth date; attach a medical justification for an out-of-range use or remove the line.",
        FixAction.ReviewClinically, true, "Age-restricted procedures (paediatric, screening ages)");

    public static readonly RuleDefinition Dem003 = new(
        "DEM-003", RuleCategory.Demographic,
        "Tanı hasta cinsiyetine uygun değil", "Diagnosis not valid for the patient's sex",
        "Tanı {actual} yalnızca {expected} hastalarda kullanılabilir.",
        "Diagnosis {actual} can only be used for {expected} patients.",
        "Tanı kodunu veya hasta cinsiyetini düzeltin.",
        "Correct the diagnosis code or the patient's recorded sex.",
        FixAction.CorrectDiagnosis, false, "ICD-10 sex-specific chapters (e.g. O00-O9A)");

    public static readonly RuleDefinition Cln001 = new(
        "CLN-001", RuleCategory.Clinical,
        "Gerekli tanı yok", "Required diagnosis missing",
        "Satır {line} ({code}) şu tanılardan biri olmadan faturalandırılamaz: {expected}.",
        "Line {line} ({code}) cannot be billed without one of these diagnoses: {expected}.",
        "Endikasyonu destekleyen tanıyı ({expected}) ekleyin; endikasyon yoksa satırı çıkarın.",
        "Add the supporting diagnosis ({expected}); if there is no indication, remove the line.",
        FixAction.AddDiagnosis, true, "Indication-bound services (endikasyon)");

    public static readonly RuleDefinition Cln002 = new(
        "CLN-002", RuleCategory.Clinical,
        "Hekim branşı yetkili değil", "Physician specialty not authorised",
        "Satır {line} ({code}) hekim branşı {actual}; yetkili branşlar: {expected}.",
        "Line {line} ({code}) was performed by specialty {actual}; authorised specialties: {expected}.",
        "Satırı yetkili branştaki ({expected}) hekime bağlayın veya istem/konsültasyon kaydını ekleyin.",
        "Attribute the line to a physician in {expected} or attach the request/consultation record.",
        FixAction.ChangeDoctor, true, "Specialty-restricted services (uzman hekim şartı)");

    public static readonly RuleDefinition Frq001 = new(
        "FRQ-001", RuleCategory.Frequency,
        "Sıklık sınırı aşıldı", "Frequency limit exceeded",
        "Satır {line} ({code}) dönemsel sınırı aşıyor: {actual} adet, izin verilen {expected}.",
        "Line {line} ({code}) exceeds its frequency limit: {actual} units, allowed {expected}.",
        "Önceki uygulama tarihini kontrol edin; sıklık dışı tekrar için gerekçe raporu ekleyin veya satırı çıkarın.",
        "Check the previous date; attach a justification for an early repeat or remove the line.",
        FixAction.RemoveLine, true, "Frequency-limited tests (e.g. HbA1c, screening)");

    public static readonly RuleDefinition Frq002 = new(
        "FRQ-002", RuleCategory.Frequency,
        "10 gün içinde aynı branşta muayene", "Repeat examination in the same specialty",
        "Satır {line} ({code}) muayenesi, {actual} tarihindeki aynı branş muayenesinden sonraki {expected} gün içinde.",
        "Line {line} ({code}): examination within {expected} days of a same-specialty examination on {actual}.",
        "Muayene satırını çıkarın; kontrol muayenesi ayrıca faturalandırılmaz (ek işlemler faturalandırılabilir).",
        "Remove the examination line; a follow-up visit is not billed again (additional procedures still are).",
        FixAction.RemoveLine, false, "Outpatient examination repeat window (SUT 2.4.4.A structure)");

    public static readonly RuleDefinition Pkg001 = new(
        "PKG-001", RuleCategory.Package,
        "Paket kapsamındaki hizmet ayrıca faturalandırılmış", "Service included in a package billed separately",
        "Satır {line} ({code}) {expected} paketinin kapsamında; ayrıca faturalandırılamaz.",
        "Line {line} ({code}) is included in package {expected} and cannot be billed separately.",
        "Satır {line}'i çıkarın; bedel {expected} paket fiyatına dahildir.",
        "Remove line {line}; its cost is included in the price of package {expected}.",
        FixAction.RemoveLine, true, "Diagnosis-based packages (EK-2/C) absorb their components");

    public static readonly RuleDefinition Pkg002 = new(
        "PKG-002", RuleCategory.Package,
        "Birlikte faturalandırılamayan işlemler", "Codes that cannot be billed together",
        "Satır {line} ({code}), aynı gün {expected} ile birlikte faturalandırılamaz.",
        "Line {line} ({code}) cannot be billed on the same day as {expected}.",
        "Satır {line}'i çıkarın; {expected} işlemi bu hizmeti kapsar.",
        "Remove line {line}; {expected} already covers it.",
        FixAction.RemoveLine, true, "Combined-billing prohibitions in annex notes");

    public static readonly RuleDefinition Pkg003 = new(
        "PKG-003", RuleCategory.Package,
        "Yatarak tedavi gerektiren işlem ayaktan faturalandırılmış", "Inpatient-only service on an outpatient provision",
        "Satır {line} ({code}) yalnızca yatarak tedavide faturalandırılır; provizyon {actual}.",
        "Line {line} ({code}) is billable in inpatient care only; the provision is {actual}.",
        "Provizyon tedavi türünü {expected} olarak düzeltin veya hizmeti yatış provizyonuna taşıyın.",
        "Change the provision treatment type to {expected} or move the service to the inpatient provision.",
        FixAction.ChangeProvision, true, "Treatment-setting restrictions");

    public static readonly RuleDefinition Pkg004 = new(
        "PKG-004", RuleCategory.Package,
        "Yatak günü yatış süresini aşıyor", "Bed-days exceed the length of stay",
        "Satır {line} ({code}) toplam {actual} yatak günü; yatış süresi {expected} gün.",
        "Line {line} ({code}) bills {actual} bed-days; the length of stay is {expected} day(s).",
        "Yatak günü adedini {expected} olarak düzeltin.",
        "Set the bed-day quantity to {expected}.",
        FixAction.ReduceQuantity, false, "Bed-day billing tied to admission and discharge dates");

    public static readonly RuleDefinition Prv001 = new(
        "PRV-001", RuleCategory.Provision,
        "Kaza provizyonunda dış neden kodu yok", "Accident provision without an external-cause code",
        "Provizyon türü {actual}, ancak dış neden tanısı ({expected}) yok.",
        "Provision type is {actual} but there is no external-cause diagnosis ({expected}).",
        "Kazanın dış neden kodunu ({expected}) ikincil tanı olarak ekleyin; kaza değilse provizyon türünü düzeltin.",
        "Add the external-cause code ({expected}) as a secondary diagnosis, or correct the provision type.",
        FixAction.AddDiagnosis, false, "Work/traffic accident cases are recoverable from third parties");

    public static readonly RuleDefinition Adm001 = new(
        "ADM-001", RuleCategory.Administrative,
        "Fatura geç düzenlenmiş", "Invoice issued late",
        "Fatura, son hizmetten {actual} gün sonra düzenlenmiş; sınır {expected} gün.",
        "The invoice was issued {actual} days after the last service; the limit is {expected} days.",
        "Dönem faturasına zamanında ekleyin; gecikme gerekçesini fatura ekinde belirtin.",
        "Include it in the period invoice on time; state the reason for the delay in the invoice annex.",
        FixAction.ChangeInvoiceDate, false, "Monthly invoicing deadlines");

    public static IReadOnlyList<RuleDefinition> All { get; } =
    [
        Mdl001, Mdl002, Mdl003, Mdl004, Mdl005, Mdl006, Mdl007,
        Prc001, Prc002,
        Dem001, Dem002, Dem003,
        Cln001, Cln002,
        Frq001, Frq002,
        Pkg001, Pkg002, Pkg003, Pkg004,
        Prv001,
        Adm001,
    ];

    public static RuleDefinition? Find(string id) => All.FirstOrDefault(r => r.Id == id);
}
