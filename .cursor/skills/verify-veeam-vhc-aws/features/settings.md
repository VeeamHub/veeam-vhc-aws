# Settings

Settings edits the `global` and `daily_summary` sections. The page also shows the config path and the state path. It does not restart the monitor.

## Sub-features

- `settings-labels` gives every field an accessible name, including the number boxes.
- `settings-save` writes the log level and log file, then shows them again after a reload.
- `settings-daily` toggles the daily summary checkbox. The schedule itself stays on the task, not this page.

## How to get to it (user POV)

- Choose **Settings** in the top navigation.
- Open `/settings`.

## Driving it with vhc-verify

Preconditions:

- `vhc-verify doctor` prints `doctor=ok`.
- The config file may be missing. The first save creates it.

- **Open the page.** The heading is Global Settings. **Log level** is a select. **Log file path** is a text box. The checkbox label is `Also log to console (stderr)`. Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser goto /settings` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser wait-heading "Global Settings"`.
- **Change two fields.** Set **Log level** to `INFO`. Set **Log file path** to `./ops.log`. Choose **Save Settings**. The flash says `Settings saved`. Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser select --label "Log level" --value INFO` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser fill --label "Log file path" --value ./ops.log` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser click --role button --name "Save Settings"`.
- **Reload.** Open `/settings` again. **Log level** is `INFO` and **Log file path** is `./ops.log`. The yaml contains both.
- **Proof.** Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser screenshot --path "$EVIDENCE/settings/saved.png"` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser snapshot --path "$EVIDENCE/settings/saved.aria.txt"`.

## Gotchas

- The Files table is read-only. The state path is where Alerts and Captures read `veeam-vhc-aws-state.json` unless `global.state_file` says otherwise. Under this helper that path is the project directory, because `dotnet run` sets the working directory there.
- Number boxes use `for` and `id`. `fill --label` uses the label text.
- Saving writes the logging map and the HTTP keys together. A field you did not edit is still written at the value shown on the page. The monitor reads `logging.level`. A save does not also write `global.log_level`.
- The daily summary line says the time is controlled by the scheduled task.
