# attestree-publish

`#4830 U13` (plan `docs/plans/2026-10-24-1609-feat-own-apps-and-private-winget-sources-plan.md`, R15, R36, R37; KTD8, KTD9, KTD24, KTD25, KTD26). The publish CLI. The GitHub Action (`tools/publish-action`) and the Azure DevOps task (`tools/publish-ado-task`) are thin wrappers around it.

## Two modes

**Push** (`--instance`): the instance must be reachable from the runner.

1. Hashes each installer, one per architecture.
2. Obtains the CI system's OIDC token: GitHub Actions for the instance's audience (`--audience attestree:<instance-id>`, from the binding screen), or Azure Pipelines through `System.OidcRequestUri` for the bound service connection. It asks for a fresh token per publish, and again if a wait outlives one.
3. `POST /api/v1/publish/{id}/{version}` names every installer with its SHA-256, and optionally `--manifest` (one YAML document per file). A manifest that names another id or version, or two installers for one architecture, is refused before anything is sent. The fields the instance ignored are printed.
4. `PUT`s each installer into the upload slot the answer names.
5. With `--wait`, reads the status until a terminal state.

**Repository** (`--to-repository owner/repo`): for an instance with no inbound route (KTD25). With the job's own `GITHUB_TOKEN` it attaches each installer to a release, then commits `manifests/<letter>/<Publisher>/<Package>/<version>/` and the package's `versions.json` in one commit. Each manifest names its installer by the release asset's API URL (`api.github.com/repos/<owner>/<repo>/releases/assets/<id>`), the URL `docs/research/2026-10-30-github-release-asset-download-spike.md` settled: it downloads under the source's GitHub-token sign-in, where a private repository's browser download URL does not. This mode never asks for an OIDC token and never calls the instance; the instance reads the repository as a folder source and finds the version on its next freshness check.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Accepted (a repeat of the same publish included); with `--wait`, attested |
| 1 | Usage: the arguments, a file or the manifest cannot be used; nothing was sent |
| 2 | Refused by the instance (or the token), or the version was refused |
| 3 | Duplicate: the version exists with other content, arrived another way, or was removed from the catalog |
| 4 | Transient: the network, a rate limit, or a 503 that outlasted the retries (only a 503 is retried) |
| 10 | `--wait`: superseded |
| 11 | `--wait`: not admitted (no subscription, no grant, a carve-out, firing disarmed) |
| 12 | `--wait`: binding removed |
| 13 | `--wait`: source removed |
| 14 | `--wait`: halted |
| 15 | `--wait`: no terminal state within `--wait-timeout` |

No token is ever printed, `--verbose` included: tokens this run holds, and anything shaped like a JWT or a GitHub token, print as `[redacted]`.

## License

Apache-2.0 (`LICENSE`; `docs/adr/2026-10-31-001-the-publish-tooling-is-apache-2-0.md`). The Action and the Azure DevOps task carry the same license in their own directories. A single-file build carries YamlDotNet and the .NET runtime, both MIT: `THIRD-PARTY-NOTICES.md` holds their notices, and each release ships it next to the binaries, with the runtime's own notices, `dotnet-runtime-THIRD-PARTY-NOTICES.txt`.

## Releases

`.github/workflows/release-publish-tooling.yml` releases the CLI, the Action and the task together, from a `publish-v<major>.<minor>.<patch>` tag on main (`docs/runbooks/publish-tooling-release.md`). It mirrors `build/publish-repository-manifest.json`'s tree to `attestree/publish` (the Action at its root, this directory under `cli/`, the task under `azure-pipelines/`) and builds the four binaries from that tree, so the published source builds the published binaries. The Windows build is Authenticode-signed. The release writes the builds' SHA-256 into both wrappers' `cli-sha256.txt`, publishes the builds as the `cli-v<version>` release of `attestree/publish`, tags the Action `v<version>` and moves `v<major>`, and publishes the task to the Visual Studio Marketplace. The copies of `cli-sha256.txt` here pin no build, so a wrapper run from this repository fails closed.
