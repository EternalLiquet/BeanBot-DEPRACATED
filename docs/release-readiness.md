# Release Readiness and Operations

This document is the required gate for an intentional `develop` to `master` promotion and the release that follows it. Record links to the final commit, required checks, verifier result, reviewer result, and smoke-test run in the promotion PR rather than committing transient evidence files.

## GitHub repository rules

Apply these rules after the named checks have run once on each branch so GitHub can select them without locking maintainers out.

### `develop`

- Require a pull request before merging and one approving review.
- Dismiss stale approvals and require approval of the most recent reviewable push.
- Require conversation resolution.
- Require `Repository Verification / Full repository verification`, `Dependency Review / Reject vulnerable dependency changes`, and `CodeQL / Analyze C#`.
- Require branches to be up to date before merging.
- Block branch deletion, force pushes, and ordinary direct pushes, including for administrators.

### `master`

- Apply the same review, conversation, update, deletion, force-push, and direct-push rules as `develop`.
- Require the same three exact-head checks. Repository verification rejects a promotion unless current `master` is already contained by the proposed head.
- Normal promotions must originate from `develop`. Emergency changes use a `hotfix/*` branch created from current `master`, still pass the required PR checks, and are merged or backported into `develop` immediately afterward.
- If an administrative bypass is retained for a hosting emergency, restrict it to repository administrators, document every use in an incident issue, and do not use it to bypass verification.

Enable GitHub dependency graph, Dependabot alerts/security updates, CodeQL code scanning, secret scanning, and push protection where the repository plan provides them. Treat unavailable plan features as a documented release risk; do not silently mark them complete.

## Automated release gate

1. Fetch `origin/master` and `origin/develop` and confirm `git merge-base --is-ancestor origin/master origin/develop` succeeds.
2. From a clean checkout of the exact `develop` head, run `./scripts/verify.sh full`.
3. Confirm the exact-head required checks are green and retain the coverage artifact.
4. Obtain an independent Verifier PASS followed by an independent Reviewer CLEAN.
5. Open the intentional `develop` to `master` promotion PR and repeat the required exact-head checks.
6. After promotion, run **Intentional BeanBot Release** on the exact current `master` commit and supply a stable `MAJOR.MINOR.PATCH` version. Normal promotions normally increment the minor version; an emergency compatible hotfix increments the patch version.

The release workflow publishes one immutable OCI image index with exactly `linux/amd64` and `linux/arm64` children. Full repository verification still builds and smoke-tests the native amd64 image first. The release transaction then builds both platform children from the same exact `master` checkout and explicit BeanBot version/commit identity, validates each child platform and OCI build-identity labels, and runs the hardened container smoke plus SIGTERM shutdown test against the selected amd64 child and the selected arm64 child. ARM64 runtime validation uses QEMU on the GitHub-hosted amd64 runner and is time-bounded; failure blocks publication.

The authoritative release digest is the OCI index digest. Both the commit-SHA tag and SemVer tag must resolve to that same immutable index. Release reconciliation rejects indexes with missing, duplicate, or unexpected platforms, conflicting child digests, or child images whose build labels do not match the requested commit/version. Promotion preflights both public aliases before mutating either one, so a conflict on the second alias cannot partially publish the first.

The Dockerfile remains digest-pinned for both .NET base images. The release's two-platform Buildx build is also the release-time proof that those pinned bases and the locked application dependency graph resolve for both supported architectures; a base pin that stops exposing either target architecture fails before release evidence or immutable promotion.

A newly staged index receives build provenance for the authoritative index digest. Because that digest cryptographically binds the exact child manifests, provenance identifies the complete multi-platform release. Release evidence additionally contains explicit `beanbot-amd64.spdx.json` and `beanbot-arm64.spdx.json` child SBOMs plus `beanbot.spdx.json`, an SPDX release document that cryptographically references both child SBOM files by SHA-256 and records the index/child digest mapping. That release SPDX document is attested to the authoritative index digest in GHCR, while all three SPDX documents and the platform metadata are included in the checksummed GitHub Release evidence. This keeps both children inside the release trust chain without pretending an index reference is itself a single filesystem to scan. A reused index never receives misleading fresh build provenance; its existing OCI provenance must verify against this repository, `.github/workflows/autorelease.yml`, the exact `master` commit and ref, and the selected index digest before evidence or tags can be published.

Release evidence uses the stable workflow-run artifact name `release-evidence-${{ github.run_id }}`. Re-running only the failed release job therefore downloads the evidence produced by the already-successful image job even though `github.run_attempt` increased. A complete workflow rerun intentionally replaces that workflow-run artifact through the pinned upload action's explicit `overwrite: true` behavior, after regenerating and verifying the complete evidence payload. A retry may reuse a version tag or release only when it already resolves to the same verified index and platform children; conflicting or unproven immutable identity fails the run.

When intentionally updating packages, edit `Directory.Packages.props`, run `dotnet restore BeanBot.sln --use-lock-file --force-evaluate`, review both `packages.lock.json` files, and rerun full verification.

## Inspecting a multi-platform release

Supported production platforms are exactly:

- `linux/amd64`
- `linux/arm64`

Docker automatically selects the matching child when a normal SemVer or commit-SHA image tag is pulled on either supported Linux architecture. BeanBot has no architecture-specific application configuration.

For troubleshooting, inspect the immutable index and its platform children without changing tags:

```bash
docker buildx imagetools inspect ghcr.io/eternalliquet/beanbot-depracated:VERSION
docker buildx imagetools inspect --raw ghcr.io/eternalliquet/beanbot-depracated:VERSION | jq .
```

`release-metadata.json` records the authoritative index digest plus the exact `linux/amd64` and `linux/arm64` child digests. Unsupported architectures are intentionally absent and should fail instead of receiving an untested image. For a forensic deployment, use the immutable index digest from release metadata; Docker still selects the matching child from that index.

## Release-candidate smoke test

Perform this checklist against the exact final `develop` commit using non-production test credentials and an isolated Mongo database where practical:

- [ ] Confirm Server Members Intent and Message Content Intent remain enabled; Presence Intent remains disabled.
- [ ] Build the image and confirm `./scripts/container-smoke.sh IMAGE` passes as the configured non-root user.
- [ ] Start with the documented read-only-root, capability-drop, `no-new-privileges`, data-volume, and `/tmp` settings.
- [ ] Confirm invalid/missing configuration fails safely without printing configured values.
- [ ] Confirm the Discord Gateway reaches `Ready` and `/healthz` becomes healthy with the expected version and commit SHA.
- [ ] Run a basic prefix command and mention-prefix command.
- [ ] Run a command containing arguments/remainder text.
- [ ] Confirm the direct-message behavior used by BeanBot.
- [ ] Confirm reaction add/remove behavior and Mongo-backed reaction-role persistence.
- [ ] Complete a pagination/interactive flow.
- [ ] Restart the container and confirm health and persisted reaction roles recover.
- [ ] Exercise a reproducible Discord disconnect/recovery cycle without creating competing reconnect attempts.
- [ ] Confirm persisted outage/recovery notification survives a process restart and is cleared only after delivery succeeds.
- [ ] Stop the container and confirm graceful shutdown completes within the two-minute host budget even if one injected teardown stage fails.

## Hardened deployment

BeanBot's image runs as the .NET application UID and writes persistent data only beneath `/app/BeanBotFiles`. A new named volume inherits the image directory ownership. For a host bind mount, create the directory and grant UID `1654` write access before starting the container.

The same release index digest is valid on supported amd64 and arm64 Linux hosts; Docker selects the matching child manifest automatically.

```bash
docker run -d \
  --name beanbot \
  --restart unless-stopped \
  --stop-timeout 130 \
  --env-file .env \
  --read-only \
  --tmpfs /tmp:rw,noexec,nosuid,size=64m \
  --cap-drop ALL \
  --security-opt no-new-privileges \
  -p 8080:8080 \
  -v beanbot-data:/app/BeanBotFiles \
  ghcr.io/eternalliquet/beanbot-depracated@sha256:RELEASE_INDEX_DIGEST
```

The 130-second container grace period is intentionally longer than BeanBot's
two-minute Generic Host shutdown budget. Docker's 10-second default is too short
for the bounded Discord and persistence cleanup sequence.

For Docker Compose, use the same deadline explicitly:

```yaml
services:
  beanbot:
    image: ghcr.io/eternalliquet/beanbot-depracated@sha256:RELEASE_INDEX_DIGEST
    stop_grace_period: 2m10s
```

## Rollback

1. Retain the existing data volume; do not rewrite or delete outage or reaction-role state.
2. Stop the failed container within the normal shutdown budget.
3. Start the preceding known-good release by its immutable **index** digest with the same environment and volume. The host selects its matching amd64/arm64 child automatically.
4. Confirm the logged/health build identity, Discord `Ready`, `/healthz`, reaction roles, and outage recovery.
5. Record the failed/restored index digests and, when architecture-specific diagnosis matters, the selected child digest from each release's metadata in an incident issue. Fix forward through `develop`, except for a critical production hotfix PR from `master`, followed immediately by its `develop` backport.
