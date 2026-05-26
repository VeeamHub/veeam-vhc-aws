---
project: veeam-vhc-aws
task: "Project ISA — veeam-vhc-aws"
effort: E3
effort_source: explicit
phase: observe
progress: 0/25
mode: interactive
started: 2026-05-15T00:00:00Z
updated: 2026-05-15T00:00:00Z
---

# Project ISA — veeam-vhc-aws

> **Seed-generated draft.** Sections drafted from `README.md`, `CLAUDE.md`, `VeeamVhcAws.csproj`, source tree, and last 30 git commits. `Principles`, `Decisions`, `Changelog`, `Verification` are intentionally absent (author-driven). Run `Skill("ISA", "interview me on ISA.md")` to deepen Vision/Goal/Principles, refine the ISC granularity, and add the first Decisions entries.

## Problem

The community Veeam Health Check tooling produces *point-in-time* reports — useful for assessments but blind to drift, regressions, and credential expiry between reports. Veeam admins running VBR plus VBAWS need continuous monitoring that detects repository unreachability, retention policy violations, orphaned backups, worker session failures, and credential rot — and that alerts only when state *changes*, not every five minutes. Existing pipelines either silently fail (no notification when something starts working again), require a separate observability stack to operate, or can't span both VBR and VBAWS in one configured runtime.

## Vision

A Veeam admin runs `setup.ps1` on a Windows box, walks through a 7-step wizard, and walks away with a continuously-monitored backup estate. `veeam-vhc-aws ui` is the operator console — open a browser, see what's wrong, click suppress, move on. The scheduled task fires every five minutes invisibly; webhooks and email fire only when reality changes. When something resolves, the resolution notification fires too. The admin trusts the tool because it speaks plainly, points at the specific resource, and survives single-server outages without losing the others.

## Out of Scope

- **Not an officially supported Veeam product.** Community-supported; lives under VeeamHub.
- **Not a remediation tool.** Alerts only. We do not call write-side VBR or VBAWS APIs. We do not restart workers, force rescans, or rotate credentials.
- **Not a replacement for VBR Console or VBAWS Console.** This tool surfaces health signals; the consoles remain authoritative for configuration changes.
- **Cross-server cross-correlation is not in scope.** `CrossCorrelator.Correlate()` operates per-server only. Linking a finding on `prod-vbr` to a finding on `aws-backup` is explicitly excluded.
- **Not a backup product.** No data movement, no restore, no chunk handling.
- **No bundled secret-management infrastructure.** AES-GCM obfuscation on disk only — not a KMS, not a vault. Plaintext-in-memory remains the runtime model.

## Constraints

- **Runtime:** .NET 10, C# 12, `Nullable enable`, `ImplicitUsings enable`. Single-file publish, trimmed (`PublishSingleFile=true`, `PublishTrimmed=true`).
- **Project SDK:** `Microsoft.NET.Sdk.Web` (admin GUI is in-process Blazor Server). `Microsoft.Extensions.Logging` implicit-using suppressed to avoid Serilog.ILogger clash.
- **Distribution:** Single self-contained exe per target. Primary: `win-x64`. Also: `linux-x64`, `osx-arm64`, `osx-x64`.
- **Install paths (Windows):** binary at `C:\Program Files\VHC\veeam-vhc-aws.exe`; config at `C:\ProgramData\VHC\veeam-vhc-aws.yaml`; state at `C:\ProgramData\VHC\veeam-vhc-aws-state.json`; logs at `C:\ProgramData\VHC\veeam-vhc-aws-YYYYMMDD.log`.
- **Scheduled tasks:** `Veeam VHC AWS` (every 5 minutes), `Veeam VHC AWS Daily Summary` (configurable time, default 8 AM local).
- **APIs:**
  - VBR — OAuth2 at `/api/oauth2/token`, API version `1.3-rev1`. Read-only endpoints only (repos, SOBR, sessions, jobs, backups, restorePoints; serverInfo, managedServers, inventory, connectionCertificate informational).
  - VBAWS — OAuth2 at `/api/oauth2/token`; `/api/v1/sessions` with client-side date filtering.
- **Crypto:** Passwords obfuscated with AES-GCM via .NET BCL primitives. `ENC:` prefix marks an obfuscated value. We do not roll our own cryptography.
- **Concurrency:** ≤2 servers run sequentially; ≥3 servers run in parallel via `Task.WhenAll` capped at 20 workers (`MonitorRunner.RunAllServers`).
- **Failure isolation:** One server's exception must not abort the others.
- **Resource prefix:** every `Finding.Resource` carries a `[server-name]` prefix; every `MonitorResult` carries a `server` field.
- **Exit codes:** worst severity across all findings → `0` (OK), `1` (Warning), `2` (Critical), `3` (Error).
- **Logging:** Serilog, daily rotating file sink, console mirror to stderr (`console: true`), redacted credentials. Disk-free threshold check on every run bypasses dedup.
- **Test framework:** xUnit; HTTP mocking via WireMock.Net; fixtures in `tests/VeeamVhcAws.Tests/Fixtures/`.

## Goal

Deliver a continuously-running, single-binary monitor for Veeam VBR and VBAWS backup infrastructure that (1) detects repository, retention, and worker-health issues across multiple servers, (2) deduplicates alerts via a persistent state file so admins are paged only on change, and (3) surfaces findings through a browser admin GUI plus a stack of pluggable notification handlers (webhook, email, Prometheus, JSON), all installable via a single PowerShell setup script and runnable as a Windows Scheduled Task.

## Criteria

- [ ] **ISC-1**: `dotnet restore src/VeeamVhcAws/VeeamVhcAws.csproj` exits 0.
- [ ] **ISC-2**: `dotnet test tests/VeeamVhcAws.Tests/VeeamVhcAws.Tests.csproj` passes all suites (14 test files: Ui/, Monitors/, Core/, Web/, Integration/, Outputs/).
- [ ] **ISC-3**: `dotnet publish src/VeeamVhcAws/VeeamVhcAws.csproj -c Release -r win-x64 --self-contained` produces a single-file `veeam-vhc-aws.exe`.
- [ ] **ISC-4**: `veeam-vhc-aws version` prints the version from `VeeamVhcAws.csproj` `<Version>` element.
- [ ] **ISC-5**: `veeam-vhc-aws setup` (interactive) writes a config file with at least one server, at least one output handler, and any plaintext passwords stored as `ENC:` AES-GCM-obfuscated strings.
- [ ] **ISC-6**: `veeam-vhc-aws all -c config.yaml` exits with code matching the worst-severity finding (0/1/2/3).
- [ ] **ISC-7**: `RepoHealthMonitor` detects an unreachable repo and emits a `Finding` with resource format `[server-name] repo:<Name>`.
  - [ ] **ISC-7.1**: SOBR extents covered (parent + child extents, not just parent).
  - [ ] **ISC-7.2**: Repo-related session types scanned: `ConfigurationResynchronize`, `ExternalMaintenance`, `RepositoryRescan`, `RepositoryMaintenance`.
- [ ] **ISC-8**: `RetentionMonitor` detects an orphan backup AND skips Kasten/external policy-managed backups (those with `policyUniqueId`) AND respects the `exclude_backups` list.
- [ ] **ISC-9**: `WorkerHealthMonitor` produces a `Critical` finding when VBAWS session failure rate exceeds the configured threshold.
- [ ] **ISC-10**: `CrossCorrelator.Correlate()` links a credential-expiry finding to a retention failure on the same server within one run.
- [ ] **ISC-11**: `FindingState` dedups: same finding present twice in consecutive runs fires exactly one webhook on first detection and zero on the second; when the finding disappears, a `RESOLVED` notification fires once.
- [ ] **ISC-12**: Adaptive lookback narrows the session window after a clean run (`now - last_success + overlap_minutes`) and falls back to the full `session_lookback_hours` after a failed run or on first run.
- [ ] **ISC-13**: `veeam-vhc-aws ui` (no `--bind`) starts Blazor admin on `http://127.0.0.1:9101` with no token required; Dashboard and Alerts pages render.
- [ ] **ISC-14**: `veeam-vhc-aws ui --bind 0.0.0.0 --port 9101` generates a 64-char token, persists it at `C:\ProgramData\VHC\ui-token.txt` (Windows) or `~/.vhc/ui-token.txt`, and rejects requests without `?t=<token>` or the auth cookie.
- [ ] **ISC-15**: `PasswordObfuscator` round-trips: `Obfuscate(plaintext)` → `Deobfuscate(...)` yields the original plaintext; obfuscated value starts with `ENC:`.
- [ ] **ISC-16**: `setup.ps1` (run as Administrator) creates a Windows Scheduled Task named exactly `Veeam VHC AWS` that runs every 5 minutes.
- [ ] **ISC-17**: Logging respects `global.logging.{level,file,rotation_keep,console,disk_warning_pct}`; log file is named `veeam-vhc-aws-YYYYMMDD.log` (dash before date).
- [ ] **ISC-18**: When `output: [json_stdout]` is the only handler, stdout is a single valid JSON document per run (no chrome).
- [ ] **ISC-19**: `WebhookHandler` renders all five templates (`ntfy`, `slack`, `teams`, `pagerduty`, `generic`) without throwing and `min_severity` filtering is applied before send.
- [ ] **ISC-20**: `EmailHandler` does **not** send when the digest contains zero findings (empty-email suppression).
- [ ] **ISC-21**: With 3 servers configured, `MonitorRunner` dispatches them via `Task.WhenAll` and total wall-clock time is below the sum of individual server times.
- [ ] **ISC-22**: With one server configured to an unreachable URL and two reachable, the unreachable server raises a single error-severity finding and the other two complete normally (failure isolation).
- [ ] **Anti: ISC-23**: No write-side VBR or VBAWS API call appears anywhere in `Core/Clients/`. The codebase does not POST/PUT/DELETE to repo, job, session, or worker endpoints.
- [ ] **Anti: ISC-24**: `CrossCorrelator.Correlate()` is never called with `Finding` lists spanning more than one server. Cross-server correlation is structurally impossible.
- [ ] **Anti: ISC-25**: No custom AES, no hand-rolled key derivation, no homemade HMAC. All crypto goes through `System.Security.Cryptography` BCL primitives.

## Test Strategy

| ISC    | Type        | Check                                                                                                              | Threshold | Tool                       |
| ------ | ----------- | ------------------------------------------------------------------------------------------------------------------ | --------- | -------------------------- |
| ISC-1  | build       | `dotnet restore src/VeeamVhcAws/VeeamVhcAws.csproj`                                                                | exit 0    | dotnet CLI                 |
| ISC-2  | unit/intg   | `dotnet test tests/VeeamVhcAws.Tests/`                                                                             | 100% pass | xUnit + WireMock.Net       |
| ISC-3  | build       | `dotnet publish -c Release -r win-x64 --self-contained` produces single-file exe                                  | exit 0    | dotnet CLI                 |
| ISC-4  | smoke       | run `veeam-vhc-aws version`; grep stdout for csproj `<Version>` value                                              | exact     | Bash                       |
| ISC-5  | smoke       | scripted `setup` run, then `grep -c '^.*ENC:' config.yaml` on password fields                                       | ≥1        | Bash + grep                |
| ISC-6  | smoke       | run all 4 severity scenarios; assert exit codes 0,1,2,3                                                            | exact     | Bash matrix                |
| ISC-7  | unit        | `RepoHealthMonitorTests` — unreachable repo fixture                                                                 | pass      | xUnit + WireMock           |
| ISC-7.1| unit        | `RepoHealthMonitorTests` — SOBR fixture covers parent + extents                                                     | pass      | xUnit + WireMock           |
| ISC-7.2| unit        | `RepoHealthMonitorTests` — session-type matrix                                                                      | 4 types   | xUnit + WireMock           |
| ISC-8  | unit        | `RetentionMonitorTests` — orphan with `policyUniqueId` is skipped; `exclude_backups` entries skipped                | pass      | xUnit + WireMock           |
| ISC-9  | unit        | `WorkerHealthMonitorTests` — failure-rate threshold breach → Critical finding                                       | pass      | xUnit + WireMock           |
| ISC-10 | unit        | `CrossCorrelator` test: credential-expiry + retention-failure on same server linked                                 | pass      | xUnit                      |
| ISC-11 | unit        | `FindingStateTests` — dedup + resolve flow                                                                          | pass      | xUnit                      |
| ISC-12 | unit/intg   | `AdaptiveLookbackTests` + `PerformanceIntegrationTests`                                                             | pass      | xUnit                      |
| ISC-13 | E2E         | launch `ui`, GET `/`, assert 200 + Blazor markup; check `127.0.0.1:9101`                                            | exact     | Interceptor or curl        |
| ISC-14 | E2E         | launch `ui --bind 0.0.0.0`, assert token file exists, GET without token returns 401                                 | exact     | Bash + curl                |
| ISC-15 | unit        | `PasswordObfuscatorTests` round-trip                                                                                | pass      | xUnit                      |
| ISC-16 | manual      | Run `setup.ps1` on Windows VM; `Get-ScheduledTask "Veeam VHC AWS"` returns 5-min trigger                            | pass      | PowerShell                 |
| ISC-17 | smoke       | inspect log file name format + check rotation after day boundary                                                    | regex     | Bash + ls                  |
| ISC-18 | smoke       | run with `output: [json_stdout]`; `jq .` succeeds on full stdout                                                    | exit 0    | Bash + jq                  |
| ISC-19 | unit        | `WebhookHandlerTests` — render every template                                                                       | 5 pass    | xUnit                      |
| ISC-20 | unit        | `WebhookHandlerTests`/`EmailHandlerTests` — zero-finding suppression                                                | pass      | xUnit                      |
| ISC-21 | intg        | 3-server WireMock fixture; assert wall-clock < sum-of-individuals                                                   | pass      | xUnit + WireMock           |
| ISC-22 | intg        | 3-server fixture, one URL unreachable; assert remaining two finish + 1 error finding                                | pass      | xUnit + WireMock           |
| ISC-23 | static      | `grep -rE '(POST\|PUT\|DELETE)' src/VeeamVhcAws/Core/Clients/` matches only auth-token POSTs                        | exact     | Bash + grep                |
| ISC-24 | static      | search `CrossCorrelator.Correlate` callsites — all receive `Finding[]` from a single `ServerContext`                | exact     | code review + grep         |
| ISC-25 | static      | `grep -rE '(Aes\.Create\|new HMAC\|Rfc2898)' src/` — confirm BCL-only crypto, no hand-rolled primitives             | exact     | Bash + grep                |

## Features

| Name                 | Description                                                                                                       | Satisfies                          | Depends On            | Parallelizable |
| -------------------- | ----------------------------------------------------------------------------------------------------------------- | ---------------------------------- | --------------------- | -------------- |
| `commands`           | `System.CommandLine` CLI entry points: `all`, `repo-health`, `retention`, `worker-health`, `serve`, `summary`, `setup`, `diagnose`, `captures`, `test-connection`, `version`, `encrypt-config`, `ui` | ISC-4, ISC-5, ISC-6, ISC-18 | `core`, `monitors`, `outputs`, `web` | no |
| `monitors`           | `RepoHealthMonitor`, `RetentionMonitor`, `WorkerHealthMonitor` — auto-routed to applicable server type            | ISC-7, ISC-7.1, ISC-7.2, ISC-8, ISC-9 | `core/clients`, `core/patterns` | yes |
| `core/auth`          | OAuth2 token acquisition for VBR + VBAWS                                                                          | ISC-7, ISC-8, ISC-9, ISC-23        | none                  | yes |
| `core/clients`       | Typed HTTP clients with Polly retry/timeout, JSON reflection re-enabled                                            | ISC-7, ISC-8, ISC-9, ISC-12, ISC-21, ISC-22, ISC-23 | `core/auth` | yes |
| `core/config`        | YAML config loader, server array, `PasswordObfuscator` (AES-GCM, `ENC:` prefix)                                    | ISC-5, ISC-15, ISC-25              | none                  | yes |
| `core/state`         | `FindingState` — persistent dedup, captured errors, suppressions                                                   | ISC-11, ISC-17                     | none                  | yes |
| `core/patterns`      | Pattern engine: known error strings → severity + remediation hint                                                  | ISC-7, ISC-8, ISC-9                | none                  | yes |
| `infrastructure`     | `MonitorRunner` (sequential ≤2 / parallel ≥3), `ServerContext`, `ServerContextBuilder`, `CrossCorrelator`         | ISC-10, ISC-21, ISC-22, ISC-24     | `monitors`, `core`    | no |
| `outputs`            | `JsonStdoutHandler`, `JsonFileHandler`, `WebhookHandler` (5 templates), `PrometheusHandler`, `EmailHandler`        | ISC-18, ISC-19, ISC-20             | `core`                | yes |
| `web`                | Embedded ASP.NET Core + Blazor Server admin GUI (Dashboard, Alerts, Servers, Outputs, Thresholds, Captures, Settings); token auth for non-loopback bind | ISC-13, ISC-14 | `core`, `infrastructure` | no |
| `installer`          | `setup.ps1` (interactive + parameterized), `build.ps1`/`build.sh`, Windows Scheduled Task creation, daily-summary task | ISC-3, ISC-16          | `commands`            | no |
| `logging`            | Serilog with daily rotation, dash-before-date filename, disk-space alert bypass                                    | ISC-17                             | none                  | yes |

---

<!-- Sections below are author-driven. Run `Skill("ISA", "interview me on ISA.md")` to populate. -->

<!--
status: created
sources_consulted:
  - README.md
  - CLAUDE.md
  - src/VeeamVhcAws/VeeamVhcAws.csproj
  - src/VeeamVhcAws/ source tree (Commands/, Monitors/, Outputs/, Core/, Infrastructure/, Web/)
  - tests/VeeamVhcAws.Tests/ inventory (14 test files)
  - last 30 git commits (10a0751..aac6407)
  - docs/dotnet-rewrite-plan.md (existence noted)
  - Plans/sorry-i-wanted-a-rippling-hamster.md (existence noted)
sections_drafted: [Problem, Vision, Out of Scope, Constraints, Goal, Criteria, Test Strategy, Features]
sections_skipped: [Principles, Decisions, Changelog, Verification]
isc_count: 25
review_required: true
-->
