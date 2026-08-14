# BeanBot Development Policy

## Working Rules

- Perform coding work on a dedicated branch created from the latest `origin/master`; never implement directly on `master`.
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

## Development Loop

- Use Planner → approved plan → Implementer → Verifier → Reviewer for coding changes.
- The main Codex thread is the sole Implementer and sole source-file writer. Planner, Verifier, and Reviewer report evidence and findings; they do not repair code.
- The Implementer corrects Verifier or Reviewer findings, reruns focused checks, and requests fresh verification. Review begins only after verification passes.
- Stop after two reasonable attempts at the same materially unchanged failure, or sooner for unavailable infrastructure, conflicting requirements, missing permissions/tools, architectural conflict, or a fix that would require weakened tests or unrelated redesign. Report the command, evidence, attempts, and safest next action.
- Never commit ordinary transient plans, verification reports, or review results.

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
