# Alerts

Alerts lists active findings and can suppress one. Suppression is stored in the state file the monitor task writes. The page does not query a Veeam server itself.

## Sub-features

- `alerts-empty` shows the empty filter result on a new install.
- `alerts-filter` narrows the table by severity, server, and search text.
- `alerts-suppress` silences one finding and shows an Unsuppress button.
- `alerts-refresh` reloads the table from the state file.

## How to get to it (user POV)

- Choose **Alerts** in the top navigation.
- Open `/alerts`.

## Driving it with vhc-verify

Preconditions:

- `vhc-verify doctor` prints `doctor=ok`.
- `alerts-empty` needs no findings.
- `alerts-filter` and `alerts-suppress` need a state file already written by `veeam-vhc-aws all` (or by the scheduled task) at the state path shown on Settings. Put that file in place before launch. The UI resolves the path once, at process start, from `global.state_file` or `./veeam-vhc-aws-state.json` in the data directory. This checkout has no Veeam server, so a fresh launch cannot produce findings. Report that precondition instead of inventing rows.

- **Empty page.** The heading is Alerts and the page says `No alerts match the current filters.` Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser goto /alerts` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser wait-heading Alerts` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser assert-text "No alerts match the current filters."`.
- **Search.** Type into the box labeled **Search**. Its placeholder is `resource or message`. Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser fill --label Search --value repo`. Matching is against the resource and the message, and it updates as the operator types.
- **Severity and server.** The **Severity** and **Server** dropdowns are labeled selects. **Server** only lists names present on findings or captured errors. Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser select --label Severity --value warning`.
- **Suppress.** On a row, choose **Suppress**. Success flashes `Suppressed` plus the first characters of the key, and the button becomes **Unsuppress**. Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser click --role button --name Suppress` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser wait-text Suppressed`.
- **Confirm the side effect.** Reload `/alerts`. The row stays suppressed. The state file in the data directory has the key under `suppressions`. Do not call `POST /api/alerts/{key}/suppress`.
- **Proof.** Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser screenshot --path "$EVIDENCE/alerts/list.png"` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser snapshot --path "$EVIDENCE/alerts/list.aria.txt"`.

## Gotchas

- On an empty install the Suppress button does not exist. Stopping at the empty copy is the honest result.
- Suppress succeeds only when the finding key is also in `captured_errors`. Otherwise the flash is `Could not suppress` and `not found in captured_errors`. A finding that was never captured cannot be silenced from this page.
- The table lists active findings, not the capture log. Captures (`/captures`) is a different page.
- The page reloads from disk every 5 seconds and replaces the table. Finish a click before that refresh, then reload yourself to confirm.
- The search label wraps its input. Severity and Server do too. Use `fill --label` and `select --label`, not a CSS nth-child.
