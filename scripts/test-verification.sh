#!/usr/bin/env bash
set -Eeuo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
verify_script="$repository_root/scripts/verify.sh"
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
if [[ -n "${BEANBOT_VERIFY_TEST_FAIL:-}" && "$command_name $*" == *"$BEANBOT_VERIFY_TEST_FAIL"* ]]; then
  exit 17
fi
STUB
chmod +x "$stub"
for command_name in python3 dotnet git docker; do
  cp "$stub" "$stub_directory/$command_name"
done

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
assert_contains "$repository_root|dotnet|test BeanBot.sln --configuration Release --no-build" "$fast_log"
assert_not_contains "|dotnet|list " "$fast_log"
assert_not_contains "|docker|" "$fast_log"

full_log="$temporary_directory/full.log"
(
  cd /tmp
  PATH="$stub_directory:$PATH" BEANBOT_VERIFY_TEST_LOG="$full_log" \
    BEANBOT_VERIFY_SKIP_SELF_TEST=1 "$verify_script" full
)
assert_contains "$repository_root|dotnet|list BeanBot.sln package --vulnerable --include-transitive" "$full_log"
assert_contains "$repository_root|docker|build --tag beanbot-verification:local ." "$full_log"
assert_count 1 "|python3|scripts/validate-workflow.py" "$full_log"
assert_count 1 "|dotnet|restore BeanBot.sln" "$full_log"
assert_count 1 "|dotnet|build BeanBot.sln --configuration Release --no-restore" "$full_log"
assert_count 1 "|dotnet|test BeanBot.sln --configuration Release --no-build" "$full_log"
assert_count 1 "|dotnet|list BeanBot.sln package --vulnerable --include-transitive" "$full_log"
assert_count 1 "|docker|build --tag beanbot-verification:local ." "$full_log"

invalid_log="$temporary_directory/invalid.log"
if PATH="$stub_directory:$PATH" BEANBOT_VERIFY_TEST_LOG="$invalid_log" \
  BEANBOT_VERIFY_SKIP_SELF_TEST=1 "$verify_script" unsupported >"$temporary_directory/invalid.out" 2>&1; then
  echo "Invalid mode unexpectedly succeeded" >&2
  exit 1
fi
assert_contains "Unknown verification mode: unsupported" "$temporary_directory/invalid.out"

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

echo "Verification orchestration tests passed."
