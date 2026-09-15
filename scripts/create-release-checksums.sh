#!/usr/bin/env bash
set -Eeuo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
release_directory="${1:-$repository_root/.artifacts/release}"
(
  cd "$release_directory"
  payloads=(beanbot.spdx.json)
  if [[ -e beanbot-amd64.spdx.json || -e beanbot-arm64.spdx.json ]]; then
    [[ -s beanbot-amd64.spdx.json && -s beanbot-arm64.spdx.json ]] \
      || { echo "Both platform SBOMs are required for a multi-platform release." >&2; exit 1; }
    payloads+=(beanbot-amd64.spdx.json beanbot-arm64.spdx.json)
  fi
  payloads+=(release-metadata.json)
  sha256sum "${payloads[@]}" > SHA256SUMS
  sha256sum -c SHA256SUMS
)
