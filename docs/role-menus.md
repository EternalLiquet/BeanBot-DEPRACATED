# Dropdown role menus

BeanBot can publish persistent Discord panels that let members add or remove an administrator-approved set of roles. These panels are separate from the existing reaction-role system; legacy reaction-role messages and commands continue to work unchanged until an administrator explicitly retires a legacy panel.

## Requirements

The administrator running `/role-menu create`, `/role-menu delete`, or `/role-menu retire-legacy` must:

- run the command in a server, not a direct message;
- have the server-level **Manage Roles** permission; and
- have a highest role above every role configured in a native role menu when creating or publishing one, unless they own the server.

BeanBot must have:

- **Manage Roles** at the server level for native role-menu assignment;
- a highest role above every role configured in a native role menu; and
- **View Channel**, **Send Messages**, **Embed Links**, and **Read Message History** in native role-menu target channels.

Discord does not allow BeanBot to assign `@everyone`, integration-managed roles, or roles at or above BeanBot's highest role. BeanBot validates these rules during setup, immediately before publication, when a member opens a panel, and immediately before applying a selection.

## Create and publish a panel

1. Run `/role-menu create`.
2. In Discord's setup form, enter a title, optionally enter a description, choose 1–25 existing roles, choose single- or multiple-selection mode, and choose a normal text channel.
3. Review the private preview.
4. Select **Publish**. BeanBot rechecks roles and channel permissions, publishes the public panel, and saves its configuration in MongoDB.

The preview expires after 10 minutes. BeanBot holds at most 64 previews at once and replaces an administrator's previous preview in the same server when they create a new one. A failed persistence write rolls back the newly posted panel when Discord permits it. If BeanBot cannot confirm whether Discord posted the panel or MongoDB saved its settings, it closes the preview and disables automatic retry to avoid creating a duplicate. Inspect the target channel, remove any orphaned panel, and confirm the saved state before creating a replacement.

Each public panel contains a stable **Manage Roles** button and its menu ID in the embed footer. Saved settings include the server, channel, message, title, description, allowlisted role IDs, selection mode, and UTC timestamps, so published panels continue to work after BeanBot restarts.

## Member behavior

Selecting **Manage Roles** opens a private selector bound to that member and panel. Current roles from that menu are preselected.

- In **multiple** mode, any combination of the configured roles is allowed.
- In **single** mode, choosing a new configured role replaces the old configured role. BeanBot adds the replacement first and keeps the old role if the add fails.
- **Clear menu roles** removes every currently assigned role from this menu.
- Roles that are not configured in the menu are never added or removed.

Submissions for the same member are serialized, including submissions from overlapping menus. Different members may update roles from the same menu concurrently. Publication and deletion take an exclusive menu lifecycle lock so they cannot race member changes or resurrect a deleted configuration. Before changing anything, BeanBot reloads the persisted configuration, confirms the original panel still exists and belongs to BeanBot, fetches current member roles, revalidates role hierarchy, and rejects malformed or non-allowlisted values. After every valid submission, including a no-op or interrupted mutation, BeanBot performs a separate bounded read of Discord's current member roles. It reports confirmed results from that observed state and explicitly asks the member to reopen the menu when the final state cannot be confirmed.

## Delete a native role menu

Run `/role-menu delete` to choose from the 25 newest saved menus. For an older menu, copy the ID from its panel footer and run `/role-menu delete menu-id:<id>`.

Deletion requires a private confirmation. BeanBot deletes a matching BeanBot-owned panel before removing its saved configuration. A missing panel is treated as already removed. If the referenced message no longer looks like the saved BeanBot panel, it is left untouched while the stale configuration is removed. If Discord denies panel deletion, the saved configuration is retained so an administrator can correct permissions and retry.

## Retire a legacy reaction-role panel

Use `/role-menu retire-legacy legacy-message-id:<id>` only after you have decided that one old reaction-role panel is no longer needed, for example after validating a replacement workflow. Retirement is explicit and one panel at a time; BeanBot never bulk-retires legacy panels and never automatically retires one merely because another panel exists.

BeanBot first loads the exact saved legacy record for that Discord message ID and current server. It then positively identifies the saved channel/message as a BeanBot-authored legacy role panel with the expected `Role Group:` footer and configured role mentions. The private preview shows the source and configured roles before any destructive action. If the source message or channel is definitely already missing, the preview makes that clear and confirmation removes only the stale saved configuration. A message that exists but does not match the expected BeanBot legacy panel is never deleted.

At confirmation time BeanBot rechecks the administrator's current **Manage Roles** permission, reloads the saved source, and revalidates the Discord panel. A confirmed retirement deletes the Discord panel first, then deletes the exact saved legacy record and evicts it from the in-memory reaction-role cache. New reaction processing for that source is coordinated against retirement so a concurrent cache fill cannot resurrect retired settings. A reaction-role mutation that was already admitted before retirement began may finish; retirement never revokes or otherwise changes roles members already have.

Failure handling is intentionally conservative:

- if Discord definitely refuses deletion or the source no longer matches the expected legacy panel, saved configuration is kept;
- if a Discord delete times out or fails after it may have been sent, BeanBot does **not** retry the delete automatically; it may perform one bounded read to aid reconciliation, but it keeps saved configuration and requires an administrator to inspect Discord and rerun the command before persistence cleanup;
- if the panel is confirmed gone on a later run but MongoDB cleanup fails, the panel stays gone and rerunning the same command finishes the stale persistence/cache cleanup;
- if MongoDB's delete outcome is uncertain, BeanBot performs one bounded exact-key read rather than assuming success;
- if either system remains ambiguous, the response tells the administrator to inspect the exact source and rerun the same command. No replacement message is created.

Retirement affects only the legacy panel and its legacy saved configuration. It does not delete native role-menu settings and does not add, remove, or rewrite existing member roles.

## Manual smoke check

Use a test server with BeanBot's role below one test role and above two other test roles.

1. Confirm `/role-menu create` is unavailable to a member without **Manage Roles** and cannot run in a direct message.
2. Confirm setup rejects `@everyone`, a managed role, the role above BeanBot, and a role at or above a non-owner administrator.
3. Publish a two-role multiple menu and confirm the preview is private while the panel is public in the selected channel.
4. Open the same panel as two different members and confirm each receives an independent private selector with only their own current menu roles preselected.
5. As a member, add both menu roles, remove one, and clear the menu. Confirm an unrelated role remains assigned throughout.
6. Publish a single menu, switch between its roles, and confirm no gap is introduced when the replacement can be added.
7. Delete one configured role in Discord and confirm the stale panel fails privately without changing any remaining or unrelated role.
8. Restart BeanBot and confirm the remaining panels still work from their persisted configuration.
9. Delete a panel through `/role-menu delete`, then confirm its old controls cannot mutate roles.
10. Temporarily remove BeanBot's hierarchy or permissions and confirm operations fail privately without exposing exception details or changing unrelated roles.
11. Use an existing legacy reaction-role panel and confirm its reactions still add and remove roles exactly as before.
12. Run `/role-menu retire-legacy` for that legacy panel, review the private confirmation, and cancel once to confirm nothing changes.
13. Confirm retirement removes the expected legacy panel and saved configuration without removing any roles already held by members, then rerun the command and confirm the result is idempotent.
14. Repeat with a legacy panel whose Discord message is already missing and confirm only stale saved configuration is removed.
15. Change the source message or remove the administrator's **Manage Roles** permission between preview and confirmation and confirm retirement stops without deleting the source or saved configuration.

## Code organization

`RoleMenuAdminModule` and `RoleMenuMemberModule` validate Discord control bindings and acknowledge interactions before starting work. Their shared `RoleMenuModuleBase` handles private responses, mention suppression, and acknowledgement reconciliation. Legacy-panel retirement is implemented in the existing `RoleMenuAdminModule` command group so it inherits the same guild-only and **Manage Roles** authorization surface instead of registering a second `/role-menu` root.

`RoleMenuAdministrationService` prepares drafts and connects native role-menu publication and deletion to persistence. `RoleMenuMemberService` loads selectors and applies member choices through the mutation coordinator. These services take IDs and submitted values, without an interaction context. Each member operation keeps its own Discord member reference, so concurrent requests cannot share a mutation target.

`DiscordRoleMenuClient` contains native role-menu REST reads and mutations. `LegacyReactionRoleRetirementClient` performs only the exact legacy source reads/deletion needed by the retirement workflow. `RoleMenuSetupValidation` and `RoleMenuPresentation` keep validation and result text separate from transport. The publication, deletion, member, and legacy-retirement workflows retain independently tested rules for ambiguous outcomes, rollback or no-retry behavior, final-state reconciliation, and cancellation. `RoleMenuInteractionService` supplies the shared bounded execution, draft, persistence, and native role-menu lock operations used by those entry points. Legacy reaction-role retirement additionally uses a fixed-size striped reader/writer coordinator in `ReactionRoleService` so normal reaction admission and destructive retirement cannot race through persistence/cache invalidation.

Shutdown closes normal interaction, busy-response, and command-registration admission. If a Discord request ignores cancellation and outlives the drain timeout, its ownership remains visible to the application, which skips Discord stop/disposal until the request actually completes. A late `Ready` event cannot restart command registration after shutdown.
