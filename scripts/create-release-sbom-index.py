#!/usr/bin/env python3
"""Create an SPDX document that binds BeanBot's index to both child SBOMs."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import re
import sys
from typing import Any

DIGEST_RE = re.compile(r"^sha256:[0-9a-f]{64}$")


def die(message: str) -> "NoReturn":
    raise ValueError(message)


def load_child(path: Path) -> tuple[dict[str, Any], str, str]:
    document = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(document, dict) or document.get("spdxVersion") != "SPDX-2.3":
        die(f"{path} is not an SPDX 2.3 document")
    namespace = document.get("documentNamespace")
    created = document.get("creationInfo", {}).get("created")
    if not isinstance(namespace, str) or not namespace or not isinstance(created, str) or not created:
        die(f"{path} is missing SPDX document identity")
    checksum = hashlib.sha256(path.read_bytes()).hexdigest()
    return document, namespace, created


def main() -> int:
    if len(sys.argv) != 8:
        print(
            "Usage: create-release-sbom-index.py VERSION INDEX_DIGEST AMD64_DIGEST AMD64_SBOM ARM64_DIGEST ARM64_SBOM OUTPUT",
            file=sys.stderr,
        )
        return 2

    version, index_digest, amd64_digest, amd64_path_text, arm64_digest, arm64_path_text, output_text = sys.argv[1:]
    try:
        for label, digest in (("index", index_digest), ("amd64", amd64_digest), ("arm64", arm64_digest)):
            if not DIGEST_RE.fullmatch(digest):
                die(f"invalid {label} digest: {digest}")
        if amd64_digest == arm64_digest:
            die("amd64 and arm64 child digests must be distinct")

        amd64_path = Path(amd64_path_text)
        arm64_path = Path(arm64_path_text)
        _, amd64_namespace, amd64_created = load_child(amd64_path)
        _, arm64_namespace, arm64_created = load_child(arm64_path)
        created = max(amd64_created, arm64_created)
        index_hex = index_digest.removeprefix("sha256:")

        def external_ref(identifier: str, namespace: str, path: Path) -> dict[str, Any]:
            return {
                "externalDocumentId": identifier,
                "spdxDocument": namespace,
                "checksum": {
                    "algorithm": "SHA256",
                    "checksumValue": hashlib.sha256(path.read_bytes()).hexdigest(),
                },
            }

        document = {
            "spdxVersion": "SPDX-2.3",
            "dataLicense": "CC0-1.0",
            "SPDXID": "SPDXRef-DOCUMENT",
            "name": f"BeanBot {version} multi-platform release SBOM",
            "documentNamespace": f"https://github.com/EternalLiquet/BeanBot-DEPRACATED/releases/spdx/{version}/{index_hex}",
            "creationInfo": {
                "created": created,
                "creators": ["Tool: BeanBot release pipeline"],
            },
            "externalDocumentRefs": [
                external_ref("DocumentRef-linux-amd64", amd64_namespace, amd64_path),
                external_ref("DocumentRef-linux-arm64", arm64_namespace, arm64_path),
            ],
            "annotations": [
                {
                    "annotationDate": created,
                    "annotationType": "OTHER",
                    "annotator": "Tool: BeanBot release pipeline",
                    "comment": f"OCI index {index_digest} contains linux/amd64 child {amd64_digest}, described by DocumentRef-linux-amd64.",
                },
                {
                    "annotationDate": created,
                    "annotationType": "OTHER",
                    "annotator": "Tool: BeanBot release pipeline",
                    "comment": f"OCI index {index_digest} contains linux/arm64 child {arm64_digest}, described by DocumentRef-linux-arm64.",
                },
            ],
        }
        Path(output_text).write_text(json.dumps(document, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    except (OSError, json.JSONDecodeError, ValueError) as error:
        print(f"Unable to create multi-platform release SBOM index: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
