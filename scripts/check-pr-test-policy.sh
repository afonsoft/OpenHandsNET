#!/usr/bin/env bash
# PR test policy (OmniRoute pattern, adapted to .NET):
#   1. Production C# changed (src/ cli/ runtime/) -> tests/ must change too.
#   2. Test-masking guard (advisory): changed test files must not net-remove
#      [Fact]/[Theory]/assertion lines.
# Usage: check-pr-test-policy.sh <base-sha> <head-sha>
set -uo pipefail

BASE="${1:?base sha required}"
HEAD="${2:?head sha required}"

git diff --name-only "$BASE" "$HEAD" > /tmp/pr-files.txt

prod=$(grep -cE '^(src|cli|runtime)/.*\.cs$' /tmp/pr-files.txt || true)
tests=$(grep -cE '^tests/.*\.cs$' /tmp/pr-files.txt || true)

{
  echo "## PR test policy"
  echo ""
  echo "- Production .cs files changed: $prod"
  echo "- Test .cs files changed: $tests"
  echo ""
} >> "${GITHUB_STEP_SUMMARY:-/dev/stdout}"

if [[ "$prod" -gt 0 ]] && [[ "$tests" -eq 0 ]]; then
  {
    echo "**FAIL**: production code changed without any test change."
    echo ""
    echo '```'
    grep -E '^(src|cli|runtime)/.*\.cs$' /tmp/pr-files.txt | head -20
    echo '```'
  } >> "${GITHUB_STEP_SUMMARY:-/dev/stdout}"
  echo "::error::PR changes $prod production .cs file(s) but no file under tests/." >&2
  exit 1
fi

# Test-masking guard (advisory — warning, not a gate yet)
mask=0
while IFS= read -r f; do
  [[ -f "$f" ]] || continue
  removed=$(git diff "$BASE" "$HEAD" -- "$f" | grep -cE '^-.*(\[Fact|\[Theory|Assert\.|\.Should[A-Z])' || true)
  added=$(git diff "$BASE" "$HEAD" -- "$f" | grep -cE '^\+.*(\[Fact|\[Theory|Assert\.|\.Should[A-Z])' || true)
  if [[ "$removed" -gt "$((added + 2))" ]]; then
    echo "::warning file=$f::possible test-masking — removed $removed assertion/test lines, added $added"
    mask=$((mask + 1))
  fi
done < <(grep -E '^tests/.*\.cs$' /tmp/pr-files.txt || true)

if [[ "$mask" -gt 0 ]]; then
  echo "::notice::$mask changed test file(s) net-removed assertions — review for test-masking."
fi

echo "PR test policy OK ($prod prod files, $tests test files)"
