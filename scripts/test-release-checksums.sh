#!/usr/bin/env bash
set -Eeuo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
temporary_directory="$(mktemp -d)"
trap 'find "$temporary_directory" -mindepth 1 -delete; rmdir "$temporary_directory"' EXIT
mkdir -p "$temporary_directory/.artifacts/release"
printf 'sbom\n' >"$temporary_directory/.artifacts/release/beanbot.spdx.json"
printf 'metadata\n' >"$temporary_directory/.artifacts/release/release-metadata.json"
"$repository_root/scripts/create-release-checksums.sh" \
  "$temporary_directory/.artifacts/release"

clean_directory="$temporary_directory/downloaded"
mkdir "$clean_directory"
cp "$temporary_directory/.artifacts/release/beanbot.spdx.json" "$clean_directory/"
cp "$temporary_directory/.artifacts/release/release-metadata.json" "$clean_directory/"
cp "$temporary_directory/.artifacts/release/SHA256SUMS" "$clean_directory/"
(
  cd "$clean_directory"
  sha256sum -c SHA256SUMS
)
if grep -Fq '.artifacts/release/' "$clean_directory/SHA256SUMS"; then
  echo "Release checksums unexpectedly contain build-workspace paths." >&2
  exit 1
fi

echo "Release checksum tests passed."
