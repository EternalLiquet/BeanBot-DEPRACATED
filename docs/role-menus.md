# Dropdown role menus

BeanBot can publish persistent Discord panels that let members add or remove an administrator-approved set of roles. These panels coexist with the existing reaction-role system; legacy reaction-role messages and commands continue to work unchanged until an administrator deliberately retires them.

## Requirements

The administrator running `/role-menu create`, `/role-menu migrate`, or `/role-menu delete` must:

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
2. In Discord's setup form, enter a title, optionally enter a description, choose 1–25 existing roles, choose single- or multiple-selection mode, and choose a normal text channel.
3. Review the private preview.
4. Select **Publish**. BeanBot rechecks roles and channel permissions, publishes the public panel, and saves its configuration in MongoDB.

The preview expires after 10 minutes. BeanBot holds at most 64 previews at once and replaces an administrator's previous preview in the same server when they create a new one. A failed persistence write rolls back the newly posted panel when Discord permits it. If BeanBot cannot confirm whether Discord posted the panel or MongoDB saved its settings, it closes the preview and disables automatic retry to avoid creating a duplicate. Inspect the target channel, remove any orphaned panel, and confirm the saved state before creating a replacement.

Each public panel contains a stable **Manage Roles** button and its menu ID in the embed footer. Saved settings include the server, channel, message, title, description, allowlisted role IDs, selection mode, optional legacy-migration provenance, and UTC timestamps, so published panels continue to work after BeanBot restarts.

## Migrate a legacy reaction-role panel

Use `/role-menu migrate legacy-message-id:<id>` to prepare one saved legacy reaction-role panel for migration. This is intentionally one panel at a time; BeanBot does not scan or bulk-convert legacy configuration.

The command looks up the exact saved reaction-role record for that message ID and positively identifies the current Discord message as a BeanBot legacy `Role Group:` panel before offering a private preview. When the legacy footer contains a usable role-group label, BeanBot suggests it as the new menu title. You may supply a replacement `title`, optional `description`, and optional `target-channel` directly on the command. If no target is supplied, the replacement defaults to the legacy panel's channel.

Legacy reaction-role behavior maps to a **multiple-selection** role menu: the persisted role allowlist is preserved, while the old emoji IDs are intentionally discarded because dropdown role menus no longer use reaction emoji as controls. The preview shows the exact legacy source message, roles, target channel, and retirement warning before anything is published.

Selecting **Publish migration** performs the safety checks again from current state. BeanBot reloads the persisted legacy configuration and source message, revalidates administrator and bot hierarchy, verifies the roles have not changed since the preview, and checks target-channel permissions before using the normal role-menu publication workflow. If any of that state changed, publication stops and the administrator is asked to create a fresh preview.

Migration uses a deterministic role-menu ID derived from the server and legacy source message and stores the source message ID with the new menu. This provenance makes rerunning the same migration idempotent across restarts and serializes concurrent confirmations through the normal per-menu lifecycle lock. If Discord or MongoDB returns an ambiguous publication result, BeanBot does not blindly retry the write; inspect the target channel and persisted role-menu state, then rerun the migration for the same source so the stable identity can reconcile the result rather than creating a second saved replacement.

Migration **does not delete, disable, or edit the legacy reaction-role message or its saved configuration**. Verify the new dropdown panel first, then deliberately retire the old panel through the existing legacy administration workflow. Until you do, both controls may remain active.

## Member behavior

Selecting **Manage Roles** opens a private selector bound to that member and panel. Current roles from that menu are preselected.

- In **multiple** mode, any combination of the configured roles is allowed.
- In **single** mode, choosing a new configured role replaces the old configured role. BeanBot adds the replacement first and keeps the old role if the add fails.
- **Clear menu roles** removes every currently assigned role from this menu.
- Roles that are not configured in the menu are never added or removed.

Submissions for the same member are serialized, including submissions from overlapping menus. Different members may update roles from the same menu concurrently. Publication and deletion take an exclusive menu lifecycle lock so they cannot race member changes or resurrect a deleted configuration. Before changing anything, BeanBot reloads the persisted configuration, confirms the original panel still exists and belongs to BeanBot, fetches current member roles, revalidates role hierarchy, and rejects malformed or non-allowlisted values. After every valid submission, including a no-op or interrupted mutation, BeanBot performs a separate bounded read of Discord's current member roles. It reports confirmed results from that observed state and explicitly asks the member to reopen the menu when the final state cannot be confirmed.

## Delete a panel

Run `/role-menu delete` to choose from the 25 newest saved menus. For an older menu, copy the ID from its panel footer and run `/role-menu delete menu-id:<id>`.

Deletion requires a private confirmation. BeanBot deletes a matching BeanBot-owned panel before removing its saved configuration. A missing panel is treated as already removed. If the referenced message no longer looks like the saved BeanBot panel, it is left untouched while the stale configuration is removed. If Discord denies panel deletion, the saved configuration is retained so an administrator can correct permissions and retry.

## Manual smoke check

Use a test server with BeanBot's role below one test role and above two other test roles.

1. Confirm `/role-menu create`, `/role-menu migrate`, and `/role-menu delete` are unavailable to a member without **Manage Roles** and cannot run in a direct message.
2. Confirm setup rejects `@everyone`, a managed role, the role above BeanBot, and a role at or above a non-owner administrator.
3. Publish a two-role multiple menu and confirm the preview is private while the panel is public in the selected channel.
4. Open the same panel as two different members and confirm each receives an independent private selector with only their own current menu roles preselected.
5. As a member, add both menu roles, remove one, and clear the menu. Confirm an unrelated role remains assigned throughout.
6. Publish a single menu, switch between its roles, and confirm no gap is introduced when the replacement can be added.
7. Delete one configured role in Discord and confirm the stale panel fails privately without changing any remaining or unrelated role.
8. Restart BeanBot and confirm the remaining panels still work from their persisted configuration.
9. Delete a panel through `/role-menu delete`, then confirm its old controls cannot mutate roles.
10. Temporarily remove BeanBot's hierarchy or permissions and confirm operations fail privately without exposing exception details or changing unrelated roles.
11. Migrate an existing legacy reaction-role panel. Confirm the preview is private, maps the roles to multiple-selection mode, and does not modify the source panel or source persistence.
12. Rerun migration for the same legacy message after publication and confirm BeanBot reports the existing replacement rather than publishing another one.
13. Change or remove a legacy role between preview and confirmation and confirm migration stops before publishing. Restore the source, create a fresh preview, and verify the replacement.
14. Retire the legacy panel only after verifying the dropdown replacement, and confirm the new menu continues to work independently.

## Code organization

`RoleMenuAdminModule` and `RoleMenuMemberModule` validate Discord control bindings and acknowledge interactions before starting work. Migration interaction handlers are a partial continuation of the same `RoleMenuAdminModule`, so Discord registers exactly one `/role-menu` command group. Their shared `RoleMenuModuleBase` handles private responses, mention suppression, and acknowledgement reconciliation.

`RoleMenuAdministrationService` prepares ordinary creation drafts and connects publication and deletion to persistence. `RoleMenuMigrationService` performs exact legacy-source lookup, validation, preview creation, final revalidation, and durable idempotency before delegating publication to the same administration workflow. `LegacyReactionRoleMigrationClient` contains the read-only Discord lookup used to positively identify the source panel. `RoleMenuMemberService` loads selectors and applies member choices through the mutation coordinator. These services take IDs and submitted values, without an interaction context. Each member operation keeps its own Discord member reference, so concurrent requests cannot share a mutation target.

`DiscordRoleMenuClient` contains Discord REST reads and mutations. `RoleMenuSetupValidation` and `RoleMenuPresentation` keep validation and result text separate from transport. The publication, deletion, migration, and member workflows retain their independently tested rules for ambiguous outcomes, rollback, final-state reconciliation, and cancellation. `RoleMenuInteractionService` supplies the shared bounded execution, draft, persistence, and lock operations used by those entry points.

Shutdown closes normal interaction, busy-response, and command-registration admission. If a Discord request ignores cancellation and outlives the drain timeout, its ownership remains visible to the application, which skips Discord stop/disposal until the request actually completes. A late `Ready` event cannot restart command registration after shutdown.
