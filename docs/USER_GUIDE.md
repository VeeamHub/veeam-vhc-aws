# Veeam VHC AWS user guide

Veeam VHC AWS watches your Veeam Backup & Replication (VBR) and Veeam Backup for AWS (VBAWS) servers and tells you when something goes wrong: a repository filling up or going unreachable, restore points falling out of policy, orphaned backups, worker sessions failing, credentials expiring. It alerts when something changes, and again when it's fixed. It doesn't repeat itself every five minutes.

## Already using it with YAML? Start here

Nothing you set up has changed, and you don't have to use the web UI.

- **The scheduled task works the same way.** It still runs every few minutes, reads the same `veeam-vhc-aws.yaml`, and sends alerts to the same outputs.

- **The web UI edits that same file.** Adding a server or changing a threshold in the UI writes to `veeam-vhc-aws.yaml`, and the next scheduled run picks the change up. You can keep editing YAML by hand, and the UI will show your edits.

- **The UI only runs while you have it open.** It isn't a service. Closing its console window stops it, and alerting carries on without it.

- **Passwords you save in the UI are encrypted** in the YAML with a key that only works on this machine (they show up as `ENC:...`). Plain-text passwords you typed by hand still work. Run `veeam-vhc-aws encrypt-config --config <file>` to encrypt them too.

What the UI adds: you can see current alerts at a glance, silence one with a click, and add servers and notification channels through forms instead of YAML.

## Open the web UI

**Windows (installed with `setup.ps1`):** open the Start Menu and click **VHC AWS Admin**. Accept the Windows administrator prompt. A console window opens and your browser goes to <http://127.0.0.1:9101>. Keep the console window open while you use the UI, and close it when you're done. If the UI is already running, clicking the shortcut again just opens the browser.

**From a command line:** open an elevated PowerShell and run

```powershell
& "$env:ProgramFiles\VHC\veeam-vhc-aws.exe" ui -c "$env:ProgramFiles\VHC\veeam-vhc-aws.yaml"
```

**Linux, macOS or Docker:** run `veeam-vhc-aws ui -c <path to your yaml>` and open <http://127.0.0.1:9101>.

### Why it asks for administrator rights

Setup locks the config file, the encryption key and the state file so that only Administrators and SYSTEM can read them, because they hold your server credentials. The UI has to read and write those files, so it runs elevated, the same way the scheduled task does.

### Opening it from another computer

By default the UI only listens on the server itself (`127.0.0.1`), so you RDP in and use a browser there. To manage it from your workstation instead, start it with a wider bind:

```powershell
& "$env:ProgramFiles\VHC\veeam-vhc-aws.exe" ui -c "$env:ProgramFiles\VHC\veeam-vhc-aws.yaml" --bind 0.0.0.0
```

The console prints a URL with a `?t=` token. Open that exact URL from your workstation. The token is stored in a cookie after the first visit. Anyone who has the token can change your config, so treat it like a password. Rotate it with `veeam-vhc-aws ui --regen-token`. Put the UI behind HTTPS if you use it across a network you don't trust.

## Set it up from scratch

The Dashboard shows a **Getting started** checklist until these four steps are done, and each step links to the page that does it.

1. **Add a server.** On **Servers**, click **+ Add Server**. Pick VBR or VBAWS, enter the address (for example `https://vbr01:9419`), and enter a user that can read the REST API. Leave **Verify SSL** on unless it's a lab server with a self-signed certificate.

2. **Let it connect.** The scheduled task contacts each server on its next run. The step is done once every server has a successful run on record. If a server never gets one, run `veeam-vhc-aws test-connection -c <yaml>` to see why.

3. **Tell it where to send alerts.** On **Outputs**, add at least one channel: a webhook (Slack, Teams, PagerDuty, ntfy, or generic), email, a JSON file, or Prometheus. Printing to the console (`json_stdout`) doesn't count, because nobody sees it when the scheduled task runs.

4. **Wait for the first scheduled run.** The task runs every 5 minutes by default. The checklist disappears once a run has landed and the other steps are done.

## Pages

### Dashboard

Overall health at a glance: how many servers you have, how many findings are open, how many problems are on record in Captures, and when the last run finished. Start here.

### Alerts

Every finding that's open right now, with its severity, server and resource. Click **Suppress** on a finding you know about and accept for now. A suppressed finding stops sending notifications and no longer counts toward the overall severity. Click **Unsuppress** to bring it back. The page refreshes itself every few seconds.

### Servers

The VBR and VBAWS servers being monitored. Add, edit or remove them here. If you change a server's address, enter its password again.

### Outputs

Where alerts go: webhooks, email, JSON files, Prometheus. Every output fires on every run. Webhooks and email only send when something is new or resolved. JSON and Prometheus always record the full current state.

### Thresholds

When a finding counts as a warning or as critical, for each monitor: repository health (for example, how full a repository can get), retention, and worker health. Changes apply on the next scheduled run.

### Captures

The history of every problem the monitors have reported, including ones that have since cleared, with when each was first and last seen and how many times. Use it to spot something that keeps coming back. You can suppress or unsuppress from here too, and filter to show only suppressed entries.

### Settings

Tool-wide settings: log level, API timeouts and retries, page sizes, and whether the daily summary is on. Most people never need to change these. Raise the session timeout if a very large VBR server times out.

### Help

This guide.

## Common questions

### Why am I not getting alerts?

Check these in order:

1. **Outputs.** Is there an output other than the console? Without one, findings only show up in the UI.

2. **Nothing changed.** Webhooks and email only send when a finding is new or resolved. If everything was already reported, a run sends nothing. Set `deduplicate: false` on an output to send on every run.

3. **Minimum severity.** Each output has a `min_severity`. Email defaults to `critical`, so it won't send warnings unless you lower it. Webhooks send everything by default.

4. **The finding is suppressed.** Suppressed findings stay on **Alerts** but are dimmed, with an **Unsuppress** button.

5. **The scheduled task isn't running.** If **Last Run** on the Dashboard is old, open Task Scheduler and check the **Veeam VHC AWS** task.

### How do I test a server?

Run `veeam-vhc-aws test-connection -c <yaml>`. It tries every configured server and explains failures, for example a wrong password, a blocked port, or a rejected certificate.

### How do I silence one alert?

On **Alerts**, click **Suppress** on that row. This works for one finding, not a whole server or monitor. To loosen a rule for everything, change it on **Thresholds** instead.

### Where are my files?

On a Windows install:

| What | Where |
|---|---|
| Program | `C:\Program Files\VHC\veeam-vhc-aws.exe` |
| Config | `C:\Program Files\VHC\veeam-vhc-aws.yaml` |
| State, logs, UI token | `C:\ProgramData\VHC\` |

The **Settings** page shows the exact config and state file paths this instance is using.

### Does closing the browser stop monitoring?

No. Monitoring runs from the scheduled task. Closing the browser, or the UI's console window, only stops the UI.

## More detail

The full reference, covering every config key, command and output format, is in the [README](https://github.com/VeeamHub/veeam-vhc-aws#readme). VHC AWS is a community tool from VeeamHub, not an officially supported Veeam product. Report problems at <https://github.com/VeeamHub/veeam-vhc-aws/issues>.
