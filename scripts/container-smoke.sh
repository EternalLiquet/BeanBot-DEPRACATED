#!/usr/bin/env bash
set -Eeuo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "$repository_root"

if [[ $# -ne 1 || -z "$1" ]]; then
  echo "Usage: $0 IMAGE_TAG" >&2
  exit 2
fi

image_tag="$1"
configured_user="$(docker image inspect --format '{{.Config.User}}' "$image_tag")"
if [[ -z "$configured_user" || "$configured_user" == "0" || "$configured_user" == "root" ]]; then
  echo "Container image must configure a non-root default user." >&2
  exit 1
fi

volume_name="beanbot-smoke-${RANDOM}-$$"
docker volume create "$volume_name" >/dev/null
cleanup() {
  docker volume rm --force "$volume_name" >/dev/null
}
trap cleanup EXIT

docker run --rm \
  --network none \
  --read-only \
  --tmpfs /tmp:rw,noexec,nosuid,size=16m \
  --cap-drop ALL \
  --security-opt no-new-privileges \
  --volume "$volume_name:/app/BeanBotFiles" \
  "$image_tag" \
  --container-smoke-test
