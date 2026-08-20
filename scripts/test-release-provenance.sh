#!/usr/bin/env bash
set -Eeuo pipefail

case "$(basename -- "$0")" in
  generate-provenance|upload-evidence|promote-tags|publish-release)
    printf '%s %s\n' "$(basename -- "$0")" "$*" >>"$TEST_TRANSACTION_LOG"
    exit 0
    ;;
esac

if [[ "$(basename -- "$0")" == "gh" ]]; then
  if [[ -n "${TEST_TRANSACTION_LOG:-}" ]]; then
    printf 'verify-provenance %s\n' "$*" >>"$TEST_TRANSACTION_LOG"
  fi
  printf '%s\n' "$*" >"$TEST_GH_LOG"
  [[ "$1" == "attestation" && "$2" == "verify" ]] || exit 2
  shift 2
  oci_reference="$1"
  shift
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --repo) repository="$2"; shift 2 ;;
      --bundle-from-oci) bundle_from_oci=true; shift ;;
      --predicate-type) predicate_type="$2"; shift 2 ;;
      --signer-workflow) signer_workflow="$2"; shift 2 ;;
      --source-digest) source_digest="$2"; shift 2 ;;
      --source-ref) source_ref="$2"; shift 2 ;;
      --format) output_format="$2"; shift 2 ;;
      *) exit 2 ;;
    esac
  done

  [[ "$oci_reference" == "oci://$TEST_ATTESTATION_IMAGE@$TEST_ATTESTATION_DIGEST" ]] || exit 1
  [[ "$repository" == "$TEST_ATTESTATION_REPOSITORY" ]] || exit 1
  [[ "${bundle_from_oci:-}" == "true" ]] || exit 1
  [[ "$predicate_type" == "https://slsa.dev/provenance/v1" ]] || exit 1
  [[ "$signer_workflow" == "$TEST_ATTESTATION_SIGNER_WORKFLOW" ]] || exit 1
  [[ "$source_digest" == "$TEST_ATTESTATION_SOURCE_DIGEST" ]] || exit 1
  [[ "$source_ref" == "$TEST_ATTESTATION_SOURCE_REF" ]] || exit 1
  [[ "$output_format" == "json" ]] || exit 1

  case "${TEST_ATTESTATION_RESULT:-valid}" in
    valid)
      jq -n \
        --arg image "$TEST_ATTESTATION_IMAGE" \
        --arg digest "${TEST_ATTESTATION_DIGEST#sha256:}" \
        '[{verificationResult:{statement:{subject:[{name:$image,digest:{sha256:$digest}}]}}}]'
      ;;
    missing) exit 1 ;;
    api-failure) echo "HTTP 503: service unavailable" >&2; exit 42 ;;
    malformed) printf 'not-json\n' ;;
    empty) printf '[]\n' ;;
    wrong-subject)
      jq -n \
        --arg image "$TEST_ATTESTATION_IMAGE" \
        --arg digest "$(printf 'f%.0s' {1..64})" \
        '[{verificationResult:{statement:{subject:[{name:$image,digest:{sha256:$digest}}]}}}]'
      ;;
    *) exit 2 ;;
  esac
  exit 0
fi

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
temporary_directory="$(mktemp -d)"
trap 'find "$temporary_directory" -mindepth 1 -delete; rmdir "$temporary_directory"' EXIT
mkdir "$temporary_directory/bin"
ln -s "$repository_root/scripts/test-release-provenance.sh" "$temporary_directory/bin/gh"
for command_name in generate-provenance upload-evidence promote-tags publish-release; do
  ln -s "$repository_root/scripts/test-release-provenance.sh" \
    "$temporary_directory/bin/$command_name"
done
export PATH="$temporary_directory/bin:$PATH"
export TEST_GH_LOG="$temporary_directory/gh.log"
export TEST_TRANSACTION_LOG="$temporary_directory/transaction.log"
export TEST_ATTESTATION_IMAGE="ghcr.io/example/beanbot"
export TEST_ATTESTATION_DIGEST="sha256:$(printf 'a%.0s' {1..64})"
export TEST_ATTESTATION_REPOSITORY="example/beanbot"
export TEST_ATTESTATION_SIGNER_WORKFLOW="example/beanbot/.github/workflows/autorelease.yml"
export TEST_ATTESTATION_SOURCE_DIGEST="0123456789abcdef0123456789abcdef01234567"
export TEST_ATTESTATION_SOURCE_REF="refs/heads/master"
verifier="$repository_root/scripts/verify-release-provenance.sh"
selector="$repository_root/scripts/select-release-image.sh"

verify() {
  "$verifier" \
    "$TEST_ATTESTATION_IMAGE" "$TEST_ATTESTATION_DIGEST" \
    "$TEST_ATTESTATION_REPOSITORY" "$TEST_ATTESTATION_SOURCE_DIGEST" \
    "$TEST_ATTESTATION_SOURCE_REF"
}

assert_fails() {
  local description="$1"
  shift
  if "$@"; then
    echo "$description unexpectedly passed provenance verification." >&2
    exit 1
  fi
}

verify
expected_command="attestation verify oci://$TEST_ATTESTATION_IMAGE@$TEST_ATTESTATION_DIGEST --repo $TEST_ATTESTATION_REPOSITORY --bundle-from-oci --predicate-type https://slsa.dev/provenance/v1 --signer-workflow $TEST_ATTESTATION_SIGNER_WORKFLOW --source-digest $TEST_ATTESTATION_SOURCE_DIGEST --source-ref $TEST_ATTESTATION_SOURCE_REF --format json"
grep -Fxq "$expected_command" "$TEST_GH_LOG"

export TEST_ATTESTATION_RESULT=missing
assert_fails "Missing attestation" verify
export TEST_ATTESTATION_RESULT=api-failure
assert_fails "GitHub API failure" verify
export TEST_ATTESTATION_RESULT=malformed
assert_fails "Malformed verification output" verify
export TEST_ATTESTATION_RESULT=empty
assert_fails "Empty verification output" verify
export TEST_ATTESTATION_RESULT=wrong-subject
assert_fails "Different image digest" verify
export TEST_ATTESTATION_RESULT=valid

assert_fails "Invalid image digest argument" \
  "$verifier" "$TEST_ATTESTATION_IMAGE" "sha256:not-a-digest" \
    "$TEST_ATTESTATION_REPOSITORY" "$TEST_ATTESTATION_SOURCE_DIGEST" \
    "$TEST_ATTESTATION_SOURCE_REF"
assert_fails "Non-master source ref argument" \
  "$verifier" "$TEST_ATTESTATION_IMAGE" "$TEST_ATTESTATION_DIGEST" \
    "$TEST_ATTESTATION_REPOSITORY" "$TEST_ATTESTATION_SOURCE_DIGEST" \
    "refs/heads/develop"

original_repository="$TEST_ATTESTATION_REPOSITORY"
export TEST_ATTESTATION_REPOSITORY="other/repository"
assert_fails "Attestation from another repository" \
  "$verifier" "$TEST_ATTESTATION_IMAGE" "$TEST_ATTESTATION_DIGEST" \
    "$original_repository" "$TEST_ATTESTATION_SOURCE_DIGEST" "$TEST_ATTESTATION_SOURCE_REF"
export TEST_ATTESTATION_REPOSITORY="$original_repository"

original_signer="$TEST_ATTESTATION_SIGNER_WORKFLOW"
export TEST_ATTESTATION_SIGNER_WORKFLOW="example/beanbot/.github/workflows/other.yml"
assert_fails "Attestation from another signer workflow" verify
export TEST_ATTESTATION_SIGNER_WORKFLOW="$original_signer"

original_source_digest="$TEST_ATTESTATION_SOURCE_DIGEST"
export TEST_ATTESTATION_SOURCE_DIGEST="fedcba9876543210fedcba9876543210fedcba98"
assert_fails "Attestation from another source commit" \
  "$verifier" "$TEST_ATTESTATION_IMAGE" "$TEST_ATTESTATION_DIGEST" \
    "$TEST_ATTESTATION_REPOSITORY" "$original_source_digest" "$TEST_ATTESTATION_SOURCE_REF"
export TEST_ATTESTATION_SOURCE_DIGEST="$original_source_digest"

original_source_ref="$TEST_ATTESTATION_SOURCE_REF"
export TEST_ATTESTATION_SOURCE_REF="refs/heads/develop"
assert_fails "Attestation from another source ref" \
  "$verifier" "$TEST_ATTESTATION_IMAGE" "$TEST_ATTESTATION_DIGEST" \
    "$TEST_ATTESTATION_REPOSITORY" "$TEST_ATTESTATION_SOURCE_DIGEST" "$original_source_ref"
export TEST_ATTESTATION_SOURCE_REF="$original_source_ref"

run_transaction() {
  local existing_digest="$1" staged_digest="$2" outputs action digest
  outputs="$temporary_directory/selected-image"
  : >"$outputs"
  if ! "$selector" "$TEST_ATTESTATION_IMAGE" "$existing_digest" "$staged_digest" \
    "2.18.0" "$outputs"; then
    return 1
  fi
  action="$(sed -n 's/^provenance-action=//p' "$outputs")"
  digest="$(sed -n 's/^image-digest=//p' "$outputs")"

  case "$action" in
    generate)
      generate-provenance "$TEST_ATTESTATION_IMAGE" "$digest"
      ;;
    verify)
      if ! "$verifier" "$TEST_ATTESTATION_IMAGE" "$digest" \
        "$TEST_ATTESTATION_REPOSITORY" "$TEST_ATTESTATION_SOURCE_DIGEST" \
        "$TEST_ATTESTATION_SOURCE_REF"; then
        return 1
      fi
      ;;
    *)
      echo "Unknown provenance action: $action" >&2
      return 1
      ;;
  esac

  upload-evidence "$digest"
  promote-tags "$digest"
  publish-release "$digest"
}

# Drive the executable selection logic through the same ordered transaction as
# the workflow. A fresh staged digest generates provenance before every
# mutation, while a reused digest verifies existing provenance and never
# invokes generation.
: >"$TEST_TRANSACTION_LOG"
run_transaction "" "$TEST_ATTESTATION_DIGEST"
cat >"$temporary_directory/expected-fresh" <<EOF
generate-provenance $TEST_ATTESTATION_IMAGE $TEST_ATTESTATION_DIGEST
upload-evidence $TEST_ATTESTATION_DIGEST
promote-tags $TEST_ATTESTATION_DIGEST
publish-release $TEST_ATTESTATION_DIGEST
EOF
cmp "$temporary_directory/expected-fresh" "$TEST_TRANSACTION_LOG"

: >"$TEST_TRANSACTION_LOG"
run_transaction "$TEST_ATTESTATION_DIGEST" ""
grep -Fxq "verify-provenance $expected_command" "$TEST_TRANSACTION_LOG"
! grep -Fq 'generate-provenance' "$TEST_TRANSACTION_LOG"
[[ "$(sed -n '$p' "$TEST_TRANSACTION_LOG")" == "publish-release $TEST_ATTESTATION_DIGEST" ]]

# Missing or indeterminate provenance stops before evidence, immutable tag, or
# GitHub Release mutation.
: >"$TEST_TRANSACTION_LOG"
export TEST_ATTESTATION_RESULT=missing
assert_fails "Reused image without provenance transaction" \
  run_transaction "$TEST_ATTESTATION_DIGEST" ""
grep -Fxq "verify-provenance $expected_command" "$TEST_TRANSACTION_LOG"
! grep -Eq 'generate-provenance|upload-evidence|promote-tags|publish-release' \
  "$TEST_TRANSACTION_LOG"
export TEST_ATTESTATION_RESULT=valid

# A partial transaction discovered through an immutable tag or durable
# metadata resumes through verification. Repeated resumes never mint build
# provenance for the reused digest.
: >"$TEST_TRANSACTION_LOG"
run_transaction "$TEST_ATTESTATION_DIGEST" ""
run_transaction "$TEST_ATTESTATION_DIGEST" ""
[[ "$(grep -c '^verify-provenance ' "$TEST_TRANSACTION_LOG")" -eq 2 ]]
[[ "$(grep -c '^promote-tags ' "$TEST_TRANSACTION_LOG")" -eq 2 ]]
[[ "$(grep -c '^publish-release ' "$TEST_TRANSACTION_LOG")" -eq 2 ]]
! grep -Fq 'generate-provenance' "$TEST_TRANSACTION_LOG"

assert_fails "Ambiguous reused and staged image state" \
  "$selector" "$TEST_ATTESTATION_IMAGE" "$TEST_ATTESTATION_DIGEST" \
    "$TEST_ATTESTATION_DIGEST" "2.18.0" "$temporary_directory/ambiguous"

echo "Release provenance tests passed."
