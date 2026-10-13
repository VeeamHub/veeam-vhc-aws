# Thresholds

Thresholds edits the numbers the monitors compare against. The page writes `repo_health`, `retention`, or `worker_health` into the yaml file. The next monitor run picks the new values up. The page does not run the monitors.

## Sub-features

- `thresholds-repo` saves repo free-space and lookback fields.
- `thresholds-retention` saves retention multipliers and the orphan toggle.
- `thresholds-worker` saves VBAWS worker failure thresholds.
- `thresholds-tabs` switches sections without saving.

## How to get to it (user POV)

- Choose **Thresholds** in the top navigation.
- Open `/thresholds`.

## Driving it with vhc-verify

Preconditions:

- `vhc-verify doctor` prints `doctor=ok`.
- The config file may be missing. The first save creates it.

- **Open the page.** The heading is Alert Thresholds. The visible section is Repo Health. Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser goto /thresholds` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser wait-heading "Alert Thresholds"` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser wait-heading "Repo Health"`.
- **Change a number.** Set **Free space warning threshold (% free)** to `17`. Choose **Save Repo Health**. The flash says `Repo Health thresholds saved`. Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser fill --label "Free space warning threshold" --value 17` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser click --role button --name "Save Repo Health"`.
- **Reload.** Open `/thresholds` again. The same field still shows `17`. The yaml contains `17` under `repo_health`.
- **Other tabs.** Choose **Retention**, then **Save Retention**. The flash says `Retention thresholds saved`. Choose **Worker Health**, then **Save Worker Health**. The flash says `Worker Health thresholds saved`. Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser click --role button --name Retention` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser click --role button --name "Save Retention"`.
- **Proof.** Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser screenshot --path "$EVIDENCE/thresholds/repo.png"` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser snapshot --path "$EVIDENCE/thresholds/repo.aria.txt"`.

## Gotchas

- Repo Health, Retention, and Worker Health are both tab buttons and headings. `wait-heading` is the heading. `click --role button` is the tab. **Save Repo Health** is a different button from the tab.
- Only the active tab's fields are in the page. Switch tabs before filling a retention or worker field.
- Number boxes are not wrapped by their labels. `fill --label` uses the label text in that `form-row`. A short prefix is enough when it is unique. `Free space warning threshold` does not match the critical field.
- Defaults appear even when the yaml has no section yet. They are the in-page defaults (`15` warning, `5` critical on repo) until a save writes them.
- The muted line under the heading says changes apply on the next scheduled run. Saving here does not contact Veeam.
- Enabled checkboxes use a wrapping label (`Enabled`, `Include external repositories`, and the others). Click them with `click --role checkbox` and the label's accessible name.
