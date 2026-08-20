#!/usr/bin/env bash
set -Eeuo pipefail

usage() {
  echo "Usage: $0 {inspect|stage|promote} IMAGE SHA VERSION OUTPUT_OR_DIGEST [LOCAL_TAG]" >&2
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

validate_digest() {
  [[ "$1" =~ ^sha256:[0-9a-f]{64}$ ]] \
    || { echo "Invalid or unavailable image digest for $2." >&2; exit 1; }
}

validate_identity() {
  local reference="$1" expected_sha="$2" expected_version="$3" pull_image="${4:-true}"
  if [[ "$pull_image" == "true" ]]; then
    docker pull "$reference" >/dev/null
  fi
  local actual_sha actual_version
  actual_sha="$(docker image inspect --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}' "$reference")"
  actual_version="$(docker image inspect --format '{{ index .Config.Labels "org.opencontainers.image.version" }}' "$reference")"
  [[ "$actual_sha" == "$expected_sha" && "$actual_version" == "$expected_version" ]] \
    || { echo "Image $reference has build identity '$actual_sha'/'$actual_version', expected '$expected_sha'/'$expected_version'." >&2; exit 1; }
}

[[ $# -ge 5 ]] || { usage; exit 2; }
command_name="$1"
image_name="$2"
commit_sha="$3"
release_version="$4"
value="$5"

case "$command_name" in
  inspect)
    [[ $# -eq 5 || $# -eq 6 ]] || { usage; exit 2; }
    authoritative_digest="${6:-}"
    commit_reference="$image_name:$commit_sha"
    version_reference="$image_name:$release_version"
    commit_digest="$(probe_optional_digest "$commit_reference")" || exit $?
    version_digest="$(probe_optional_digest "$version_reference")" || exit $?
    if [[ -n "$commit_digest" ]]; then
      validate_digest "$commit_digest" "$commit_reference"
      validate_identity "$commit_reference" "$commit_sha" "$release_version"
    fi
    if [[ -n "$version_digest" ]]; then
      validate_digest "$version_digest" "$version_reference"
      validate_identity "$version_reference" "$commit_sha" "$release_version"
    fi
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
      validate_identity "$image_name@$authoritative_digest" "$commit_sha" "$release_version"
      selected_digest="$authoritative_digest"
    fi
    {
      echo "reuse=$([[ -n "$selected_digest" ]] && echo true || echo false)"
      echo "image-digest=$selected_digest"
    } >>"$value"
    ;;
  stage)
    [[ $# -eq 6 ]] || { usage; exit 2; }
    local_tag="$6"
    candidate_tag="$image_name:release-candidate-${GITHUB_RUN_ID:?}-${GITHUB_RUN_ATTEMPT:?}"
    validate_identity "$local_tag" "$commit_sha" "$release_version" false
    docker tag "$local_tag" "$candidate_tag"
    docker push "$candidate_tag"
    candidate_digest="$(probe_digest "$candidate_tag")"
    validate_digest "$candidate_digest" "$candidate_tag"
    echo "image-digest=$candidate_digest" >>"$value"
    ;;
  promote)
    [[ $# -eq 5 ]] || { usage; exit 2; }
    selected_digest="$value"
    validate_digest "$selected_digest" "$image_name"
    tags=("$commit_sha" "$release_version")
    existing_digests=()
    # Preflight both immutable aliases before creating either one. An
    # indeterminate or conflicting second lookup must not leave the first tag
    # newly published.
    for tag in "${tags[@]}"; do
      reference="$image_name:$tag"
      existing_digest="$(probe_optional_digest "$reference")" || exit $?
      existing_digests+=("$existing_digest")
      if [[ -n "$existing_digest" && "$existing_digest" != "$selected_digest" ]]; then
        echo "Immutable image tag $reference already points to a different digest." >&2
        exit 1
      fi
      if [[ -n "$existing_digest" ]]; then
        validate_identity "$reference" "$commit_sha" "$release_version"
      fi
    done
    for index in "${!tags[@]}"; do
      tag="${tags[$index]}"
      reference="$image_name:$tag"
      if [[ -z "${existing_digests[$index]}" ]]; then
        docker buildx imagetools create --prefer-index=false \
          --tag "$reference" "$image_name@$selected_digest"
      fi
      [[ "$(probe_digest "$reference")" == "$selected_digest" ]] \
        || { echo "Immutable image tag $reference did not resolve to the selected digest." >&2; exit 1; }
      validate_identity "$reference" "$commit_sha" "$release_version"
    done
    ;;
  *) usage; exit 2 ;;
esac
