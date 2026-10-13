# Veeam VHC AWS verification map

This directory is the maintained source for verifying the web admin UI. Read this index, then use the matching feature file.

## Baseline preconditions

- Launch with `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify launch` so the UI starts from an empty data directory.
- Require `doctor=ok` before the first click.
- Drive only the run this helper started. A second run needs its own `VHC_VERIFY_RUN_ID`.
- The checked-out app is the one under test. Do not point `-c` at an operator's real `veeam-vhc-aws.yaml`.

## Driving conventions

- Start from the empty UI unless the feature file names a precondition.
- Use the exact `vhc-verify browser` commands in the feature file.
- Prefer headings, button names, link names, and label text. Do not click by coordinates.
- After a mutation, reload the page and read the config file the UI wrote.
- Leave proof files in the evidence directory. Cleanup does not delete them.

## Proof and skip reporting

- Record the feature file name with the evidence.
- A flash message by itself is not proof. Reload the page or open the other page that shows the same data.
- If a path cannot be reached, report the command and the missing precondition. Do not substitute a different page and call the feature verified.
- `GET /api/config` and `POST /api/alerts/{key}/suppress` are not proof. The operator uses the pages below.

## Features

- [Servers](./servers.md) adds, edits, and deletes a VBR or VBAWS server. This is the path that creates a config file from an empty directory.
- [Dashboard](./dashboard.md) shows the status banner, finding counts, and the server list.
- [Alerts](./alerts.md) filters findings and suppresses one. An empty install has no rows.
- [Outputs](./outputs.md) adds, edits, and deletes a notification handler.
- [Thresholds](./thresholds.md) edits repo, retention, and worker thresholds.
- [Settings](./settings.md) edits logging, HTTP tuning, and the daily summary toggle.
- [Captures](./captures.md) lists captured errors and suppresses one. An empty install has no rows.
