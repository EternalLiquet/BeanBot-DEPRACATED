# Dropdown role menus

BeanBot can publish persistent Discord panels that let members add or remove an administrator-approved set of roles. These panels are separate from the existing reaction-role system; legacy reaction-role messages and commands continue to work unchanged.

## Requirements

The administrator running `/role-menu create`, `/role-menu migrate`, `/role-menu edit`, `/role-menu audit`, `/role-menu delete`, or `/role-menu retire-legacy` must:

- run the command in a server, not a direct message;
- have the server-level **Manage Roles** permission; and
- have a highest role above every role configured in the menu, unless they own the server.

BeanBot must have:

- **Manage Roles** at the server level;
- a highest role above every role configured in the menu; and
- **View Channel**, **Send Messages**, **Embed Links**, and **Read Message History** in the target channel.

Discord does not allow BeanBot to assign `@everyone`, integration-managed roles, or roles at or above BeanBot's highest role. BeanBot validates these rules during setup, immediately before publication or an edit, when a member opens a panel, immediately before applying a selection, and when an administrator audits a saved menu.

## Create and publish a panel

1. Run `/role-menu create`.
2. In Discord's setup form, enter a title, optionally enter a description, choose 1–25 existing roles, choose whether members can pick **One role** or **Any number**, and choose a normal text channel.
3. Review the private preview.
4. Select **Publish menu**. BeanBot rechecks roles and channel permissions, publishes the public panel, and saves its configuration in MongoDB.

The preview expires after 10 minutes. BeanBot holds at most 64 previews at once and replaces an administrator's previous preview in the same server when they create a new one. A failed persistence write rolls back the newly posted panel when Discord permits it. If BeanBot cannot confirm whether Discord posted the panel or MongoDB saved its settings, it closes the preview and disables automatic retry to avoid creating a duplicate. Inspect the target channel, remove any orphaned panel, and confirm the saved state before creating a replacement.

Each public panel contains a stable **Choose your roles** button. The footer shows only the selection mode; internal menu IDs stay in component data rather than appearing on new public panels. Panels published before this change keep working. BeanBot doesn't sweep old messages at startup, so their old footer stays until a later edit or repair flow rewrites the panel. Saved settings include the server, channel, message, title, description, allowlisted role IDs, selection mode, and UTC timestamps, so published panels continue to work after BeanBot restarts.

## Migrate a legacy reaction-role panel

Use `/role-menu migrate legacy-message-id:<id>` to prepare one saved legacy reaction-role panel for migration. This is intentionally one panel at a time; BeanBot does not scan or bulk-convert legacy configuration.

The command looks up the exact saved reaction-role record for that message ID and positively identifies the current Discord message as a BeanBot legacy `Role Group:` panel before offering a private preview. When the legacy footer contains a usable role-group label, BeanBot suggests it as the new menu title. You may supply a replacement `title`, optional `description`, and optional `target-channel` directly on the command. If no target is supplied, the replacement defaults to the legacy panel's channel.

Legacy reaction-role behavior maps to a **multiple-selection** role menu: the persisted role allowlist is preserved, while the old emoji IDs are intentionally discarded because dropdown role menus no longer use reaction emoji as controls. The preview shows the exact legacy source message, roles, target channel, and retirement warning before anything is published.

Selecting **Publish migration** performs the safety checks again from current state. BeanBot reloads the persisted legacy configuration and source message, revalidates administrator and bot hierarchy, verifies the roles have not changed since the preview, and checks target-channel permissions before using the normal role-menu publication workflow. If any of that state changed, publication stops and the administrator is asked to create a fresh preview.

Migration uses a deterministic role-menu ID derived from the server and legacy source message and stores the source message ID with the new menu. This provenance makes rerunning the same migration idempotent across restarts and serializes concurrent confirmations through the normal per-menu lifecycle lock. If Discord or MongoDB returns an ambiguous publication result, BeanBot does not blindly retry the write; inspect the target channel and persisted role-menu state, then rerun the migration for the same source so the stable identity can reconcile the result rather than creating a second saved replacement.

Migration **does not delete, disable, or edit the legacy reaction-role message or its saved configuration**. Verify the new dropdown panel first, then deliberately retire the old panel through the existing legacy administration workflow. Until you do, both controls may remain active.

## Edit a published panel

Run `/role-menu edit` to choose from every saved menu in a private, paginated list. Each page shows up to 25 menus.

BeanBot first shows the menu's current title, description, roles, and selection mode privately. Select **Edit values** to open a pre-filled form, then change any of these fields:

- title;
- optional description;
- 1–25 self-assignable roles; and
- multiple- or single-selection mode.

Editing is intentionally in place. BeanBot keeps the same saved menu ID, server, channel, public message, and original creation timestamp. Moving a panel to another channel is not supported by edit; delete and recreate the panel when its location must change.

When the form is submitted, BeanBot reloads the saved menu, confirms it still matches the values shown in the preview, and rechecks the administrator permission, current role existence and hierarchy, BeanBot's role permissions, channel permissions, and the exact public message identity. The edit runs under the same exclusive per-menu write coordination used by publication and deletion, so it cannot race a member role mutation or another menu-level write. Member submissions also reload persisted settings before any role change, so a selector opened before an edit cannot grant a role that the edited menu no longer allows.

Persisted settings are the authorization source of truth. BeanBot saves the replacement configuration before modifying the existing public message. This gives failures deterministic behavior:

- if the persistence write cannot be confirmed, BeanBot leaves the public panel untouched and asks the administrator to inspect persisted state before retrying;
- if persistence succeeds but the panel is definitely missing or no longer matches the expected BeanBot panel, the new configuration remains authoritative and the unrelated/missing message is not modified;
- if the Discord message update has an ambiguous outcome, BeanBot does not retry, roll the saved configuration back, or publish a replacement panel; inspect the existing message and rerun the same edit to reconcile it in place;
- rerunning the same edit is safe because it targets the same stable saved menu and same existing message rather than creating another panel.

Edit previews are bounded and expire after 10 minutes. If the interaction times out while a MongoDB or Discord call is still running, BeanBot gives bounded feedback and keeps shutdown ownership until the call settles.

## Member behavior

Selecting **Choose your roles** opens a private selector bound to that member and panel. Current roles from that menu are preselected.

- In **Any number** mode, any combination of the configured roles is allowed.
- In **One role** mode, choosing a new configured role replaces the old configured role. BeanBot adds the replacement first and keeps the old role if the add fails.
- **Remove my roles from this menu** removes every currently assigned role from this menu.
- Roles that are not configured in the menu are never added or removed.

Submissions for the same member are serialized, including submissions from overlapping menus. Different members may update roles from the same menu concurrently. Publication, editing, and deletion take an exclusive menu lifecycle lock so they cannot race member changes or resurrect a deleted configuration. Before changing anything, BeanBot reloads the persisted configuration, confirms the original panel still exists and belongs to BeanBot, fetches current member roles, revalidates role hierarchy, and rejects malformed or non-allowlisted values. After every valid submission, including a no-op or interrupted mutation, BeanBot performs a separate bounded read of Discord's current member roles. It reports only confirmed results from that observed state, in short plain sentences such as "Added Gamer.", and asks the member to reopen the menu when a change failed or the final state cannot be confirmed. Recheck details stay in the logs.

## Audit published panels

Run `/role-menu audit` to inspect the 25 newest saved menus without changing Discord or MongoDB. Its private result includes each menu's ID. If you already have an ID, run `/role-menu audit menu-id:<id>` to inspect that menu in detail. Newly published panel footers do not show IDs.

Each menu is reported as:

- **Healthy** when BeanBot can positively verify the saved configuration, current roles and hierarchy, required channel permissions, and the original BeanBot-authored panel identity.
- **Broken** when BeanBot can positively identify a stale or unusable condition, such as a deleted channel or message, a role that was deleted or moved above BeanBot, a managed role, missing permissions, or a message that no longer matches the saved panel.
- **Unknown** when a bounded MongoDB or Discord lookup fails, times out, or otherwise prevents BeanBot from proving the current state. Unknown is intentionally not treated as Broken; retry the audit after the transient problem clears.

Bulk auditing is deliberately capped at 25 menus and checks them sequentially so a server with many historical menus cannot create an unbounded MongoDB query or Discord REST burst. Audit lookups share the normal interaction shutdown cancellation and use a shorter per-lookup cancellation bound. Audit never republishes panels, edits or deletes messages, changes roles, or modifies saved menu records.

For **Broken** results, correct the reported Discord role/channel permission problem when possible. If the saved message or channel is gone, use `/role-menu delete` to select and remove the stale saved configuration, then `/role-menu create` if a replacement panel is needed. For **Unknown**, do not delete state based only on the audit result; retry after the dependency recovers.

## Delete a panel

Right-click a role-menu panel and choose **Apps → Delete Role Menu**. BeanBot accepts only its own panel in the channel where the command was used. The panel's button and saved record must also match by server, channel and message.

`/role-menu delete` opens a private picker instead. It lists every saved menu, newest first, 25 per page, with **Previous** and **Next** buttons. Each entry shows the title (or "Untitled role menu"), channel and creation time, so same-titled menus remain distinguishable. Paging reads one bounded page from MongoDB at a time, ordered by creation time with the menu ID as a tie-breaker. Menus whose message or channel was deleted stay listed so they can be cleaned up.

Both paths show the same private confirmation, bound to the administrator who opened it. It shows the title, channel, role count, selection mode, creation time and, when the panel still exists, a **View menu** link. Before showing it, the picker checks the panel:

- A message or channel that Discord reports as deleted can be cleaned up.
- A message that is no longer this BeanBot panel is left alone, and only the saved menu is removed.
- If BeanBot can't open the message or the check fails, no **Delete** button is shown. That isn't proof the message is gone.

Picker and confirmation controls expire 15 minutes after the command was used. On **Delete**, BeanBot takes the menu's lifecycle lock and rereads the saved menu. If it was deleted or changed after the confirmation was shown, BeanBot stops; it never substitutes another menu with the same title. A repeated click while deletion is running is ignored. The existing deletion workflow then rechecks **Manage Roles**, deletes a matching BeanBot-owned panel, and removes its saved configuration. If Discord denies panel deletion or the outcome is unclear, the saved configuration is kept so an administrator can retry.

Titles are not required to be unique, and existing titles are never renamed. New titles must contain visible text and fit within 100 characters after trimming.

## Retire a legacy reaction-role panel

Run `/role-menu retire-legacy` to open a private list of saved legacy panels. The picker shows a channel, saved role names and posting time; missing channels and deleted roles have readable fallbacks. It reads at most 26 records per request to show 25 choices and a **Next** button, ordered by stable MongoDB document ID. **Previous** reads one bounded page in the other direction. You do not need to copy a message ID. The preview shows available custom emoji names and role names, and a link to the panel when it still exists.

Retirement requires **Manage Roles** and a private confirmation bound to the administrator and exact saved panel. It expires after 10 minutes. Before changing anything, BeanBot reloads the saved record, rechecks the administrator's permission, and verifies that a present panel is a BeanBot-authored legacy reaction-role message. A changed record or unrecognized message stops retirement. A definitely missing message or channel can be cleaned up from the saved record after confirmation; a timeout or unknown Discord result is not treated as missing.

BeanBot deletes a verified Discord panel once, then conditionally removes the exact saved record and invalidates its reaction-role cache entry. If Discord deletion is ambiguous, BeanBot leaves the saved record and does not retry automatically. Check the panel before running the command again. If Discord deletion succeeds but saving cleanup fails, the panel remains gone; rerun the command to finish cleanup. Retirement does not remove roles that members already hold and does not automatically follow migration.

## Manual smoke check

Use a test server with BeanBot's role below one test role and above two other test roles.

1. Confirm `/role-menu create`, `/role-menu edit`, `/role-menu audit`, and `/role-menu delete` are unavailable to a member without **Manage Roles** and cannot run in a direct message.
2. Confirm setup rejects `@everyone`, a managed role, the role above BeanBot, and a role at or above a non-owner administrator.
3. Publish a two-role multiple menu and confirm the preview is private while the panel is public in the selected channel.
4. Run `/role-menu audit` and confirm it reports **Healthy** without changing the panel, roles, or saved record.
5. Edit the panel through `/role-menu edit` and confirm the message ID stays the same and stale edit previews cannot overwrite newer settings.
6. Open the same panel as two different members and confirm each receives an independent private selector with only their own current menu roles preselected.
6. As a member, add both menu roles, remove one, and clear the menu. Confirm an unrelated role remains assigned throughout.
7. Publish a single menu, switch between its roles, and confirm no gap is introduced when the replacement can be added.
8. Delete one configured role in Discord and confirm the stale panel fails privately without changing any remaining or unrelated role; audit the menu and confirm it reports **Broken**.
9. Restart BeanBot and confirm the remaining panels still work from their persisted configuration.
10. Delete one panel through **Apps → Delete Role Menu** and another through `/role-menu delete`, then confirm their old controls cannot mutate roles. Confirm neither panel nor the publication and deletion replies show an internal ID.
11. Temporarily remove BeanBot's hierarchy or permissions and confirm operations fail privately without exposing exception details or changing unrelated roles; audit reports **Broken** for a positively verified permission failure.
12. Use an existing legacy reaction-role panel and confirm its reactions still add and remove roles exactly as before.
13. Run `/role-menu retire-legacy`, page past 25 panels if available, review the private preview, and cancel once. Confirm nothing changes.
14. Confirm retirement removes only the selected panel and saved settings while members keep their roles. Repeat with a missing panel and confirm stale settings are removed.
15. Change the saved panel or remove **Manage Roles** between preview and confirmation; verify deletion stops.

## Wording

Role-menu text follows the customer-facing text standard in `AGENTS.md`. Members see short outcome messages, such as "Removed Test Role.", and administrators see plain next steps when something only partly worked. Deleting a menu removes its message and saved settings; members keep the roles they already have.

## Code organization

`RoleMenuAdminModule`, `RoleMenuMessageCommandModule` and `RoleMenuMemberModule` validate Discord control bindings and acknowledge interactions before starting work. Their shared `RoleMenuModuleBase` handles private responses, mention suppression, and acknowledgement reconciliation.

`RoleMenuAdministrationService` prepares drafts and connects publication, editing, and deletion to persistence. `RoleMenuMigrationService` performs one-source migration through shared publication. `RoleMenuAuditService` performs bounded read-only health checks against saved role menus and current Discord state. `RoleMenuMemberService` loads selectors and applies member choices through the mutation coordinator. These services take IDs and submitted values, without an interaction context. Each member operation keeps its own Discord member reference, so concurrent requests cannot share a mutation target.

`DiscordRoleMenuClient` contains native role-menu REST reads and mutations. `LegacyReactionRoleRetirementClient` handles exact legacy source reads and deletion; its workflow coordinates with `ReactionRoleService` so a retired setting cannot be restored from cache. `RoleMenuSetupValidation` and `RoleMenuPresentation` keep validation and result text separate from transport. The publication, deletion, and member workflows retain their independently tested rules for ambiguous outcomes, rollback, final-state reconciliation, and cancellation. `RoleMenuInteractionService` supplies the shared bounded execution, draft, persistence, and lock operations used by those entry points.

Shutdown closes normal interaction, busy-response, and command-registration admission. If a Discord request ignores cancellation and outlives the drain timeout, its ownership remains visible to the application, which skips Discord stop/disposal until the request actually completes. A late `Ready` event cannot restart command registration after shutdown.
