#!/usr/bin/env python3
"""Validate BeanBot's checked-in Codex and CI workflow infrastructure."""

from __future__ import annotations

import os
from pathlib import Path
import re
import stat
import sys
import tomllib

try:
    import yaml
except ImportError:  # GitHub itself parses workflows before a job can start.
    yaml = None


REPOSITORY_ROOT = Path(__file__).resolve().parent.parent
SKILL_NAMES = ("beanbot-plan", "beanbot-implement", "beanbot-verify", "beanbot-review")
AGENTS = {
    "beanbot_planner.toml": ("beanbot_planner", "read-only"),
    "beanbot_verifier.toml": ("beanbot_verifier", "workspace-write"),
    "beanbot_reviewer.toml": ("beanbot_reviewer", "read-only"),
}


def fail(message: str) -> None:
    raise ValueError(message)


def validate_yaml(path: Path) -> None:
    text = path.read_text(encoding="utf-8")
    if "\t" in text:
        fail(f"{path.relative_to(REPOSITORY_ROOT)} contains a YAML tab indentation")
    if yaml is not None:
        try:
            yaml.compose(text)
        except yaml.YAMLError as error:
            fail(f"{path.relative_to(REPOSITORY_ROOT)} is not valid YAML: {error}")


def parse_skill_frontmatter(text: str, path: Path) -> dict[str, str]:
    if not text.startswith("---\n") or "\n---\n" not in text[4:]:
        fail(f"{path.relative_to(REPOSITORY_ROOT)} has invalid frontmatter")
    frontmatter_text, _ = text[4:].split("\n---\n", 1)
    metadata: dict[str, str] = {}
    for line in frontmatter_text.splitlines():
        key, separator, value = line.partition(":")
        if not separator or not key.strip() or not value.strip():
            fail(f"{path.relative_to(REPOSITORY_ROOT)} has invalid frontmatter content")
        metadata[key.strip()] = value.strip()
    return metadata


def validate_skills() -> None:
    for skill_name in SKILL_NAMES:
        skill_directory = REPOSITORY_ROOT / ".agents" / "skills" / skill_name
        skill_path = skill_directory / "SKILL.md"
        text = skill_path.read_text(encoding="utf-8")
        metadata = parse_skill_frontmatter(text, skill_path)
        _, body = text[4:].split("\n---\n", 1)
        if set(metadata) != {"name", "description"}:
            fail(f"{skill_path.relative_to(REPOSITORY_ROOT)} must contain only name and description metadata")
        if metadata["name"] != skill_name or not metadata["description"].strip():
            fail(f"{skill_path.relative_to(REPOSITORY_ROOT)} metadata does not match its directory")
        if not body.strip() or "TODO" in text:
            fail(f"{skill_path.relative_to(REPOSITORY_ROOT)} is incomplete")

        ui_path = skill_directory / "agents" / "openai.yaml"
        validate_yaml(ui_path)
        if f"${skill_name}" not in ui_path.read_text(encoding="utf-8"):
            fail(f"{ui_path.relative_to(REPOSITORY_ROOT)} default_prompt must mention ${skill_name}")


def validate_custom_agents() -> None:
    agent_directory = REPOSITORY_ROOT / ".codex" / "agents"
    for filename, (expected_name, expected_sandbox) in AGENTS.items():
        path = agent_directory / filename
        with path.open("rb") as stream:
            data = tomllib.load(stream)
        required = {"name", "description", "developer_instructions"}
        if not required.issubset(data):
            fail(f"{path.relative_to(REPOSITORY_ROOT)} is missing required custom-agent fields")
        if data["name"] != expected_name or data.get("sandbox_mode") != expected_sandbox:
            fail(f"{path.relative_to(REPOSITORY_ROOT)} has unexpected name or sandbox_mode")
        if "model" in data or "model_reasoning_effort" in data:
            fail(f"{path.relative_to(REPOSITORY_ROOT)} must inherit the parent model policy")


def validate_workflows() -> None:
    validation_path = REPOSITORY_ROOT / ".github" / "workflows" / "dotnetaction.yml"
    release_path = REPOSITORY_ROOT / ".github" / "workflows" / "autorelease.yml"
    dependency_review_path = REPOSITORY_ROOT / ".github" / "workflows" / "dependency-review.yml"
    codeql_path = REPOSITORY_ROOT / ".github" / "workflows" / "codeql.yml"
    dependabot_path = REPOSITORY_ROOT / ".github" / "dependabot.yml"
    workflow_paths = sorted((REPOSITORY_ROOT / ".github" / "workflows").glob("*.yml"))
    for path in (*workflow_paths, dependabot_path):
        validate_yaml(path)

    validation = validation_path.read_text(encoding="utf-8")
    release = release_path.read_text(encoding="utf-8")
    dependency_review = dependency_review_path.read_text(encoding="utf-8")
    codeql = codeql_path.read_text(encoding="utf-8")
    dependabot = dependabot_path.read_text(encoding="utf-8")
    required_validation_fragments = (
        "name: Repository Verification",
        "runs-on: ubuntu-24.04",
        "timeout-minutes: 45",
        "dotnet-version: 10.0.x",
        "./scripts/verify.sh full",
        "persist-credentials: false",
        "BEANBOT_BRANCH_INTEGRITY_CANDIDATE: ${{ github.event.pull_request.head.sha || github.sha }}",
        "BEANBOT_PR_HEAD_REPOSITORY: ${{ github.event.pull_request.head.repo.full_name || github.repository }}",
        ".artifacts/coverage",
    )
    required_release_fragments = (
        "name: Intentional BeanBot Release",
        "workflow_dispatch:",
        "./scripts/verify.sh full",
        "packages: write",
        "attestations: write",
        "beanbot.spdx.json",
        "./scripts/release-assets.sh inspect",
        "./scripts/release-assets.sh publish",
        "./scripts/release-image.sh inspect",
        "./scripts/release-image.sh stage",
        "./scripts/release-image.sh promote",
        "./scripts/create-release-checksums.sh",
        "RELEASE_DIGEST: ${{ steps.existing-release.outputs.release-digest }}",
        "cancel-in-progress: false",
    )
    for fragment in required_validation_fragments:
        if fragment not in validation:
            fail(f"dotnetaction.yml is missing required content: {fragment}")
    for fragment in required_release_fragments:
        if fragment not in release:
            fail(f"autorelease.yml is missing required content: {fragment}")
    if "build-test" in release or "release-on-push-action" in release:
        fail("release workflow must use full verification and GitHub-native release creation")
    if "--clobber" in release:
        fail("release workflow must never overwrite published evidence")

    ordered_release_steps = (
        "- name: Inspect existing GitHub release transaction",
        "- name: Inspect existing image transaction",
        "- name: Full release-quality verification",
        "- name: Stage verified image candidate",
        "- name: Generate SPDX SBOM",
        "- name: Attest image provenance",
        "- name: Attest image SBOM",
        "- name: Create release metadata and checksums",
        "- name: Upload release evidence",
        "- name: Promote immutable image tags",
    )
    step_positions = [release.index(step) for step in ordered_release_steps]
    if step_positions != sorted(step_positions):
        fail("release transaction steps are not in durable preflight/evidence/promotion order")

    for fragment in ("fail-on-severity: high", "timeout-minutes:"):
        if fragment not in dependency_review:
            fail(f"dependency-review.yml is missing required content: {fragment}")
    for fragment in ("languages: csharp", "build-mode: manual", "--locked-mode", "security-events: write"):
        if fragment not in codeql:
            fail(f"codeql.yml is missing required content: {fragment}")
    for ecosystem in ("nuget", "github-actions", "docker"):
        if f"package-ecosystem: {ecosystem}" not in dependabot:
            fail(f"dependabot.yml does not cover {ecosystem}")
    if dependabot.count("target-branch: develop") != 3:
        fail("all Dependabot ecosystems must target develop")

    immutable_action = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)?@[0-9a-f]{40}$")
    for path in workflow_paths:
        text = path.read_text(encoding="utf-8")
        for line in text.splitlines():
            stripped = line.strip()
            if not stripped.startswith("uses:"):
                continue
            action = stripped.removeprefix("uses:").split("#", 1)[0].strip()
            if action.startswith("./"):
                continue
            if not immutable_action.fullmatch(action):
                fail(f"{path.relative_to(REPOSITORY_ROOT)} has a mutable action reference: {action}")


def validate_policy_and_scripts() -> None:
    policy = (REPOSITORY_ROOT / "AGENTS.md").read_text(encoding="utf-8")
    for heading in ("## Working Rules", "## Engineering Invariants", "## Development Loop", "## Code Review Rules"):
        if heading not in policy:
            fail(f"AGENTS.md is missing {heading}")

    for relative_path in (
        "scripts/verify.sh",
        "scripts/test-verification.sh",
        "scripts/validate-workflow.py",
        "scripts/check-vulnerable-packages.py",
        "scripts/check-coverage.py",
        "scripts/check-branch-integrity.sh",
        "scripts/test-branch-integrity.sh",
        "scripts/container-smoke.sh",
        "scripts/release-image.sh",
        "scripts/release-assets.sh",
        "scripts/create-release-checksums.sh",
        "scripts/test-release-resume.sh",
        "scripts/test-release-checksums.sh",
    ):
        path = REPOSITORY_ROOT / relative_path
        if not path.stat().st_mode & stat.S_IXUSR:
            fail(f"{relative_path} must be executable")

    verifier = (REPOSITORY_ROOT / "scripts" / "verify.sh").read_text(encoding="utf-8")
    if 'run_stage "Test verification orchestration" scripts/test-verification.sh' not in verifier:
        fail("scripts/verify.sh must run the orchestration self-test")
    if "dotnet format BeanBot.sln --verify-no-changes --no-restore --severity warn" not in verifier:
        fail("repository verification must enforce .NET formatting and analyzer conventions")
    if "--vulnerable --include-transitive" not in verifier or "--format json --output-version 1" not in verifier:
        fail("full verification must request a machine-readable solution vulnerability report")
    if "scripts/check-vulnerable-packages.py" not in verifier:
        fail("full verification must reject findings in the vulnerability report")
    if "dotnet restore BeanBot.sln --locked-mode" not in verifier:
        fail("repository verification must use locked-mode restore")
    if "scripts/check-coverage.py" not in verifier or "coverage.runsettings" not in verifier:
        fail("full verification must enforce and publish the coverage baseline")
    if "BEANBOT_BRANCH_INTEGRITY_CANDIDATE" not in verifier:
        fail("full verification must reject master/develop branch drift")
    if "scripts/container-smoke.sh" not in verifier:
        fail("full verification must smoke test the hardened image")
    if "scripts/test-release-resume.sh" not in verifier or "scripts/test-release-checksums.sh" not in verifier:
        fail("repository verification must exercise release resumption and portable checksums")

    release_image = (REPOSITORY_ROOT / "scripts" / "release-image.sh").read_text(encoding="utf-8")
    release_assets = (REPOSITORY_ROOT / "scripts" / "release-assets.sh").read_text(encoding="utf-8")
    if "--prefer-index=false" not in release_image:
        fail("immutable image promotion must preserve the selected manifest digest")
    if "probe_optional_digest" not in release_image or "|| true" in release_image:
        fail("registry tag discovery must distinguish explicit absence from lookup failure")
    if "--clobber" in release_assets:
        fail("release asset publication must not overwrite evidence")
    if "probe_release_target" not in release_assets or "release_exists" in release_assets:
        fail("release discovery must fail closed on indeterminate GitHub API results")

    deployment = (REPOSITORY_ROOT / "docs" / "release-readiness.md").read_text(encoding="utf-8")
    if "--stop-timeout 130" not in deployment or "stop_grace_period: 2m10s" not in deployment:
        fail("deployment examples must exceed the two-minute Generic Host shutdown budget")

    dockerfile = (REPOSITORY_ROOT / "Dockerfile").read_text(encoding="utf-8")
    if dockerfile.count("@sha256:") != 2 or "USER $APP_UID" not in dockerfile:
        fail("Dockerfile must pin both base images and run as the .NET non-root user")
    if "dotnet restore \"BeanBot/BeanBot.csproj\" --locked-mode" not in dockerfile:
        fail("Docker restore must use the committed lock file")

    gitignore = (REPOSITORY_ROOT / ".gitignore").read_text(encoding="utf-8").splitlines()
    for ignored_directory in (".dotnet/", ".dotnet-home/"):
        if ignored_directory not in gitignore:
            fail(f".gitignore must exclude verification cache {ignored_directory}")


def main() -> int:
    os.chdir(REPOSITORY_ROOT)
    try:
        validate_skills()
        validate_custom_agents()
        validate_workflows()
        validate_policy_and_scripts()
    except (KeyError, OSError, TypeError, ValueError, tomllib.TOMLDecodeError) as error:
        print(f"Workflow validation failed: {error}", file=sys.stderr)
        return 1
    print("Workflow infrastructure validation passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
