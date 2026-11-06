# Threat model

Scope: the pre-check API, the `precheck` CLI and the Oracle bulk path, deployed inside a hospital network next
to the HBYS (hospital information system). STRIDE-style, kept short and specific to this design.

## Assets

| Asset | Why it matters |
|---|---|
| Invoice batches (patient birth date, sex, diagnoses, services) | Health data is special-category personal data under KVKK (Law No. 6698, art. 6) |
| SUT reference data (prices, limits, rule activation) | Tampering silently changes what is flagged, so wrong invoices reach SGK or correct ones are held back |
| Audit trail (`precheck_run`, `precheck_finding`) | Shows who checked what and when; needed for disputes with the institution |
| Rejection-reason reports | Shared with billing staff; must not widen access to health data |

## Actors

Billing clerk (legitimate API/CLI user), HBYS integration (service caller), DBA, an attacker on the hospital
network, and a compromised API host.

## Threats and mitigations

| STRIDE | Threat | Mitigation in this repository | Residual / next step |
|---|---|---|---|
| Spoofing | Unauthenticated caller submits batches or reads reports | Not solved here: the API is meant to sit behind the HBYS gateway | Add OIDC/mTLS at the gateway; the API has no auth by design (README limitations) |
| Tampering | Changing prices or rule activation to let wrong invoices pass | Reference data is only writable by `PRECHECK_OWNER`; the API account has no grant on `sut_*` (tested: ORA-00942); reference data comes from a versioned JSON file in source control and is reloaded atomically | Sign the catalog file; alert on reference-data reloads |
| Tampering | SQL injection through invoice fields | Every value is a bind variable (array binding); the only identifiers in SQL text are validated (`OracleNames.Identifier`) | – |
| Repudiation | "I never ran that check" / disputed findings | `precheck_run` stores `SESSION_USER`, timestamps and counts; every finding is stored with its run; the app role cannot delete them (tested) | Map the DB session to the end user (client identifier / proxy user); retention policy |
| Information disclosure | Reports or logs leak patient identity | Patients are referenced by an opaque token; reports contain invoice numbers and codes only; request logging records method, path, status and timing, never bodies; problem details never include exception text | Encrypt the Oracle tablespace (TDE) in real deployments |
| Information disclosure | One batch visible to another session | Staging is in session-private global temporary tables, emptied at commit; concurrent-session test proves isolation | – |
| Denial of service | Huge batches exhaust memory or temp space | 32 MB request cap, 5,000 invoices per request, shape validation before evaluation; one transaction per batch | Rate limiting at the gateway |
| Elevation of privilege | Compromised API host gains DDL or data-change rights | Two-account model: the API holds only `CREATE SESSION`, staging `INSERT/SELECT` and `EXECUTE` on a definer-rights package; DDL credentials are only given to the one-shot installer; container is chiseled and non-root | Store credentials in a vault; rotate the app password |

## Out of scope

The real MEDULA web services and SGK credentials (this project never calls them), real patient data (none is
used, see README), and the HBYS itself.
