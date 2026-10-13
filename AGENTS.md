# Agent notes

The operator surface to prove is the web admin UI, not the unit tests alone.

- Build with `dotnet build src/VeeamVhcAws/VeeamVhcAws.csproj`.
- Test with `dotnet test tests/VeeamVhcAws.Tests/`.
- Prove a UI change with `.cursor/skills/verify-veeam-vhc-aws/SKILL.md`. Launch, run doctor, drive the feature file, keep the evidence directory, then clean up.
- After the UI changes, run `/maintain-verification-skill` so the feature map stays aligned with the pages.
- The app targets `net10.0`. A machine without the SDK should run `bash .cursor/install.sh` before build or launch.
- `veeam-vhc-aws ui` reads and writes the config path from `-c`. Verification launches use a temp data directory. Do not point them at a real server config.
