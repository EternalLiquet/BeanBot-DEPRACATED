#!/usr/bin/env bash
set -Eeuo pipefail

[[ $# -eq 5 ]] || {
  echo "Usage: $0 IMAGE_NAME EXISTING_DIGEST STAGED_DIGEST RELEASE_VERSION OUTPUT_FILE" >&2
  exit 2
}

image_name="$1"
existing_digest="$2"
staged_digest="$3"
release_version="$4"
output_file="$5"

if [[ -n "$existing_digest" && -n "$staged_digest" ]]; then
  echo "A release image cannot be both reused and newly staged." >&2
  exit 1
fi

if [[ -n "$existing_digest" ]]; then
  image_digest="$existing_digest"
  provenance_action=verify
elif [[ -n "$staged_digest" ]]; then
  image_digest="$staged_digest"
  provenance_action=generate
else
  echo "No release image digest was selected." >&2
  exit 1
fi

[[ "$image_digest" =~ ^sha256:[0-9a-f]{64}$ ]] \
  || { echo "No valid release image digest was selected." >&2; exit 1; }

{
  echo "image-name=$image_name"
  echo "image-digest=$image_digest"
  echo "release-version=$release_version"
  echo "provenance-action=$provenance_action"
} >>"$output_file"
