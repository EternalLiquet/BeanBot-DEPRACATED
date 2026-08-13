---
name: beanbot-verify
description: Independently verify a completed BeanBot diff against its approved plan and acceptance criteria. Use after implementation or corrections when a fresh verifier must inspect the diff, run all required gates, detect workspace mutations, and return PASS or FAIL without editing source files.
---

# Verify a BeanBot change independently

Require the original goal, approved plan, acceptance criteria, base branch, and complete diff. Read applicable `AGENTS.md`; inspect the diff instead of trusting the Implementer's summary.

1. Capture `git status --porcelain=v1 --untracked-files=all` before running commands. Record tracked and non-ignored files.
2. Map every acceptance criterion to concrete evidence.
3. Run relevant focused tests, then every applicable deterministic repository gate. Normally run `./scripts/verify.sh fast` and `./scripts/verify.sh full`; a required failed or skipped gate means FAIL.
4. Confirm local commands match CI, and inspect Docker/configuration implications where applicable.
5. Check the diff and workspace for secrets, generated junk, weakened tests, and unexpected files.
6. Capture the same status baseline afterward and confirm it is unchanged. Ignored `bin`, `obj`, test-result, package, or Docker output is allowed.
7. Do not edit or repair implementation files. Do not commit, push, or open a PR.

Return exactly this structure:

```text
Result: PASS | FAIL

Acceptance criteria:
- criterion -> pass/fail with evidence

Commands run:
- command -> result

Findings:
- actionable correctness or verification issue

Unverified:
- unavailable, skipped, CI-only, or manual item with reason
```

Never report PASS while omitting a required check.
