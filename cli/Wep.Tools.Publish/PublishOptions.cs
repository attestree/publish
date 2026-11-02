namespace Wep.Tools.Publish;

/// <summary>One installer a run names: its architecture, and either a file to send or an https URL with its SHA-256.</summary>
/// <param name="Architecture">x86, x64, arm, arm64 or neutral; one installer per architecture (KTD9).</param>
/// <param name="Path">The installer file, for an upload or a release asset; null for a URL.</param>
/// <param name="Url">An https URL the instance fetches the installer from (AE7); null for a file.</param>
/// <param name="Sha256">For a URL, the SHA-256 the bytes hash to; a file's is computed.</param>
internal sealed record InstallerArgument(string Architecture, string? Path, string? Url, string? Sha256);

/// <summary>#4830 U13: what one run of <c>attestree-publish</c> was asked to do, after <see cref="CommandLine"/> checked it.</summary>
internal sealed record PublishOptions
{
    /// <summary>The winget package id, inside one of the instance's prefixes.</summary>
    public required string PackageId { get; init; }

    /// <summary>The version.</summary>
    public required string Version { get; init; }

    /// <summary>Every installer of the version, one per architecture.</summary>
    public required IReadOnlyList<InstallerArgument> Installers { get; init; }

    /// <summary>KTD24: a singleton manifest file or a folder of manifest files; null without one.</summary>
    public string? ManifestPath { get; init; }

    /// <summary>Push mode: the instance's base URL. Null in repository mode.</summary>
    public Uri? Instance { get; init; }

    /// <summary>Push mode on GitHub Actions: the audience the binding screen shows, <c>attestree:&lt;instance-id&gt;</c>.</summary>
    public string? Audience { get; init; }

    /// <summary>Push mode on Azure Pipelines: the service connection whose federated token the publish carries.</summary>
    public string? ServiceConnectionId { get; init; }

    /// <summary>Push mode: wait on the version's status until a terminal state (KTD9).</summary>
    public bool Wait { get; init; }

    /// <summary>How long a wait lasts before it ends with <see cref="ExitCodes.WaitTimedOut"/>.</summary>
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>Repository mode (KTD25): <c>owner/repo</c>. Null in push mode.</summary>
    public string? Repository { get; init; }

    /// <summary>Repository mode: the branch the manifests are committed to.</summary>
    public string Branch { get; init; } = "main";

    /// <summary>Repository mode: the folder in the repository that holds winget-pkgs' layout.</summary>
    public string ManifestsPath { get; init; } = "manifests";

    /// <summary>Repository mode: the release tag the installers are attached to; null for <c>&lt;id&gt;-&lt;version&gt;</c>.</summary>
    public string? ReleaseTag { get; init; }

    /// <summary>Repository mode without a manifest: the publisher's name.</summary>
    public string? Publisher { get; init; }

    /// <summary>Repository mode without a manifest: the app's name; null for the package id's last segment.</summary>
    public string? PackageName { get; init; }

    /// <summary>Repository mode without a manifest: the winget installer type (msi, exe, inno, nullsoft, wix, burn, msix…).</summary>
    public string? InstallerType { get; init; }

    /// <summary>Repository mode without a manifest: machine or user; null to leave it out.</summary>
    public string? Scope { get; init; }

    /// <summary>Repository mode without a manifest: the silent install switches; null to leave them out.</summary>
    public string? SilentSwitches { get; init; }

    /// <summary>Repository mode without a manifest: the licence the locale manifest names.</summary>
    public string License { get; init; } = "Proprietary";

    /// <summary>Repository mode without a manifest: the locale manifest's short description; null for the app's name.</summary>
    public string? ShortDescription { get; init; }

    /// <summary>Log each request's method, host and path, and each answer's status. Never a token, in any mode.</summary>
    public bool Verbose { get; init; }

    /// <summary>True in repository mode.</summary>
    public bool ToRepository => Repository is not null;
}
