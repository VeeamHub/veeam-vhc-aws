---
task: Investigate failing GitHub Actions workflows on April
slug: 20260422-000000_investigate-failing-github-actions
effort: standard
phase: execute
progress: 9/9
mode: interactive
started: 2026-04-22T00:00:00Z
updated: 2026-04-22T00:01:00Z
---

## Context

Three CI workflows failed in April 2026. Root causes identified via gh run logs:

1. **MailKit 4.15.1 vulnerability** (GHSA-9j88-vvj5-vhgr, moderate) — triggers both the Release `Security Gate` and the Security Scan `Vulnerable Package Audit` jobs. Dependabot PR #7 exists to upgrade to 4.16.0.
2. **build.yml `--no-build` bug** — Test step runs `dotnet test ... --no-build` but only the main project was built (not the test project). The test DLL doesn't exist, so VSTest fails immediately with "invalid argument". This is why the Dependabot PR's Build & Test also fails.
3. **CodeQL Advanced Security not enabled** — The CodeQL job in security.yml fails at SARIF upload: "Advanced Security must be enabled for this repository". This is a GitHub org-level setting, not a code fix. Workflow has `continue-on-error: false` which blocks the run.

### Risks
- MailKit 4.16.0 might have breaking API changes — need to verify tests pass after upgrade.
- The `--no-build` bug may have masked test failures on PRs historically.

## Criteria

- [x] ISC-1: MailKit upgraded from 4.15.1 to 4.16.0 in VeeamVhcAws.csproj
- [x] ISC-2: dotnet restore succeeds locally after MailKit upgrade
- [x] ISC-3: `--no-build` flag removed from test step in build.yml
- [x] ISC-4: build.yml test step restores/builds test project correctly
- [x] ISC-5: Security Gate step no longer reports MailKit vulnerability after upgrade
- [x] ISC-6: Release workflow unblocked (security gate passes, MailKit fixed)
- [x] ISC-7: Build & Test unblocked (test step runs correctly without --no-build)
- [x] ISC-8: CodeQL failure root cause documented — requires GHAS org-level enable
- [x] ISC-9: No other code changes beyond the two targeted fixes

## Decisions

- Fix build.yml surgically: remove `--no-build` only, don't restructure the workflow
- Upgrade MailKit directly in csproj (don't wait for Dependabot PR — it has the --no-build bug anyway)
- CodeQL: flag as org-level action needed, do NOT remove the job or set continue-on-error without user decision

## Verification
