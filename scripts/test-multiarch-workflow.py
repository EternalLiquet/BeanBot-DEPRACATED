#!/usr/bin/env python3
"""Deterministic assertions for BeanBot's intentional multi-platform release workflow."""

from pathlib import Path
import re
import yaml

ROOT = Path(__file__).resolve().parent.parent
WORKFLOW = ROOT / ".github" / "workflows" / "autorelease.yml"
MULTIARCH = ROOT / "scripts" / "release-multiarch.sh"
RELEASE_IMAGE = ROOT / "scripts" / "release-image.sh"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def main() -> int:
    text = WORKFLOW.read_text(encoding="utf-8")
    workflow = yaml.safe_load(text)
    job = workflow["jobs"]["verify-and-publish-image"]
    steps = job["steps"]
    names = [step.get("name") for step in steps]

    require(job["runs-on"] == "ubuntu-24.04", "release job must retain the native amd64 hosted runner")
    require(job["timeout-minutes"] <= 90, "release job must remain time-bounded")

    qemu = next(step for step in steps if step.get("name") == "Set up QEMU for ARM64 runtime validation")
    require(re.fullmatch(r"docker/setup-qemu-action@[0-9a-f]{40}", qemu["uses"]), "QEMU action must be commit-pinned")
    require(qemu.get("with", {}).get("platforms") == "arm64", "QEMU setup must be limited to arm64")

    buildx = next(step for step in steps if step.get("name") == "Set up Docker Buildx")
    require(re.fullmatch(r"docker/setup-buildx-action@[0-9a-f]{40}", buildx["uses"]), "Buildx action must be commit-pinned")

    amd64_smoke = names.index("Smoke test selected amd64 release child")
    arm64_smoke = names.index("Smoke test selected ARM64 release child")
    evidence = names.index("Upload release evidence")
    promotion = names.index("Promote immutable image tags")
    require(amd64_smoke < evidence < promotion, "amd64 selected-child smoke must block evidence and promotion")
    require(arm64_smoke < evidence < promotion, "arm64 selected-child smoke must block evidence and promotion")

    arm64_step = steps[arm64_smoke]
    require("smoke-platform" in arm64_step["run"] and "linux/arm64" in arm64_step["run"], "ARM64 gate must run the actual child through the hardened smoke script")
    require(arm64_step.get("env", {}).get("BEANBOT_PLATFORM_SMOKE_TIMEOUT_SECONDS") == 120, "ARM64 smoke must have an explicit timeout")

    for asset in ("beanbot-amd64.spdx.json", "beanbot-arm64.spdx.json"):
        require(asset in text, f"release workflow must generate {asset}")
    require("./scripts/create-release-metadata.sh" in text, "release workflow must bind index and child digests into metadata")

    helper = MULTIARCH.read_text(encoding="utf-8")
    require("--platform linux/amd64,linux/arm64" in helper, "multi-platform build must contain exactly amd64 and arm64")
    require("--provenance=false" in helper, "Buildx must not inject unknown-platform attestation descriptors into the two-child index")
    require("docker buildx imagetools create --tag" in helper and "--prefer-index=false" not in helper.split("promote)", 1)[1], "multi-platform promotion must preserve the OCI index")

    compatibility = RELEASE_IMAGE.read_text(encoding="utf-8")
    require("BEANBOT_RELEASE_MULTIARCH" in compatibility and "release-multiarch.sh" in compatibility, "intentional release must delegate through the existing release-image entry point without changing legacy self-test semantics")

    print("Multi-platform release workflow tests passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
