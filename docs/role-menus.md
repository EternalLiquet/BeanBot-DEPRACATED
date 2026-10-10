# Dropdown role menus

BeanBot can publish persistent Discord panels that let members add or remove an administrator-approved set of roles. These panels are separate from the existing reaction-role system; legacy reaction-role messages and commands continue to work unchanged.

## Requirements

The administrator running `/role-menu create` or `/role-menu delete` must:

- run the command in a server, not a direct message;
- have the server-level **Manage Roles** permission; and
- have a highest role above every role configured in the menu, unless they own the server.

BeanBot must have:

- **Manage Roles** at the server level;
- a highest role above every role configured in the menu; and
- **View Channel**, **Send Messages**, **Embed Links**, and **Read Message History** in the target channel.

Discord does not allow BeanBot to assign `@everyone`, integration-managed roles, or roles at or above BeanBot's highest role. BeanBot validates these rules during setup, immediately before publication, when a member opens a panel, and immediately before applying a selection.

## Create and publish a panel

1. Run `/role-menu create`.
2. In Discord's setup form, enter a title, optionally enter a description, choose 1–25 existing roles, choose whether members can pick **One role** or **Any number**, and choose a normal text channel.
3. Review the private preview.
4. Select **Publish menu**. BeanBot rechecks roles and channel permissions, publishes the public panel, and saves its configuration in MongoDB.

The preview expires after 10 minutes. BeanBot holds at most 64 previews at once and replaces an administrator's previous preview in the same server when they create a new one. A failed persistence write rolls back the newly posted panel when Discord permits it. If BeanBot cannot confirm whether Discord posted the panel or MongoDB saved its settings, it closes the preview and disables automatic retry to avoid creating a duplicate. Inspect the target channel, remove any orphaned panel, and confirm the saved state before creating a replacement.

Each public panel contains a stable **Choose your roles** button. The footer shows only the selection mode; internal menu IDs stay in component data and are never shown to people. Panels published before this change keep working. BeanBot doesn't sweep old messages at startup, so their old footer stays until a later edit or repair flow rewrites the panel. Saved settings include the server, channel, message, title, description, allowlisted role IDs, selection mode, and UTC timestamps, so published panels continue to work after BeanBot restarts.

## Member behavior

Selecting **Choose your roles** opens a private selector bound to that member and panel. Current roles from that menu are preselected.

- In **Any number** mode, any combination of the configured roles is allowed.
- In **One role** mode, choosing a new configured role replaces the old configured role. BeanBot adds the replacement first and keeps the old role if the add fails.
- **Remove my roles from this menu** removes every currently assigned role from this menu.
- Roles that are not configured in the menu are never added or removed.

Submissions for the same member are serialized, including submissions from overlapping menus. Different members may update roles from the same menu concurrently. Publication and deletion take an exclusive menu lifecycle lock so they cannot race member changes or resurrect a deleted configuration. Before changing anything, BeanBot reloads the persisted configuration, confirms the original panel still exists and belongs to BeanBot, fetches current member roles, revalidates role hierarchy, and rejects malformed or non-allowlisted values. After every valid submission, including a no-op or interrupted mutation, BeanBot performs a separate bounded read of Discord's current member roles. It reports only confirmed results from that observed state, in short plain sentences such as "Added Gamer.", and asks the member to reopen the menu when a change failed or the final state cannot be confirmed. Recheck details stay in the logs.

## Delete a panel

Right-click a role-menu panel and choose **Apps → Delete Role Menu**. BeanBot accepts only its own panel in the channel where the command was used. The panel's button and saved record must also match by server, channel and message.

`/role-menu delete` opens a private picker instead. It lists every saved menu, newest first, 25 per page, with **Previous** and **Next** buttons. Each entry shows the title (or "Untitled role menu"), channel and creation time, so same-titled menus remain distinguishable. Paging reads one bounded page from MongoDB at a time, ordered by creation time with the menu ID as a tie-breaker. Menus whose message or channel was deleted stay listed so they can be cleaned up.

Both paths show the same private confirmation, bound to the administrator who opened it. It shows the title, channel, role count, selection mode, creation time and, when the panel still exists, a **View menu** link. Before showing it, the picker checks the panel:

- A message or channel that Discord reports as deleted can be cleaned up.
- A message that is no longer this BeanBot panel is left alone, and only the saved menu is removed.
- If BeanBot can't open the message or the check fails, no **Delete** button is shown. That isn't proof the message is gone.

Picker and confirmation controls expire 15 minutes after the command was used. On **Delete**, BeanBot takes the menu's lifecycle lock and rereads the saved menu. If it was deleted or changed after the confirmation was shown, BeanBot stops; it never substitutes another menu with the same title. A repeated click while deletion is running is ignored. The existing deletion workflow then rechecks **Manage Roles**, deletes a matching BeanBot-owned panel, and removes its saved configuration. If Discord denies panel deletion or the outcome is unclear, the saved configuration is kept so an administrator can retry.

Titles are not required to be unique, and existing titles are never renamed. New titles must contain visible text and fit within 100 characters after trimming.

### Future editing integration

`/role-menu edit` (PR #193) is still on `develop` and is not part of this master fix. When this change is forward-ported, the edit flow should reuse the same ID-free picker. That means `RoleMenuAdministrationService.LoadDeletionPageAsync` (or a renamed shared page loader), `RoleMenuDeletionTargets.BuildPage` and the paged selector, with an edit-specific select custom ID. Editing should also confirm against the menu version it showed. It must not reintroduce an ID argument or a "copy the ID" fallback.

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
9. Delete one panel through **Apps → Delete Role Menu** and another through `/role-menu delete`, then confirm their old controls cannot mutate roles. Confirm neither panel nor any admin reply shows an internal ID.
10. Temporarily remove BeanBot's hierarchy or permissions and confirm operations fail privately without exposing exception details or changing unrelated roles.
11. Use an existing legacy reaction-role panel and confirm its reactions still add and remove roles exactly as before.

## Wording

Role-menu text follows the customer-facing text standard in `AGENTS.md`. Members see short outcome messages, such as "Removed Test Role.", and administrators see plain next steps when something only partly worked. Deleting a menu removes its message and saved settings; members keep the roles they already have.

## Code organization

`RoleMenuAdminModule`, `RoleMenuMessageCommandModule` and `RoleMenuMemberModule` validate Discord control bindings and acknowledge interactions before starting work. Their shared `RoleMenuModuleBase` handles private responses, mention suppression, and acknowledgement reconciliation.

`RoleMenuAdministrationService` prepares drafts and connects publication and deletion to persistence. `RoleMenuMemberService` loads selectors and applies member choices through the mutation coordinator. These services take IDs and submitted values, without an interaction context. Each member operation keeps its own Discord member reference, so concurrent requests cannot share a mutation target.

`DiscordRoleMenuClient` contains Discord REST reads and mutations. `RoleMenuSetupValidation` and `RoleMenuPresentation` keep validation and result text separate from transport. The publication, deletion, and member workflows retain their independently tested rules for ambiguous outcomes, rollback, final-state reconciliation, and cancellation. `RoleMenuInteractionService` supplies the shared bounded execution, draft, persistence, and lock operations used by those entry points.

Shutdown closes normal interaction, busy-response, and command-registration admission. If a Discord request ignores cancellation and outlives the drain timeout, its ownership remains visible to the application, which skips Discord stop/disposal until the request actually completes. A late `Ready` event cannot restart command registration after shutdown.
