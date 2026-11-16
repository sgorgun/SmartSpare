# Contributing

SmartSpare follows **GitHub Flow**: `main` is always releasable and deployable; all work happens on short-lived branches and reaches `main` only through a pull request with a green CI.

## Branches

- `main` — protected. No direct pushes, no force pushes; changes arrive only by pull request.
- Work branches are created from the latest `main`, live for days rather than weeks, and are deleted after merge.

| Prefix | Use | Example |
|---|---|---|
| `feature/` | New functionality | `feature/stage-2-data-layer`, `feature/inventory-transfers` |
| `fix/` | Bug fixes | `fix/reservation-expiry` |
| `docs/` | Documentation only | `docs/adr-0011-caching` |
| `chore/` | Tooling, build, dependencies, CI | `chore/git-workflow` |
| `refactor/` | Code changes without behaviour change | `refactor/result-mapping` |

Large stages are split into several pull requests where possible (for example, one per module).

## Commits

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/):

```
<type>(<scope>): <imperative summary, max 72 chars>

<body: what and why, wrapped at 72 chars>
```

- **Types:** `feat`, `fix`, `docs`, `test`, `refactor`, `perf`, `build`, `ci`, `chore`.
- **Scopes:** a module (`inventory`, `fleet`, `procurement`, `forecasting`, `identity`, `reporting`) or an area (`host`, `kernel`, `infra`, `db`, `web`, `deps`).
- Examples: `feat(inventory): add transfer approval endpoint`, `fix(db): guard numeric cast in demand import`, `ci: cache NuGet packages by lock file`.
- One logical change per commit. Commits are authored by the configured git user only; no trailers.

## Pull requests

1. Update `main` and branch off: `git switch main && git pull && git switch -c feature/<name>`.
2. Commit, push and open a pull request into `main` (`gh pr create --fill`); open it as a draft while work is in progress.
3. Fill in the pull request template. The PR title follows the commit convention, because it becomes the commit message on `main`.
4. CI (**Build and test**) must pass. Review the diff yourself before merging.
5. **Squash and merge**; the branch is deleted automatically.

Before pushing, run locally:

```
dotnet build SmartSpare.slnx -warnaserror
dotnet test SmartSpare.slnx --filter "Category!=Integration"
dotnet format SmartSpare.slnx --verify-no-changes
```

## Releases

- Versions follow [Semantic Versioning](https://semver.org/). Until the defence the project stays in `0.x`; each completed stage of the work plan is a minor release (`v0.1.0` = skeleton, `v0.2.0` = data layer, …).
- A release is an annotated tag on `main` plus a GitHub Release with generated notes:

```
git switch main && git pull
git tag -a v0.2.0 -m "Stage 2: data layer"
git push origin v0.2.0
gh release create v0.2.0 --generate-notes
```

## Dependencies

Dependabot opens grouped weekly pull requests for NuGet packages and GitHub Actions. They follow the same rules: green CI, then squash and merge.
