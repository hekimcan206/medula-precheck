using System.Globalization;
using System.Net;
using System.Text;
using MedulaPrecheck.Engine;

namespace MedulaPrecheck.Reporting;

public enum ReportFormat
{
    Json,
    Markdown,
    Html,
}

public static class ReportRenderer
{
    // Reports are byte-identical on Windows and Linux; the committed samples are approval files.
    private const string Lf = "\n";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Render(BatchReport report, ReportFormat format) => format switch
    {
        ReportFormat.Json => report.ToJson(),
        ReportFormat.Markdown => Markdown(report),
        ReportFormat.Html => Html(report),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    public static string Markdown(BatchReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var s = report.Summary;
        var sb = new StringBuilder();
        sb.AppendLine("# MEDULA pre-check report / Ön kontrol raporu").AppendLine();
        sb.AppendLine(Inv, $"Generated {report.GeneratedAt:yyyy-MM-dd HH:mm} UTC by `{report.Engine}`. Synthetic data.").AppendLine();
        sb.AppendLine("| Invoices | Clean | Needs review | Would be rejected | Findings | Total (TRY) | At risk (TRY) |");
        sb.AppendLine("|---:|---:|---:|---:|---:|---:|---:|");
        sb.AppendLine(Inv, $"| {s.Invoices} | {s.Clean} | {s.NeedsReview} | {s.Rejected} | {s.Findings} | {s.TotalAmount:N2} | {s.AmountAtRisk:N2} |");
        sb.AppendLine();

        sb.AppendLine("## Findings by rule").AppendLine();
        sb.AppendLine("| Rule | Kural | Rule (EN) | Count |").AppendLine("|---|---|---|---:|");
        foreach (var r in report.ByRule)
        {
            sb.AppendLine(Inv, $"| `{r.RuleId}` | {r.TitleTr} | {r.TitleEn} | {r.Count} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Invoices with findings").AppendLine();
        foreach (var invoice in report.Invoices.Where(i => i.Findings.Count > 0))
        {
            sb.AppendLine(Inv, $"### {invoice.InvoiceNo} — {invoice.Outcome} ({invoice.SutVersion ?? "no version"}, at risk {invoice.AmountAtRisk:N2} TRY)").AppendLine();
            sb.AppendLine("| Rule | Severity | Line | Code | Neden / Reason | Öneri / Suggestion |");
            sb.AppendLine("|---|---|---:|---|---|---|");
            foreach (var f in invoice.Findings)
            {
                sb.AppendLine(Inv,
                    $"| `{f.RuleId}` | {f.Severity} | {f.LineNo?.ToString(Inv) ?? "-"} | {f.SutCode ?? "-"} | {Cell(f.Reason.Tr)}<br>_{Cell(f.Reason.En)}_ | {Cell(f.Suggestion.Tr)}<br>_{Cell(f.Suggestion.En)}_ |");
            }

            sb.AppendLine();
        }

        return sb.ToString().ReplaceLineEndings(Lf);
    }

    public static string Html(BatchReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var s = report.Summary;
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html lang=\"tr\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine("<title>MEDULA pre-check report</title><style>");
        sb.AppendLine("""
            :root{--fg:#1d2433;--muted:#5b6475;--line:#d9dee7;--bg:#f7f8fa;--err:#b42318;--warn:#b54708;--ok:#067647}
            body{font:14px/1.5 system-ui,-apple-system,Segoe UI,Roboto,sans-serif;color:var(--fg);background:var(--bg);margin:0;padding:24px 16px}
            main{max-width:1100px;margin:0 auto}h1{font-size:22px;margin:0 0 4px}h2{font-size:17px;margin:28px 0 8px}
            .meta{color:var(--muted);margin:0 0 16px}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(140px,1fr));gap:8px}
            .card{background:#fff;border:1px solid var(--line);border-radius:8px;padding:10px 12px}.card b{display:block;font-size:20px}
            table{width:100%;border-collapse:collapse;background:#fff;border:1px solid var(--line);margin-bottom:8px}
            th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line);vertical-align:top}th{background:#eef1f5;font-weight:600}
            td.n{text-align:right;font-variant-numeric:tabular-nums}.en{color:var(--muted);display:block}
            .sev-Error{color:var(--err);font-weight:600}.sev-Warning{color:var(--warn);font-weight:600}.o-Clean{color:var(--ok)}
            details{background:#fff;border:1px solid var(--line);border-radius:8px;margin:6px 0;padding:6px 10px}summary{cursor:pointer}
            code{font:12px ui-monospace,Consolas,monospace}.scroll{overflow-x:auto}
            """);
        sb.AppendLine("</style></head><body><main>");
        sb.AppendLine("<h1>MEDULA ön kontrol raporu <span class=\"en\" style=\"display:inline\">/ pre-check report</span></h1>");
        sb.AppendLine(Inv, $"<p class=\"meta\">Generated {report.GeneratedAt:yyyy-MM-dd HH:mm} UTC by <code>{E(report.Engine)}</code>. All data is synthetic.</p>");
        sb.AppendLine("<div class=\"cards\">");
        Card(sb, "Invoices / Fatura", s.Invoices.ToString(Inv));
        Card(sb, "Clean / Temiz", s.Clean.ToString(Inv));
        Card(sb, "Needs review / İnceleme", s.NeedsReview.ToString(Inv));
        Card(sb, "Would be rejected / Ret", s.Rejected.ToString(Inv));
        Card(sb, "Total / Toplam (TRY)", s.TotalAmount.ToString("N2", Inv));
        Card(sb, "At risk / Risk (TRY)", s.AmountAtRisk.ToString("N2", Inv));
        sb.AppendLine("</div>");

        sb.AppendLine("<h2>Findings by rule / Kurala göre bulgular</h2><div class=\"scroll\"><table><tr><th>Rule</th><th>Kural / Rule</th><th>Count</th></tr>");
        foreach (var r in report.ByRule)
        {
            sb.AppendLine(Inv, $"<tr><td><code>{E(r.RuleId)}</code></td><td>{E(r.TitleTr)}<span class=\"en\">{E(r.TitleEn)}</span></td><td class=\"n\">{r.Count}</td></tr>");
        }

        sb.AppendLine("</table></div><h2>Invoices / Faturalar</h2>");
        foreach (var invoice in report.Invoices)
        {
            sb.AppendLine(Inv,
                $"<details{(invoice.Outcome == InvoiceOutcome.Rejected ? " open" : string.Empty)}><summary><code>{E(invoice.InvoiceNo)}</code> · <span class=\"o-{invoice.Outcome}\">{invoice.Outcome}</span> · {E(invoice.SutVersion ?? "no version")} · total {invoice.Total:N2} · at risk {invoice.AmountAtRisk:N2} TRY</summary>");
            if (invoice.Findings.Count > 0)
            {
                sb.AppendLine("<div class=\"scroll\"><table><tr><th>Rule</th><th>Line</th><th>Neden / Reason</th><th>Öneri / Suggestion</th></tr>");
                foreach (var f in invoice.Findings)
                {
                    sb.AppendLine(Inv,
                        $"<tr><td><code>{E(f.RuleId)}</code><br><span class=\"sev-{f.Severity}\">{f.Severity}</span></td><td class=\"n\">{f.LineNo?.ToString(Inv) ?? "-"}</td><td>{E(f.Reason.Tr)}<span class=\"en\">{E(f.Reason.En)}</span></td><td>{E(f.Suggestion.Tr)}<span class=\"en\">{E(f.Suggestion.En)}</span></td></tr>");
                }

                sb.AppendLine("</table></div>");
            }

            sb.AppendLine("</details>");
        }

        sb.AppendLine("</main></body></html>");
        return sb.ToString().ReplaceLineEndings(Lf);
    }

    private static void Card(StringBuilder sb, string label, string value) =>
        sb.AppendLine(CultureInfo.InvariantCulture, $"<div class=\"card\">{E(label)}<b>{E(value)}</b></div>");

    private static string E(string value) => WebUtility.HtmlEncode(value);

    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
