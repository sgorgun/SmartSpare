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
3. Fill in the pull request template. The PR title follows the commit convention, because a squash merge uses it as the commit message on `main`.
4. CI (**Build and test**) must pass. Review the diff yourself before merging.
5. Merge with the strategy that fits the branch history (the branch is deleted automatically):
   - **Rebase and merge** when every commit is a clean, self-contained Conventional Commit that builds on its own — the usual case for feature branches. Each commit lands on `main` as is.
   - **Squash and merge** for single-purpose changes, Dependabot updates, or branches with work-in-progress or fix-up commits. The PR title becomes the commit message.

   Merge commits are disabled: `main` keeps a linear history either way. Before using rebase, tidy the branch if needed (`git rebase -i main` to squash fix-ups) so that every commit on `main` is meaningful.

Before pushing, run the same checks as CI. CI calls `tools/check.sh` step by step, so a green local run means a green CI run (Release configuration, coverage collected exactly as in CI):

```
tools/check.sh              # all steps, needs Docker: stale terms, locked restore, vulnerable packages,
                            # build, format, check-model, tests, coverage gate, integration tests, database
tools/check.sh fast         # the steps without Docker (the CI "Build and test" job)
tools/check.sh build test   # selected steps
tools/check.sh e2e topology # optional, after a build: the Playwright smoke test and the services topology
                            # (gateway + services + PostgreSQL + RabbitMQ); CI runs each as its own job
```

Stage new files (`git add`) first: the stale-terms check scans tracked files only. On Windows, run the script in Git Bash.

**Memory.** Locally the script runs the test projects one at a time, with each project's test collections in series, so
at most one Testcontainers PostgreSQL runs at once (`CHECK_SEQUENTIAL=1`, the default unless `CI=true`). The same tests
run, just more slowly. CI keeps the parallel run; `CHECK_SEQUENTIAL=0 tools/check.sh` reproduces it locally if the
machine has the memory. After the `integration`, `database`, `e2e` and `topology` steps the script lists any container the tests
left behind (normally only Testcontainers' Ryuk reaper, which removes itself).

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

Dependabot opens grouped weekly pull requests for NuGet packages and GitHub Actions. They follow the same rules: green CI, then squash and merge. Packages versioned with the runtime (`Microsoft.Extensions.*`, `Microsoft.AspNetCore.*`, EF Core, Npgsql) only get updates within the .NET 10 line (ADR 0007).

Dependabot updates the lock file of the project that references a package, but not the lock files of projects that depend on it, so CI fails at `dotnet restore --locked-mode` with NU1004. Fix it on the Dependabot branch:

```
gh pr checkout <number>
dotnet restore SmartSpare.slnx --force-evaluate
git commit -am "build(deps): refresh lock files"
git push
```
