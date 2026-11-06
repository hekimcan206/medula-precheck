# ADR 0001: Effective-dated SUT rule sets, written as deltas

- Status: accepted
- Date: 2026-11-06

## Context

SGK amends the SUT (Sağlık Uygulama Tebliği, the Health Implementation Communiqué that defines what SGK pays
for and how) several times a year. An amendment changes prices, adds or withdraws codes, tightens limits and
sometimes turns a soft check into a blocking one. A pre-check that applies today's rules to last quarter's
invoice gives wrong answers in both directions: it rejects invoices that were correct when issued and passes
invoices that break the rules of their own period.

We need three things: (1) pick the rule set that was in force for an invoice, (2) keep each amendment
reviewable on its own, and (3) give the .NET engine and the Oracle package exactly the same reference data.

## Options considered

1. **Rules in code with `if (date >= ...)` branches.** Simple at first. Every amendment touches rule code, the
   history is spread across many files, and the PL/SQL side would need the same branches written again.
2. **A full copy of the catalog per version.** Easy to read one version. An amendment is then a 300-line diff
   in which the three real changes are hard to find, and copies drift apart.
3. **A base version plus deltas (`inherits`, `procedureChanges`, `removedProcedures`, rule severity changes),
   resolved at load time into immutable snapshots.** This is how amendments are published (değişiklik
   tebliğleri): each one lists only what changes.

## Decision

Option 3. `sut-catalog.json` (embedded in `MedulaPrecheck.Core`) holds versions with `effectiveFrom`; a version's
`effectiveTo` is the next version's start, so there are no gaps or overlaps. `SutCatalog.Resolve` turns the
deltas into full `SutVersion` snapshots. The engine resolves the version **valid on the invoice date**
(inclusive start, exclusive end) and runs only the rules that version activates, with the severity that
version assigns. An invoice dated before the first version gets `SYS-001` and is rejected rather than checked
against an arbitrary rule set.

Severity lives on the version, not on the rule, because amendments do change it (`PRV-001` is a warning in
2025 and blocking from `SUT-2026.01`). Numeric thresholds that change (`examinationRepeatDays`,
`maxInvoiceLagDays`) are version parameters for the same reason.

The Oracle installer loads the **resolved snapshots** into `sut_*` tables keyed by `version_id`, and the PL/SQL
package resolves the version with the same date predicate. One JSON file is the single source of truth for both
evaluators.

## Consequences

- An amendment is a small, reviewable JSON delta plus golden cases. `VersioningTests` pin the boundaries:
  price, code, frequency limit, rule activation, severity and parameter changes across versions.
- The loader validates rule ids, so a typo in a version fails at start-up instead of silently disabling a rule.
- Real SUT prices and limits apply by **service date**, and one provision can span an amendment. The brief and
  this model use the invoice date for the whole invoice. Per-line versioning is listed in the README limitations.
- Data, not code, changes per version. A new *kind* of rule still needs code, plus PL/SQL if it is on the bulk
  path (ADR 0002).
