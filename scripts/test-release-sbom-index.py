#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "scripts" / "create-release-sbom-index.py"
INDEX = "sha256:" + "1" * 64
AMD64 = "sha256:" + "a" * 64
ARM64 = "sha256:" + "b" * 64


class ReleaseSbomIndexTests(unittest.TestCase):
    def test_bundle_binds_both_child_documents_and_digests(self) -> None:
        with tempfile.TemporaryDirectory() as temp_text:
            temp = Path(temp_text)
            children = []
            for arch, created in (("amd64", "2026-09-15T15:00:00Z"), ("arm64", "2026-09-15T15:00:01Z")):
                path = temp / f"{arch}.json"
                path.write_text(json.dumps({
                    "spdxVersion": "SPDX-2.3",
                    "documentNamespace": f"https://example.invalid/{arch}",
                    "creationInfo": {"created": created},
                }) + "\n", encoding="utf-8")
                children.append(path)
            output = temp / "bundle.json"
            subprocess.run([
                str(SCRIPT), "2.18.0", INDEX, AMD64, str(children[0]), ARM64, str(children[1]), str(output)
            ], check=True)
            document = json.loads(output.read_text(encoding="utf-8"))
            refs = {entry["externalDocumentId"]: entry for entry in document["externalDocumentRefs"]}
            self.assertEqual(set(refs), {"DocumentRef-linux-amd64", "DocumentRef-linux-arm64"})
            self.assertEqual(refs["DocumentRef-linux-amd64"]["checksum"]["checksumValue"], hashlib.sha256(children[0].read_bytes()).hexdigest())
            self.assertEqual(refs["DocumentRef-linux-arm64"]["checksum"]["checksumValue"], hashlib.sha256(children[1].read_bytes()).hexdigest())
            comments = "\n".join(annotation["comment"] for annotation in document["annotations"])
            self.assertIn(INDEX, comments)
            self.assertIn(AMD64, comments)
            self.assertIn(ARM64, comments)

    def test_rejects_non_spdx_child(self) -> None:
        with tempfile.TemporaryDirectory() as temp_text:
            temp = Path(temp_text)
            good = temp / "good.json"
            bad = temp / "bad.json"
            good.write_text(json.dumps({"spdxVersion":"SPDX-2.3","documentNamespace":"https://example.invalid/good","creationInfo":{"created":"2026-09-15T15:00:00Z"}}), encoding="utf-8")
            bad.write_text("{}", encoding="utf-8")
            result = subprocess.run([str(SCRIPT), "2.18.0", INDEX, AMD64, str(good), ARM64, str(bad), str(temp / "out.json")])
            self.assertNotEqual(result.returncode, 0)


if __name__ == "__main__":
    unittest.main()
