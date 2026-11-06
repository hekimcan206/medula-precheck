# Benchmarks

Throughput of the two evaluators on the same synthetic batch, measured on a developer laptop. All numbers below
come from real runs of `benchmarks/MedulaPrecheck.Throughput`; nothing is estimated or rounded up.

## Method

- **Batch:** 20,000 synthetic invoices from `InvoiceGenerator` (seed 1, 30% with one injected defect, invoice
  dates 2025-02-01..2026-06-30, so all three SUT versions are involved): 53,443 service lines (2.67 per invoice).
- **.NET engine:** `PrecheckEngine.CheckAll` in-process; three warm-up passes, then 15 measured passes with a
  forced GC before each. Three variants: all 22 rules sequential, all 22 rules with PLINQ, and the 13-rule
  PL/SQL subset sequential (the like-for-like comparison with Oracle).
- **Oracle bulk path:** `OracleBulkPrecheck.RunAsync` as `PRECHECK_APP` against a fresh
  `gvenzl/oracle-free:23-slim-faststart` container started by the harness (Testcontainers), schema and reference
  data installed exactly as in production. One full-size warm-up run, then 15 measured runs. Each run is one
  transaction: array-insert the batch into the staging GTTs ("stage"), execute `precheck_pkg.run_batch` and fetch
  every finding ("check + fetch"), commit. The run and all findings are also written to the audit tables.
- Throughput = invoices / wall-clock time of the measured step. The tables report the median and the min/max
  over the 15 runs.

## Environment

| | |
|---|---|
| CPU | Intel Core i7-13620H (10 cores / 16 threads), laptop |
| RAM | 15.7 GB host; Docker Desktop VM 7.6 GB, 16 vCPU |
| OS | Windows 11 (10.0.26200), x64 |
| .NET | .NET 10.0.12 runtime, Release build, server GC, tiered PGO |
| Docker | Docker Desktop 29.9.1 (WSL2 kernel 6.18) |
| Database | Oracle AI Database 26ai Free (gvenzl/oracle-free:23-slim-faststart, banner 23.26.3), default memory settings |

The machine was **not idle**: about 40 unrelated containers from other projects were running, and other
work was sharing the CPU. That explains the wide min/max spread. Medians are the honest
summary; treat the extremes as noise.

## Results (2026-11-06, 15 runs)

| Path | Rules | Median inv/s | Min inv/s | Max inv/s | Median ms per batch |
|---|---:|---:|---:|---:|---:|
| .NET engine, sequential | 22 | 142,305 | 39,289 | 265,411 | 140.5 |
| .NET engine, parallel (PLINQ) | 22 | 34,107 | 17,587 | 224,443 | 586.4 |
| .NET engine, sequential, PL/SQL subset | 13 | 206,849 | 13,044 | 340,268 | 96.7 |
| Oracle PL/SQL bulk, end to end (stage + check + fetch + commit) | 13 | 8,695 | 2,431 | 12,379 | 2,300.2 |
| Oracle PL/SQL bulk, check + fetch only | 13 | 27,582 | 8,079 | 37,880 | 725.1 |

Oracle split (median): stage 1,443 ms, check + fetch 725 ms; 4,384 findings per run.

## What the numbers say

1. **The in-memory .NET engine is the fastest evaluator per invoice**: about 142k invoices/s with all 22 rules
   on one thread. A 20,000-invoice month-end batch takes roughly 0.14 s once the data is in memory.
2. **The PL/SQL bulk path checks about 8.7k invoices/s end to end**, and the set-based check itself runs at
   27.6k invoices/s. Two thirds of the end-to-end time is staging (shipping the batch into Oracle). When the
   invoices already sit in Oracle tables, which is the HBYS case this path exists for, the staging step goes
   away and the check would read the billing tables directly. The 725 ms figure is the relevant one there.
3. **PLINQ was slower than a single thread on this machine.** The engine allocates per invoice (findings lists,
   LINQ in a few rules). On a loaded laptop with server GC, the coordination and GC cost outweighed the gain.
   The API therefore only parallelises batches above 200 invoices, and this result says even that threshold is
   worth re-measuring on the target server before relying on it.
4. **GTT plan stability matters.** In a first run of the harness the warm-up used a 2,000-invoice batch. The
   cursor was hard-parsed with dynamic sampling of the small temporary tables and then reused for 20,000
   invoices: check + fetch took a median of **5,526 ms** (3,619 inv/s) instead of 725 ms. Warming up at full
   size fixed it. In production, batches of very different sizes on global temporary tables need either
   session-level statistics (`DBMS_STATS.SET_TABLE_STATS` after staging), a cardinality hint, or adaptive
   plans checked with `DBMS_XPLAN`. The first run (5 runs, small warm-up) measured: .NET sequential 77,193
   inv/s median; Oracle end to end 2,414 inv/s, check + fetch 3,619 inv/s.

## Reproduce

```bash
# Docker must be running; the harness starts and removes its own Oracle container.
dotnet run -c Release --project benchmarks/MedulaPrecheck.Throughput -- 20000 15
# .NET engine only:
dotnet run -c Release --project benchmarks/MedulaPrecheck.Throughput -- 20000 15 --no-oracle
```

The harness prints the Markdown table above, plus OS, runtime and batch details.
