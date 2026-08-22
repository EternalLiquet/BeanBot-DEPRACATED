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

BeanBot registers its interaction modules globally after the Discord client is connected. Normal concurrent or repeated `Ready` events share the same in-flight registration operation, and successful registration becomes a process-level no-op on later `Ready` events. Registration waits are bounded, but a timed-out REST task remains the sole in-flight attempt until it eventually succeeds or fails. A late success is retained; a completed failure allows a later `Ready` event to retry without ever starting overlapping global registrations.

Global application-command changes can take time to propagate in Discord. Do not repeatedly restart BeanBot to force propagation.

## Lifecycle and failure behavior

Interaction handlers are owned by a dedicated hosted service registered after the main BeanBot hosted service. The interaction service therefore stops before the core Discord runtime during normal Generic Host shutdown. Event subscriptions are removed on stop.

Interaction execution failures are logged through BeanBot's structured logging path. Users receive a generic ephemeral failure response rather than internal exception details. In-flight interaction work receives the host shutdown signal and is given a bounded drain window before Discord teardown continues.
