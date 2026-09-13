# .github

CI/CD and repository templates.

| File | Runs when | Does |
|---|---|---|
| `workflows/ci.yml` | every pull request and push to `main` | restore, build and test on Windows and Linux; publishes a win-x64 preview build as an artifact |
| `workflows/release.yml` | a `v*` tag is pushed | publishes win-x64, linux-x64 and osx-arm64, zips them with checksums, creates a GitHub Release |
| `PULL_REQUEST_TEMPLATE.md` | opening a pull request | checklist |

## The preview build is the review app

A desktop application has no preview URL. The reviewable artifact is the binary
itself: `ci.yml` publishes a single-file win-x64 build and uploads it, and the
reviewer downloads it from the Actions tab of the pull request. Artifacts are
kept 14 days.

## Cutting a release

```bash
git tag -a v0.2.0 -m "Huffman codec"
git push origin v0.2.0
```

CI does the rest. A tag containing `-alpha`, `-beta` or `-rc` is published as a
pre-release, which is how release channels are expressed — see
[ADR-0012](../ADR.md).

## Branch protection

`main` requires a pull request, green checks from both CI jobs, one approval and
resolved conversations. Force pushes and deletions are disabled. Configure this
in Settings → Rules; it is not stored in the repository.
