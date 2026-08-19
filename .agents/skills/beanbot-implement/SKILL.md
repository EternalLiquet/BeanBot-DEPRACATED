---
name: beanbot-implement
description: Implement an approved BeanBot plan as the main-thread sole writer and drive it through focused checks, independent verification, and independent review. Use for BeanBot coding tasks after the user has approved a concrete plan and acceptance criteria.
---

# Implement an approved BeanBot plan

1. Require the approved plan and acceptance criteria. Read applicable `AGENTS.md` and inspect repository reality before following the plan. Stop if they materially conflict.
2. Select the correct base branch before writing code. For ordinary features, refactors, chores, and bug fixes, confirm a clean dedicated branch from current `origin/develop`. Use `origin/master` only for an explicitly justified production hotfix or release/promotion task. Never implement directly on `develop` or `master`.
3. Add meaningful behavioral and failure-path tests. Preserve existing architecture, reliability rules, validation, authentication, and failure handling.
4. Run the narrowest relevant checks first and repair failures. Then run `./scripts/verify.sh fast` and, before PR completion, `./scripts/verify.sh full` when its dependencies are available.
5. Give a fresh Verifier the complete original goal, approved plan, acceptance criteria, base branch, intended PR target, and full diff. Correct every actionable finding and request fresh verification.
6. Request a fresh independent Reviewer only after `Result: PASS`. Correct consequential review findings, rerun verification, and request review again.
7. Prepare, commit, push, and open or update the PR only when required gates pass. Ordinary work targets `develop`. Release promotion uses an intentional `develop` → `master` PR. An emergency hotfix may target `master`, but its completion criteria must include merging or backporting the same fix into `develop`. If local infrastructure alone blocks a required gate, use a draft PR for exact-head CI evidence, then return that evidence to a fresh Verifier.
8. Mark the PR ready only after Verifier PASS, Reviewer CLEAN, and required exact-head CI succeeds. Never merge or enable auto-merge without explicit instruction. Do not create a GitHub Release merely because ordinary development was merged; follow the repository's intentional release trigger/versioning workflow.

Keep the repair loop bounded: stop after two reasonable attempts at the same materially unchanged failure, or sooner for an environmental failure, unavailable Discord/GitHub/NuGet/Docker infrastructure, conflicting requirements, missing tools or permissions, repository/plan conflict, required test weakening, or unrelated redesign. Report the failing command, evidence, attempts, and safe next action.

The main thread remains the only writer. Do not delegate implementation to another agent.
