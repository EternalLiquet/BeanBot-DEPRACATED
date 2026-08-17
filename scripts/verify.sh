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
}

build_and_test() {
  run_stage "Restore .NET dependencies" dotnet restore BeanBot.sln
  run_stage "Verify .NET formatting and analyzers" \
    dotnet format BeanBot.sln --verify-no-changes --no-restore --severity warn
  run_stage "Build Release" dotnet build BeanBot.sln --configuration Release --no-restore
  run_stage "Run Release tests" dotnet test BeanBot.sln --configuration Release --no-build
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
  if ! dotnet list BeanBot.sln package --vulnerable --include-transitive \
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
  run_stage "Check vulnerable NuGet packages" check_vulnerable_packages
  run_stage "Build Docker image" docker build --tag beanbot-verification:local .
fi

echo "Verification mode '$mode' completed successfully."
