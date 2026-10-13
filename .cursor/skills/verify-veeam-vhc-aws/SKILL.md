---
name: verify-veeam-vhc-aws
description: "Drive the Veeam VHC AWS web admin UI (Blazor Server at /) from an empty config and prove a page saved what the operator sees. Use when changing the admin UI, server or output forms, thresholds, alerts, or config files the UI writes."
---

# Verify Veeam VHC AWS

The primary surface is the web admin UI started by `veeam-vhc-aws ui`. It is Blazor Server on a localhost port, default 9101. The CLI (`all`, `setup`, `serve`, and the other commands in the README) is a second surface. This skill does not drive the Spectre terminal UI.

Run every command from the repo root. The helper is `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify`.

Keep the map honest with `/maintain-verification-skill` after UI changes.

## Launch

Launch reaches a usable empty UI. It does not create a config file. The Servers page creates `veeam-vhc-aws.yaml` on the first save.

```bash
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify launch
```

That command does all of the following.

1. It creates `/tmp/vhc-verify/runs/<run-id>/data/` and uses that directory as the process working directory.
2. It picks a free loopback port. It does not reuse 9101 if something else owns it.
3. It runs this from the data directory, with `<repo>` as the checkout and `<port>` as the chosen port.

```bash
dotnet run --project <repo>/src/VeeamVhcAws/VeeamVhcAws.csproj -- ui \
  -c <data>/veeam-vhc-aws.yaml \
  --bind 127.0.0.1 \
  --port <port> \
  --no-browser
```

4. It waits until `GET /healthz` returns `{"status":"ok"}` and `GET /` contains both `VHC AWS` and `Dashboard`. The console line `Veeam VHC AWS Admin UI:` prints before the server accepts connections. That line is not readiness.

Loopback has no auth token. Do not pass `--bind 0.0.0.0`. A non-loopback bind reads or creates `~/.vhc/ui-token.txt`, which is shared by every UI on the machine.

The state file defaults to `./veeam-vhc-aws-state.json` and is resolved from the process working directory when the process starts. Two UIs can run together only when each launch has its own `VHC_VERIFY_RUN_ID`, its own data directory, and its own port. Leave the variable unset for a single run. The helper records that run in `/tmp/vhc-verify/latest`.

Requires the .NET 10 SDK (`dotnet` on `PATH`) and Google Chrome (`CHROME_BIN` or a standard install path). On a Cloud Agent checkout that has neither SDK nor packages, run `bash .cursor/install.sh` once.

## Doctor

Run this before the first click, and again after a drive that behaved strangely. It only reads.

```bash
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify doctor
```

Exit 0 prints `doctor=ok` plus the URL, pid, port, config path, and whether the config file exists. Exit 1 means the process is dead, the port is not owned by that process, `/healthz` is not ok, the homepage is missing the shell, or the bind is not loopback. Do not drive a failed doctor. Launch again, or clean up and launch again.

## Drive

Read `features/README.md`, then the feature file. Prefer the commands in that file over ad hoc clicks.

The browser talks to the UI with role, accessible name, and the visible label text. Most labels are not tied to their inputs with `for` or by wrapping the control. `fill` and `select` find the label text, then the control in that label or in the same `form-row`.

```bash
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser goto /servers
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser click --role button --name "+ Add Your First Server"
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser fill --label Name --value lab-vbr
```

`click` uses an exact accessible name. `wait-heading` waits for an exact heading. `wait-text` waits for a substring.

Do not prove a save with `POST /api/alerts` or `GET /api/config`. Those routes exist, and they are not the page the operator uses.

Buttons are inert until the Blazor circuit connects. `browser goto` waits for the page websocket and for the `Loading…` line to disappear before it returns.

## Evidence

Proof shows the action and the state after it, plus the file the UI wrote.

Save screenshots, ARIA snapshots, and the copied config under the directory printed by:

```bash
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify evidence-dir
```

That path is `/tmp/vhc-verify/evidence/<run-id>/`. Put a feature's files in a subdirectory, for example `servers/after-reload.png`.

For a server save, the proof is all of the following.

- The flash `Server 'lab-vbr' added` and the server row.
- A reload of `/servers` that still shows the row and does not show `No servers configured yet.`
- `servers/config.yaml`, copied from the UI's config file, containing the name, URL, and username, a password that starts with `ENC:`, and not the plaintext password.

```bash
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify assert-server \
  --name lab-vbr \
  --url https://lab-vbr.example:9419 \
  --username backupadmin \
  --password-absent verify-secret
```

A dry run does not exist for this UI. Saving a server writes the yaml file. Do not point the config path at a real operator config.

## Cleanup

```bash
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify cleanup
```

Cleanup sends `SIGTERM` to the UI process group and the browser daemon started for this run id. It deletes `/tmp/vhc-verify/runs/<run-id>/` only. It leaves `/tmp/vhc-verify/evidence/<run-id>/` in place. It does not kill other `dotnet` processes.

Run cleanup after a failed launch too, so the port and the Chrome process do not linger. Set `VHC_VERIFY_RUN_ID` to the same value you launched with when more than one run exists.

## Helpers

Invoke the helper by path. `chmod +x` is already set in git.

```bash
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify launch
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify doctor
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify evidence-dir
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser goto /servers
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser click --role link --name Servers
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser click --role button --name Save
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser click --role button --name Delete --within-heading "Delete server?"
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser fill --label Name --value lab-vbr
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser select --label Type --value json_file
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser wait-heading "Add Server"
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser wait-text "Server 'lab-vbr' added"
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser wait-value --label URL --value https://lab-vbr:9419
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser wait-hidden "No servers configured yet."
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser assert-text lab-vbr
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser assert-absent "No servers configured yet."
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser screenshot --path /tmp/vhc-verify/evidence/<run-id>/servers/page.png
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser snapshot --path /tmp/vhc-verify/evidence/<run-id>/servers/page.aria.txt
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify assert-server --name lab-vbr --url https://lab-vbr.example:9419 --username backupadmin --password-absent verify-secret
.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify cleanup
```

`browser` starts `scripts/browser-daemon.mjs` on first use. The daemon uses `playwright-core` and the system Chrome. The first browser command runs `npm install --omit=dev` in `scripts/` if `node_modules` is missing. Cleanup stops the daemon.

Unit tests for the monitors and config loader are separate from this UI proof.

```bash
dotnet test tests/VeeamVhcAws.Tests/
```
