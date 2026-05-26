---
task: Fix empty emails, unknown workloads, duplicate findings
slug: 20260511-120000_fix-empty-emails-unknown-workloads-dedup
effort: standard
phase: complete
progress: 8/8
mode: interactive
started: 2026-05-11T12:00:00Z
updated: 2026-05-11T12:10:00Z
---

## Context

Three user-reported issues with the VHC output quality:
1. Hourly emails sent even when there's nothing to report
2. Orphan detection showing "Workloads: unknown" when backup has 0 restore points
3. Same orphan backup appearing 8+ times (VBR returns multiple backup objects with identical names but different IDs)

## Criteria

- [x] ISC-1: EmailHandler skips send when no non-Ok findings exist
- [x] ISC-2: EmailHandler skips send when no errors exist
- [x] ISC-3: Skip applies to both regular and summary emails
- [x] ISC-4: Skip logs informational reason for skipping
- [x] ISC-5: Orphan findings grouped by backup name, not backup ID
- [x] ISC-6: Duplicate backup objects shown with (xN) suffix in message
- [x] ISC-7: "Workloads:" section omitted entirely when no workload names available
- [x] ISC-8: All 104 existing tests pass after changes

## Decisions

- Deduplication at source (RetentionMonitor) fixes all outputs (email, webhook, console) simultaneously
- Two-pass approach: collect into orphanGroups dict keyed by name, then emit one finding per name
- Value tuple mutation: reference types (List, HashSet) mutate in place; only int TotalRpCount needs reassignment
- Changed details key from `backup_id` to `backup_ids` (list) — no existing tests relied on that key

## Verification

- 104/104 tests passing including all 4 orphan detection tests
- EmailHandler.cs lines 139-145: hasContent guard added before BuildHtml
- RetentionMonitor.cs lines 252-308: two-pass grouping with countSuffix and workloadPart
