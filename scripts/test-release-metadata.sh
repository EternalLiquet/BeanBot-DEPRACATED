#!/usr/bin/env bash
set -Eeuo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
temporary_directory="$(mktemp -d)"
trap 'rm -rf -- "$temporary_directory"' EXIT

sha="0123456789abcdef0123456789abcdef01234567"
index_digest="sha256:$(printf '1%.0s' {1..64})"
amd64_digest="sha256:$(printf 'a%.0s' {1..64})"
arm64_digest="sha256:$(printf 'b%.0s' {1..64})"
image="ghcr.io/example/beanbot"

for output in first second; do
  "$repository_root/scripts/create-release-metadata.sh" \
    2.18.0 "$sha" "$image" "$index_digest" "$amd64_digest" "$arm64_digest" \
    "$temporary_directory/$output.json"
done
cmp --silent "$temporary_directory/first.json" "$temporary_directory/second.json"
jq -e --arg digest "$index_digest" --arg amd64 "$amd64_digest" --arg arm64 "$arm64_digest" \
  '.digest == $digest and .platforms == {"linux/amd64":$amd64,"linux/arm64":$arm64}' \
  "$temporary_directory/first.json" >/dev/null

if "$repository_root/scripts/create-release-metadata.sh" \
  2.18.0 "$sha" "$image" "$index_digest" "$amd64_digest" "$amd64_digest" \
  "$temporary_directory/invalid.json"; then
  echo "Release metadata unexpectedly accepted identical child digests." >&2
  exit 1
fi

echo "Release metadata tests passed."
