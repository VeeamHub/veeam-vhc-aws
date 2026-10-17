# Servers

Servers lets an operator add, edit, or delete a VBR or VBAWS server. The first save creates the config file when it does not exist yet.

## Sub-features

- `servers-empty` shows the empty state and the config path.
- `servers-add` opens the Add Server dialog, saves a server, and shows it in the table.
- `servers-persist` shows the same server after a reload, and the yaml on disk matches the form.
- `servers-cancel` closes the dialog without writing a new server.
- `servers-edit` rejects a URL change until the operator types the password again. Leaving the URL unchanged keeps the stored password.
- `servers-delete` removes a server after the confirm dialog.

## How to get to it (user POV)

- Choose **Servers** in the top navigation.
- From the dashboard empty state, choose **Add a server**.
- Open `/servers` directly.

## Driving it with vhc-verify

Preconditions:

- `vhc-verify doctor` prints `doctor=ok`.
- The data directory has no `veeam-vhc-aws.yaml`, or it has no server named `lab-vbr`.
- `EVIDENCE` is the directory printed by `vhc-verify evidence-dir`.

- **Open the page.** The heading is Servers. On an empty file the page shows `No servers configured yet.` and the warning `file not found — changes will create it`. Run `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser goto /servers` and `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser wait-heading Servers`.
- **Open the dialog.** Choose **+ Add Your First Server** when the list is empty. When a server already exists, choose **+ Add Server** instead. Run `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser click --role button --name "+ Add Your First Server"` and `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser wait-heading "Add Server"`.
- **Name.** Type `lab-vbr`. The URL field fills itself with `https://lab-vbr:9419` while the operator has not edited the URL. Run `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser fill --label Name --value lab-vbr` and `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser wait-value --label URL --value https://lab-vbr:9419`.
- **URL.** Replace the suggestion with `https://lab-vbr.example:9419`. Run `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser fill --label URL --value https://lab-vbr.example:9419`.
- **Account.** Type username `backupadmin` and password `verify-secret`. Leave **Verify SSL certificate** checked. It starts checked, and the saved row shows `yes`. Leave API version on `1.3-rev1`. Run `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser fill --label Username --value backupadmin` and `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser fill --label Password --value verify-secret`.
- **Save.** Choose **Save**. The flash says `Server 'lab-vbr' added` and the table lists `lab-vbr`. Run `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser click --role button --name Save` and `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser wait-text "Server 'lab-vbr' added"`.
- **Capture the save.** Run `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser screenshot --path "$EVIDENCE/servers/after-save.png"` and `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser snapshot --path "$EVIDENCE/servers/after-save.aria.txt"`.
- **Reload.** Open `/servers` again. The row is still there and `No servers configured yet.` is gone. Run `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser goto /servers`, `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser assert-text lab-vbr`, and `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser assert-absent "No servers configured yet."`.
- **Capture the reloaded page.** Run `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser screenshot --path "$EVIDENCE/servers/after-reload.png"` and `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser snapshot --path "$EVIDENCE/servers/after-reload.aria.txt"`.
- **Config file.** The yaml contains `lab-vbr`, `https://lab-vbr.example:9419`, `backupadmin`, `verify_ssl: true`, and a password prefixed with `ENC:`. It does not contain `verify-secret`. Run `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify assert-server --name lab-vbr --url https://lab-vbr.example:9419 --username backupadmin --password-absent verify-secret --verify-ssl true`. The command copies the file to `$EVIDENCE/servers/config.yaml`.
- **Cancel.** Choose **+ Add Server**, type a name, and choose **Cancel**. The dialog closes and that name is not in the table. Run `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser click --role button --name "+ Add Server"`, `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser fill --label Name --value discard-me`, and `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser click --role button --name Cancel`.
- **Delete.** On the `lab-vbr` row, choose **Delete lab-vbr**. The heading `Delete server?` appears. Choose **Confirm delete**. The flash says `Server 'lab-vbr' deleted`. Run `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser click --role button --name "Delete lab-vbr"`, `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser wait-heading "Delete server?"`, and `.claude/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser click --role button --name "Confirm delete"`.

## Gotchas

- The empty page shows **+ Add Your First Server** and does not show **+ Add Server**. After the first save, only **+ Add Server** is in the header.
- Typing the name overwrites the URL until the operator edits the URL. Wait for `https://<name>:9419` before replacing it. A name with a space does not auto-fill, because that host would be an invalid URL.
- The password field's accessible name is **Password**.
- **Verify SSL certificate** starts checked. A saved server shows `yes` in the table and `verify_ssl: true` in the yaml until the operator clears the box.
- A new server rejects a blank name, a blank URL, a URL that is not absolute, or a blank username. The dialog shows that error and does not write the file.
- Edit loads the password box as `********` and does not change the stored password unless the operator types a new one. Changing the URL without typing the password shows `A server URL change requires a new password.` and leaves the yaml unchanged. The row button is **Edit lab-vbr**.
- Clicking the dimmed area behind a dialog does not close it. **Cancel** and Escape do.
- The row button is **Delete lab-vbr**. The dialog button is **Confirm delete**.
- The duplicate-name flash is `Server '<name>' already exists`. Names are compared without case sensitivity.
