# attestree/publish

Publish your own Windows apps to [Attestree](https://attestree.com) from CI.

> **Status: in development.** Nothing here is ready to use yet. This repository will hold the GitHub Action and the downloads of the `attestree-publish` command-line tool. Watch it, or check back for the first `v1` release.

## What it will do

- **Publish a new version of your app.** A pipeline sends the new version and its installers straight to your Attestree instance. Attestree then test-installs it in isolation and attests it, as it does every other app.
- **No stored secret.** The pipeline signs in with the short-lived OpenID Connect token that GitHub Actions or Azure Pipelines issues for that run. You allow one repository and workflow on your instance, and only that one can publish.
- **Works with Attestree Community Edition and dedicated instances.** The instance must be reachable from your CI runner.

Planned shape, subject to change:

```yaml
permissions:
  id-token: write
  contents: read

steps:
  - uses: attestree/publish@v1
    with:
      instance: https://attestree.example.com
      package-id: Contoso.Tool
      version: 1.2.0
      installers: dist/*.msi
```

## Security

Report a vulnerability to security@attestree.com. Please don't open a public issue for it.

## Licence

[Apache License 2.0](LICENSE).
