#!/usr/bin/env bash
set -Eeuo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
temporary_directory="$(mktemp -d)"
trap 'find "$temporary_directory" -mindepth 1 -delete; rmdir "$temporary_directory"' EXIT

git -C "$temporary_directory" init --quiet --initial-branch=master
git -C "$temporary_directory" config user.name "BeanBot Tests"
git -C "$temporary_directory" config user.email "beanbot-tests@example.invalid"
touch "$temporary_directory/baseline"
git -C "$temporary_directory" add baseline
git -C "$temporary_directory" commit --quiet -m baseline
git -C "$temporary_directory" branch develop

git -C "$temporary_directory" switch --quiet develop
touch "$temporary_directory/develop-change"
git -C "$temporary_directory" add develop-change
git -C "$temporary_directory" commit --quiet -m develop
(
  cd "$temporary_directory"
  "$repository_root/scripts/check-branch-integrity.sh" master HEAD
)

git -C "$temporary_directory" switch --quiet master
touch "$temporary_directory/master-hotfix"
git -C "$temporary_directory" add master-hotfix
git -C "$temporary_directory" commit --quiet -m hotfix
if (
  cd "$temporary_directory"
  "$repository_root/scripts/check-branch-integrity.sh" master develop
); then
  echo "Branch drift unexpectedly passed the ancestry guard." >&2
  exit 1
fi

git -C "$temporary_directory" branch --force develop master
if GITHUB_EVENT_NAME=pull_request GITHUB_BASE_REF=master GITHUB_HEAD_REF=feature/unsafe \
  bash -c 'cd "$1" && "$2" master develop' _ \
    "$temporary_directory" "$repository_root/scripts/check-branch-integrity.sh"; then
  echo "Unexpected master PR source branch passed the guard." >&2
  exit 1
fi
GITHUB_EVENT_NAME=pull_request GITHUB_BASE_REF=master GITHUB_HEAD_REF=hotfix/urgent \
  bash -c 'cd "$1" && "$2" master develop' _ \
    "$temporary_directory" "$repository_root/scripts/check-branch-integrity.sh"

echo "Branch integrity tests passed."
