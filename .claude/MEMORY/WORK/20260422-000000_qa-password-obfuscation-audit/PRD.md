---
task: QA audit password obfuscation coverage veeam-vhc-aws
slug: 20260422-000000_qa-password-obfuscation-audit
effort: standard
phase: complete
progress: 8/9
mode: interactive
started: 2026-04-22T00:00:00Z
updated: 2026-04-22T00:01:00Z
---

## Context

QA audit of password obfuscation coverage in veeam-vhc-aws .NET 10 C# CLI. Validates that all password fields in the config are obfuscated by `encrypt-config` and deobfuscated at read time. One ISC failed (ISC-6): example.yaml email section is missing smtp_username, smtp_password, and use_tls fields.

## Criteria

- [x] ISC-1: EncryptConfigCommand regex matches both `password:` and `smtp_password:` YAML keys
- [x] ISC-2: EncryptConfigCommand regex skips ENC: values for both key names (quoted and unquoted)
- [x] ISC-3: OutputHandlerFactory.cs wraps smtp_password with PasswordObfuscator.Deobfuscate()
- [x] ISC-4: TestConnectionCommand.cs wraps smtp_password with PasswordObfuscator.Deobfuscate()
- [x] ISC-5: ServerContextBuilder.cs wraps server password with Deobfuscate() — no regression
- [ ] ISC-6: example.yaml email section includes smtp_username, smtp_password, use_tls fields
- [x] ISC-7: No other password-containing config keys lack Deobfuscate() coverage
- [x] ISC-8: Unit tests confirm regex matches smtp_password and skips ENC: prefixed values
- [x] ISC-9: All existing tests pass (104/104)

## Verification

- ISC-1: EncryptConfigCommand.cs line 9 — regex `(?:smtp_)?password:` matches both keys. Unit tests Regex_MatchesPasswordLine and Regex_MatchesSmtpPasswordLine pass.
- ISC-2: Regex uses negative lookahead `(?![ \t]*"?ENC:)`. Unit test Regex_SkipsAlreadyObfuscatedValues covers 4 cases (quoted/unquoted for both keys). All pass.
- ISC-3: OutputHandlerFactory.cs line 32 — `PasswordObfuscator.Deobfuscate(cfg.Get("smtp_password", ""))` confirmed present.
- ISC-4: TestConnectionCommand.cs line 75 — `PasswordObfuscator.Deobfuscate(output.Get("smtp_password", ""))` confirmed present.
- ISC-5: ServerContextBuilder.cs line 18 — `PasswordObfuscator.Deobfuscate(serverCfg.Get("password", ""))` confirmed present, no regression.
- ISC-6: FAIL — config/example.yaml email section (lines 102-107) only has smtp_host, smtp_port, from_addr, to_addrs, min_severity. Missing: smtp_username, smtp_password, use_tls.
- ISC-7: Grep for `.Get("*password*"` across src/ — only 3 hits, all wrapped in Deobfuscate(). No uncovered password fields.
- ISC-8: EncryptConfigCommandRegexTests class has 10 theory tests. All pass in 104/104 test run.
- ISC-9: `dotnet test` result: Failed: 0, Passed: 104, Skipped: 0, Total: 104.
