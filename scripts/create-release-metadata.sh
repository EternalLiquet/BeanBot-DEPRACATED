#!/usr/bin/env bash
set -Eeuo pipefail

if [[ $# -ne 7 ]]; then
  echo "Usage: $0 VERSION COMMIT_SHA IMAGE INDEX_DIGEST AMD64_DIGEST ARM64_DIGEST OUTPUT" >&2
  exit 2
fi

version="$1"
commit_sha="$2"
image_name="$3"
index_digest="$4"
amd64_digest="$5"
arm64_digest="$6"
output_file="$7"

validate_digest() {
  [[ "$1" =~ ^sha256:[0-9a-f]{64}$ ]] \
    || { echo "Invalid $2 digest: $1" >&2; exit 1; }
}

validate_digest "$index_digest" "image index"
validate_digest "$amd64_digest" "linux/amd64 child"
validate_digest "$arm64_digest" "linux/arm64 child"
[[ "$amd64_digest" != "$arm64_digest" ]] \
  || { echo "amd64 and arm64 child digests must be distinct." >&2; exit 1; }

jq -n \
  --arg version "$version" \
  --arg commitSha "$commit_sha" \
  --arg image "$image_name" \
  --arg digest "$index_digest" \
  --arg amd64Digest "$amd64_digest" \
  --arg arm64Digest "$arm64_digest" \
  '{
    version:$version,
    commitSha:$commitSha,
    image:$image,
    digest:$digest,
    platforms:{
      "linux/amd64":$amd64Digest,
      "linux/arm64":$arm64Digest
    }
  }' >"$output_file"
