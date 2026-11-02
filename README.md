# attestree/publish

Publish your own Windows apps to [Attestree](https://attestree.com) from CI.

- **The GitHub Action** (this repository's root, `attestree/publish@v1`).
- **The Azure Pipelines task**, *Attestree publish* on the Visual Studio Marketplace. Its source is in [`azure-pipelines/`](azure-pipelines/).
- **The `attestree-publish` command-line tool** that both run, for Windows x64, Linux x64, Linux arm64 and macOS arm64, as this repository's [`cli-v<version>` releases](https://github.com/attestree/publish/releases). Its source is in [`cli/`](cli/).

## Publish to your instance

The job sends the version and its installers to your Attestree instance, which test-installs it in isolation and attests it, as it does every other app. It stores no secret: it signs in with the short-lived OpenID Connect token GitHub issues for the run, and your instance accepts only the repository and workflow you bound on it. The instance must be reachable from the runner.

```yaml
permissions:
  id-token: write
  contents: read

steps:
  - uses: attestree/publish@v1
    with:
      instance: https://attestree.example.com
      audience: attestree:<instance-id>
      package-id: Contoso.Tool
      version: 1.2.0
      installers: |
        x64=dist/tool-x64.msi
        arm64=dist/tool-arm64.msi
      wait: true
```

`audience` is shown on the instance's binding screen, exactly as the job must request it.

## Publish into your own winget repository

For an instance with no inbound route. The job commits the version's manifests to your own GitHub repository and attaches each installer to a release there, with the job's own token. Nothing is sent to the instance, which reads the repository as a source.

```yaml
permissions:
  contents: write

steps:
  - uses: attestree/publish@v1
    with:
      to-repository: contoso/winget
      package-id: Contoso.Tool
      version: 1.2.0
      installers: |
        x64=dist/tool-x64.msi
      publisher: Contoso
      installer-type: msi
```

## Inputs

| Input | Mode | What it is |
|---|---|---|
| `package-id` | both | The winget package id, inside one of the instance's prefixes. |
| `version` | both | The version to publish. |
| `installers` | both | One installer per line, as `<arch>=<path>`. One installer per architecture. |
| `manifest` | both | Optional. A winget manifest file or folder whose installer type, scope and switches the version uses. |
| `instance` | push | The instance's https base URL. |
| `audience` | push | `attestree:<instance-id>`, as the binding screen shows it. |
| `wait` | push | Wait until the version is attested or reaches another final state; the step's exit code says which. |
| `wait-timeout` | push | Minutes to wait, 60 by default. |
| `to-repository` | repository | `<owner>/<repo>` of your winget repository. |
| `branch` | repository | The branch the manifests are committed to, `main` by default. |
| `manifests-path` | repository | The folder holding winget-pkgs' layout, `manifests` by default. |
| `publisher` | repository | Without a manifest: the publisher's name. |
| `installer-type` | repository | Without a manifest: `msi`, `exe`, `inno`, `nullsoft`, `wix`, `burn` or `msix`. |
| `github-token` | repository | The token that writes the repository and its release, the job's own by default. |
| `verbose` | both | Log each request's method, host and path. No token is ever printed. |

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Accepted, a repeat of the same publish included. With `wait`, attested. |
| 1 | The inputs, a file or the manifest cannot be used. Nothing was sent. |
| 2 | Refused by the instance, or the version was refused. |
| 3 | The version exists with other content, arrived another way, or was removed from the catalog. |
| 4 | Transient: the network, a rate limit, or an instance that stayed unavailable. |
| 10 to 15 | With `wait`: superseded, not admitted, binding removed, source removed, halted, or no final state before the timeout. |

## What runs, and how to check it

Each version of the Action and the task carries `cli-sha256.txt`: the SHA-256 of every `attestree-publish` build it runs. It downloads the build for the runner from the matching `cli-v<version>` release and refuses one whose hash differs, before running it. Pin the Action to a full version (`attestree/publish@v1.0.0`) or a commit to fix exactly which build runs; `@v1` follows the newest 1.x release.

Each release carries the four builds, `cli-sha256.txt`, the licence and the third-party notices. The Windows build is Authenticode-signed. To check a download by hand:

```sh
grep ' attestree-publish-linux-x64$' cli-sha256.txt | sha256sum -c
```

The builds are made from the source in [`cli/`](cli/) at the release's commit.

## Security

Report a vulnerability to security@attestree.com. Please don't open a public issue for it.

## Licence

[Apache License 2.0](LICENSE). The command-line tool's builds also carry YamlDotNet and the .NET runtime, both under the MIT licence: see each release's `THIRD-PARTY-NOTICES.md` and `dotnet-runtime-THIRD-PARTY-NOTICES.txt`.
