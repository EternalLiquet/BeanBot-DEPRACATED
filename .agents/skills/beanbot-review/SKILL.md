---
name: beanbot-review
description: Independently review a fully verified BeanBot diff for consequential correctness, reliability, security, test, and deployment risks. Use only after a fresh Verifier has returned PASS and the reviewer must report findings or CLEAN without editing files.
---

# Review a verified BeanBot change independently

Require the original goal, approved plan, acceptance criteria, base branch, complete diff, and fresh Verifier PASS evidence.

1. Read applicable `AGENTS.md`, especially `Code Review Rules`.
2. Inspect the complete diff and relevant surrounding code. Do not trust summaries as evidence.
3. Prioritize correctness, Discord lifecycle races, persistence semantics, health truthfulness, security, MongoDB/cache consistency, Docker compatibility, and meaningful test gaps. Avoid style-only comments unless they conceal a real defect.
4. For each finding, provide severity, file/behavior, evidence, impact, and expected correction.
5. List residual risks and intentionally manual checks. Return `Result: CLEAN` when no consequential finding remains.
6. Do not edit files, repair findings, commit, push, or open a PR.

Review only after verification passes. A correction invalidates prior verification and review; require fresh verification before reviewing again.
