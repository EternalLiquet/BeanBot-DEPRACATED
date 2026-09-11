# Dropdown role menus

BeanBot can publish persistent Discord panels that let members add or remove an administrator-approved set of roles. These panels are separate from the existing reaction-role system; legacy reaction-role messages and commands continue to work unchanged.

## Requirements

The administrator running `/role-menu create`, `/role-menu edit`, or `/role-menu delete` must:

- run the command in a server, not a direct message;
- have the server-level **Manage Roles** permission; and
- have a highest role above every role configured in the menu, unless they own the server.

BeanBot must have:

- **Manage Roles** at the server level;
- a highest role above every role configured in the menu; and
- **View Channel**, **Send Messages**, **Embed Links**, and **Read Message History** in the target channel.

Discord does not allow BeanBot to assign `@everyone`, integration-managed roles, or roles at or above BeanBot's highest role. BeanBot validates these rules during setup, immediately before publication or an edit, when a member opens a panel, and immediately before applying a selection.

## Create and publish a panel

1. Run `/role-menu create`.
2. In Discord's setup form, enter a title, optionally enter a description, choose 1–25 existing roles, choose single- or multiple-selection mode, and choose a normal text channel.
3. Review the private preview.
4. Select **Publish**. BeanBot rechecks roles and channel permissions, publishes the public panel, and saves its configuration in MongoDB.

The preview expires after 10 minutes. BeanBot holds at most 64 previews at once and replaces an administrator's previous preview in the same server when they create a new one. A failed persistence write rolls back the newly posted panel when Discord permits it. If BeanBot cannot confirm whether Discord posted the panel or MongoDB saved its settings, it closes the preview and disables automatic retry to avoid creating a duplicate. Inspect the target channel, remove any orphaned panel, and confirm the saved state before creating a replacement.

Each public panel contains a stable **Manage Roles** button and its menu ID in the embed footer. Saved settings include the server, channel, message, title, description, allowlisted role IDs, selection mode, and UTC timestamps, so published panels continue to work after BeanBot restarts.

## Edit a published panel

Run `/role-menu edit` to choose from the 25 newest saved menus. For an older menu, copy the stable ID from its panel footer and run `/role-menu edit menu-id:<id>`.

BeanBot first shows the menu's current title, description, roles, and selection mode privately. Select **Edit values** to open a pre-filled form, then change any of these fields:

- title;
- optional description;
- 1–25 self-assignable roles; and
- multiple- or single-selection mode.

Editing is intentionally in place. BeanBot keeps the same saved menu ID, server, channel, public message, and original creation timestamp. Moving a panel to another channel is not supported by edit; delete and recreate the panel when its location must change.

When the form is submitted, BeanBot reloads the saved menu and rechecks the administrator permission, current role existence and hierarchy, BeanBot's role permissions, channel permissions, and the exact public message identity. The edit runs under the same exclusive per-menu write coordination used by publication and deletion, so it cannot race a member role mutation or another menu-level write. Member submissions also reload persisted settings before any role change, so a selector opened before an edit cannot grant a role that the edited menu no longer allows.

Persisted settings are the authorization source of truth. BeanBot saves the replacement configuration before modifying the existing public message. This gives failures deterministic behavior:

- if the persistence write cannot be confirmed, BeanBot leaves the public panel untouched and asks the administrator to inspect persisted state before retrying;
- if persistence succeeds but the panel is definitely missing or no longer matches the expected BeanBot panel, the new configuration remains authoritative and the unrelated/missing message is not modified;
- if the Discord message update has an ambiguous outcome, BeanBot does not retry, roll the saved configuration back, or publish a replacement panel; inspect the existing message and rerun the same edit to reconcile it in place;
- rerunning the same edit is safe because it targets the same stable saved menu and same existing message rather than creating another panel.

Edit previews are bounded and expire after 10 minutes. All persistence and Discord work remains under the role-menu interaction timeout and application-shutdown ownership.

## Member behavior

Selecting **Manage Roles** opens a private selector bound to that member and panel. Current roles from that menu are preselected.

- In **multiple** mode, any combination of the configured roles is allowed.
- In **single** mode, choosing a new configured role replaces the old configured role. BeanBot adds the replacement first and keeps the old role if the add fails.
- **Clear menu roles** removes every currently assigned role from this menu.
- Roles that are not configured in the menu are never added or removed.

Submissions for the same member are serialized, including submissions from overlapping menus. Different members may update roles from the same menu concurrently. Publication, editing, and deletion take an exclusive menu lifecycle lock so they cannot race member changes or resurrect stale configuration. Before changing anything, BeanBot reloads the persisted configuration, confirms the original panel still exists and belongs to BeanBot, fetches current member roles, revalidates role hierarchy, and rejects malformed or non-allowlisted values. After every valid submission, including a no-op or interrupted mutation, BeanBot performs a separate bounded read of Discord's current member roles. It reports confirmed results from that observed state and explicitly asks the member to reopen the menu when the final state cannot be confirmed.

## Delete a panel

Run `/role-menu delete` to choose from the 25 newest saved menus. For an older menu, copy the ID from its panel footer and run `/role-menu delete menu-id:<id>`.

Deletion requires a private confirmation. BeanBot deletes a matching BeanBot-owned panel before removing its saved configuration. A missing panel is treated as already removed. If the referenced message no longer looks like the saved BeanBot panel, it is left untouched while the stale configuration is removed. If Discord denies panel deletion, the saved configuration is retained so an administrator can correct permissions and retry.

## Manual smoke check

Use a test server with BeanBot's role below one test role and above two other test roles.

1. Confirm `/role-menu create`, `/role-menu edit`, and `/role-menu delete` are unavailable to a member without **Manage Roles** and cannot run in a direct message.
2. Confirm setup rejects `@everyone`, a managed role, the role above BeanBot, and a role at or above a non-owner administrator.
3. Publish a two-role multiple menu and confirm the preview is private while the panel is public in the selected channel.
4. Run `/role-menu edit` for that panel, confirm the current values are shown privately, then rename it, remove one role, add another, and switch selection mode. Confirm the public message ID and footer menu ID are unchanged.
5. Open a member selector before an edit removes one role, submit that now-stale role after the edit, and confirm BeanBot refuses it without changing member roles.
6. Temporarily prevent the existing panel from being updated after persistence and confirm BeanBot keeps the edited configuration, does not publish another panel, and reports that presentation needs reconciliation. Restore access and rerun the same edit to reconcile the existing message.
7. Open the same panel as two different members and confirm each receives an independent private selector with only their own current menu roles preselected.
8. As a member, add both menu roles, remove one, and clear the menu. Confirm an unrelated role remains assigned throughout.
9. Publish a single menu, switch between its roles, and confirm no gap is introduced when the replacement can be added.
10. Delete one configured role in Discord and confirm the stale panel fails privately without changing any remaining or unrelated role.
11. Restart BeanBot and confirm the remaining panels still work from their persisted configuration.
12. Delete a panel through `/role-menu delete`, then confirm its old controls cannot mutate roles.
13. Temporarily remove BeanBot's hierarchy or permissions and confirm operations fail privately without exposing exception details or changing unrelated roles.
14. Use an existing legacy reaction-role panel and confirm its reactions still add and remove roles exactly as before.

## Code organization

`RoleMenuAdminModule`, `RoleMenuEditAdminModule`, and `RoleMenuMemberModule` validate Discord control bindings and acknowledge interactions before starting work. Their shared `RoleMenuModuleBase` handles private responses, mention suppression, and acknowledgement reconciliation.

`RoleMenuAdministrationService` prepares drafts and connects publication, editing, and deletion to persistence. `RoleMenuMemberService` loads selectors and applies member choices through the mutation coordinator. These services take IDs and submitted values, without an interaction context. Each member operation keeps its own Discord member reference, so concurrent requests cannot share a mutation target.

`DiscordRoleMenuClient` contains Discord REST reads and mutations. `RoleMenuSetupValidation` and `RoleMenuPresentation` keep validation and result text separate from transport. The publication, edit, deletion, and member workflows retain independently tested rules for ambiguous outcomes, rollback or no-rollback decisions, final-state reconciliation, and cancellation. `RoleMenuInteractionService` supplies the shared bounded execution, draft, persistence, and lock operations used by those entry points.

Shutdown closes normal interaction, busy-response, and command-registration admission. If a Discord request ignores cancellation and outlives the drain timeout, its ownership remains visible to the application, which skips Discord stop/disposal until the request actually completes. A late `Ready` event cannot restart command registration after shutdown.
