#!/usr/bin/env bash
set -Eeuo pipefail

usage() {
  echo "Usage: $0 inspect REPOSITORY VERSION COMMIT_SHA [IMAGE OUTPUT]" >&2
  echo "       $0 publish REPOSITORY VERSION COMMIT_SHA ASSET_DIRECTORY IMAGE DIGEST" >&2
}

[[ $# -ge 4 ]] || { usage; exit 2; }
command_name="$1"
repository="$2"
version="$3"
commit_sha="$4"
tag="v$version"

probe_release_target() {
  local error_file target status
  error_file="$(mktemp)"
  if target="$(gh release view "$tag" --repo "$repository" \
      --json targetCommitish --jq .targetCommitish 2>"$error_file")"; then
    rm -f "$error_file"
    if [[ -n "$target" ]]; then
      printf '%s\n' "$target"
      return 0
    fi
    echo "GitHub returned a malformed release lookup for $tag." >&2
    return 2
  else
    status=$?
  fi
  if grep -Eiq 'release not found|not found.*release|HTTP 404' "$error_file"; then
    rm -f "$error_file"
    return 1
  fi
  echo "GitHub release lookup for $tag failed (exit $status):" >&2
  sed -n '1,20p' "$error_file" >&2
  rm -f "$error_file"
  return 2
}

validate_target() {
  local target="$1"
  [[ "$target" == "$commit_sha" ]] \
    || { echo "Existing release $tag targets '$target', not '$commit_sha'." >&2; exit 1; }
}

validate_multiarch_metadata() {
  local metadata_path="$1" amd64_digest arm64_digest
  jq -e '
    (.platforms | type) == "object" and
    (.platforms | keys | sort) == ["linux/amd64", "linux/arm64"]
  ' "$metadata_path" >/dev/null \
    || { echo "Release metadata has an invalid supported-platform set." >&2; return 1; }
  amd64_digest="$(jq -er '.platforms["linux/amd64"]' "$metadata_path")"
  arm64_digest="$(jq -er '.platforms["linux/arm64"]' "$metadata_path")"
  [[ "$amd64_digest" =~ ^sha256:[0-9a-f]{64}$ && "$arm64_digest" =~ ^sha256:[0-9a-f]{64}$ && "$amd64_digest" != "$arm64_digest" ]] \
    || { echo "Release metadata contains invalid platform child digests." >&2; return 1; }
}

case "$command_name" in
  inspect)
    [[ $# -eq 4 || $# -eq 6 ]] || { usage; exit 2; }
    if release_target="$(probe_release_target)"; then
      validate_target "$release_target"
      if [[ $# -eq 6 ]]; then
        image_name="$5"
        output_file="$6"
        temporary_directory="$(mktemp -d)"
        trap 'find "$temporary_directory" -mindepth 1 -delete; rmdir "$temporary_directory"' EXIT
        asset_inventory="$(gh release view "$tag" --repo "$repository" --json assets \
          --jq '.assets[] | [.name, .state, (.size | tostring)] | @tsv')"
        metadata_uploaded=false
        while IFS=$'\t' read -r asset state size; do
          [[ -n "$asset" ]] || continue
          case "$asset" in
            release-metadata.json|beanbot.spdx.json|beanbot-amd64.spdx.json|beanbot-arm64.spdx.json|SHA256SUMS)
              if [[ "$state" == "uploaded" ]]; then
                [[ "$asset" != "release-metadata.json" ]] || metadata_uploaded=true
              elif [[ "$state" != "starter" || "$size" != "0" ]]; then
                echo "Release asset $asset has unsupported preflight state '$state'/$size." >&2
                exit 1
              fi
              ;;
          esac
        done <<<"$asset_inventory"

        if [[ "$metadata_uploaded" == "true" ]]; then
          gh release download "$tag" --repo "$repository" \
            --pattern release-metadata.json --dir "$temporary_directory"
          metadata_path="$temporary_directory/release-metadata.json"
          release_digest="$(jq -er \
            --arg version "$version" \
            --arg commitSha "$commit_sha" \
            --arg image "$image_name" \
            'select(.version == $version and .commitSha == $commitSha and .image == $image) | .digest' \
            "$metadata_path")" \
            || { echo "Existing release metadata conflicts with the requested transaction." >&2; exit 1; }
          [[ "$release_digest" =~ ^sha256:[0-9a-f]{64}$ ]] \
            || { echo "Existing release metadata contains an invalid image digest." >&2; exit 1; }
          echo "release-digest=$release_digest" >>"$output_file"

          if jq -e 'has("platforms")' "$metadata_path" >/dev/null; then
            validate_multiarch_metadata "$metadata_path"
            echo "release-amd64-digest=$(jq -r '.platforms["linux/amd64"]' "$metadata_path")" >>"$output_file"
            echo "release-arm64-digest=$(jq -r '.platforms["linux/arm64"]' "$metadata_path")" >>"$output_file"
          fi
        elif awk -F '\t' '$1 == "beanbot.spdx.json" || $1 == "beanbot-amd64.spdx.json" || $1 == "beanbot-arm64.spdx.json" || $1 == "SHA256SUMS" { if ($2 == "uploaded") found=1 } END { exit !found }' \
            <<<"$asset_inventory"; then
          echo "Existing release evidence is missing its authoritative metadata." >&2
          exit 1
        fi
      fi
    else
      status=$?
      [[ "$status" == "1" ]] || exit "$status"
    fi
    ;;
  publish)
    [[ $# -eq 7 ]] || { usage; exit 2; }
    asset_directory="$5"
    image_name="$6"
    image_digest="$7"

    [[ -s "$asset_directory/release-metadata.json" && -s "$asset_directory/beanbot.spdx.json" && -s "$asset_directory/SHA256SUMS" ]] \
      || { echo "Required release evidence is missing or empty." >&2; exit 1; }

    payload_assets=(release-metadata.json beanbot.spdx.json)
    if jq -e 'has("platforms")' "$asset_directory/release-metadata.json" >/dev/null; then
      validate_multiarch_metadata "$asset_directory/release-metadata.json"
      [[ -s "$asset_directory/beanbot-amd64.spdx.json" && -s "$asset_directory/beanbot-arm64.spdx.json" ]] \
        || { echo "Multi-platform release evidence is missing a platform SBOM." >&2; exit 1; }
      payload_assets+=(beanbot-amd64.spdx.json beanbot-arm64.spdx.json)
    fi
    assets=("${payload_assets[@]}" SHA256SUMS)

    if release_target="$(probe_release_target)"; then
      validate_target "$release_target"
    else
      status=$?
      [[ "$status" == "1" ]] || exit "$status"
      gh release create "$tag" \
        --repo "$repository" \
        --target "$commit_sha" \
        --title "BeanBot $tag" \
        --generate-notes \
        --fail-on-no-commits \
        --notes "Verified image: $image_name@$image_digest" \
        --draft
    fi

    temporary_directory="$(mktemp -d)"
    working_directory="$temporary_directory/working"
    downloaded_directory="$temporary_directory/downloaded"
    mkdir "$working_directory" "$downloaded_directory"
    trap 'find "$temporary_directory" -mindepth 1 -delete; rmdir "$temporary_directory"' EXIT
    asset_inventory="$(gh release view "$tag" --repo "$repository" --json assets \
      --jq '.assets[] | [.name, .state, (.size | tostring)] | @tsv')"
    while IFS=$'\t' read -r asset state size; do
      [[ -n "$asset" ]] || continue
      case "$asset" in
        release-metadata.json|beanbot.spdx.json|beanbot-amd64.spdx.json|beanbot-arm64.spdx.json|SHA256SUMS)
          if [[ "$state" == "starter" && "$size" == "0" ]]; then
            gh release delete-asset "$tag" "$asset" --repo "$repository" --yes
          elif [[ "$state" != "uploaded" ]]; then
            echo "Release asset $asset has unsupported state '$state'; refusing to mutate it." >&2
            exit 1
          fi
          ;;
        *)
          if [[ "$state" != "uploaded" ]]; then
            echo "Release asset $asset has unsupported state '$state'; refusing to mutate it." >&2
            exit 1
          fi
          ;;
      esac
    done <<<"$asset_inventory"
    existing_assets="$(gh release view "$tag" --repo "$repository" --json assets --jq '.assets[].name')"

    for asset in "${payload_assets[@]}"; do
      if grep -Fxq "$asset" <<<"$existing_assets"; then
        gh release download "$tag" --repo "$repository" --pattern "$asset" --dir "$working_directory"
      else
        cp "$asset_directory/$asset" "$working_directory/$asset"
      fi
    done

    jq -e \
      --arg version "$version" \
      --arg commitSha "$commit_sha" \
      --arg image "$image_name" \
      --arg digest "$image_digest" \
      '.version == $version and .commitSha == $commitSha and .image == $image and .digest == $digest' \
      "$working_directory/release-metadata.json" >/dev/null \
      || { echo "Release metadata does not match the requested transaction identity." >&2; exit 1; }
    if jq -e 'has("platforms")' "$working_directory/release-metadata.json" >/dev/null; then
      validate_multiarch_metadata "$working_directory/release-metadata.json"
    fi

    (
      cd "$working_directory"
      sha256sum "${payload_assets[@]}" >SHA256SUMS
      sha256sum -c SHA256SUMS
    )
    if grep -Fxq SHA256SUMS <<<"$existing_assets"; then
      gh release download "$tag" --repo "$repository" --pattern SHA256SUMS --dir "$downloaded_directory"
      cmp --silent "$working_directory/SHA256SUMS" "$downloaded_directory/SHA256SUMS" \
        || { echo "Existing release checksums do not match its durable assets." >&2; exit 1; }
    fi

    for asset in "${assets[@]}"; do
      if ! grep -Fxq "$asset" <<<"$existing_assets"; then
        gh release upload "$tag" "$working_directory/$asset" --repo "$repository"
      fi
    done

    existing_assets="$(gh release view "$tag" --repo "$repository" --json assets --jq '.assets[].name')"
    for asset in "${assets[@]}"; do
      grep -Fxq "$asset" <<<"$existing_assets" \
        || { echo "Release asset $asset was not uploaded." >&2; exit 1; }
      find "$downloaded_directory" -mindepth 1 -delete
      gh release download "$tag" --repo "$repository" --pattern "$asset" --dir "$downloaded_directory"
      cmp --silent "$working_directory/$asset" "$downloaded_directory/$asset" \
        || { echo "Uploaded release asset $asset failed verification." >&2; exit 1; }
    done
    gh release edit "$tag" --repo "$repository" --draft=false
    ;;
  *) usage; exit 2 ;;
esac
