# Contributing

## Prerequisites

- .NET SDK 10.0.400 or later (`global.json` rolls forward to the latest feature band)
- Docker (the Oracle tests start `gvenzl/oracle-free:23-slim-faststart` through Testcontainers; `docker compose up` runs the demo)

## Workflow

1. Open an issue first for anything larger than a small fix.
2. Branch from `main`; one concern per pull request.
3. Before pushing:

   ```bash
   dotnet build                                   # warnings are errors
   dotnet test                                    # unit + golden + property + Oracle parity tests
   dotnet format --verify-no-changes
   ```

## Changing a rule or a SUT version

- Rule metadata (ids, bilingual texts, fix action) lives in `src/MedulaPrecheck.Core/Rules/RuleDefinitions.cs`;
  reference data and versions in `src/MedulaPrecheck.Core/ReferenceData/sut-catalog.json` (see ADR 0001).
- Every rule needs at least one passing and one failing golden case in
  `tests/MedulaPrecheck.UnitTests/Golden/GoldenCases.cs`. A test enforces it.
- If the rule is on the PL/SQL bulk path (`OracleBulk: true`), change `db/oracle/schema/02-precheck-pkg.sql` in the
  same pull request; the parity tests fail otherwise (see ADR 0002).
- Regenerate the approval samples: `dotnet run --project src/MedulaPrecheck.Cli -- samples docs/samples`.

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/) keep the history readable and make the hand-written
CHANGELOG easy to assemble at release time
(`feat:`, `fix:`, `perf:` are user-visible; `docs:`, `test:`, `build:`, `ci:`, `refactor:`, `chore:` are not).

## Architecture decisions

A change to a decision in `docs/adr/` needs a new ADR that supersedes the old one (MADR format).
