# Release notes

One file per release, plus a rolling draft (`RELEASE_NOTES_next.md`) that entries are appended to as they
land. At release time the draft is renamed and a fresh empty draft is created.

## Versioning

`YY.feature.patch` (CalVer):

- **YY** — two-digit year (`26` = 2026)
- **feature** — incremented for each release containing new features
- **patch** — incremented for bug-fix-only releases within the same feature version

Examples: `26.1.0` (first feature release of 2026), `26.1.1` (a patch), `26.2.0` (the next feature release).
The version is bumped **at release time**, not during development.

## Two files must agree

| file | field |
|---|---|
| `dotnet/Directory.Build.props` | `<Version>` — flows to every assembly and to `ospreytool --version` |
| `dotnet/src/OspreyTool.App/tool-inf/info.properties` | `Version = ` — the Skyline tool manifest |

Skyline keys tool upgrades on `Identifier` + `Version` from the manifest, so a mismatch means Skyline thinks
it has a different version than the binaries report. `.github/workflows/release.yml` fails the release if
either file disagrees with the tag, so a mismatch cannot ship.

## Release process

1. Finalize `RELEASE_NOTES_next.md`; rename it to `RELEASE_NOTES_v{version}.md` and update its heading.
   Create a fresh empty `RELEASE_NOTES_next.md` from the template below.
2. Bump `{version}` in **both** files above.
3. Run the ship gate locally: `pwsh -File dotnet/build/package-and-verify.ps1`
   (tests → package → launch the packaged exe). Confirm `ospreytool --version` prints `{version}.0`.
4. Commit and merge to `main`.
5. Tag and push: `git tag v{version} && git push origin v{version}`

   **Pushing the tag both builds the artifacts and creates the GitHub Release** (`release.yml`), attaching
   `OspreyTool.zip` and using `release-notes/RELEASE_NOTES_v{version}.md` as the release body. Do not
   hand-create the Release.

## Style

- Past tense (“Added”, “Fixed”, “Removed”).
- Lead with user impact, not implementation detail.
- Include specific numbers where they mean something (counts, AUCs, timings).
- Reference options by their CLI flag or settings name.
- Omit empty sections.

## Template

```markdown
# OspreyTool v{version} Release Notes

One-sentence summary of the release.

## New Features

## Bug Fixes

## Performance

## Breaking Changes
```
