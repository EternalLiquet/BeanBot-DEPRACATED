# Dropdown role menus

BeanBot can publish persistent Discord panels that let members add or remove an administrator-approved set of roles. These panels are separate from the existing reaction-role system; legacy reaction-role messages and commands continue to work unchanged.

## Requirements

The administrator running a `/role-menu` administration command must:

- run the command in a server, not a direct message; and
- have the server-level **Manage Roles** permission.

Creating and publishing a menu additionally requires the administrator's highest role to be above every configured role, unless they own the server. Auditing does not use the auditing administrator's personal hierarchy as a panel-health signal; it reports whether BeanBot itself can still operate the saved menu.

BeanBot must have:

- **Manage Roles** at the server level;
- a highest role above every role configured in the menu; and
- **View Channel**, **Send Messages**, **Embed Links**, and **Read Message History** in the target channel.

Discord does not allow BeanBot to assign `@everyone`, integration-managed roles, or roles at or above BeanBot's highest role. BeanBot validates these rules during setup, immediately before publication, when a member opens a panel, immediately before applying a selection, and when an administrator audits a saved panel.

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

## Audit saved panels

Run `/role-menu audit` to check the 25 newest saved menus in the current server, or `/role-menu audit menu-id:<id>` to inspect exactly one menu using the stable ID shown in its panel footer. Audit responses are private to the administrator.

Each saved menu receives one of these states:

- **Healthy** — BeanBot positively confirmed the saved server/channel/message identity, the expected BeanBot-owned role-menu control, every configured role, BeanBot's role hierarchy and **Manage Roles** permission, and the target channel permissions required by the live role-menu workflow.
- **Broken** — a concrete mismatch or missing prerequisite was observed, such as a deleted channel/message/role, a replaced panel message, a managed or too-high role, or permission drift. The result includes the concrete reason so the administrator can repair permissions/roles or recreate the panel.
- **Unknown** — a bounded Discord lookup timed out or failed unexpectedly. Unknown is intentionally different from Broken because BeanBot did not positively establish configuration drift; retry the audit after Discord connectivity recovers.

The audit is strictly read-only. It never deletes MongoDB settings or Discord messages, republishes panels, changes the role allowlist, moves roles, changes permissions, or adds/removes member roles. Server-wide audits are hard-capped at 25 saved menus and process at most four menu audits concurrently. If a server has older saved menus, the response says so and directs the administrator to audit one by footer ID.

Audit Discord requests use the same cancellation-aware interaction lifecycle as the rest of the role-menu surface. Each menu has a finite lookup budget and is not retried after a timeout or unexpected Discord failure. Shutdown cancellation stops the audit instead of being converted into an Unknown result, so interaction ownership remains visible during host teardown.

## Delete a panel

Run `/role-menu delete` to choose from the 25 newest saved menus. For an older menu, copy the ID from its panel footer and run `/role-menu delete menu-id:<id>`.

Deletion requires a private confirmation. BeanBot deletes a matching BeanBot-owned panel before removing its saved configuration. A missing panel is treated as already removed. If the referenced message no longer looks like the saved BeanBot panel, it is left untouched while the stale configuration is removed. If Discord denies panel deletion, the saved configuration is retained so an administrator can correct permissions and retry.

## Manual smoke check

Use a test server with BeanBot's role below one test role and above two other test roles.

1. Confirm `/role-menu create`, `/role-menu audit`, and `/role-menu delete` are unavailable to a member without **Manage Roles** and cannot run in a direct message.
2. Confirm setup rejects `@everyone`, a managed role, the role above BeanBot, and a role at or above a non-owner administrator.
3. Publish a two-role multiple menu and confirm the preview is private while the panel is public in the selected channel.
4. Run `/role-menu audit menu-id:<id>` and confirm the published panel reports **Healthy**.
5. Temporarily remove one required channel permission, delete a configured role, or replace/delete the panel message one at a time; confirm the audit reports **Broken** with the expected reason and does not modify Discord or MongoDB state. Restore each condition after checking it.
6. Open the same panel as two different members and confirm each receives an independent private selector with only their own current menu roles preselected.
7. As a member, add both menu roles, remove one, and clear the menu. Confirm an unrelated role remains assigned throughout.
8. Publish a single menu, switch between its roles, and confirm no gap is introduced when the replacement can be added.
9. Delete one configured role in Discord and confirm the stale panel fails privately without changing any remaining or unrelated role.
10. Restart BeanBot and confirm the remaining panels still work from their persisted configuration.
11. Delete a panel through `/role-menu delete`, then confirm its old controls cannot mutate roles.
12. Temporarily remove BeanBot's hierarchy or permissions and confirm operations fail privately without exposing exception details or changing unrelated roles.
13. Use an existing legacy reaction-role panel and confirm its reactions still add and remove roles exactly as before.

## Code organization

`RoleMenuAdminModule` and `RoleMenuMemberModule` validate Discord control bindings and acknowledge interactions before starting work. Their shared `RoleMenuModuleBase` handles private responses, mention suppression, and acknowledgement reconciliation.

`RoleMenuAdministrationService` prepares drafts and connects publication and deletion to persistence. `RoleMenuMemberService` loads selectors and applies member choices through the mutation coordinator. These services take IDs and submitted values, without an interaction context. Each member operation keeps its own Discord member reference, so concurrent requests cannot share a mutation target.

`RoleMenuAuditWorkflow` evaluates persisted settings against read-only Discord snapshots and reuses the same saved-settings, panel-identity, and bot-role validity rules used by live member operations. `RoleMenuAuditor` supplies the finite per-menu lookup budget and the four-menu bulk concurrency cap; its operation seam exposes reads only, which keeps audit code structurally separate from role-menu mutations.

`DiscordRoleMenuClient` contains Discord REST reads and mutations. `RoleMenuSetupValidation` and `RoleMenuPresentation` keep validation and result text separate from transport. The publication, deletion, and member workflows retain their independently tested rules for ambiguous outcomes, rollback, final-state reconciliation, and cancellation. `RoleMenuInteractionService` supplies the shared bounded execution, draft, persistence, and lock operations used by those entry points.

Shutdown closes normal interaction, busy-response, and command-registration admission. If a Discord request ignores cancellation and outlives the drain timeout, its ownership remains visible to the application, which skips Discord stop/disposal until the request actually completes. A late `Ready` event cannot restart command registration after shutdown.
