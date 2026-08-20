# Slash commands

BeanBot supports Discord application commands alongside the existing message-command system. The initial slash-command surface is intentionally small so application-command infrastructure can ship without rewriting legacy commands.

## Initial commands

- `/ping` checks that BeanBot can receive and answer an interaction.
- `/pun` uses the same cached `IPunProvider` as the legacy `%pun` command.
- `/help` summarizes the slash-command surface and points users to `%help` for the complete legacy command list.

The existing `%`, `succ `, and mention-prefix commands remain supported and are not replaced by slash commands.

## Discord application setup

The bot installation must include the `applications.commands` OAuth2 scope in addition to the permissions already required by BeanBot. Existing installations created with a modern Discord bot authorization URL may already include application-command access; if slash commands do not appear, re-authorize the application with the required scope.

BeanBot registers its interaction modules globally after the Discord client is connected. Registration is process-idempotent: repeated `Ready` events share the same registration operation and do not create competing command-registration calls. A failed registration can retry on a later `Ready` event. Registration waits are bounded so an unresponsive Discord REST call does not block the hosted service forever.

Global application-command changes can take time to propagate in Discord. Do not repeatedly restart BeanBot to force propagation.

## Lifecycle and failure behavior

Interaction handlers are owned by a dedicated hosted service registered after the main BeanBot hosted service. The interaction service therefore stops before the core Discord runtime during normal Generic Host shutdown. Event subscriptions are removed on stop.

Interaction execution failures are logged through BeanBot's structured logging path. Users receive a generic ephemeral failure response rather than internal exception details.
