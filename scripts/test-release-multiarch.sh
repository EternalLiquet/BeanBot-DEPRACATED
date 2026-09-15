#!/usr/bin/env bash
set -Eeuo pipefail

if [[ "$(basename -- "$0")" == "docker" ]]; then
  command_name="${1:-}"; shift || true
  case "$command_name" in
    buildx)
      subcommand="${1:-}"; shift || true
      if [[ "$subcommand" == "imagetools" && "${1:-}" == "inspect" ]]; then
        if [[ "${2:-}" == "--raw" ]]; then reference="$3"; raw=true; else reference="$2"; raw=false; fi
        row="$(awk -F'|' -v ref="$reference" '$1 == ref {value=$0} END {print value}' "$TEST_REGISTRY")"
        if [[ -z "$row" && "$reference" == *@* ]]; then
          digest="${reference##*@}"
          row="$(awk -F'|' -v digest="$digest" '$2 == digest {value=$0} END {print value}' "$TEST_REGISTRY")"
        fi
        if [[ -z "$row" ]]; then echo "manifest unknown: not found" >&2; exit 1; fi
        IFS='|' read -r _ index_digest amd64_digest arm64_digest <<<"$row"
        if [[ "$raw" == "true" ]]; then
          cat <<JSON
{"schemaVersion":2,"mediaType":"application/vnd.oci.image.index.v1+json","manifests":[{"mediaType":"application/vnd.oci.image.manifest.v1+json","digest":"$amd64_digest","platform":{"os":"linux","architecture":"amd64"}},{"mediaType":"application/vnd.oci.image.manifest.v1+json","digest":"$arm64_digest","platform":{"os":"linux","architecture":"arm64"}}]}
JSON
        else
          echo "Digest: $index_digest"
        fi
      elif [[ "$subcommand" == "imagetools" && "${1:-}" == "create" ]]; then
        shift
        while [[ $# -gt 0 ]]; do
          case "$1" in
            --tag) target="$2"; shift 2 ;;
            *) source="$1"; shift ;;
          esac
        done
        digest="${source##*@}"
        row="$(awk -F'|' -v digest="$digest" '$2 == digest {value=$0} END {print value}' "$TEST_REGISTRY")"
        [[ -n "$row" ]] || exit 1
        IFS='|' read -r _ index_digest amd64_digest arm64_digest <<<"$row"
        printf '%s|%s|%s|%s\n' "$target" "$index_digest" "$amd64_digest" "$arm64_digest" >>"$TEST_REGISTRY"
        printf 'create %s\n' "$target" >>"$TEST_MUTATIONS"
      else
        [[ "$subcommand" == "build" ]] || exit 2
        while [[ $# -gt 0 ]]; do
          case "$1" in
            --tag) target="$2"; shift 2 ;;
            --platform|--file|--build-arg) shift 2 ;;
            --provenance=false|--push) shift ;;
            .) shift ;;
            *) echo "Unexpected buildx build arg: $1" >&2; exit 2 ;;
          esac
        done
        printf '%s|%s|%s|%s\n' "$target" "$TEST_INDEX_DIGEST" "$TEST_AMD64_DIGEST" "$TEST_ARM64_DIGEST" >>"$TEST_REGISTRY"
        printf 'build %s\n' "$target" >>"$TEST_MUTATIONS"
      fi
      ;;
    pull) exit 0 ;;
    image)
      [[ "$1" == "inspect" && "$2" == "--format" ]] || exit 2
      format="$3"; reference="$4"; digest="${reference##*@}"
      if [[ "$digest" == "$TEST_AMD64_DIGEST" ]]; then platform="linux/amd64"; else platform="linux/arm64"; fi
      if [[ "$reference" != *@* ]]; then platform="linux/amd64"; fi
      if [[ "$format" == *'.Os'* ]]; then
        echo "$platform"
      elif [[ "$format" == *revision* ]]; then
        if [[ "${TEST_WRONG_IDENTITY_DIGEST:-}" == "$digest" ]]; then echo "wrong-sha"; else echo "$TEST_SHA"; fi
      elif [[ "$format" == *version* ]]; then
        if [[ "${TEST_WRONG_VERSION_DIGEST:-}" == "$digest" ]]; then echo "9.9.9"; else echo "$TEST_VERSION"; fi
      else
        exit 2
      fi
      ;;
    *) exit 2 ;;
  esac
  exit 0
fi

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
temporary_directory="$(mktemp -d)"
trap 'find "$temporary_directory" -mindepth 1 -delete; rmdir "$temporary_directory"' EXIT
mkdir -p "$temporary_directory/bin"
ln -s "$repository_root/scripts/test-release-multiarch.sh" "$temporary_directory/bin/docker"
export PATH="$temporary_directory/bin:$PATH"
export TEST_REGISTRY="$temporary_directory/registry"
export TEST_MUTATIONS="$temporary_directory/mutations"
export TEST_SHA="0123456789abcdef0123456789abcdef01234567"
export TEST_VERSION="2.18.0"
export TEST_INDEX_DIGEST="sha256:$(printf '1%.0s' {1..64})"
export TEST_AMD64_DIGEST="sha256:$(printf 'a%.0s' {1..64})"
export TEST_ARM64_DIGEST="sha256:$(printf 'b%.0s' {1..64})"
export GITHUB_RUN_ID=200
export GITHUB_RUN_ATTEMPT=1
image="ghcr.io/example/beanbot"
: >"$TEST_REGISTRY"
: >"$TEST_MUTATIONS"

stage_output="$temporary_directory/stage-output"
"$repository_root/scripts/release-multiarch.sh" stage \
  "$image" "$TEST_SHA" "$TEST_VERSION" "$stage_output" "$image:$TEST_SHA"
grep -Fxq "image-digest=$TEST_INDEX_DIGEST" "$stage_output"
grep -Fxq "amd64-digest=$TEST_AMD64_DIGEST" "$stage_output"
grep -Fxq "arm64-digest=$TEST_ARM64_DIGEST" "$stage_output"
grep -Fxq "build $image:release-candidate-$GITHUB_RUN_ID-$GITHUB_RUN_ATTEMPT" "$TEST_MUTATIONS"

inspect_output="$temporary_directory/inspect-output"
"$repository_root/scripts/release-multiarch.sh" inspect \
  "$image" "$TEST_SHA" "$TEST_VERSION" "$inspect_output" \
  "$TEST_INDEX_DIGEST" "$TEST_AMD64_DIGEST" "$TEST_ARM64_DIGEST"
grep -Fxq 'reuse=true' "$inspect_output"
if "$repository_root/scripts/release-multiarch.sh" inspect \
  "$image" "$TEST_SHA" "$TEST_VERSION" "$temporary_directory/bad-inspect" \
  "$TEST_INDEX_DIGEST" "sha256:$(printf 'c%.0s' {1..64})" "$TEST_ARM64_DIGEST"; then
  echo "Mismatched durable child metadata unexpectedly reconciled." >&2
  exit 1
fi

export TEST_WRONG_IDENTITY_DIGEST="$TEST_ARM64_DIGEST"
if "$repository_root/scripts/release-multiarch.sh" validate \
  "$image" "$TEST_SHA" "$TEST_VERSION" "$TEST_INDEX_DIGEST" "$temporary_directory/validate"; then
  echo "Wrong ARM64 build identity unexpectedly validated." >&2
  exit 1
fi
unset TEST_WRONG_IDENTITY_DIGEST
export TEST_WRONG_VERSION_DIGEST="$TEST_AMD64_DIGEST"
if "$repository_root/scripts/release-multiarch.sh" validate \
  "$image" "$TEST_SHA" "$TEST_VERSION" "$TEST_INDEX_DIGEST" "$temporary_directory/validate-version"; then
  echo "Wrong amd64 release version unexpectedly validated." >&2
  exit 1
fi
unset TEST_WRONG_VERSION_DIGEST

conflicting_digest="sha256:$(printf '2%.0s' {1..64})"
printf '%s|%s|%s|%s\n' "$image:$TEST_VERSION" "$conflicting_digest" "$TEST_AMD64_DIGEST" "$TEST_ARM64_DIGEST" >>"$TEST_REGISTRY"
: >"$TEST_MUTATIONS"
if "$repository_root/scripts/release-multiarch.sh" promote \
  "$image" "$TEST_SHA" "$TEST_VERSION" "$TEST_INDEX_DIGEST"; then
  echo "Conflicting version alias unexpectedly allowed multi-platform promotion." >&2
  exit 1
fi
[[ ! -s "$TEST_MUTATIONS" ]]
grep -Fvq "$image:$TEST_SHA|" "$TEST_REGISTRY"

awk -F'|' -v version="$image:$TEST_VERSION" '$1 != version' "$TEST_REGISTRY" >"$temporary_directory/registry-clean"
mv "$temporary_directory/registry-clean" "$TEST_REGISTRY"
"$repository_root/scripts/release-multiarch.sh" promote \
  "$image" "$TEST_SHA" "$TEST_VERSION" "$TEST_INDEX_DIGEST"
grep -Fq "$image:$TEST_SHA|$TEST_INDEX_DIGEST|$TEST_AMD64_DIGEST|$TEST_ARM64_DIGEST" "$TEST_REGISTRY"
grep -Fq "$image:$TEST_VERSION|$TEST_INDEX_DIGEST|$TEST_AMD64_DIGEST|$TEST_ARM64_DIGEST" "$TEST_REGISTRY"

echo "Multi-platform release transaction tests passed."
