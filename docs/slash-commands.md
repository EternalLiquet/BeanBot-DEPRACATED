# Slash commands

BeanBot supports Discord application commands alongside the existing message-command system.

## Commands

- `/ping` checks that BeanBot can receive and answer an interaction.
- `/pun` uses the same cached `IPunProvider` as the legacy `%pun` command.
- `/help` summarizes the slash-command surface and points users to `%help` for the complete legacy command list.
- `/role-menu create` opens a native Discord setup form for an administrator with **Manage Roles**.
- `/role-menu delete [menu-id]` removes a published dropdown role panel and its saved configuration. The optional exact ID supports servers with more than 25 menus.

Role-menu setup and deletion are server-only. Members manage their own allowlisted roles from each published panel without needing **Manage Roles**. See [Dropdown role menus](role-menus.md) for the full workflow and operational checks.

The existing `%`, `succ `, and mention-prefix commands remain supported and are not replaced by slash commands.

## Discord application setup

The bot installation must include the `applications.commands` OAuth2 scope in addition to the permissions already required by BeanBot. Existing installations created with a modern Discord bot authorization URL may already include application-command access; if slash commands do not appear, re-authorize the application with the required scope.

By default, BeanBot registers and synchronizes its interaction modules globally after the Discord client is connected. Set `BEANBOT_INTERACTION_GUILD_ID` to one non-zero Discord guild ID to use staging mode instead. In staging mode BeanBot registers and synchronizes the same command surface only in that guild and never invokes the global registration path. Leaving the setting unset or blank preserves the existing global behavior.

The registration scope is selected once from startup configuration and remains fixed for the process lifetime. Startup logs identify whether registration is global or guild-scoped and include the selected guild ID when applicable. Invalid or zero guild IDs fail configuration validation before normal Discord startup.

Normal concurrent or repeated `Ready` events share the same in-flight registration operation regardless of scope, and successful registration becomes a process-level no-op on later `Ready` events. Registration waits are bounded, but a timed-out REST task remains the sole in-flight attempt until it eventually succeeds or fails. A late success is retained; a completed failure allows a later `Ready` event to retry without ever starting overlapping registrations.

Both global and guild registration synchronize with `deleteMissing: true`, but only inside the selected scope. Guild staging therefore cannot delete or reconcile global commands. Conversely, returning a deployment to global mode does not clean up commands previously registered in a staging guild. BeanBot intentionally does not perform cross-scope cleanup because doing so would let one deployment mutate another deployment's command surface.

### Staging workflow

1. Set `BEANBOT_INTERACTION_GUILD_ID` to the Discord guild ID used for testing and start the staging BeanBot instance.
2. Confirm startup logs report `Guild` registration with the expected guild ID, then validate command changes in that guild. Guild-scoped command changes normally appear much faster than global changes.
3. When the change is ready for production, remove or blank `BEANBOT_INTERACTION_GUILD_ID` and deploy the production instance so it resumes global registration.
4. If the staging guild should no longer retain those commands, clean them up deliberately using Discord administration or a dedicated staging deployment. Switching BeanBot's scope does not mutate the old scope.

Do not run production and staging instances with the same bot application against the same registration scope unless you intentionally want both processes reconciling that scope. A staging process configured with a guild ID is isolated from global command reconciliation, but it still uses the same Discord application identity and command definitions.

Global application-command changes can take time to propagate in Discord. Do not repeatedly restart BeanBot to force propagation.

## Lifecycle and failure behavior

Interaction handlers are owned by a dedicated hosted service registered after the main BeanBot hosted service. The interaction service therefore stops before the core Discord runtime during normal Generic Host shutdown. Event subscriptions are removed on stop.

Interaction execution failures are logged through BeanBot's structured logging path. Users receive a generic ephemeral failure response rather than internal exception details. Gateway callbacks hand work off immediately so a long command cannot block Discord's event loop. BeanBot admits at most 64 interaction operations at once and reserves four short response slots for overload notices; additional work is not queued without a bound. In-flight interaction work receives the host shutdown signal and is given a bounded drain window before Discord teardown continues.
