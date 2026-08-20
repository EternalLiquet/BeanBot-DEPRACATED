#!/usr/bin/env bash
set -Eeuo pipefail

usage() {
  echo "Usage: $0 IMAGE DIGEST REPOSITORY COMMIT_SHA SOURCE_REF" >&2
}

[[ $# -eq 5 ]] || { usage; exit 2; }

image_name="$1"
image_digest="$2"
repository="$3"
commit_sha="$4"
source_ref="$5"

[[ "$image_name" =~ ^ghcr\.io/[a-z0-9._/-]+$ ]] \
  || { echo "Release provenance requires a fully qualified lowercase GHCR image name." >&2; exit 2; }
[[ "$image_digest" =~ ^sha256:[0-9a-f]{64}$ ]] \
  || { echo "Release provenance requires a valid SHA-256 image digest." >&2; exit 2; }
[[ "$repository" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] \
  || { echo "Release provenance requires a repository in OWNER/NAME form." >&2; exit 2; }
[[ "$commit_sha" =~ ^[0-9a-f]{40}$ ]] \
  || { echo "Release provenance requires a full lowercase commit SHA." >&2; exit 2; }
[[ "$source_ref" == "refs/heads/master" ]] \
  || { echo "Release provenance must originate from refs/heads/master." >&2; exit 2; }

oci_reference="oci://$image_name@$image_digest"
signer_workflow="$repository/.github/workflows/autorelease.yml"
result_file="$(mktemp)"
trap 'rm -f -- "$result_file"' EXIT

if ! gh attestation verify "$oci_reference" \
    --repo "$repository" \
    --bundle-from-oci \
    --predicate-type "https://slsa.dev/provenance/v1" \
    --signer-workflow "$signer_workflow" \
    --source-digest "$commit_sha" \
    --source-ref "$source_ref" \
    --format json >"$result_file"; then
  echo "Existing build provenance could not be verified for $image_name@$image_digest." >&2
  exit 1
fi

digest_hex="${image_digest#sha256:}"
if ! jq -e \
    --arg image "$image_name" \
    --arg digest "$digest_hex" \
    'type == "array" and length > 0 and
      all(.[].verificationResult.statement.subject;
        type == "array" and length > 0 and
        all(.[]; .name == $image and .digest.sha256 == $digest))' \
    "$result_file" >/dev/null; then
  echo "Existing build provenance returned malformed, empty, or conflicting subject data." >&2
  exit 1
fi

echo "Existing build provenance verified for $image_name@$image_digest."
