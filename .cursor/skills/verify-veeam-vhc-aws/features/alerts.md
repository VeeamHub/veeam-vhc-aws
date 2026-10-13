# Alerts

Alerts lists active findings and can suppress one. Suppression is stored in the state file the monitor task writes. The page does not query a Veeam server itself.

## Sub-features

- `alerts-empty` says there are no alerts yet on a new install.
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
- `alerts-filter` and `alerts-suppress` need a state file at the path shown on Settings and printed by launch as `state`. The default is `./veeam-vhc-aws-state.json` in the process working directory. `dotnet run` uses the project directory, so this helper's file is `<repo>/src/VeeamVhcAws/veeam-vhc-aws-state.json`. Write it after launch, then reload `/alerts`. The page reads the file on each load. This checkout has no Veeam server, so a fresh launch cannot produce findings. Report that precondition instead of inventing rows through the API.

- **Empty page.** The heading is Alerts and the page says `No alerts yet.` It does not say `No alerts match the current filters.` Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser goto /alerts` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser wait-heading Alerts` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser assert-text "No alerts yet."`.
- **Search.** Type into the box labeled **Search**. Its placeholder is `resource or message`. Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser fill --label Search --value repo`. Matching is against the resource and the message, and it updates as the operator types.
- **Severity and server.** The **Severity** and **Server** dropdowns are labeled selects. **Server** only lists names present on findings or captured errors. Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser select --label Severity --value warning`.
- **Suppress.** On a row, choose **Suppress** plus the resource name, for example **Suppress repo-disk**. Success flashes `Suppressed repo-disk`, and the button becomes **Unsuppress repo-disk**. The finding does not have to exist under `captured_errors`. Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser click --role button --name "Suppress repo-disk"` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser wait-text "Suppressed repo-disk"`.
- **Confirm the side effect.** Reload `/alerts`. The row stays suppressed. The state file in the data directory has the key under `suppressions`. Do not call `POST /api/alerts/{key}/suppress`.
- **Proof.** Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser screenshot --path "$EVIDENCE/alerts/list.png"` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser snapshot --path "$EVIDENCE/alerts/list.aria.txt"`.

## Gotchas

- On an empty install the Suppress button does not exist. The copy is `No alerts yet.` A search that excludes every row says `No alerts match the current filters.`
- Suppress writes the key into `suppressions` when that key is in `findings` or in `captured_errors`. A finding that was never captured can be silenced from this page. The flash names the resource, not a requirement that the key live in `captured_errors`.
- The table lists active findings, not the capture log. Captures (`/captures`) is a different page.
- Two rows with the same resource get distinct names, such as `Suppress repo-disk (abc)`.
- The page reloads from disk every 5 seconds and replaces the table. Finish a click before that refresh, then reload yourself to confirm.
- The search label wraps its input. Severity and Server do too. Use `fill --label` and `select --label`, not a CSS nth-child.
