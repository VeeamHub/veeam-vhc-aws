# Outputs

Outputs adds the notification handlers a monitor run will call. The page writes the `output` section of the same yaml file as Servers.

## Sub-features

- `outputs-empty` shows the empty state.
- `outputs-add-json` saves a JSON file handler without sending anything over the network.
- `outputs-add-webhook` saves a webhook URL and template.
- `outputs-cancel` discards the dialog.
- `outputs-delete` removes a handler after confirmation.

## How to get to it (user POV)

- Choose **Outputs** in the top navigation.
- Open `/outputs`.

## Driving it with vhc-verify

Preconditions:

- `vhc-verify doctor` prints `doctor=ok`.
- No handler should already use the proof path `./veeam-vhc-aws-output.json` if you need the empty-state button. After a server save, the file exists and the empty button may already be gone. Use **+ Add Output** in that case.

- **Open the page.** The heading is Output Handlers. An empty config shows `No output handlers configured.` Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser goto /outputs` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser wait-heading "Output Handlers"`.
- **Open the dialog.** Choose **+ Add Your First Handler** or **+ Add Output**. The heading is `Add Output Handler`. The type starts as Webhook. Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser click --role button --name "+ Add Output"` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser wait-heading "Add Output Handler"`.
- **JSON file, no network.** Set **Type** to `json_file`. Set **File path** to `./veeam-vhc-aws-output.json`. Choose **Save**. The flash says `Output handler added` and the table shows `json_file`. Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser select --label Type --value json_file`, `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser fill --label "File path" --value ./veeam-vhc-aws-output.json`, and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser click --role button --name Save`.
- **Reload and file.** Open `/outputs` again and confirm `json_file` is still listed. The config yaml contains `json_file` and `veeam-vhc-aws-output.json`. Saving does not create that JSON output file. The monitor creates it on a later run.
- **Webhook.** Add another handler, leave type `webhook`, set **URL** to `https://example.invalid/hook`, and set **Template** to `ntfy`. **Save** is rejected with `Webhook URL is required` when the URL is blank.
- **Proof.** Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser screenshot --path "$EVIDENCE/outputs/list.png"` and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser snapshot --path "$EVIDENCE/outputs/list.aria.txt"`. Copy the yaml next to them and confirm the handler is in it.
- **Delete.** Choose **Delete** on the row, wait for `Delete output handler?`, then choose **Delete** inside that dialog. The flash says `Output handler deleted`. Run `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser click --role button --name Delete`, `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser wait-heading "Delete output handler?"`, and `.cursor/skills/verify-veeam-vhc-aws/scripts/vhc-verify browser click --role button --name Delete --within-heading "Delete output handler?"`.

## Gotchas

- Changing **Type** clears the fields and applies that type's defaults. Set the type before the path or URL.
- Webhook **Save** requires a URL. Email **Save** requires an SMTP host. JSON file **Save** requires a path. Prometheus has defaults and can save without extra typing.
- SMTP password and similar secret fields are stored with the `ENC:` prefix once the operator types them. The form shows `********` after a reload.
- **Min Severity** on a webhook defaults to `warning`. The select values are `ok`, `warning`, and `critical`.
- The dialog's **URL** label is the same word as the server form's **URL** label. Only one dialog is open. Do not drive Servers and Outputs in two browsers against one circuit.
- A webhook save does not POST to the URL. The next `veeam-vhc-aws all` run would. Do not use a live customer webhook in this proof.
