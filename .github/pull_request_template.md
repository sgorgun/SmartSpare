## What

<!-- What changes and why. Link the ADR or stage of the work plan if relevant. -->

## How to verify

<!-- Steps, endpoints or tests that show it works. -->

## Checklist

- [ ] Title follows Conventional Commits (`type(scope): summary`)
- [ ] `dotnet build -warnaserror`, tests and `dotnet format --verify-no-changes` pass locally
- [ ] Tests added or updated for the change
- [ ] Module boundaries respected (architecture tests green)
- [ ] Docs / ADRs updated if a decision or behaviour changed
- [ ] No secrets, no generated files, English only
