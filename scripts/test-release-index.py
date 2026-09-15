#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import pathlib
import unittest

SCRIPT = pathlib.Path(__file__).with_name("release_index.py")
spec = importlib.util.spec_from_file_location("release_index", SCRIPT)
assert spec and spec.loader
release_index = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release_index)

AMD64 = "sha256:" + "a" * 64
ARM64 = "sha256:" + "b" * 64


def descriptor(os_name: str, architecture: str, digest: str) -> dict[str, object]:
    return {
        "mediaType": "application/vnd.oci.image.manifest.v1+json",
        "digest": digest,
        "platform": {"os": os_name, "architecture": architecture},
    }


def index(*manifests: dict[str, object]) -> dict[str, object]:
    return {
        "schemaVersion": 2,
        "mediaType": "application/vnd.oci.image.index.v1+json",
        "manifests": list(manifests),
    }


class ReleaseIndexTests(unittest.TestCase):
    def test_accepts_exact_supported_platforms(self) -> None:
        result = release_index.validate_index(
            index(descriptor("linux", "amd64", AMD64), descriptor("linux", "arm64", ARM64))
        )
        self.assertEqual(result, {"amd64-digest": AMD64, "arm64-digest": ARM64})

    def test_accepts_docker_manifest_list_media_type(self) -> None:
        document = index(descriptor("linux", "arm64", ARM64), descriptor("linux", "amd64", AMD64))
        document["mediaType"] = "application/vnd.docker.distribution.manifest.list.v2+json"
        self.assertEqual(release_index.validate_index(document)["arm64-digest"], ARM64)

    def test_rejects_missing_required_platform(self) -> None:
        with self.assertRaisesRegex(ValueError, "missing required platform"):
            release_index.validate_index(index(descriptor("linux", "amd64", AMD64)))

    def test_rejects_duplicate_required_platform(self) -> None:
        with self.assertRaisesRegex(ValueError, "duplicate platform"):
            release_index.validate_index(
                index(descriptor("linux", "amd64", AMD64), descriptor("linux", "amd64", ARM64))
            )

    def test_rejects_unexpected_platform(self) -> None:
        with self.assertRaisesRegex(ValueError, "Unexpected image-index platform"):
            release_index.validate_index(
                index(
                    descriptor("linux", "amd64", AMD64),
                    descriptor("linux", "arm64", ARM64),
                    descriptor("linux", "s390x", "sha256:" + "c" * 64),
                )
            )

    def test_rejects_attestation_descriptor_as_extra_platform(self) -> None:
        with self.assertRaisesRegex(ValueError, "Unexpected image-index platform"):
            release_index.validate_index(
                index(
                    descriptor("linux", "amd64", AMD64),
                    descriptor("linux", "arm64", ARM64),
                    descriptor("unknown", "unknown", "sha256:" + "c" * 64),
                )
            )

    def test_rejects_malformed_child_digest(self) -> None:
        with self.assertRaisesRegex(ValueError, "invalid digest"):
            release_index.validate_index(
                index(descriptor("linux", "amd64", "sha256:not-a-digest"), descriptor("linux", "arm64", ARM64))
            )

    def test_rejects_single_image_manifest(self) -> None:
        document = index(descriptor("linux", "amd64", AMD64), descriptor("linux", "arm64", ARM64))
        document["mediaType"] = "application/vnd.oci.image.manifest.v1+json"
        with self.assertRaisesRegex(ValueError, "Expected an OCI image index"):
            release_index.validate_index(document)


if __name__ == "__main__":
    unittest.main()
