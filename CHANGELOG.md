# Changelog

All notable changes to this project are documented here. The file is maintained by hand, following
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.0.0] - 2026-11-07

### Added

- Domain model of a MEDULA provision (takip), invoice, service lines, diagnoses and physician branch.
- Effective-dated SUT rule sets written as deltas (three synthetic versions); the version valid on the invoice
  date decides prices, codes, limits, active rules and severities.
- 22 illustrative rules across structural, pricing, demographic, clinical, frequency, package, provision and
  administrative categories, each with Turkish and English rejection reasons and a machine-readable fix.
- Oracle schema and `precheck_pkg`: set-based bulk pre-check of a staged batch for 13 rules, called through
  ODP.NET array binding as a least-privileged account, with an audit trail of every run.
- Minimal API (`/api/v1/precheck`, `/api/v1/precheck/oracle`, SUT version and rule discovery), `precheck` CLI and
  JSON, Markdown and HTML rejection-reason reports.
- Bogus (tr) synthetic invoice generator with labelled defect injection.

[Unreleased]: https://github.com/hekimcan206/medula-precheck/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/hekimcan206/medula-precheck/releases/tag/v1.0.0
