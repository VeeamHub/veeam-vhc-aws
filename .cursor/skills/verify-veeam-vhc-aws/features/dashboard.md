# Dashboard

Dashboard is the home page. It shows whether any findings are active, counts servers and captured errors, and lists each configured server.

## Sub-features

- `dashboard-empty` tells the operator to add a server.
- `dashboard-clear` shows an OK banner when no finding is an error, critical, or warning.
- `dashboard-servers` lists each server's name, type, URL, and last successful run.
- `dashboard-link` opens Servers from the empty-state link.

## How to get to it (user POV)

- Open the UI. The first page is the dashboard.
- Choose **Dashboard** in the top navigation.
- Open `/`.

## Driving it with vhc-verify

Preconditions:

- `vhc-verify doctor` prints `doctor=ok`.
- For `dashboard-servers`, the Servers feature has already saved `lab-vbr`. An empty launch only proves `dashboard-empty`.

- **Empty home.** The heading is Dashboard. The banner contains `All monitors clear` and `0 server(s), 0 findings`. The servers section says `No servers configured.` and links **Add a server**. Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser goto /` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser wait-heading Dashboard` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser assert-text "No servers configured."`.
- **Open Servers from the banner link.** Choose **Add a server**. The Servers heading appears. Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser click --role link --name "Add a server"` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser wait-heading Servers`.
- **After a server exists.** Return home. The server card shows `1` and `1 VBR, 0 VBAWS`. The table contains `lab-vbr` and `https://lab-vbr.example:9419`. Last successful run is `—` until a monitor run records one. Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser goto /` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser assert-text lab-vbr` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser assert-text "1 VBR, 0 VBAWS"`.
- **Proof.** Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser screenshot --path "$EVIDENCE/dashboard/home.png"` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser snapshot --path "$EVIDENCE/dashboard/home.aria.txt"`. The header `VHC AWS` is in both files.

## Gotchas

- The OK banner is shown when there are zero error, critical, and warning findings. It does not mean a monitor has run. Last run reads `no runs yet` until the state file has `last_run`.
- The page reloads its data every 10 seconds. Wait for the heading or the server name, not for a fixed sleep.
- `Add a server` is a link to `/servers`. It does not open the Add Server dialog by itself.
- Counts come from the config and state files of this process. A server saved in a different run's data directory does not appear here.
