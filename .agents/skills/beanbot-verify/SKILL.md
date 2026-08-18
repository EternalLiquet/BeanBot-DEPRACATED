---
name: beanbot-verify
description: Independently verify a completed BeanBot diff against its approved plan and acceptance criteria. Use after implementation or corrections when a fresh verifier must inspect the diff, run all required gates, validate branch/PR policy, detect workspace mutations, and return PASS or FAIL without editing source files.
---

# Verify a BeanBot change independently

Require the original goal, approved plan, acceptance criteria, base branch, intended PR target, and complete diff. Read applicable `AGENTS.md`; inspect the diff instead of trusting the Implementer's summary.

1. Capture `git status --porcelain=v1 --untracked-files=all` before running commands. Record tracked and non-ignored files.
2. Confirm the branch strategy follows repository policy. Ordinary features, refactors, chores, and bug fixes must be based on and target `develop`. `master` is acceptable only for an intentional release/promotion or explicitly justified production hotfix; hotfix acceptance criteria must include merging/backporting the same fix into `develop`.
3. Map every acceptance criterion to concrete evidence.
4. Run relevant focused tests, then every applicable deterministic repository gate. Normally run `./scripts/verify.sh fast` and `./scripts/verify.sh full`; a required failed or skipped gate means FAIL.
5. Confirm local commands match CI, and inspect Docker/configuration and release-workflow implications where applicable. Flag any change that would cause routine development merges to create unintended GitHub Releases.
6. Check the diff and workspace for secrets, generated junk, weakened tests, and unexpected files.
7. Capture the same status baseline afterward and confirm it is unchanged. Ignored `bin`, `obj`, test-result, package, or Docker output is allowed.
8. Do not edit or repair implementation files. Do not commit, push, open a PR, merge, or create a release.

Return exactly this structure:

```text
Result: PASS | FAIL

Acceptance criteria:
- criterion -> pass/fail with evidence

Branch policy:
- base branch / PR target -> pass/fail with evidence

Commands run:
- command -> result

Findings:
- actionable correctness or verification issue

Unverified:
- unavailable, skipped, CI-only, or manual item with reason
```

Never report PASS while omitting a required check.
