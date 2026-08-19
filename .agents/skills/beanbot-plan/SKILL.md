---
name: beanbot-plan
description: Create an implementation-ready, read-only plan for a BeanBot change. Use when a BeanBot goal needs current behavior, affected files, reliability constraints, acceptance criteria, branch strategy, and a concrete test plan established before implementation.
---

# Plan a BeanBot change

1. Read every applicable `AGENTS.md` before analyzing the request.
2. Inspect the current code, tests, configuration, Docker behavior, CI, and documentation relevant to the goal. Treat the repository as authoritative.
3. Confirm current behavior and distinguish evidence from assumptions. Identify affected files, compatibility risks, and any unresolved decision that would materially change the implementation.
4. Determine the branch strategy. Ordinary features, refactors, chores, and bug fixes should be based on `develop` and target `develop`. Use `master` only for an explicitly justified production hotfix or release/promotion task; hotfix plans must include merging or backporting the fix into `develop`.
5. Produce a bounded, implementation-ready plan with these sections:
   - Confirmed current behavior
   - Requested behavior
   - Relevant files/components
   - Branch strategy and intended PR target
   - Reliability/security constraints
   - Implementation plan
   - Test plan
   - Acceptance criteria
   - Risks/assumptions
6. Stop after returning the plan. If a material decision remains unresolved, state it and request direction.

Do not edit files, commit, push, open a pull request, or begin implementation.
