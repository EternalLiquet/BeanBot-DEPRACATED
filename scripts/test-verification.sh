#!/usr/bin/env bash
set -Eeuo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
verify_script="$repository_root/scripts/verify.sh"
real_python="$(command -v python3)"
temporary_directory="$(mktemp -d)"
trap 'rm -rf -- "$temporary_directory"' EXIT

stub_directory="$temporary_directory/bin"
mkdir -p "$stub_directory"
stub="$temporary_directory/command-stub"

cat >"$stub" <<'STUB'
#!/usr/bin/env bash
set -Eeuo pipefail
command_name="$(basename -- "$0")"
echo "$PWD|$command_name|$*" >>"$BEANBOT_VERIFY_TEST_LOG"
if [[ "$command_name" == "dotnet" && "$*" == *"--format json"* ]]; then
  if [[ -n "${BEANBOT_VERIFY_TEST_VULNERABILITY_JSON:-}" ]]; then
    printf '%s\n' "$BEANBOT_VERIFY_TEST_VULNERABILITY_JSON"
  else
    printf '%s\n' '{"version":1,"projects":[]}'
  fi
fi
if [[ -n "${BEANBOT_VERIFY_TEST_FAIL:-}" && "$command_name $*" == *"$BEANBOT_VERIFY_TEST_FAIL"* ]]; then
  exit 17
fi
STUB
chmod +x "$stub"
for command_name in dotnet git docker; do
  cp "$stub" "$stub_directory/$command_name"
done
cat >"$stub_directory/python3" <<'PYTHON_STUB'
#!/usr/bin/env bash
set -Eeuo pipefail
echo "$PWD|python3|$*" >>"$BEANBOT_VERIFY_TEST_LOG"
if [[ "$1" == "scripts/check-vulnerable-packages.py" ]]; then
  exec "${BEANBOT_VERIFY_REAL_PYTHON:-/usr/bin/python3}" "$@"
fi
if [[ -n "${BEANBOT_VERIFY_TEST_FAIL:-}" && "python3 $*" == *"$BEANBOT_VERIFY_TEST_FAIL"* ]]; then
  exit 17
fi
PYTHON_STUB
chmod +x "$stub_directory/python3"

assert_contains() {
  local expected="$1"
  local file="$2"
  if ! grep -Fq -- "$expected" "$file"; then
    echo "Expected '$expected' in $file" >&2
    exit 1
  fi
}

assert_not_contains() {
  local unexpected="$1"
  local file="$2"
  if grep -Fq -- "$unexpected" "$file"; then
    echo "Did not expect '$unexpected' in $file" >&2
    exit 1
  fi
}

assert_count() {
  local expected_count="$1"
  local expected="$2"
  local file="$3"
  local actual_count
  actual_count="$(grep -Fc -- "$expected" "$file" || true)"
  if [[ "$actual_count" -ne "$expected_count" ]]; then
    echo "Expected '$expected' $expected_count time(s) in $file, found $actual_count" >&2
    exit 1
  fi
}

fast_log="$temporary_directory/fast.log"
(
  cd /tmp
  PATH="$stub_directory:$PATH" BEANBOT_VERIFY_TEST_LOG="$fast_log" \
    BEANBOT_VERIFY_SKIP_SELF_TEST=1 "$verify_script" fast
)
assert_contains "$repository_root|dotnet|restore BeanBot.sln" "$fast_log"
assert_contains "$repository_root|dotnet|format BeanBot.sln --verify-no-changes --no-restore --severity warn" "$fast_log"
assert_contains "$repository_root|dotnet|test BeanBot.sln --configuration Release --no-build" "$fast_log"
assert_not_contains "|dotnet|list " "$fast_log"
assert_not_contains "|docker|" "$fast_log"

full_log="$temporary_directory/full.log"
(
  cd /tmp
  PATH="$stub_directory:$PATH" BEANBOT_VERIFY_TEST_LOG="$full_log" \
    BEANBOT_VERIFY_REAL_PYTHON="$real_python" \
    BEANBOT_VERIFY_SKIP_SELF_TEST=1 "$verify_script" full
)
assert_contains "$repository_root|dotnet|list BeanBot.sln package --vulnerable --include-transitive --format json --output-version 1" "$full_log"
assert_contains "$repository_root|docker|build --tag beanbot-verification:local ." "$full_log"
assert_count 1 "|python3|scripts/validate-workflow.py" "$full_log"
assert_count 1 "|dotnet|restore BeanBot.sln" "$full_log"
assert_count 1 "|dotnet|format BeanBot.sln --verify-no-changes --no-restore --severity warn" "$full_log"
assert_count 1 "|dotnet|build BeanBot.sln --configuration Release --no-restore" "$full_log"
assert_count 1 "|dotnet|test BeanBot.sln --configuration Release --no-build" "$full_log"
assert_count 1 "|dotnet|list BeanBot.sln package --vulnerable --include-transitive --format json --output-version 1" "$full_log"
assert_count 1 "|docker|build --tag beanbot-verification:local ." "$full_log"

invalid_log="$temporary_directory/invalid.log"
if PATH="$stub_directory:$PATH" BEANBOT_VERIFY_TEST_LOG="$invalid_log" \
  BEANBOT_VERIFY_SKIP_SELF_TEST=1 "$verify_script" unsupported >"$temporary_directory/invalid.out" 2>&1; then
  echo "Invalid mode unexpectedly succeeded" >&2
  exit 1
fi
assert_contains "Unknown verification mode: unsupported" "$temporary_directory/invalid.out"

format_failure_log="$temporary_directory/format-failure.log"
if PATH="$stub_directory:$PATH" BEANBOT_VERIFY_TEST_LOG="$format_failure_log" \
  BEANBOT_VERIFY_TEST_FAIL="dotnet format BeanBot.sln" BEANBOT_VERIFY_SKIP_SELF_TEST=1 \
  "$verify_script" fast; then
  echo "Injected formatter failure unexpectedly succeeded" >&2
  exit 1
fi
assert_contains "|dotnet|format BeanBot.sln --verify-no-changes --no-restore --severity warn" "$format_failure_log"
assert_not_contains "|dotnet|build " "$format_failure_log"
assert_not_contains "|dotnet|test " "$format_failure_log"

failure_log="$temporary_directory/failure.log"
if PATH="$stub_directory:$PATH" BEANBOT_VERIFY_TEST_LOG="$failure_log" \
  BEANBOT_VERIFY_TEST_FAIL="dotnet build BeanBot.sln" BEANBOT_VERIFY_SKIP_SELF_TEST=1 \
  "$verify_script" full; then
  echo "Injected child-command failure unexpectedly succeeded" >&2
  exit 1
fi
assert_contains "|dotnet|build BeanBot.sln --configuration Release --no-restore" "$failure_log"
assert_not_contains "|dotnet|test " "$failure_log"
assert_not_contains "|docker|" "$failure_log"

vulnerable_log="$temporary_directory/vulnerable.log"
vulnerability_json='{"version":1,"projects":[{"path":"Fixture.csproj","frameworks":[{"framework":"net10.0","topLevelPackages":[{"id":"Example.Package","resolvedVersion":"1.2.3","vulnerabilities":[{"severity":"High","advisoryurl":"https://example.invalid/advisory"}]}]}]}]}'
if PATH="$stub_directory:$PATH" BEANBOT_VERIFY_TEST_LOG="$vulnerable_log" \
  BEANBOT_VERIFY_REAL_PYTHON="$real_python" \
  BEANBOT_VERIFY_TEST_VULNERABILITY_JSON="$vulnerability_json" \
  BEANBOT_VERIFY_SKIP_SELF_TEST=1 "$verify_script" full; then
  echo "Reported vulnerable dependency unexpectedly passed full verification" >&2
  exit 1
fi
assert_contains "|python3|scripts/check-vulnerable-packages.py" "$vulnerable_log"
assert_not_contains "|docker|" "$vulnerable_log"

echo "Verification orchestration tests passed."
