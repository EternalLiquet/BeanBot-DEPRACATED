#!/usr/bin/env bash
set -Eeuo pipefail

if [[ "$(basename -- "$0")" == "docker" ]]; then
  command_name="${1:-}"
  shift || true
  case "$command_name" in
    buildx)
      subcommand="$1"; shift
      if [[ "$subcommand" == "imagetools" && "$1" == "inspect" ]]; then
        reference="$2"
        if [[ "${TEST_REGISTRY_LOOKUP_FAILURE_REF:-}" == "$reference" ]]; then
          echo "unauthorized: authentication required" >&2
          exit 42
        fi
        if [[ "${TEST_REGISTRY_MALFORMED_REF:-}" == "$reference" ]]; then
          echo "Registry response without a digest"
          exit 0
        fi
        digest="$(awk -F '|' -v ref="$reference" '$1 == ref { value=$2 } END { print value }' "$TEST_REGISTRY")"
        if [[ -z "$digest" ]]; then
          echo "manifest unknown: not found" >&2
          exit 1
        fi
        echo "Digest: $digest"
      elif [[ "$subcommand" == "imagetools" && "$1" == "create" ]]; then
        shift
        while [[ $# -gt 0 ]]; do
          case "$1" in
            --prefer-index=false) shift ;;
            --tag) target="$2"; shift 2 ;;
            *) source="$1"; shift ;;
          esac
        done
        digest="${source##*@}"
        echo "$target|$digest" >>"$TEST_REGISTRY"
        printf 'create %s\n' "$target" >>"$TEST_REGISTRY_MUTATIONS"
      else
        exit 2
      fi
      ;;
    image)
      [[ "$1" == "inspect" ]] || exit 2
      format="$3"
      if [[ "$format" == *revision* ]]; then echo "$TEST_SHA"; else echo "$TEST_VERSION"; fi
      ;;
    pull) exit 0 ;;
    tag) exit 0 ;;
    push)
      reference="$1"
      echo "$reference|$TEST_DIGEST" >>"$TEST_REGISTRY"
      printf 'push %s\n' "$reference" >>"$TEST_REGISTRY_MUTATIONS"
      ;;
    *) exit 2 ;;
  esac
  exit 0
fi

if [[ "$(basename -- "$0")" == "gh" ]]; then
  shift # release
  action="$1"; shift
  tag="${1:-}"; [[ "$tag" == --* ]] || shift || true
  case "$action" in
    view)
      if [[ -n "${TEST_RELEASE_LOOKUP_FAILURE:-}" ]]; then
        echo "HTTP 503: service unavailable" >&2
        exit 42
      fi
      if [[ ! -f "$TEST_RELEASE/target" ]]; then
        echo "release not found" >&2
        exit 1
      fi
      if [[ " $* " == *" --json targetCommitish "* ]]; then
        if [[ -z "${TEST_RELEASE_MALFORMED_LOOKUP:-}" ]]; then
          sed -n '1p' "$TEST_RELEASE/target"
        fi
      elif [[ " $* " == *" --json assets "* ]]; then
        if [[ " $* " == *"@tsv"* ]]; then
          for path in "$TEST_RELEASE"/assets/*; do
            [[ -f "$path" ]] || continue
            asset="$(basename -- "$path")"
            state="$(sed -n '1p' "$TEST_RELEASE/states/$asset")"
            size="$(wc -c <"$path")"
            printf '%s\t%s\t%s\n' "$asset" "$state" "$size"
          done | sort
        else
          find "$TEST_RELEASE/assets" -maxdepth 1 -type f -printf '%f\n' | sort
        fi
      fi
      ;;
    create)
      mkdir -p "$TEST_RELEASE/assets" "$TEST_RELEASE/states"
      while [[ $# -gt 0 ]]; do
        if [[ "$1" == "--target" ]]; then printf '%s\n' "$2" >"$TEST_RELEASE/target"; shift 2; else shift; fi
      done
      printf 'true\n' >"$TEST_RELEASE/draft"
      printf 'create release\n' >>"$TEST_RELEASE_MUTATIONS"
      ;;
    upload)
      asset="$1"
      mkdir -p "$TEST_RELEASE/assets" "$TEST_RELEASE/states"
      count=0
      [[ ! -f "$TEST_RELEASE/upload-count" ]] || count="$(sed -n '1p' "$TEST_RELEASE/upload-count")"
      count=$((count + 1)); printf '%s\n' "$count" >"$TEST_RELEASE/upload-count"
      if [[ -n "${TEST_FAIL_UPLOAD_AT:-}" && "$count" == "$TEST_FAIL_UPLOAD_AT" ]]; then
        : >"$TEST_RELEASE/assets/$(basename -- "$asset")"
        printf 'starter\n' >"$TEST_RELEASE/states/$(basename -- "$asset")"
        exit 1
      fi
      cp "$asset" "$TEST_RELEASE/assets/$(basename -- "$asset")"
      printf 'uploaded\n' >"$TEST_RELEASE/states/$(basename -- "$asset")"
      printf 'upload %s\n' "$(basename -- "$asset")" >>"$TEST_RELEASE_MUTATIONS"
      ;;
    download)
      while [[ $# -gt 0 ]]; do
        case "$1" in
          --pattern) asset="$2"; shift 2 ;;
          --dir) destination="$2"; shift 2 ;;
          *) shift ;;
        esac
      done
      cp "$TEST_RELEASE/assets/$asset" "$destination/$asset"
      ;;
    delete-asset)
      asset="$1"
      rm -f "$TEST_RELEASE/assets/$asset" "$TEST_RELEASE/states/$asset"
      count=0
      [[ ! -f "$TEST_RELEASE/delete-count" ]] || count="$(sed -n '1p' "$TEST_RELEASE/delete-count")"
      printf '%s\n' "$((count + 1))" >"$TEST_RELEASE/delete-count"
      printf 'delete %s\n' "$asset" >>"$TEST_RELEASE_MUTATIONS"
      ;;
    edit) printf 'false\n' >"$TEST_RELEASE/draft" ;;
    *) exit 2 ;;
  esac
  exit 0
fi

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
temporary_directory="$(mktemp -d)"
trap 'find "$temporary_directory" -mindepth 1 -delete; rmdir "$temporary_directory"' EXIT
mkdir -p "$temporary_directory/bin" "$temporary_directory/release/assets" \
  "$temporary_directory/release/states" "$temporary_directory/local-assets"
ln -s "$repository_root/scripts/test-release-resume.sh" "$temporary_directory/bin/docker"
ln -s "$repository_root/scripts/test-release-resume.sh" "$temporary_directory/bin/gh"
export PATH="$temporary_directory/bin:$PATH"
export TEST_REGISTRY="$temporary_directory/registry"
export TEST_REGISTRY_MUTATIONS="$temporary_directory/registry-mutations"
export TEST_RELEASE="$temporary_directory/release"
export TEST_RELEASE_MUTATIONS="$temporary_directory/release-mutations"
export TEST_SHA="0123456789abcdef0123456789abcdef01234567"
export TEST_VERSION="2.18.0"
export TEST_DIGEST="sha256:$(printf 'a%.0s' {1..64})"
export GITHUB_RUN_ID=100
export GITHUB_RUN_ATTEMPT=1
image="ghcr.io/example/beanbot"
touch "$TEST_REGISTRY" "$TEST_REGISTRY_MUTATIONS" "$TEST_RELEASE_MUTATIONS"

# Registry authentication and malformed-response failures are indeterminate,
# never absence. Inspection and promotion must fail closed without mutation.
lookup_output="$temporary_directory/lookup-failure-output"
export TEST_REGISTRY_LOOKUP_FAILURE_REF="$image:$TEST_SHA"
if "$repository_root/scripts/release-image.sh" inspect \
  "$image" "$TEST_SHA" "$TEST_VERSION" "$lookup_output"; then
  echo "Registry authentication failure unexpectedly looked absent." >&2
  exit 1
fi
if "$repository_root/scripts/release-image.sh" promote \
  "$image" "$TEST_SHA" "$TEST_VERSION" "$TEST_DIGEST"; then
  echo "Promotion unexpectedly mutated after an indeterminate registry lookup." >&2
  exit 1
fi
[[ ! -s "$TEST_REGISTRY_MUTATIONS" ]]
unset TEST_REGISTRY_LOOKUP_FAILURE_REF
export TEST_REGISTRY_MALFORMED_REF="$image:$TEST_SHA"
if "$repository_root/scripts/release-image.sh" inspect \
  "$image" "$TEST_SHA" "$TEST_VERSION" "$lookup_output"; then
  echo "Malformed registry response unexpectedly looked absent." >&2
  exit 1
fi
if "$repository_root/scripts/release-image.sh" promote \
  "$image" "$TEST_SHA" "$TEST_VERSION" "$TEST_DIGEST"; then
  echo "Promotion unexpectedly mutated after a malformed registry response." >&2
  exit 1
fi
[[ ! -s "$TEST_REGISTRY_MUTATIONS" ]]
unset TEST_REGISTRY_MALFORMED_REF

# Promotion preflights both aliases before creating either. A failure or
# conflict discovered on the second (version) alias leaves the absent commit
# alias untouched.
export TEST_REGISTRY_LOOKUP_FAILURE_REF="$image:$TEST_VERSION"
if "$repository_root/scripts/release-image.sh" promote \
  "$image" "$TEST_SHA" "$TEST_VERSION" "$TEST_DIGEST"; then
  echo "Second immutable-tag lookup failure unexpectedly allowed mutation." >&2
  exit 1
fi
[[ ! -s "$TEST_REGISTRY_MUTATIONS" ]]
unset TEST_REGISTRY_LOOKUP_FAILURE_REF
conflicting_digest="sha256:$(printf 'b%.0s' {1..64})"
printf '%s|%s\n' "$image:$TEST_VERSION" "$conflicting_digest" >>"$TEST_REGISTRY"
if "$repository_root/scripts/release-image.sh" promote \
  "$image" "$TEST_SHA" "$TEST_VERSION" "$TEST_DIGEST"; then
  echo "Second immutable-tag conflict unexpectedly allowed mutation." >&2
  exit 1
fi
[[ ! -s "$TEST_REGISTRY_MUTATIONS" ]]
: >"$TEST_REGISTRY"

for checkpoint in candidate sbom attestation evidence; do
  : >"$TEST_REGISTRY"
  transaction_directory="$temporary_directory/transaction-$checkpoint"
  mkdir "$transaction_directory"
  inspect_output="$transaction_directory/inspect-output"
  "$repository_root/scripts/release-image.sh" inspect "$image" "$TEST_SHA" "$TEST_VERSION" "$inspect_output"
  grep -Fxq 'reuse=false' "$inspect_output"
  stage_output="$transaction_directory/stage-output"
  "$repository_root/scripts/release-image.sh" stage \
    "$image" "$TEST_SHA" "$TEST_VERSION" "$stage_output" "$image:$TEST_SHA"
  [[ "$checkpoint" == candidate ]] || printf 'sbom\n' >"$transaction_directory/beanbot.spdx.json"
  if [[ "$checkpoint" == attestation || "$checkpoint" == evidence ]]; then
    printf 'attested\n' >"$transaction_directory/attestation"
  fi
  if [[ "$checkpoint" == evidence ]]; then
    printf 'uploaded\n' >"$transaction_directory/evidence"
  fi

  # A rerun after every pre-promotion checkpoint still sees no final identity,
  # can safely restage, and promotes exactly the chosen candidate digest.
  ! grep -Fq "$image:$TEST_SHA|" "$TEST_REGISTRY"
  ! grep -Fq "$image:$TEST_VERSION|" "$TEST_REGISTRY"
  rerun_output="$transaction_directory/rerun-output"
  "$repository_root/scripts/release-image.sh" inspect "$image" "$TEST_SHA" "$TEST_VERSION" "$rerun_output"
  grep -Fxq 'reuse=false' "$rerun_output"
  "$repository_root/scripts/release-image.sh" stage \
    "$image" "$TEST_SHA" "$TEST_VERSION" "$rerun_output" "$image:$TEST_SHA"
  "$repository_root/scripts/release-image.sh" promote \
    "$image" "$TEST_SHA" "$TEST_VERSION" "$TEST_DIGEST"
  grep -Fq "$image:$TEST_SHA|$TEST_DIGEST" "$TEST_REGISTRY"
  grep -Fq "$image:$TEST_VERSION|$TEST_DIGEST" "$TEST_REGISTRY"
done

# Interruption after only the commit tag reuses its validated digest and creates the missing alias.
: >"$TEST_REGISTRY"
docker buildx imagetools create --prefer-index=false --tag "$image:$TEST_SHA" "$image@$TEST_DIGEST"
inspect_output="$temporary_directory/inspect-commit"
"$repository_root/scripts/release-image.sh" inspect "$image" "$TEST_SHA" "$TEST_VERSION" "$inspect_output"
grep -Fxq 'reuse=true' "$inspect_output"
"$repository_root/scripts/release-image.sh" promote "$image" "$TEST_SHA" "$TEST_VERSION" "$TEST_DIGEST"
grep -Fq "$image:$TEST_VERSION|$TEST_DIGEST" "$TEST_REGISTRY"

# The symmetric version-only interruption is also repaired.
: >"$TEST_REGISTRY"
docker buildx imagetools create --prefer-index=false --tag "$image:$TEST_VERSION" "$image@$TEST_DIGEST"
inspect_output="$temporary_directory/inspect-version"
"$repository_root/scripts/release-image.sh" inspect "$image" "$TEST_SHA" "$TEST_VERSION" "$inspect_output"
"$repository_root/scripts/release-image.sh" promote "$image" "$TEST_SHA" "$TEST_VERSION" "$TEST_DIGEST"
grep -Fq "$image:$TEST_SHA|$TEST_DIGEST" "$TEST_REGISTRY"

# Interruption after version-tag promotion leaves both final tags reusable on a
# fresh attempt without a reproducible rebuild requirement.
inspect_output="$temporary_directory/inspect-both-tags"
"$repository_root/scripts/release-image.sh" inspect \
  "$image" "$TEST_SHA" "$TEST_VERSION" "$inspect_output"
grep -Fxq 'reuse=true' "$inspect_output"
grep -Fxq "image-digest=$TEST_DIGEST" "$inspect_output"

printf '{"spdxVersion":"SPDX-2.3","attempt":1}\n' \
  >"$temporary_directory/local-assets/beanbot.spdx.json"
printf '{"version":"%s","commitSha":"%s","image":"%s","digest":"%s"}\n' \
  "$TEST_VERSION" "$TEST_SHA" "$image" "$TEST_DIGEST" \
  >"$temporary_directory/local-assets/release-metadata.json"
(
  cd "$temporary_directory/local-assets"
  sha256sum beanbot.spdx.json release-metadata.json >SHA256SUMS
)

# GitHub authentication/API and malformed-response failures are likewise
# indeterminate and must never create or alter a release.
export TEST_RELEASE_LOOKUP_FAILURE=1
if "$repository_root/scripts/release-assets.sh" publish example/repo "$TEST_VERSION" "$TEST_SHA" \
  "$temporary_directory/local-assets" "$image" "$TEST_DIGEST"; then
  echo "GitHub API failure unexpectedly created a release." >&2
  exit 1
fi
[[ ! -s "$TEST_RELEASE_MUTATIONS" ]]
unset TEST_RELEASE_LOOKUP_FAILURE
printf '%s\n' "$TEST_SHA" >"$TEST_RELEASE/target"
export TEST_RELEASE_MALFORMED_LOOKUP=1
if "$repository_root/scripts/release-assets.sh" publish example/repo "$TEST_VERSION" "$TEST_SHA" \
  "$temporary_directory/local-assets" "$image" "$TEST_DIGEST"; then
  echo "Malformed GitHub release lookup unexpectedly mutated the release." >&2
  exit 1
fi
[[ ! -s "$TEST_RELEASE_MUTATIONS" ]]
unset TEST_RELEASE_MALFORMED_LOOKUP
find "$TEST_RELEASE" -mindepth 1 -delete

# Durable metadata from a partial draft is authoritative before any build or
# immutable-tag promotion. With no tags, its validated digest is reused; a
# conflicting existing tag fails before mutation.
mkdir -p "$TEST_RELEASE/assets" "$TEST_RELEASE/states"
printf '%s\n' "$TEST_SHA" >"$TEST_RELEASE/target"
cp "$temporary_directory/local-assets/release-metadata.json" \
  "$TEST_RELEASE/assets/release-metadata.json"
printf 'uploaded\n' >"$TEST_RELEASE/states/release-metadata.json"
preflight_output="$temporary_directory/release-preflight-output"
"$repository_root/scripts/release-assets.sh" inspect \
  example/repo "$TEST_VERSION" "$TEST_SHA" "$image" "$preflight_output"
grep -Fxq "release-digest=$TEST_DIGEST" "$preflight_output"
: >"$TEST_REGISTRY"
: >"$TEST_REGISTRY_MUTATIONS"
image_preflight_output="$temporary_directory/image-preflight-output"
"$repository_root/scripts/release-image.sh" inspect \
  "$image" "$TEST_SHA" "$TEST_VERSION" "$image_preflight_output" "$TEST_DIGEST"
grep -Fxq 'reuse=true' "$image_preflight_output"
grep -Fxq "image-digest=$TEST_DIGEST" "$image_preflight_output"
[[ ! -s "$TEST_REGISTRY_MUTATIONS" ]]

conflicting_digest="sha256:$(printf 'b%.0s' {1..64})"
printf '%s|%s\n' "$image:$TEST_SHA" "$conflicting_digest" >>"$TEST_REGISTRY"
if "$repository_root/scripts/release-image.sh" inspect \
  "$image" "$TEST_SHA" "$TEST_VERSION" "$image_preflight_output" "$TEST_DIGEST"; then
  echo "Conflicting durable release and image identities unexpectedly reconciled." >&2
  exit 1
fi
[[ ! -s "$TEST_REGISTRY_MUTATIONS" ]]
find "$TEST_RELEASE" -mindepth 1 -delete

# Interruption immediately after draft creation is safe to resume.
gh release create "v$TEST_VERSION" --repo example/repo --target "$TEST_SHA" --draft
"$repository_root/scripts/release-assets.sh" publish example/repo "$TEST_VERSION" "$TEST_SHA" \
  "$temporary_directory/local-assets" "$image" "$TEST_DIGEST"
grep -Fxq false "$TEST_RELEASE/draft"

# Interruption during upload resumes on a fresh attempt even when regenerated SBOM
# bytes differ, preserving the already-published evidence without clobbering it.
find "$TEST_RELEASE" -mindepth 1 -delete
export TEST_FAIL_UPLOAD_AT=3
if "$repository_root/scripts/release-assets.sh" publish example/repo "$TEST_VERSION" "$TEST_SHA" \
  "$temporary_directory/local-assets" "$image" "$TEST_DIGEST"; then
  echo "Injected release asset upload interruption unexpectedly succeeded." >&2
  exit 1
fi
unset TEST_FAIL_UPLOAD_AT
published_sbom="$(sed -n '1p' "$TEST_RELEASE/assets/beanbot.spdx.json")"
printf '{"spdxVersion":"SPDX-2.3","attempt":2}\n' \
  >"$temporary_directory/local-assets/beanbot.spdx.json"
"$repository_root/scripts/release-assets.sh" publish example/repo "$TEST_VERSION" "$TEST_SHA" \
  "$temporary_directory/local-assets" "$image" "$TEST_DIGEST"
grep -Fxq "$published_sbom" "$TEST_RELEASE/assets/beanbot.spdx.json"
grep -Fxq 1 "$TEST_RELEASE/delete-count"

# Existing evidence with a different transaction identity is rejected without overwrite.
printf '{"version":"9.9.9","commitSha":"%s","image":"%s","digest":"%s"}\n' \
  "$TEST_SHA" "$image" "$TEST_DIGEST" >"$TEST_RELEASE/assets/release-metadata.json"
if "$repository_root/scripts/release-assets.sh" publish example/repo "$TEST_VERSION" "$TEST_SHA" \
  "$temporary_directory/local-assets" "$image" "$TEST_DIGEST"; then
  echo "Mismatched existing release identity unexpectedly passed." >&2
  exit 1
fi

echo "Release resumption tests passed."
