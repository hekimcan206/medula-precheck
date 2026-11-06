# ADR 0003: Staging through session-private temporary tables, array binding and a two-account model

- Status: accepted
- Date: 2026-11-06

## Context

The PL/SQL package (ADR 0002) needs a whole batch inside the database before it can run one set-based
statement. The batch must arrive quickly and must not leak into other sessions. Leftovers must not pile up,
and the account the API uses should not be able to change reference data or erase the audit trail.

## Options considered

**Getting the batch in**

1. Row-by-row `INSERT`: one round trip per row. A 20,000-invoice batch means about 100,000 round trips.
2. PL/SQL associative-array parameters: fast, but every table needs a matching collection type and the package
   signature grows with the data model.
3. **ODP.NET array binding** (`ArrayBindCount`): one `INSERT` statement, one round trip per 20,000-row chunk.

**Where it lands**

1. Permanent staging tables with a `batch_id` column: rows must be deleted afterwards, every insert writes full
   redo, and a crashed client leaves rows behind that need a cleanup job.
2. **Global temporary tables, `ON COMMIT DELETE ROWS`**: each session sees only its own rows, and commit empties
   them. Undo/redo is minimal. They need no cleanup and isolate concurrent batches by construction.
3. Private temporary tables (18c+): created per session in code. They cannot be granted or referenced from a
   compiled package.

## Decision

Array binding into global temporary tables `stg_invoice`, `stg_line`, `stg_diagnosis`, `stg_doctor` and `stg_prior`,
then `precheck_pkg.run_batch` and the findings fetch, all **in one transaction**. The commit empties the staging
tables. The package records every run (`precheck_run`: counts, timestamps, `SESSION_USER`) and every finding
(`precheck_finding`) in permanent tables, so each pre-check leaves a trace.

Two database accounts, both created by `deploy/oracle/init/01-users.sh`. Docker Compose mounts the same script,
and so do the Testcontainers tests:

| Account | Rights | Used by |
|---|---|---|
| `PRECHECK_OWNER` | `CREATE TABLE/PROCEDURE/SEQUENCE`, quota on `USERS` | `--init-oracle` only (installs schema, loads reference data) |
| `PRECHECK_APP` | `CREATE SESSION`; `SELECT, INSERT` on the five staging tables; `EXECUTE` on `precheck_pkg` | the API and the bulk path |

The package is `AUTHID DEFINER`, so it reads reference data and writes audit rows with the owner's rights while
the app account has no direct access to them. `OracleBulkTests` prove that `PRECHECK_APP` gets ORA-00942 on
`SELECT` from reference/audit tables and on `UPDATE`/`DELETE` attempts.

Schema DDL uses Oracle 23ai `CREATE ... IF NOT EXISTS` and `CREATE OR REPLACE`, so the installer is
re-runnable. Reference data is replaced in one transaction. A migration framework is not needed for one schema
version and is listed as a next step.

Identifiers that reach SQL text (schema owner, grantee) pass `OracleNames.Identifier` (plain identifier regex).
All values are bind variables.

## Consequences

- Concurrent batches are isolated without locks or `batch_id` predicates. A test runs four sessions in parallel
  and checks each sees only its own invoices.
- The whole batch is one transaction. A very large batch holds its temporary segment until commit, so callers
  should chunk huge loads. The API caps a request at 5,000 invoices.
- Optimizer statistics on GTTs are session-specific, and dynamic sampling happens at hard parse. Measured: a
  cursor first parsed for a 2,000-invoice batch and reused for 20,000 invoices took 5.5 s instead of 0.7 s
  (`docs/benchmarks.md`). Callers with very different batch sizes should set session statistics after staging
  (`DBMS_STATS.SET_TABLE_STATS`) or pin the plan. That is a documented next step, not implemented.
- Audit rows grow without limit. A retention policy (partitioning by run date plus a purge job) is out of
  scope and listed as a limitation.
