---
task: Verify CLAUDE.md and README match actual codebase
slug: 20260511-000000_verify-claude-md-readme
effort: standard
phase: complete
progress: 9/9
mode: interactive
started: 2026-05-11T00:00:00Z
updated: 2026-05-11T00:01:00Z
---

## Context

Audit CLAUDE.md and README.md against the actual codebase and patch any gaps. Research revealed several concrete discrepancies.

**CLAUDE.md gaps found:**
1. Commands list missing `CapturesCommand` and `EncryptConfigCommand` (both shipped in Program.cs)
2. API Endpoints section missing 5 VBR endpoints: `rescan`, `serverInfo`, `managedServers`, `inventory/{hostname}`, `connectionCertificate`
3. Core layer description omits `PasswordObfuscator` (AES-GCM, in `Core/Config/`)
4. Key Patterns: `FindingState` description omits `captured_errors` and `suppressions` subsystems in state file

**README.md gap found:**
- "Infrastructure Health" listed as a current feature, but `GetManagedServers`, `GetInventory`, `GetConnectionCertificate` are defined on `IVbrClient`/`VbrClient` but called by zero monitors or commands — dead/placeholder API client code

### Risks
- Infrastructure Health: removing from README features list is the right call (not implemented), but need to ensure the API endpoints section isn't confused with the feature
- CLAUDE.md API endpoints should document what's on IVbrClient, not just what monitors happen to call

## Criteria

- [x] ISC-1: CLAUDE.md Commands list includes `CapturesCommand`
- [x] ISC-2: CLAUDE.md Commands list includes `EncryptConfigCommand`
- [x] ISC-3: CLAUDE.md VBR API section notes rescan/serverInfo/managedServers/inventory/connectionCertificate exist on VbrClient (not yet used by monitors)
- [x] ISC-8: CLAUDE.md Core layer description mentions `PasswordObfuscator`
- [x] ISC-9: CLAUDE.md Key Patterns mentions `captured_errors` in FindingState context
- [x] ISC-10: CLAUDE.md Key Patterns mentions `suppressions` in FindingState context
- [x] ISC-11: README.md "Infrastructure Health" bullet removed from Features list (not implemented)
- [x] ISC-12: README does not gain any new incorrect feature claims
- [x] ISC-13: CLAUDE.md does not lose any currently-accurate information

## Decisions

## Verification
