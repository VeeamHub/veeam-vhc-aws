---
task: Audit and fix password obfuscation coverage
slug: 20260422-000000_password-obfuscation-audit
effort: standard
phase: complete
progress: 9/9
mode: interactive
started: 2026-04-22T00:00:00Z
updated: 2026-04-22T00:10:00Z
---

## Context

Audit and fix password obfuscation coverage in veeam-vhc-aws. The EncryptConfigCommand regex previously only matched `password:` YAML keys. Three changes were needed: extend regex to also match `smtp_password:`, add Deobfuscate() wrapping in OutputHandlerFactory and TestConnectionCommand, update example.yaml, and add unit tests for the regex behavior.

### Risks
- Regex backtracking could allow ENC: values to slip past the negative lookahead — resolved by using `(?![ \t]*"?ENC:)` to catch both quoted and unquoted ENC: values with leading whitespace.

## Criteria

- [x] ISC-1: EncryptConfigCommand regex pattern updated to match `smtp_password:` key
- [x] ISC-2: EncryptConfigCommand regex skips unquoted `ENC:` values for `password:` key
- [x] ISC-3: EncryptConfigCommand regex skips unquoted `ENC:` values for `smtp_password:` key
- [x] ISC-4: EncryptConfigCommand regex skips quoted `ENC:` values for both keys
- [x] ISC-5: OutputHandlerFactory wraps smtp_password with PasswordObfuscator.Deobfuscate()
- [x] ISC-6: TestConnectionCommand wraps smtp_password with PasswordObfuscator.Deobfuscate()
- [x] ISC-7: ServerContextBuilder already wraps password with Deobfuscate() — verified
- [x] ISC-8: example.yaml email section includes smtp_username and smtp_password commented fields
- [x] ISC-9: All 104 tests pass including 10 new EncryptConfigCommand regex tests

## Verification

- Grep confirms all 3 password Get() calls have Deobfuscate() wrapping
- dotnet test: Passed 104, Failed 0
- Regex logic verified: possessive/atomic approach replaced with `(?![ \t]*"?ENC:)` lookahead
