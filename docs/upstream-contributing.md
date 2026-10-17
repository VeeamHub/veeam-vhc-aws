# Upstream contributions: product vs. contributor tooling

This fork carries tooling that helps contributors and coding agents work on the project. VeeamHub
consumers do not need it to build, run, or test the product. This page lists which paths are which,
so a pull request to [VeeamHub/veeam-vhc-aws](https://github.com/VeeamHub/veeam-vhc-aws) can leave
the tooling out or send it on its own. Nothing here should be deleted from the fork.

## Product

These paths make up the shipped tool, its tests, and its build and release pipeline. A product PR
to VeeamHub contains only changes from this list.

| Path | What it is |
|---|---|
| `src/**` | The `veeam-vhc-aws` CLI, monitors, outputs, and the web admin UI |
| `tests/**` | Unit tests (`VeeamVhcAws.Tests`) and bUnit page tests (`VeeamVhcAws.UiTests`) |
| `config/example.yaml` | Reference config (keep in sync with `src/VeeamVhcAws/config/example.yaml`, which is embedded for `setup --template`) |
| `setup.ps1`, `build.ps1`, `build.sh`, `Dockerfile` | Installer, build scripts, container image |
| `VeeamVhcAws.sln`, `.gitattributes`, `.gitignore` | Solution and repo settings |
| `.github/**` | CI, release, security workflows, Dependabot, issue and PR templates |
| `README.md`, `CONTRIBUTING.md`, `DCO.md`, `LICENSE` | User and contributor docs |
| `docs/**` | Design notes, images, and this page |

## Contributor and agent tooling

These paths help a person or a coding agent work on the repo. They are not loaded by the build, the
tests, or the shipped binary.

| Path | What it is | Already on VeeamHub `dev`? |
|---|---|---|
| `.claude/skills/verify-veeam-vhc-aws/**` | Skill that launches the admin UI on a temp config and drives each page (`vhc-verify`) | No |
| `.claude/MEMORY/**` | Agent work notes and PRDs from earlier sessions | Yes |
| `.cursor/**` (`environment.json`, `install.sh`, `skills/`) | Cursor Cloud Agent setup: installs the .NET 10 SDK and points at the verify skill | No |
| `AGENTS.md` | Build, test, and UI-verification notes for coding agents | No |
| `ISA.md` | Ideal-state notes for the project (agent planning document, not user docs) | Yes |
| `MEMORY/**` | Agent work notes and PRDs | Yes |
| `.playwright-mcp/**` | Captured page snapshots from an earlier browser session | Yes |

`ISA.md` and the `MEMORY` folders describe intent at the time they were written. Where they disagree
with the code or `README.md`, the code and the README win.

## Shaping a VeeamHub pull request

1. Branch from `upstream/dev`, not from this fork's `dev`, so fork-only commits do not ride along.
2. Cherry-pick the product commits. Commits in this fork keep product and tooling changes apart
   (for example `fix(...)` / `test(...)` vs `chore(verify): ...`), so a product commit should not
   touch any tooling path.
3. Before pushing, confirm the diff has no tooling paths:

   ```bash
   git diff --name-only upstream/dev...HEAD | grep -E '^(\.cursor/|\.claude/|\.playwright-mcp/|MEMORY/|AGENTS\.md$|ISA\.md$)' \
     && echo "tooling paths in diff" || echo "product only"
   ```

4. If a product change also needs a tooling update (for example the verify skill's feature files after
   a UI change), send that as a second PR that only touches tooling paths, titled `chore(tooling): ...`.
   VeeamHub maintainers can take it, defer it, or decline it without blocking the product fix.
5. Do not add new tooling paths to a product PR. If VeeamHub decides not to carry tooling at all, the
   fork keeps it and the product PRs are unaffected.
