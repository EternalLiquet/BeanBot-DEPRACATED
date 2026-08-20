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

container_name="beanbot-shutdown-smoke-${RANDOM}-$$"
cleanup_container() {
  docker rm --force "$container_name" >/dev/null 2>&1 || true
}
trap 'cleanup_container; cleanup' EXIT

docker run --detach \
  --name "$container_name" \
  --stop-timeout 5 \
  --network none \
  --read-only \
  --tmpfs /tmp:rw,noexec,nosuid,size=16m \
  --cap-drop ALL \
  --security-opt no-new-privileges \
  --volume "$volume_name:/app/BeanBotFiles" \
  "$image_tag" \
  --container-shutdown-smoke-test >/dev/null

ready=0
for _ in {1..50}; do
  if docker logs "$container_name" 2>&1 | grep -Fq "BeanBot container shutdown smoke test ready."; then
    ready=1
    break
  fi
  sleep 0.1
done
if [[ "$ready" != "1" ]]; then
  echo "Container did not become ready for the shutdown smoke test." >&2
  docker logs "$container_name" >&2 || true
  exit 1
fi

docker stop --time 5 "$container_name" >/dev/null
exit_code="$(docker inspect --format '{{.State.ExitCode}}' "$container_name")"
if [[ "$exit_code" != "0" ]]; then
  echo "Container did not exit cleanly after SIGTERM (exit code $exit_code)." >&2
  exit 1
fi

echo "Container SIGTERM shutdown smoke test passed."
