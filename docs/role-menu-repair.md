# Repairing a missing dropdown role-menu panel

Use `/role-menu repair` when a saved native dropdown role menu still exists in MongoDB but its Discord panel was deleted. Repair is intentionally explicit and conservative: it restores a definitely missing panel without changing the menu's stable ID, title, description, configured roles, selection mode, or creation timestamp.

## Command

Run:

`/role-menu repair menu-id:<id> [target-channel:<channel>]`

The command is guild-only and requires **Manage Roles**, like the existing create/delete administration commands.

- If the saved message is missing but its channel still exists, omitting `target-channel` republishes into the saved channel.
- If the saved channel is gone, `target-channel` is required.
- An explicitly selected replacement must be a normal text channel in the same server.

BeanBot first presents an ephemeral confirmation. The confirmation shows the saved menu, configured roles, selection mode, replacement target, and the same stable menu ID that will be used by the replacement panel.

## Safety rules

Repair publishes only when BeanBot can positively establish that the saved panel is missing. A healthy saved panel is a no-op even if another target channel was supplied. If the saved location contains a message that has an unexpected author, message ID, guild/channel binding, or no longer has the expected BeanBot **Manage Roles** control, repair stops rather than guessing or creating a duplicate.

Transient or otherwise unknown Discord read failures also stop the operation before publication. BeanBot does not treat an uncertain read as proof that the panel is absent.

Immediately before publication, BeanBot reloads the persisted settings and current Discord state while holding the existing per-menu lifecycle coordinator. It rechecks:

- the original saved panel is still definitely missing;
- the saved configuration did not change while the confirmation was open;
- the administrator still has **Manage Roles** and remains above every configured role unless they own the server;
- BeanBot still has **Manage Roles** and remains above every configured role;
- all configured roles still exist and are assignable;
- the replacement channel still exists; and
- BeanBot has **View Channel**, **Send Messages**, **Embed Links**, and **Read Message History** in the replacement channel.

Repair serializes with create/publication, delete, and other menu lifecycle writes through the same menu coordinator. A second concurrent repair therefore re-reads the first repair's result instead of blindly posting another panel.

## Publication and persistence ordering

The replacement uses the existing menu ID and canonical public role-menu embed/button. BeanBot confirms or reconciles the replacement panel before updating MongoDB. The persisted update changes only the channel/message binding and normal `updatedAtUtc` metadata; the existing menu configuration and original `createdAtUtc` are preserved.

Discord sends are not blindly retried. If a send returns an ambiguous result, BeanBot performs the existing bounded recent-message reconciliation for the same stable menu ID. A later rerun with the same replacement target uses the same reconciliation path, so an already-created replacement can be adopted instead of duplicated.

If the replacement exists but the MongoDB binding update fails or has an unknown outcome, BeanBot keeps the replacement and does not delete the old saved configuration. Rerun the same repair with the same target to reconcile the binding. If the saved configuration disappears independently during repair, the normal publication rollback rules apply.

## What repair does not do

Repair does not:

- move or recreate a healthy panel;
- change the configured role allowlist, title, description, or selection mode;
- silently remove broken roles from a saved menu;
- bulk-repair every menu in a server;
- scan channels in the background;
- auto-heal deleted panels without an administrator request; or
- grant or revoke any member roles.

Use the normal edit/create/delete workflows for configuration changes rather than treating repair as a general migration or move operation.

## Manual smoke check

In a Discord test guild:

1. Publish a role menu and record its stable menu ID.
2. Run `/role-menu repair` while the panel is healthy and confirm no replacement is posted.
3. Delete the public panel manually, run repair without a target, review the ephemeral confirmation, and confirm exactly one replacement appears in the saved channel with the same menu ID and configuration.
4. Run repair again and confirm the repaired panel is treated as healthy and no duplicate appears.
5. Delete the panel and its channel, then confirm repair requires a replacement `target-channel` and succeeds in the selected text channel.
6. Open a confirmation, then delete a configured role or remove the administrator/BeanBot hierarchy or required target-channel permissions before pressing **Repair**. Confirm publication is refused without changing persistence.
7. If practical, force an ambiguous Discord send or MongoDB write failure. Confirm BeanBot reports the uncertain state, does not blindly retry, and a later repair using the same target reconciles the existing stable-ID panel instead of creating a second one.
