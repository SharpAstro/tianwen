---
name: release-tianwen
description: Cut a TianWen binary release. Triggers a workflow_dispatch run of `.github/workflows/dotnet.yml` on `main`, which builds AOT publishes for all six RIDs (win-x64, win-arm64, linux-x64, linux-arm64, osx-arm64, osx-x64) and creates a GitHub Release tagged `v4.2.<run_number>` with .tar.gz assets for tianwen-cli, tianwen-fits, tianwen-gui, and tianwen-server. Use when the user asks to release, ship, publish, or cut a TianWen release. NOT for sibling NuGet libraries -- use `/release-lib` for those.
---

Usage: `/release-tianwen` (no arguments).

## What this does

The TianWen CI workflow (`.github/workflows/dotnet.yml`) gates `publish-apps`
and `release` on `github.event_name == 'workflow_dispatch'`. Pushes to main
only run build + tests + nuget publish; binary releases are manual to keep
LFS bandwidth under the 1 GB/month free tier (six AOT publishes pull LFS
once each; on every push to main that adds up fast).

This skill triggers a manual workflow run, polls until it completes, and
reports the resulting GitHub Release URL.

## Steps

1. **Verify clean state.** Run from repo root:
   ```bash
   git status --porcelain
   git rev-parse --abbrev-ref HEAD
   ```
   - Working tree must be clean (no staged or unstaged changes).
   - Branch must be `main`. Releases off feature branches are not supported
     by the workflow's `tag_name: v${{ env.VERSION_PREFIX }}` -- the tag
     would point to a commit not on main.
   - If either check fails, stop and tell the user what to fix.

2. **Confirm sync with origin.** The workflow checks out by SHA, so any
   un-pushed commit would not be in the release. Run:
   ```bash
   git fetch origin main
   git rev-list --count HEAD..origin/main
   git rev-list --count origin/main..HEAD
   ```
   - Both counts must be 0. If `HEAD..origin/main` > 0, user needs to pull.
     If `origin/main..HEAD` > 0, user needs to push first (and re-run after
     CI passes -- otherwise release contains untested code).

3. **Show the user what will happen.** Include the current `VERSION_PREFIX`
   from `.github/workflows/dotnet.yml` and the next run number:
   ```bash
   gh api "repos/SharpAstro/tianwen/actions/workflows/25402710/runs?per_page=1" \
     -q '.workflow_runs[0].run_number'
   ```
   The release tag will be `v4.2.<that_number + 1>`.

4. **Trigger the workflow.** Dispatch on main:
   ```bash
   gh workflow run dotnet.yml --ref main
   ```

5. **Find the new run.** `gh workflow run` doesn't return the run ID. Poll
   for the run created in the last ~30s:
   ```bash
   sleep 5
   gh run list --workflow dotnet.yml --branch main --limit 5 \
     --json databaseId,createdAt,event,status \
     --jq '.[] | select(.event=="workflow_dispatch") | .databaseId' \
     | head -1
   ```
   Capture that ID into `RUN_ID`.

6. **Watch the run.** AOT publish takes 15-25 minutes across the six RIDs.
   Use `gh run watch` to stream status:
   ```bash
   gh run watch $RUN_ID --exit-status
   ```
   `--exit-status` makes the command exit non-zero if the run failed -- so
   the skill can branch on success/failure cleanly.

7. **On success**: fetch the release URL and report it.
   ```bash
   gh release view v4.2.<run_number> --json url --jq .url
   ```
   Paste the URL into the chat.

8. **On failure**: don't try to retry blindly. Show the failed-job summary:
   ```bash
   gh run view $RUN_ID --log-failed | head -200
   ```
   And ask the user how to proceed. Common failures:
   - **macOS publish OOM** -- transient, suggest re-running just the failed
     job: `gh run rerun $RUN_ID --failed`.
   - **LFS bandwidth exceeded** -- happens if the monthly cap is hit. Check
     usage in GitHub Settings > Billing. No retry will help; wait for the
     1st of the month or buy a data pack.
   - **Test failure** -- the test jobs run regardless of dispatch, and
     `release` needs them. A test broke after the last green push -- fix
     forward, push, then re-trigger the release.

## Notes

- The workflow uses `${{ github.run_number }}` for the version, so each
  dispatch produces a new tag (no version collisions). Don't try to
  pre-bump the version -- the run_number bumps automatically.
- Don't push --force or amend commits between trigger and completion --
  the workflow checks out by SHA at trigger time, so amending main would
  diverge the release tag from the published commit.
- `publish-nuget` runs on every push to main (gated on tests), independent
  of binary releases. The library can ship to NuGet without a binary
  release and vice versa.
- Run number progression: as of 2026-04-26 the latest `run_number` is 627.
  Each push to main + each workflow_dispatch increments it.

## When NOT to use this skill

- Sibling library NuGet release (DIR.Lib, Console.Lib, etc.) -- use
  `/release-lib <name>` instead.
- Just publishing a NuGet of TianWen.Lib -- that happens automatically on
  every push to main via `publish-nuget`.
- Bumping the version locally without releasing -- use `/bump-version`.
