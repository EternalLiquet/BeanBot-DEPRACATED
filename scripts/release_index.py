#!/usr/bin/env python3
"""Validate and describe BeanBot's supported OCI image index."""

from __future__ import annotations

import json
import re
import sys
from typing import Any

INDEX_MEDIA_TYPES = {
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.list.v2+json",
}
REQUIRED_PLATFORMS = (("linux", "amd64"), ("linux", "arm64"))
DIGEST_RE = re.compile(r"^sha256:[0-9a-f]{64}$")


def fail(message: str) -> "NoReturn":
    raise ValueError(message)


def validate_index(document: dict[str, Any]) -> dict[str, str]:
    media_type = document.get("mediaType")
    if media_type not in INDEX_MEDIA_TYPES:
        fail(f"Expected an OCI image index/manifest list, got {media_type!r}.")

    manifests = document.get("manifests")
    if not isinstance(manifests, list):
        fail("Image index is missing its manifests array.")

    found: dict[tuple[str, str], str] = {}
    for descriptor in manifests:
        if not isinstance(descriptor, dict):
            fail("Image index contains a non-object manifest descriptor.")
        platform = descriptor.get("platform")
        if not isinstance(platform, dict):
            fail("Image index contains a descriptor without platform metadata.")
        os_name = platform.get("os")
        architecture = platform.get("architecture")
        key = (os_name, architecture)
        if key not in REQUIRED_PLATFORMS:
            fail(f"Unexpected image-index platform {os_name}/{architecture}.")
        if key in found:
            fail(f"Image index contains duplicate platform {os_name}/{architecture}.")
        digest = descriptor.get("digest")
        if not isinstance(digest, str) or not DIGEST_RE.fullmatch(digest):
            fail(f"Image index contains an invalid digest for {os_name}/{architecture}.")
        found[key] = digest

    missing = [f"{os_name}/{arch}" for os_name, arch in REQUIRED_PLATFORMS if (os_name, arch) not in found]
    if missing:
        fail(f"Image index is missing required platform(s): {', '.join(missing)}.")
    if len(manifests) != len(REQUIRED_PLATFORMS):
        fail("Image index contains unexpected manifest descriptors.")

    return {
        "amd64-digest": found[("linux", "amd64")],
        "arm64-digest": found[("linux", "arm64")],
    }


def main() -> int:
    try:
        document = json.load(sys.stdin)
        if not isinstance(document, dict):
            fail("Image-index JSON must be an object.")
        result = validate_index(document)
    except (json.JSONDecodeError, ValueError) as error:
        print(f"Invalid BeanBot multi-platform image index: {error}", file=sys.stderr)
        return 1

    for key in ("amd64-digest", "arm64-digest"):
        print(f"{key}={result[key]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
