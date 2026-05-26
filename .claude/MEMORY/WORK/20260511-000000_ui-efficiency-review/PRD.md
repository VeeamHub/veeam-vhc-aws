---
task: review UI code changes for efficiency issues
slug: 20260511-000000_ui-efficiency-review
effort: standard
phase: learn
progress: 12/12
mode: interactive
started: 2026-05-11T00:00:00Z
updated: 2026-05-11T00:10:00Z
---

## Context

Code review of newly added Ui/ layer for the veeam-vhc-aws .NET 10 CLI. Five specific areas examined: LiveMonitorView fire-and-forget task, LiveMonitorView spin-wait for ctx init, LiveMonitorView 50ms poll loop, ConnectionTester sequential vs parallel, AllCommand hardcoded planned-work list, ResultsView multi-pass LINQ, MonitorRunner error result pattern.

The task asks for REAL ISSUE vs FALSE POSITIVE classification with impact and fix for each.

### Risks

- capturedCtx is shared between threads without volatile — compiler/JIT may cache the null check on the spin-wait thread
- plannedWork mismatch between AllCommand and MonitorRunner routing is the only correctness bug

## Criteria

- [x] ISC-1: LiveMonitorView liveTask fire-and-forget classified correctly
- [x] ISC-2: LiveMonitorView silent exception swallow impact assessed
- [x] ISC-3: LiveMonitorView spin-wait null-ctx path assessed
- [x] ISC-4: LiveMonitorView 50ms poll loop acceptability assessed
- [x] ISC-5: capturedCtx memory visibility (volatile) issue identified
- [x] ISC-6: ConnectionTester sequential penalty quantified
- [x] ISC-7: ConnectionTester AnsiConsole.Progress parallel feasibility assessed
- [x] ISC-8: AllCommand plannedWork correctness bug identified
- [x] ISC-9: AllCommand plannedWork phantom rows impact assessed
- [x] ISC-10: ResultsView multi-pass LINQ classified
- [x] ISC-11: MonitorRunner maxWorkers dead variable confirmed
- [x] ISC-12: MonitorRunner error result pattern confirmed clean

## Decisions

Grounding all analysis in the actual checked-in source, not just the snippets provided. MonitorRegistry.cs confirms server-type routing (repo_health/retention=vbr, worker_health=vbaws). AllCommand.cs line 39 confirmed as hardcoded three-monitor list.

## Verification

All findings cross-checked against the real source files. No synthetic issues introduced.
