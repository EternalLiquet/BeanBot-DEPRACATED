# Codex Development Loop

BeanBot uses repository-scoped Codex skills and custom agents to separate planning, implementation, verification, and review. `AGENTS.md` is the authoritative shared policy; role skills contain only their workflow-specific instructions.

## Flow and ownership

1. Invoke `$beanbot-plan` or the `beanbot_planner` custom agent with the user goal. It inspects the repository read-only and returns current behavior, a bounded plan, tests, acceptance criteria, and risks.
2. After the user approves the plan, invoke `$beanbot-implement` in the main thread. The main thread is the sole writer and implements the smallest complete change.
3. Hand the original goal, approved plan, acceptance criteria, base branch, intended PR target, and complete diff to a fresh `beanbot_verifier`. It may create ignored build output but must not edit source files. `Result: FAIL` returns findings to the main thread.
4. After `Result: PASS`, hand the same context, diff, and verifier evidence to a fresh `beanbot_reviewer`. Findings return to the main thread; every correction requires fresh verification before review runs again. `Result: CLEAN` completes independent review.
5. Required exact-head GitHub CI for the intended PR target must pass before the PR is marked ready. A human decides whether to merge; the workflow never merges or enables auto-merge.

Plans, handoffs, verification evidence, and review results are transient conversation artifacts. Do not commit routine `PLAN.md`, `VERIFICATION.md`, or review-result files.

## Branch and pull request model

Ordinary feature, fix, refactor, and chore branches start from the current `origin/develop` and target `develop`. Tested changes reach the production/release branch through an intentional `develop` to `master` promotion PR; `master` is not the day-to-day integration branch. An emergency production hotfix may start from and target `master`, but the same fix must then be merged or backported into `develop` before ordinary development continues.

Repository verification CI runs for pull requests targeting `develop`, `master`, or `deploy`, and for pushes to those branches. The separate automated release workflow remains limited to pushes to `master`, so ordinary `develop` activity does not create a GitHub Release. See `AGENTS.md` for the authoritative shared branch and release policy.

## Verification interfaces

Run commands from any directory; each script resolves the repository root itself.

```bash
/path/to/BeanBot-DEPRACATED/scripts/test-verification.sh
/path/to/BeanBot-DEPRACATED/scripts/verify.sh fast
/path/to/BeanBot-DEPRACATED/scripts/verify.sh full
```

`fast` is the normal implementation loop. It tests the verifier's orchestration and failure propagation, validates Codex/CI configuration, restores the solution, verifies `.editorconfig` formatting and warning-level analyzers without another restore, builds Release, runs Release tests without a second build, and checks committed, staged, and unstaged diffs for whitespace errors.

`full` runs every fast gate once, then requests a machine-readable report for direct and transitive NuGet dependencies across the complete solution, fails explicitly if that report contains a known vulnerability, and builds the Docker image. The internal `build-test` mode gives the release workflow, which currently runs only for pushes to `master`, the same orchestration self-test, validation, restore, Release build, Release test, and diff gates without duplicating full-only Docker and vulnerability work.

The script keeps the .NET CLI home and NuGet package cache in the repository's ignored `.dotnet-home` and `.dotnet` directories unless the caller explicitly supplies `DOTNET_CLI_HOME` or `NUGET_PACKAGES`.

The gates have these dependencies:

- Configuration, script orchestration, build, test, and diff checks are deterministic local checks once dependencies are restored.
- Restore and vulnerable-package checks require NuGet/network availability.
- The Docker gate requires a working Docker daemon and registry/network access when base layers are absent.
- GitHub evaluates Actions syntax/triggers and supplies exact-head required-check evidence.
- Real Discord, production MongoDB, deployment health, and post-deployment behavior remain manual. Tests must not connect to them.

Formatting and warning-level analyzer conventions are mandatory gates backed by the repository's `.editorconfig`. No coverage threshold is imposed.

## Repair and infrastructure failures

The main thread applies Verifier and Reviewer corrections and reruns focused checks before requesting fresh verification. Stop after two reasonable attempts at the same materially unchanged failure, or sooner when infrastructure, requirements, permissions, tools, or architecture block safe progress. Report the command, evidence, attempts, and safest next action; never weaken a gate.

If only local infrastructure is unavailable, open a draft PR to obtain exact-head CI evidence, then give that evidence to a fresh Verifier. Failures arriving while an active Codex task is still running can be inspected and repaired. Failures after the task ends require another user/Codex invocation.

## Automation boundaries

Skills and custom-agent files guide active Codex sessions; they do not create a persistent autonomous daemon. Repository rules can guide Codex review, but cannot enable an external GitHub review setting. GitHub Actions performs shared verification for `develop`, `master`, and `deploy`; a separate release action remains limited to `master`. PR creation/readiness and deployment checks are user- or agent-triggered, merging remains human-controlled, and no automatic merge is configured.
