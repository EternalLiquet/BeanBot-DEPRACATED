---
name: beanbot-review
description: Independently review a fully verified BeanBot diff for consequential correctness, reliability, security, test, deployment, and branch-policy risks. Use only after a fresh Verifier has returned PASS and the reviewer must report findings or CLEAN without editing files.
---

# Review a verified BeanBot change independently

Require the original goal, approved plan, acceptance criteria, base branch, intended PR target, complete diff, and fresh Verifier PASS evidence.

1. Read applicable `AGENTS.md`, especially `Working Rules`, `Pull Request and Release Flow`, and `Code Review Rules`.
2. Inspect the complete diff and relevant surrounding code. Do not trust summaries as evidence.
3. Confirm the branch/PR target follows policy: ordinary work is based on and targets `develop`; `master` is reserved for intentional release promotion or an explicitly justified production hotfix. A hotfix targeting `master` must also include a merge/backport path into `develop`.
4. Prioritize correctness, Discord lifecycle races, persistence semantics, health truthfulness, security, MongoDB/cache consistency, Docker compatibility, meaningful test gaps, and accidental release/workflow regressions. Avoid style-only comments unless they conceal a real defect.
5. For each finding, provide severity, file/behavior, evidence, impact, and expected correction.
6. List residual risks and intentionally manual checks. Return `Result: CLEAN` when no consequential finding remains.
7. Do not edit files, repair findings, commit, push, open a PR, merge, or create a release.

Review only after verification passes. A correction invalidates prior verification and review; require fresh verification before reviewing again.
