## Summary

- Describe the user-facing or operational change.

## Related Issues

- Closes #
- Related to #

## Validation

- [ ] `dotnet format --verify-no-changes` clean
- [ ] `dotnet build OpenHandsNET.slnx -c Release` clean
- [ ] `dotnet test` green locally (or a note on what only CI covers)
- [ ] Production-code changes include a new or updated test in this PR
- [ ] `packages.lock.json` regenerated when dependencies changed
      (`dotnet restore --force-evaluate`)

## Tests Added Or Updated

- List every changed or added test file.
- If no production code changed, state that here.

## Coverage Notes

- If this PR changes `src/`, `cli/` or `runtime/`, explain which tests cover it.
- If coverage moved down, explain why and what follow-up task recovers it.

## Reviewer Notes

- Risky areas, migrations, feature flags, or manual validation reviewers should know about.
