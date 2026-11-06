# ADR 0002: Which rules run in PL/SQL and which stay in .NET

- Status: accepted
- Date: 2026-11-06

## Context

Billing data in a hospital information system (HBYS) usually lives in Oracle. A month-end batch can be tens of
thousands of invoices. Two ways to pre-check them:

- pull the batch into the application and evaluate every rule in .NET, or
- push the check to the data: evaluate rules set-based inside Oracle and return only the findings.

Both have a cost. Implementing a rule twice costs maintenance and can let the two copies disagree. Implementing
it only in PL/SQL makes it harder to unit-test and to explain to the person fixing the invoice.

## Options considered

1. **Everything in .NET.** One implementation, fast to unit-test, easy to localise. The data has to leave the
   database, which is fine for an API call and wasteful for a nightly batch that is already in Oracle.
2. **Everything in PL/SQL.** Data never leaves the database. Format checks (regular expressions), bilingual
   texts and fix suggestions are clumsy there, and test cycles are slow (container start-up).
3. **Split by rule shape, with an enforced overlap.** Rules that are joins or aggregates against versioned
   reference data run in both places. Rules that are about formats, text or episode logic stay in .NET.

## Decision

Option 3. A rule goes to PL/SQL when it is **a set operation over reference data or over sibling rows**:

| Runs in PL/SQL and .NET (13) | Why it fits SQL |
|---|---|
| `MDL-002` service date outside provision | range predicate per row |
| `MDL-004` unknown code | anti-join against `sut_procedure` of the version |
| `MDL-005` duplicate line | `MIN() OVER (PARTITION BY ...)` |
| `PRC-001` price differs from tariff | join on version + code |
| `PRC-002` daily quantity | `SUM() OVER (PARTITION BY invoice, code, day)` |
| `DEM-001` / `DEM-002` sex / age limits | join + arithmetic |
| `CLN-001` required diagnosis | `NOT EXISTS` over the diagnosis set |
| `CLN-002` authorised specialty | `NOT EXISTS` over the branch list |
| `FRQ-001` frequency window | correlated window sums over history and earlier lines |
| `PKG-001` package contents | three-way join, `MIN()` for attribution |
| `PKG-002` combined-billing ban | semi-join on same-day primary |
| `PKG-003` inpatient-only | join + predicate |

| .NET only (9) | Why it stays in .NET |
|---|---|
| `MDL-001` tracking-number format, `MDL-006` registration format, `MDL-007` ICD-10 format | regular-expression validation of input shape, best done before any data is staged |
| `MDL-003` invoice vs. discharge date, `ADM-001` late invoice | per-invoice header logic that billing staff fix in the HBYS screen, not data-heavy |
| `DEM-003` diagnosis vs. sex, `PRV-001` accident external cause | coding-quality rules that change with coding guidance; cheap in memory |
| `FRQ-002` repeat examination, `PKG-004` bed-days vs. stay | episode logic with exemptions (emergency, same-day stays); clearer as code |

The package returns **codes and values only** (`rule_id`, `line_no`, `expected_value`, `actual_value`). Turkish and
English reasons and fix suggestions are rendered in .NET from the same `RuleDefinition` metadata, so wording is
never duplicated in PL/SQL.

**Parity is a test, not a hope.** `OracleBulkTests` run the shared golden cases through PL/SQL and a 3,000-invoice
synthetic batch (every defect type, all three SUT versions) through both evaluators, and require byte-identical
`(invoice, line, rule, severity, expected, actual)` sets. To make that possible:

- money is formatted with `TO_CHAR(x, 'FM...0.00')` and `ToString("0.00", InvariantCulture)`. The `.` format
  element is always a period, whatever `NLS_NUMERIC_CHARACTERS` the session inherits (ODP.NET derives NLS
  settings from the client culture, and on a Turkish machine the decimal separator is a comma);
- lists are ordered with `NLSSORT(x, 'NLS_SORT=BINARY')` in SQL and `StringComparer.Ordinal` in .NET;
- prefix matching uses `SUBSTR(..) = prefix`, not `LIKE`, so `_` and `%` can never act as wildcards;
- age uses `FLOOR((YYYYMMDD(service) - YYYYMMDD(birth)) / 10000)`, which matches the .NET completed-years
  calculation exactly, including 29 February birthdays (`MONTHS_BETWEEN` does not).

`precheck_pkg.implemented_rules` and `RuleDefinition.OracleBulk` must list the same rules; a test asserts it.

## Consequences

- The bulk path covers 13 of 22 rules. For a complete verdict a caller runs the .NET-only rules too (cheap, no
  data access). The API's `/precheck/oracle` endpoint is the PL/SQL subset by design, and the report says
  `oracle-plsql-bulk`.
- A change to an overlapping rule has to be made twice. The parity tests catch a missed half in CI.
- Measured throughput is in `docs/benchmarks.md`: about 142k invoices/s for the .NET engine (22 rules, one
  thread) against 8.7k invoices/s end to end for the PL/SQL path, or 27.6k/s for the set-based check alone.
  The in-memory .NET engine is faster per invoice. The PL/SQL path is worth it when the batch already lives in Oracle and should not leave it, not
  because it wins a micro-benchmark.
