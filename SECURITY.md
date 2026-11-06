# Security policy

## Supported versions

Only the latest release receives fixes.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private vulnerability reporting
(**Security → Report a vulnerability**). Expect an acknowledgement within 5 working days and a fix or
mitigation plan within 30 days of confirmation.

## Data

This project contains **synthetic data only**. Never submit real patient, physician, facility or claim data in
issues, pull requests, test fixtures or sample files. See `docs/threat-model.md` for how the design limits
exposure of health data (KVKK special-category data).

## Security-relevant defaults

| Area | Default |
|---|---|
| Database | The API connects as `PRECHECK_APP`: insert/select on session-private staging tables and `EXECUTE` on the package only. DDL and reference data need `PRECHECK_OWNER`, used only by the one-shot `--init-oracle` process |
| Audit | Every PL/SQL run and finding is recorded with the database user (`precheck_run`, `precheck_finding`); the app role cannot read, change or delete them |
| SQL | All values are bind variables; the only identifiers in SQL text (schema owner, grantee) are validated against a plain-identifier pattern |
| Input | Request size capped (32 MB, 5,000 invoices); shape validation returns RFC 9457 problem details; no exception details in responses |
| Reports | Carry invoice numbers and codes only, never patient name or identifiers |
| Container | Chiseled runtime image, non-root UID 1654, no shell |
| Supply chain | Central package versions; vulnerable transitive `System.Security.Cryptography.Pkcs` pinned to a patched version; updated by hand; CI fails on known-vulnerable direct or transitive packages (`dotnet list package --vulnerable --include-transitive`); CodeQL `security-and-quality` |
| Secrets | None in the repository; compose defaults are development-only and overridable via `.env` |

Not included: authentication/authorisation (expected from the hosting HBYS or gateway), rate limiting, TLS termination.
