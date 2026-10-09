# BeanBot Development Policy

## Working Rules

- For ordinary coding work, create a dedicated branch from the latest `origin/develop`; never implement directly on `develop` or `master`. Ordinary feature, refactor, chore, and bug-fix PRs target `develop`.
- Treat `master` as the production/release branch. Promote tested changes from `develop` to `master` through an intentional release/promotion PR rather than using `master` as the day-to-day integration branch.
- Critical production hotfixes may branch from the latest `origin/master` and target `master` when waiting for normal promotion is unsafe. After the hotfix lands, ensure the same fix is merged or backported into `develop` before ordinary development continues.
- Inspect current behavior, tests, configuration, Docker, CI, and documentation before editing. Implement the smallest complete change and avoid unrelated refactors.
- Keep code human-readable and human-reviewable. Prefer self-documenting method and variable names.
- Do not weaken tests, assertions, validation, authentication, rate limiting, or failure handling to obtain green output.
- Never commit or log `.env` contents, Discord tokens, MongoDB credentials, bearer tokens, channel IDs, connection strings, sensitive Discord payloads, or other deployment secrets. Secrets belong in environment variables.
- Preserve compatibility with the existing self-hosted .NET 10 Docker deployment. Runtime files belong under the existing persistent `BeanBotFiles` data directory.
- Critical bug fixes and security maintenance remain allowed. Do not add unrelated bot features, begin the Python migration, or perform the planned .NET/Discord.Net upgrade as part of another task.
- Do not merge a pull request or enable auto-merge unless the user explicitly instructs it.

## Engineering Invariants

- Register Discord event handlers and subscriptions at most once, and unsubscribe cleanly.
- Keep shutdown, cancellation, reconnect, retry, timeout, queue, and task-creation behavior bounded. Do not swallow cancellation.
- Prevent asynchronous failures from becoming unobserved or triggering recursive owner-alert failures.
- Keep Discord recovery race-safe; never start competing reconnect attempts.
- Keep persisted outage state atomic, corruption-tolerant, and retained until a recovery notification succeeds.
- Repeated Discord `Ready` events must not duplicate outage notifications, and failed delivery must not discard the persisted outage.
- Keep `/healthz` truthful about process and gateway state. Do not weaken health authentication or rate limiting.
- Preserve safe reaction-role persistence/cache consistency and cleanup behavior.

## Customer-Facing Text

This applies to everything a Discord member or administrator can read: replies, embeds, buttons, select options, placeholders, modal labels, slash-command and option descriptions, confirmations, and error messages. It applies to every feature, not only role menus. It is a review standard, not a guarantee that wording mistakes can never happen.

- Write the way a helpful person would talk in Discord. Use ordinary words and short sentences. Avoid stiff, robotic, or filler phrasing.
- Bean Bot speaks in the first person ("I couldn't…"). Address the reader as "you". Keep that voice consistent within a feature.
- Routine success says what happened and stops. Do not add reassurance, recaps, or narration about how the result was checked.
- Prompts and errors say what went wrong in plain terms and give the next safe step, such as the exact command to run again.
- Keep internal state, protocol, and storage details (database names, "reconciliation", "committed", "configuration", "panel state", menu internals, exception text) out of routine copy. Put diagnostics in the existing safe logs. Mention saved data only when a partial failure makes it relevant, and then describe it plainly ("saved settings").
- Partial and unknown results must stay truthful. Never imply full success, rollback, or "nothing changed" unless the code confirmed it. Say what was confirmed, what wasn't, and what to check before retrying, especially when a retry could create a duplicate.
- Use correct singular and plural forms, and readable lists ("A", "A and B", "A, B, and C"). Cover zero, one, and many.
- Don't accuse users. Expired, tampered, or foreign controls get a neutral "this has expired or belongs to someone else" message, while the server-side rejection stays in place.
- Wording changes must not weaken permission or hierarchy checks, ephemeral privacy, user-bound controls, mention suppression, Discord length limits, cancellation, or persistence behavior.

Examples:

| Before | After |
| --- | --- |
| "Removed (1): Test Role" + "Bean Bot rechecked Discord's current role state. No roles outside this menu were changed." | "Removed Test Role." |
| "Discord's current role state already matches your selection." | "You already have Gamer." |
| "That role selection was invalid or had been tampered with. No roles were changed." | "That selection is no longer valid. Open the menu again and choose your roles. No roles were changed." |
| "Confirm this destructive action." | "Delete this role menu?" |
| "Role-menu deletion cancelled." | "Deletion cancelled." |
| "The published panel is gone, but Bean Bot couldn't delete the saved configuration. Retry this command to finish cleanup." | "The menu message is gone, but I couldn't remove its saved settings. Run `/role-menu delete` again to finish." |

Copy review checklist for any change that adds or edits user-visible text:

- [ ] Would a regular server member understand this without knowing how Bean Bot works?
- [ ] Does a success message say only what changed?
- [ ] Does every error or prompt give a clear next step?
- [ ] Are partial and unknown outcomes still accurate about what was and wasn't confirmed?
- [ ] Are internal terms, IDs that aren't needed, and storage details kept out of routine copy and in the logs instead?
- [ ] Are singular, plural, and empty cases correct?
- [ ] Do labels and descriptions fit Discord's length limits?
- [ ] Are related screens (create, preview, publish, edit, delete, cancel) worded consistently?
- [ ] Were tests that assert the old text updated without dropping their behavioral checks?

## Development Loop

- Use Planner → approved plan → Implementer → Verifier → Reviewer for coding changes.
- The main Codex thread is the sole Implementer and sole source-file writer. Planner, Verifier, and Reviewer report evidence and findings; they do not repair code.
- The Implementer corrects Verifier or Reviewer findings, reruns focused checks, and requests fresh verification. Review begins only after verification passes.
- Stop after two reasonable attempts at the same materially unchanged failure, or sooner for unavailable infrastructure, conflicting requirements, missing permissions/tools, architectural conflict, or a fix that would require weakened tests or unrelated redesign. Report the command, evidence, attempts, and safest next action.
- Never commit ordinary transient plans, verification reports, or review results.

## Pull Request and Release Flow

- Ordinary work: `feature|fix|chore/...` → `develop`.
- Release promotion: `develop` → `master` through an intentional PR after required verification.
- Emergency hotfix: branch from `master` → `master`, then merge/backport the fix into `develop`.
- Do not create a GitHub Release merely because routine development was merged. Release creation should be tied to the repository's intentional release trigger/versioning workflow.
- Release-quality verification must prove current `master` is contained by the candidate, use committed NuGet lock files, preserve the measured non-Mongo coverage baseline, and smoke-test the hardened image. Record a separate exact-commit local MongoDB integration result; retain the historical combined coverage baseline for explicit all-tests runs. The exact operational gate is documented in `docs/release-readiness.md`.

## Code Review Rules

Prioritize consequential findings involving:

- Discord lifecycle races, duplicate subscriptions, or duplicate messages
- unbounded retries, waits, queues, or task creation; swallowed cancellation
- false healthy status or weakened health authentication/rate limiting
- loss, duplication, corruption, or non-atomic writes of persisted outage state
- recursive error reporting or unobserved asynchronous failures
- MongoDB consistency or reaction-role cache/persistence drift
- token, connection-string, bearer-token, channel-ID, or sensitive payload exposure
- Docker/runtime incompatibility
- weakened tests or assertions
- unrelated refactors
