# Attestree publish

Publish a version of your own Windows app to [Attestree](https://attestree.com) from Azure Pipelines. The task sends the version and its installers to your Attestree instance, which test-installs it in isolation and attests it, as it does every other app.

It stores no secret. The job signs in with the short-lived token Microsoft Entra issues for a workload-identity federation service connection, and your instance accepts only the connection you bound on it.

## Use it

```yaml
- task: AttestreePublish@1
  inputs:
    serviceConnection: attestree-publish
    instance: https://attestree.example.com
    packageId: Contoso.Tool
    version: $(Build.BuildNumber)
    installers: |
      x64=$(Build.ArtifactStagingDirectory)/tool-x64.msi
      arm64=$(Build.ArtifactStagingDirectory)/tool-arm64.msi
    wait: true
```

| Input | Required | What it is |
|---|---|---|
| `serviceConnection` | yes | The workload-identity federation service connection bound on the instance. |
| `instance` | yes | The instance's https base URL. It must be reachable from the agent. |
| `packageId` | yes | The winget package id, inside one of the instance's prefixes. |
| `version` | yes | The version to publish. |
| `installers` | yes | One installer per line, as `<arch>=<path>`. One installer per architecture. |
| `manifest` | no | A winget manifest file or folder whose installer type, scope and switches the version uses. |
| `wait` | no | Wait until the version is attested or reaches another final state. The step's exit code says which. |
| `waitTimeout` | no | Minutes to wait, 60 by default. |
| `verbose` | no | Log each request's method, host and path. No token is ever printed. |

## The service connection

- Create an **Azure Resource Manager** service connection with **workload identity federation**, and use it **only for Attestree**. Its Entra app must hold **no Azure role assignments**. Attestree accepts the token Entra issues for the connection, and Entra accepts that same token for Azure, so a connection that can also deploy to Azure lets anything that can publish to Attestree reach Azure too, and the reverse.
- On the instance, bind the connection's issuer and subject exactly as its *Workload Identity federation details* show them.
- To limit publishing to a branch, add a **branch control** check on the service connection. Its tokens carry no branch, so the instance cannot check one.

## What runs

The task downloads the `attestree-publish` command-line tool from the [attestree/publish releases](https://github.com/attestree/publish/releases), checks its SHA-256 against the hashes this version of the task carries, and runs it. A download whose hash differs is refused before it runs. The tool asks Azure DevOps for the job's federated token itself, so no token passes through the task's arguments or output.

It runs on Windows x64, Linux x64, Linux arm64 and macOS arm64 agents.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Accepted, a repeat of the same publish included. With `wait`, attested. |
| 1 | The inputs, a file or the manifest cannot be used. Nothing was sent. |
| 2 | Refused by the instance, or the version was refused. |
| 3 | The version exists with other content, arrived another way, or was removed from the catalog. |
| 4 | Transient: the network, a rate limit, or an instance that stayed unavailable. |
| 10 to 15 | With `wait`: superseded, not admitted, binding removed, source removed, halted, or no final state before the timeout. |

## Source and licence

The task, the Action and the command-line tool live at [attestree/publish](https://github.com/attestree/publish), under the Apache License 2.0. Report a vulnerability to security@attestree.com rather than in a public issue.
