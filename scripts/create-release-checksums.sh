#!/usr/bin/env bash
set -Eeuo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
release_directory="${1:-$repository_root/.artifacts/release}"
(
  cd "$release_directory"
  sha256sum beanbot.spdx.json release-metadata.json > SHA256SUMS
  sha256sum -c SHA256SUMS
)
