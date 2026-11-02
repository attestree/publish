using System.Globalization;

namespace Wep.Tools.Publish;

/// <summary>
/// #4830 U13: the <c>attestree-publish</c> argv parser. It checks everything it can before anything is read or sent:
/// the id and version shapes the instance takes, one installer per architecture, and exactly one mode.
/// </summary>
internal static class CommandLine
{
    /// <summary>The architectures the publish API takes (KTD9).</summary>
    public static readonly IReadOnlySet<string> Architectures =
        new HashSet<string>(StringComparer.Ordinal) { "x86", "x64", "arm", "arm64", "neutral" };

    /// <summary>The usage text.</summary>
    public const string Usage = """
        attestree-publish: publish a version of your own app to Attestree from CI.

        Push it to the instance (it must be reachable from the runner):
          attestree-publish --instance <https-url> --package-id <id> --version <version>
                            --installer <arch>=<file> [--installer <arch>=<file>]
                            [--installer-url <arch>=<https-url> --sha256 <arch>=<hex>]
                            [--manifest <file-or-folder>]
                            [--audience attestree:<instance-id>]      (GitHub Actions)
                            [--service-connection <id>]               (Azure Pipelines)
                            [--wait [--wait-timeout <minutes>]]

        Write it into your own GitHub repository, which the instance reads as a source:
          attestree-publish --to-repository <owner/repo> --package-id <id> --version <version>
                            --installer <arch>=<file> [--installer <arch>=<file>]
                            [--manifest <folder>] | --publisher <name> --installer-type <type>
                              [--package-name <name>] [--scope machine|user] [--silent <switches>]
                              [--license <licence>] [--short-description <text>]
                            [--branch <branch>] [--manifests-path <path>] [--release-tag <tag>]

        --verbose logs each request's method, host and path. No token is ever printed.
        Exit codes: 0 accepted (or attested), 1 usage, 2 refused, 3 duplicate, 4 transient;
        with --wait: 10 superseded, 11 not admitted, 12 binding removed, 13 source removed, 14 halted, 15 timed out.
        """;

    /// <summary>Parse <paramref name="args"/>: the options, or why they cannot be used.</summary>
    /// <param name="args">The arguments.</param>
    /// <param name="options">The checked options; null on an error.</param>
    /// <param name="error">Why the arguments cannot be used; null when they can, or when help was asked for.</param>
    /// <returns>False when the run should stop: an error, or <c>--help</c>.</returns>
    public static bool TryParse(string[] args, out PublishOptions? options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = null;
        error = null;
        Dictionary<string, string> single = new(StringComparer.Ordinal);
        Dictionary<string, string> paths = new(StringComparer.Ordinal);
        Dictionary<string, string> urls = new(StringComparer.Ordinal);
        Dictionary<string, string> hashes = new(StringComparer.Ordinal);
        bool wait = false;
        bool verbose = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string name = arg;
            string? inline = null;
            int equals = arg.IndexOf('=', StringComparison.Ordinal);
            if (arg.StartsWith("--", StringComparison.Ordinal) && equals > 2)
            {
                name = arg[..equals];
                inline = arg[(equals + 1)..];
            }
            switch (name)
            {
                case "--help" or "-h":
                    return false;
                case "--wait":
                    wait = true;
                    continue;
                case "--verbose":
                    verbose = true;
                    continue;
            }
            if (!name.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"'{arg}' is not an option.";
                return false;
            }
            string? value = inline;
            if (value is null)
            {
                if (i + 1 >= args.Length)
                {
                    error = $"{name} needs a value.";
                    return false;
                }
                value = args[++i];
            }
            switch (name)
            {
                case "--installer":
                    if (!TryArchitectureValue(name, value, paths, out error))
                    {
                        return false;
                    }
                    break;
                case "--installer-url":
                    if (!TryArchitectureValue(name, value, urls, out error))
                    {
                        return false;
                    }
                    break;
                case "--sha256":
                    if (!TryArchitectureValue(name, value, hashes, out error))
                    {
                        return false;
                    }
                    break;
                case "--instance" or "--package-id" or "--version" or "--manifest" or "--audience" or "--service-connection"
                    or "--wait-timeout" or "--to-repository" or "--branch" or "--manifests-path" or "--release-tag" or "--publisher"
                    or "--package-name" or "--installer-type" or "--scope" or "--silent" or "--license" or "--short-description":
                    if (!single.TryAdd(name, value))
                    {
                        error = $"{name} is given twice.";
                        return false;
                    }
                    break;
                default:
                    error = $"'{name}' is not an option.";
                    return false;
            }
        }

        string? packageId = single.GetValueOrDefault("--package-id");
        string? version = single.GetValueOrDefault("--version");
        if (packageId is null || !IsValidPackageId(packageId))
        {
            error = "--package-id must be a winget package id: two or more dot-separated segments of letters, digits, '+', '_' or '-'.";
            return false;
        }
        if (version is null || !IsValidVersion(version))
        {
            error = "--version must be a winget version: letters, digits and . - _ + ~, with at least one digit.";
            return false;
        }

        List<InstallerArgument> installers = [];
        foreach (string architecture in paths.Keys.Concat(urls.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            bool hasPath = paths.TryGetValue(architecture, out string? path);
            bool hasUrl = urls.TryGetValue(architecture, out string? url);
            if (hasPath && hasUrl)
            {
                error = $"{architecture} names both a file and a URL; one installer per architecture.";
                return false;
            }
            if (hasUrl)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) || parsed.Scheme != Uri.UriSchemeHttps || parsed.UserInfo.Length > 0)
                {
                    error = $"--installer-url {architecture}= must be an absolute https URL with no user name or password in it.";
                    return false;
                }
                if (!hashes.TryGetValue(architecture, out string? hash) || !IsSha256(hash))
                {
                    error = $"--installer-url {architecture}= needs --sha256 {architecture}=<64 hex digits>: the instance checks the bytes it fetches against it.";
                    return false;
                }
                installers.Add(new InstallerArgument(architecture, null, url, hash.ToLowerInvariant()));
            }
            else
            {
                installers.Add(new InstallerArgument(architecture, path, null, null));
            }
        }
        if (installers.Count == 0)
        {
            error = "Name at least one installer: --installer <arch>=<file>.";
            return false;
        }
        if (hashes.Keys.Except(urls.Keys, StringComparer.Ordinal).FirstOrDefault() is { } stray)
        {
            error = $"--sha256 {stray}= names no --installer-url; a file's SHA-256 is computed.";
            return false;
        }

        string? instance = single.GetValueOrDefault("--instance");
        string? repository = single.GetValueOrDefault("--to-repository");
        if ((instance is null) == (repository is null))
        {
            error = "Choose one mode: --instance <https-url> to push to the instance, or --to-repository <owner/repo> to write into your repository.";
            return false;
        }

        TimeSpan waitTimeout = TimeSpan.FromMinutes(60);
        if (single.TryGetValue("--wait-timeout", out string? minutes))
        {
            if (!int.TryParse(minutes, NumberStyles.None, CultureInfo.InvariantCulture, out int m) || m < 1 || m > 24 * 60)
            {
                error = "--wait-timeout is a whole number of minutes, from 1 to 1440.";
                return false;
            }
            waitTimeout = TimeSpan.FromMinutes(m);
        }

        if (instance is not null)
        {
            if (!Uri.TryCreate(instance, UriKind.Absolute, out Uri? instanceUri) || instanceUri.Scheme != Uri.UriSchemeHttps
                || instanceUri.UserInfo.Length > 0 || instanceUri.Query.Length > 0)
            {
                error = "--instance must be the instance's https base URL, with no user name, password or query.";
                return false;
            }
            if (RepositoryOnly(single) is { } repositoryOption)
            {
                error = $"{repositoryOption} applies only with --to-repository.";
                return false;
            }
            options = new PublishOptions
            {
                PackageId = packageId,
                Version = version,
                Installers = installers,
                ManifestPath = single.GetValueOrDefault("--manifest"),
                Instance = instanceUri,
                Audience = single.GetValueOrDefault("--audience"),
                ServiceConnectionId = single.GetValueOrDefault("--service-connection"),
                Wait = wait,
                WaitTimeout = waitTimeout,
                Verbose = verbose,
            };
            return true;
        }

        if (wait || single.ContainsKey("--wait-timeout") || single.ContainsKey("--audience") || single.ContainsKey("--service-connection"))
        {
            error = "--to-repository calls nothing on the instance: --wait, --audience and --service-connection apply only with --instance.";
            return false;
        }
        if (!IsRepository(repository!))
        {
            error = "--to-repository is <owner>/<repo>.";
            return false;
        }
        if (installers.Any(i => i.Url is not null))
        {
            error = "--to-repository attaches each installer file to a release; --installer-url applies only with --instance.";
            return false;
        }
        string? manifest = single.GetValueOrDefault("--manifest");
        if (manifest is null && (single.GetValueOrDefault("--publisher") is null || single.GetValueOrDefault("--installer-type") is null))
        {
            error = "--to-repository writes the version's manifests: give --manifest <folder>, or --publisher and --installer-type.";
            return false;
        }
        if (single.GetValueOrDefault("--scope") is { } scope && scope is not ("machine" or "user"))
        {
            error = "--scope is machine or user.";
            return false;
        }
        string manifestsPath = single.GetValueOrDefault("--manifests-path") ?? "manifests";
        if (manifestsPath.Split('/').Any(s => s is "." or ".." || s.Contains('\\', StringComparison.Ordinal)))
        {
            error = "--manifests-path is a folder path in the repository, with '/' between its segments.";
            return false;
        }
        options = new PublishOptions
        {
            PackageId = packageId,
            Version = version,
            Installers = installers,
            ManifestPath = manifest,
            Repository = repository,
            Branch = single.GetValueOrDefault("--branch") ?? "main",
            ManifestsPath = manifestsPath.Trim('/'),
            ReleaseTag = single.GetValueOrDefault("--release-tag"),
            Publisher = single.GetValueOrDefault("--publisher"),
            PackageName = single.GetValueOrDefault("--package-name"),
            InstallerType = single.GetValueOrDefault("--installer-type"),
            Scope = single.GetValueOrDefault("--scope"),
            SilentSwitches = single.GetValueOrDefault("--silent"),
            License = single.GetValueOrDefault("--license") ?? "Proprietary",
            ShortDescription = single.GetValueOrDefault("--short-description"),
            Verbose = verbose,
        };
        return true;
    }

    /// <summary>A winget package id: two or more dot-separated segments of letters, digits, '+', '_' or '-'.</summary>
    public static bool IsValidPackageId(string packageId)
    {
        string[] parts = packageId.Split('.');
        return parts.Length >= 2 && parts.All(p => p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '_' or '-'));
    }

    /// <summary>A winget version: letters, digits and <c>. - _ + ~</c>, with at least one digit.</summary>
    public static bool IsValidVersion(string version) =>
        version.Length > 0
        && version.Any(char.IsAsciiDigit)
        && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+' or '~');

    /// <summary>64 hex digits.</summary>
    public static bool IsSha256(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);

    private static bool IsRepository(string value)
    {
        string[] parts = value.Split('/');
        return parts.Length == 2
            && parts.All(p => p.Length > 0 && p is not ("." or "..") && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));
    }

    private static readonly string[] RepositoryOptions =
        ["--branch", "--manifests-path", "--release-tag", "--publisher", "--package-name", "--installer-type", "--scope", "--silent", "--license", "--short-description"];

    private static string? RepositoryOnly(Dictionary<string, string> single) => RepositoryOptions.FirstOrDefault(single.ContainsKey);

    private static bool TryArchitectureValue(string option, string value, Dictionary<string, string> into, out string? error)
    {
        error = null;
        int equals = value.IndexOf('=', StringComparison.Ordinal);
        string architecture = equals > 0 ? value[..equals].Trim().ToLowerInvariant() : string.Empty;
        string rest = equals > 0 ? value[(equals + 1)..].Trim() : string.Empty;
        if (!Architectures.Contains(architecture) || rest.Length == 0)
        {
            error = $"{option} takes <arch>=<value>, where <arch> is one of {string.Join(", ", Architectures)}.";
            return false;
        }
        if (!into.TryAdd(architecture, rest))
        {
            error = $"{option} names {architecture} twice; one installer per architecture.";
            return false;
        }
        return true;
    }
}
