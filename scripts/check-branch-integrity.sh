#!/usr/bin/env bash
set -Eeuo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 BASE_REF CANDIDATE_REF" >&2
  exit 2
fi

base_ref="$1"
candidate_ref="$2"
git rev-parse --verify --quiet "${base_ref}^{commit}" >/dev/null \
  || { echo "Required base ref '$base_ref' is unavailable." >&2; exit 1; }
git rev-parse --verify --quiet "${candidate_ref}^{commit}" >/dev/null \
  || { echo "Candidate ref '$candidate_ref' is unavailable." >&2; exit 1; }

if ! git merge-base --is-ancestor "$base_ref" "$candidate_ref"; then
  echo "$base_ref is not an ancestor of $candidate_ref; reconcile master into develop before release-quality verification." >&2
  exit 1
fi

if [[ "${GITHUB_EVENT_NAME:-}" == "pull_request" && "${GITHUB_BASE_REF:-}" == "master" ]]; then
  case "${GITHUB_HEAD_REF:-}" in
    develop|hotfix/*) ;;
    *)
      echo "Pull requests to master must originate from develop or a documented hotfix/* branch." >&2
      exit 1
      ;;
  esac
fi

echo "Branch integrity verified: $base_ref is an ancestor of $candidate_ref."
