#!/usr/bin/env bash
set -Eeuo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "$repository_root"

usage() {
  echo "Usage: $0 {fast|full|build-test}" >&2
}

if [[ $# -ne 1 ]]; then
  usage
  exit 2
fi

mode="$1"
case "$mode" in
  fast|full|build-test) ;;
  *)
    echo "Unknown verification mode: $mode" >&2
    usage
    exit 2
    ;;
esac

export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$repository_root/.dotnet-home}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$repository_root/.dotnet/packages}"
export DOTNET_CLI_TELEMETRY_OPTOUT="${DOTNET_CLI_TELEMETRY_OPTOUT:-1}"
export DOTNET_NOLOGO="${DOTNET_NOLOGO:-1}"
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES"

run_stage() {
  local stage_name="$1"
  shift
  echo "==> $stage_name"
  "$@"
}

validate_workflow() {
  run_stage "Validate workflow infrastructure" python3 scripts/validate-workflow.py
}

self_test_workflow() {
  run_stage "Test verification orchestration" scripts/test-verification.sh
  run_stage "Test branch integrity guard" scripts/test-branch-integrity.sh
}

build_and_test() {
  run_stage "Restore locked .NET dependencies" dotnet restore BeanBot.sln --locked-mode
  run_stage "Verify .NET formatting and analyzers" \
    dotnet format BeanBot.sln --verify-no-changes --no-restore --severity warn
  # Roslyn requires XML documentation generation when IDE0005 is a build warning.
  # Keep this explicit gate so redundant usings fail verification without changing publish output.
  run_stage "Verify redundant using cleanup" \
    dotnet format BeanBot.sln --verify-no-changes --no-restore --diagnostics IDE0005
  run_stage "Build Release" dotnet build BeanBot.sln --configuration Release --no-restore
  if [[ "$mode" == "full" ]]; then
    mkdir -p .artifacts/TestResults .artifacts/coverage
    find .artifacts/TestResults -mindepth 1 -delete
    run_stage "Run Release tests with coverage" \
      dotnet test BeanBot.sln --configuration Release --no-build \
        --settings coverage.runsettings --collect "Code Coverage" \
        --results-directory .artifacts/TestResults
    run_stage "Check coverage baseline" \
      python3 scripts/check-coverage.py \
        .artifacts/TestResults .config/coverage-baseline.json .artifacts/coverage
  else
    run_stage "Run Release tests" dotnet test BeanBot.sln --configuration Release --no-build
  fi
}

check_diff() {
  local base_ref="${BEANBOT_VERIFY_BASE_REF:-}"
  if [[ -z "$base_ref" ]]; then
    if [[ -n "${GITHUB_BASE_REF:-}" ]]; then
      base_ref="origin/${GITHUB_BASE_REF}"
    else
      base_ref="origin/master"
    fi
  fi

  run_stage "Check committed branch diff" git diff --check "$base_ref...HEAD"
  run_stage "Check staged diff" git diff --cached --check
  run_stage "Check unstaged diff" git diff --check
}

check_vulnerable_packages() {
  local report_path
  report_path="$(mktemp "${TMPDIR:-/tmp}/beanbot-vulnerabilities.XXXXXX.json")"
  if ! dotnet list BeanBot.sln package --vulnerable --include-transitive --no-restore \
    --format json --output-version 1 >"$report_path"; then
    rm -f -- "$report_path"
    return 1
  fi
  if ! python3 scripts/check-vulnerable-packages.py "$report_path"; then
    rm -f -- "$report_path"
    return 1
  fi
  rm -f -- "$report_path"
}

if [[ "${BEANBOT_VERIFY_SKIP_SELF_TEST:-0}" != "1" ]]; then
  self_test_workflow
fi
validate_workflow
build_and_test
check_diff

if [[ "$mode" == "full" ]]; then
  branch_integrity_candidate="${BEANBOT_BRANCH_INTEGRITY_CANDIDATE:-HEAD}"
  run_stage "Verify master ancestry" \
    scripts/check-branch-integrity.sh origin/master "$branch_integrity_candidate"
  run_stage "Check vulnerable NuGet packages" check_vulnerable_packages
  docker_image_tag="${BEANBOT_VERIFY_DOCKER_TAG:-beanbot-verification:local}"
  build_version="${BEANBOT_BUILD_VERSION:-0.0.0-local}"
  build_commit_sha="${BEANBOT_BUILD_COMMIT_SHA:-$(git rev-parse HEAD)}"
  run_stage "Build Docker image" docker build \
    --tag "$docker_image_tag" \
    --build-arg "BEANBOT_VERSION=$build_version" \
    --build-arg "BEANBOT_COMMIT_SHA=$build_commit_sha" \
    .
  run_stage "Smoke test hardened Docker image" scripts/container-smoke.sh "$docker_image_tag"
fi

echo "Verification mode '$mode' completed successfully."
