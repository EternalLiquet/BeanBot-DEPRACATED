#!/usr/bin/env bash
set -Eeuo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "$repository_root"

usage() {
  cat >&2 <<'EOF'
Usage:
  release-multiarch.sh inspect IMAGE SHA VERSION OUTPUT [AUTHORITATIVE_DIGEST [AMD64_DIGEST ARM64_DIGEST]]
  release-multiarch.sh stage IMAGE SHA VERSION OUTPUT LOCAL_AMD64_TAG
  release-multiarch.sh validate IMAGE SHA VERSION INDEX_DIGEST OUTPUT
  release-multiarch.sh smoke-platform IMAGE SHA VERSION INDEX_DIGEST PLATFORM
  release-multiarch.sh promote IMAGE SHA VERSION INDEX_DIGEST
EOF
}

validate_digest() {
  [[ "$1" =~ ^sha256:[0-9a-f]{64}$ ]] \
    || { echo "Invalid or unavailable image digest for $2." >&2; exit 1; }
}

probe_digest() {
  local reference="$1" error_file output digest status
  error_file="$(mktemp)"
  if output="$(docker buildx imagetools inspect "$reference" 2>"$error_file")"; then
    digest="$(awk '$1 == "Digest:" { print $2; exit }' <<<"$output")"
    rm -f "$error_file"
    if [[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]]; then
      printf '%s\n' "$digest"
      return 0
    fi
    echo "Registry returned an invalid digest response for $reference." >&2
    return 2
  else
    status=$?
  fi
  if grep -Eiq 'manifest unknown|no such manifest|not found' "$error_file"; then
    rm -f "$error_file"
    return 1
  fi
  echo "Registry lookup for $reference failed (exit $status):" >&2
  sed -n '1,20p' "$error_file" >&2
  rm -f "$error_file"
  return 2
}

probe_optional_digest() {
  local reference="$1" digest status
  if digest="$(probe_digest "$reference")"; then
    printf '%s\n' "$digest"
    return 0
  else
    status=$?
  fi
  if [[ "$status" == "1" ]]; then
    return 0
  fi
  return "$status"
}

read_index() {
  local reference="$1" raw_file parsed_file
  raw_file="$(mktemp)"
  parsed_file="$(mktemp)"
  if ! docker buildx imagetools inspect --raw "$reference" >"$raw_file"; then
    rm -f "$raw_file" "$parsed_file"
    echo "Unable to inspect multi-platform image index $reference." >&2
    return 1
  fi
  if ! python3 scripts/release_index.py <"$raw_file" >"$parsed_file"; then
    rm -f "$raw_file" "$parsed_file"
    return 1
  fi
  VALIDATED_AMD64_DIGEST="$(awk -F= '$1 == "amd64-digest" { print $2; exit }' "$parsed_file")"
  VALIDATED_ARM64_DIGEST="$(awk -F= '$1 == "arm64-digest" { print $2; exit }' "$parsed_file")"
  rm -f "$raw_file" "$parsed_file"
  validate_digest "$VALIDATED_AMD64_DIGEST" "$reference linux/amd64 child"
  validate_digest "$VALIDATED_ARM64_DIGEST" "$reference linux/arm64 child"
}

validate_local_identity() {
  local reference="$1" expected_sha="$2" expected_version="$3"
  local actual_sha actual_version
  actual_sha="$(docker image inspect --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}' "$reference")"
  actual_version="$(docker image inspect --format '{{ index .Config.Labels "org.opencontainers.image.version" }}' "$reference")"
  [[ "$actual_sha" == "$expected_sha" && "$actual_version" == "$expected_version" ]] \
    || { echo "Image $reference has build identity '$actual_sha'/'$actual_version', expected '$expected_sha'/'$expected_version'." >&2; exit 1; }
}

validate_child_identity() {
  local image_name="$1" platform="$2" child_digest="$3" expected_sha="$4" expected_version="$5"
  local reference="$image_name@$child_digest" actual_platform actual_sha actual_version
  docker pull --platform "$platform" "$reference" >/dev/null
  actual_platform="$(docker image inspect --format '{{.Os}}/{{.Architecture}}' "$reference")"
  actual_sha="$(docker image inspect --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}' "$reference")"
  actual_version="$(docker image inspect --format '{{ index .Config.Labels "org.opencontainers.image.version" }}' "$reference")"
  [[ "$actual_platform" == "$platform" ]] \
    || { echo "Image child $reference resolved as '$actual_platform', expected '$platform'." >&2; exit 1; }
  [[ "$actual_sha" == "$expected_sha" && "$actual_version" == "$expected_version" ]] \
    || { echo "Image child $reference has build identity '$actual_sha'/'$actual_version', expected '$expected_sha'/'$expected_version'." >&2; exit 1; }
}

validate_reference() {
  local image_name="$1" reference="$2" expected_sha="$3" expected_version="$4"
  read_index "$reference"
  validate_child_identity "$image_name" "linux/amd64" "$VALIDATED_AMD64_DIGEST" "$expected_sha" "$expected_version"
  validate_child_identity "$image_name" "linux/arm64" "$VALIDATED_ARM64_DIGEST" "$expected_sha" "$expected_version"
}

write_identity_outputs() {
  local output_file="$1" index_digest="$2"
  {
    echo "image-digest=$index_digest"
    echo "amd64-digest=$VALIDATED_AMD64_DIGEST"
    echo "arm64-digest=$VALIDATED_ARM64_DIGEST"
  } >>"$output_file"
}

[[ $# -ge 5 ]] || { usage; exit 2; }
command_name="$1"
image_name="$2"
commit_sha="$3"
release_version="$4"
value="$5"

case "$command_name" in
  inspect)
    [[ $# -eq 5 || $# -eq 6 || $# -eq 8 ]] || { usage; exit 2; }
    authoritative_digest="${6:-}"
    expected_amd64_digest="${7:-}"
    expected_arm64_digest="${8:-}"
    if [[ -n "$expected_amd64_digest" || -n "$expected_arm64_digest" ]]; then
      [[ -n "$authoritative_digest" && -n "$expected_amd64_digest" && -n "$expected_arm64_digest" ]] \
        || { echo "Durable multi-platform metadata must provide the index and both child digests." >&2; exit 1; }
      validate_digest "$expected_amd64_digest" "durable linux/amd64 metadata"
      validate_digest "$expected_arm64_digest" "durable linux/arm64 metadata"
    fi

    commit_reference="$image_name:$commit_sha"
    version_reference="$image_name:$release_version"
    commit_digest="$(probe_optional_digest "$commit_reference")" || exit $?
    version_digest="$(probe_optional_digest "$version_reference")" || exit $?
    if [[ -n "$commit_digest" ]]; then validate_digest "$commit_digest" "$commit_reference"; fi
    if [[ -n "$version_digest" ]]; then validate_digest "$version_digest" "$version_reference"; fi
    if [[ -n "$commit_digest" && -n "$version_digest" && "$commit_digest" != "$version_digest" ]]; then
      echo "Existing immutable commit and version tags disagree." >&2
      exit 1
    fi

    selected_digest="${commit_digest:-$version_digest}"
    if [[ -n "$authoritative_digest" ]]; then
      validate_digest "$authoritative_digest" "existing release metadata"
      if [[ -n "$selected_digest" && "$selected_digest" != "$authoritative_digest" ]]; then
        echo "Existing immutable image tags conflict with durable release metadata." >&2
        exit 1
      fi
      selected_digest="$authoritative_digest"
    fi

    {
      echo "reuse=$([[ -n "$selected_digest" ]] && echo true || echo false)"
      echo "image-digest=$selected_digest"
    } >>"$value"
    if [[ -n "$selected_digest" ]]; then
      validate_reference "$image_name" "$image_name@$selected_digest" "$commit_sha" "$release_version"
      if [[ -n "$expected_amd64_digest" && "$VALIDATED_AMD64_DIGEST" != "$expected_amd64_digest" ]]; then
        echo "Durable release metadata has the wrong linux/amd64 child digest." >&2
        exit 1
      fi
      if [[ -n "$expected_arm64_digest" && "$VALIDATED_ARM64_DIGEST" != "$expected_arm64_digest" ]]; then
        echo "Durable release metadata has the wrong linux/arm64 child digest." >&2
        exit 1
      fi
      {
        echo "amd64-digest=$VALIDATED_AMD64_DIGEST"
        echo "arm64-digest=$VALIDATED_ARM64_DIGEST"
      } >>"$value"
    fi
    ;;
  stage)
    [[ $# -eq 6 ]] || { usage; exit 2; }
    local_amd64_tag="$6"
    validate_local_identity "$local_amd64_tag" "$commit_sha" "$release_version"
    candidate_tag="$image_name:release-candidate-${GITHUB_RUN_ID:?}-${GITHUB_RUN_ATTEMPT:?}"
    docker buildx build \
      --platform linux/amd64,linux/arm64 \
      --provenance=false \
      --file Dockerfile \
      --build-arg "BEANBOT_VERSION=$release_version" \
      --build-arg "BEANBOT_COMMIT_SHA=$commit_sha" \
      --tag "$candidate_tag" \
      --push \
      .
    candidate_digest="$(probe_digest "$candidate_tag")"
    validate_digest "$candidate_digest" "$candidate_tag"
    validate_reference "$image_name" "$candidate_tag" "$commit_sha" "$release_version"
    write_identity_outputs "$value" "$candidate_digest"
    ;;
  validate)
    [[ $# -eq 6 ]] || { usage; exit 2; }
    output_file="$6"
    selected_digest="$value"
    validate_digest "$selected_digest" "$image_name"
    validate_reference "$image_name" "$image_name@$selected_digest" "$commit_sha" "$release_version"
    write_identity_outputs "$output_file" "$selected_digest"
    ;;
  smoke-platform)
    [[ $# -eq 6 ]] || { usage; exit 2; }
    selected_digest="$value"
    platform="$6"
    validate_digest "$selected_digest" "$image_name"
    validate_reference "$image_name" "$image_name@$selected_digest" "$commit_sha" "$release_version"
    case "$platform" in
      linux/amd64) child_digest="$VALIDATED_AMD64_DIGEST" ;;
      linux/arm64) child_digest="$VALIDATED_ARM64_DIGEST" ;;
      *) echo "Unsupported smoke-test platform: $platform" >&2; exit 2 ;;
    esac
    timeout "${BEANBOT_PLATFORM_SMOKE_TIMEOUT_SECONDS:-120}" \
      scripts/container-smoke.sh "$image_name@$child_digest" "$platform"
    ;;
  promote)
    [[ $# -eq 5 ]] || { usage; exit 2; }
    selected_digest="$value"
    validate_digest "$selected_digest" "$image_name"
    validate_reference "$image_name" "$image_name@$selected_digest" "$commit_sha" "$release_version"
    selected_amd64_digest="$VALIDATED_AMD64_DIGEST"
    selected_arm64_digest="$VALIDATED_ARM64_DIGEST"
    tags=("$commit_sha" "$release_version")
    existing_digests=()

    # Preflight both immutable aliases before creating either. Any indeterminate
    # lookup, conflicting digest, malformed index, or child-identity mismatch
    # fails closed before public mutation begins.
    for tag in "${tags[@]}"; do
      reference="$image_name:$tag"
      existing_digest="$(probe_optional_digest "$reference")" || exit $?
      existing_digests+=("$existing_digest")
      if [[ -n "$existing_digest" && "$existing_digest" != "$selected_digest" ]]; then
        echo "Immutable image tag $reference already points to a different digest." >&2
        exit 1
      fi
      if [[ -n "$existing_digest" ]]; then
        validate_reference "$image_name" "$reference" "$commit_sha" "$release_version"
        [[ "$VALIDATED_AMD64_DIGEST" == "$selected_amd64_digest" && "$VALIDATED_ARM64_DIGEST" == "$selected_arm64_digest" ]] \
          || { echo "Immutable image tag $reference has unexpected platform children." >&2; exit 1; }
      fi
    done

    for index in "${!tags[@]}"; do
      tag="${tags[$index]}"
      reference="$image_name:$tag"
      if [[ -z "${existing_digests[$index]}" ]]; then
        docker buildx imagetools create --tag "$reference" "$image_name@$selected_digest"
      fi
      [[ "$(probe_digest "$reference")" == "$selected_digest" ]] \
        || { echo "Immutable image tag $reference did not resolve to the selected index digest." >&2; exit 1; }
      validate_reference "$image_name" "$reference" "$commit_sha" "$release_version"
      [[ "$VALIDATED_AMD64_DIGEST" == "$selected_amd64_digest" && "$VALIDATED_ARM64_DIGEST" == "$selected_arm64_digest" ]] \
        || { echo "Immutable image tag $reference changed platform children during promotion." >&2; exit 1; }
    done
    ;;
  *) usage; exit 2 ;;
esac
