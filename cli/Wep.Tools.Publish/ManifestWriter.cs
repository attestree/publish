using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace Wep.Tools.Publish;

/// <summary>A file repository mode writes: its path in the repository and its text.</summary>
/// <param name="Path">The repository path, '/' between segments.</param>
/// <param name="Text">The file's text.</param>
internal sealed record RepositoryFile(string Path, string Text)
{
    /// <summary>
    /// Plan 2026-11-13-0645 U16: the build's SBOM, compared byte for byte and replaced when it differs. Every other file a
    /// run finds with other content is a changed build, which is a new version.
    /// </summary>
    public bool Replaceable { get; init; }
}

/// <summary>Plan 2026-11-13-0645 U16 (KTD13): the build's own SBOM as repository mode commits it.</summary>
/// <param name="FileName">Its fixed name beside the manifests: <c>{id}.sbom.cdx.json</c> or <c>{id}.sbom.spdx.json</c>.</param>
/// <param name="Text">Its bytes, as UTF-8 text, unchanged (line endings and any byte order mark included).</param>
/// <param name="Sha256">The SHA-256 of its bytes.</param>
/// <param name="Length">Its size in bytes.</param>
internal sealed record RepositorySbom(string FileName, string Text, string Sha256, long Length)
{
    /// <summary>
    /// Read <paramref name="path"/> for <paramref name="packageId"/>: the file must be UTF-8 JSON that names CycloneDX
    /// (<c>bomFormat</c>) or SPDX (<c>spdxVersion</c>, or SPDX 3's <c>@context</c>), because that is what names the file it is
    /// written as. Nothing else of it is checked here: the instance is the one authority on an SBOM.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="packageId">The package id.</param>
    /// <param name="sbom">The file to commit; null on an error.</param>
    /// <param name="error">Why it cannot be written; null when it can.</param>
    public static bool TryRead(string path, string packageId, out RepositorySbom? sbom, out string? error)
    {
        sbom = null;
        error = null;
        byte[] bytes = File.ReadAllBytes(path);
        string text;
        try
        {
            text = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            error = $"--sbom {path}: the file is not UTF-8 text, so it cannot be committed as it is. Nothing was written.";
            return false;
        }
        ReadOnlyMemory<byte> json = bytes.AsMemory();
        if (json.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            json = json[3..];
        }
        bool? cycloneDx;
        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);
            System.Text.Json.JsonElement root = document.RootElement;
            cycloneDx = root.ValueKind != System.Text.Json.JsonValueKind.Object ? null
                : root.TryGetProperty("bomFormat", out System.Text.Json.JsonElement format)
                    && format.ValueKind == System.Text.Json.JsonValueKind.String && format.GetString() == "CycloneDX" ? true
                : root.TryGetProperty("spdxVersion", out _) || root.TryGetProperty("@context", out _) ? false
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            cycloneDx = null;
        }
        if (cycloneDx is not { } isCycloneDx)
        {
            error = $"--sbom {path}: the file is not a CycloneDX or SPDX JSON document, so it has no name to be written under "
                + $"({packageId}.sbom.cdx.json or {packageId}.sbom.spdx.json). Nothing was written.";
            return false;
        }
        sbom = new RepositorySbom(
            isCycloneDx ? $"{packageId}.sbom.cdx.json" : $"{packageId}.sbom.spdx.json",
            text,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
            bytes.LongLength);
        return true;
    }
}

/// <summary>
/// #4830 U13 (KTD25): the version's manifest folder in winget-pkgs' layout, as repository mode writes it:
/// <c>&lt;manifests&gt;/&lt;letter&gt;/&lt;Publisher&gt;/&lt;Package&gt;/&lt;version&gt;/</c> with its version, installer and defaultLocale
/// files. Each installer is named by its release asset's API URL, the URL the release-asset spike settled
/// (<c>docs/research/2026-10-30-github-release-asset-download-spike.md</c>): it downloads under the source's GitHub token
/// sign-in, where the browser download URL of a private repository does not.
/// </summary>
internal static class ManifestWriter
{
    /// <summary>The manifest schema version a generated manifest declares.</summary>
    public const string ManifestVersion = "1.10.0";

    /// <summary>The locale a generated manifest declares.</summary>
    public const string DefaultLocale = "en-US";

    /// <summary>The package's folder: <c>&lt;manifests&gt;/&lt;letter&gt;/&lt;Publisher&gt;/&lt;Package&gt;</c>.</summary>
    public static string PackageFolder(string manifestsPath, string packageId) =>
        $"{manifestsPath}/{char.ToLowerInvariant(packageId[0])}/{string.Join('/', packageId.Split('.'))}";

    /// <summary>
    /// The version's manifest files from the run's flags, or from <paramref name="manifest"/> with each installer's URL and
    /// SHA-256 replaced by its release asset's; or null with why they cannot be written.
    /// </summary>
    /// <param name="options">The run.</param>
    /// <param name="assets">Each installer's release asset: its architecture's API URL and SHA-256.</param>
    /// <param name="manifest">The manifest files the run was given, or null to generate them.</param>
    /// <param name="error">Why the files cannot be written.</param>
    public static IReadOnlyList<RepositoryFile>? Write(
        PublishOptions options, IReadOnlyDictionary<string, (string Url, string Sha256)> assets, IReadOnlyList<ManifestFile>? manifest, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(assets);
        error = null;
        string folder = $"{PackageFolder(options.ManifestsPath, options.PackageId)}/{options.Version}";
        return manifest is null
            ? Generate(options, assets, folder)
            : Rewrite(options, assets, manifest, folder, out error);
    }

    private static List<RepositoryFile> Generate(PublishOptions options, IReadOnlyDictionary<string, (string Url, string Sha256)> assets, string folder)
    {
        string id = options.PackageId;
        string name = options.PackageName ?? id.Split('.')[^1];
        Dictionary<string, object> version = new()
        {
            ["PackageIdentifier"] = id,
            ["PackageVersion"] = options.Version,
            ["DefaultLocale"] = DefaultLocale,
            ["ManifestType"] = "version",
            ["ManifestVersion"] = ManifestVersion,
        };
        Dictionary<string, object> locale = new()
        {
            ["PackageIdentifier"] = id,
            ["PackageVersion"] = options.Version,
            ["PackageLocale"] = DefaultLocale,
            ["Publisher"] = options.Publisher!,
            ["PackageName"] = name,
            ["License"] = options.License,
            ["ShortDescription"] = options.ShortDescription ?? name,
            ["ManifestType"] = "defaultLocale",
            ["ManifestVersion"] = ManifestVersion,
        };
        Dictionary<string, object> installer = new()
        {
            ["PackageIdentifier"] = id,
            ["PackageVersion"] = options.Version,
            ["InstallerType"] = options.InstallerType!,
        };
        if (options.Scope is { } scope)
        {
            installer["Scope"] = scope;
        }
        if (options.SilentSwitches is { } silent)
        {
            installer["InstallerSwitches"] = new Dictionary<string, object> { ["Silent"] = silent, ["SilentWithProgress"] = silent };
        }
        installer["Installers"] = assets.OrderBy(a => a.Key, StringComparer.Ordinal)
            .Select(a => new Dictionary<string, object> { ["Architecture"] = a.Key, ["InstallerUrl"] = a.Value.Url, ["InstallerSha256"] = a.Value.Sha256.ToUpperInvariant() })
            .ToList();
        installer["ManifestType"] = "installer";
        installer["ManifestVersion"] = ManifestVersion;

        // A version such as 1.2 or a value such as true is quoted, so it reads back as the string it is.
        ISerializer yaml = new SerializerBuilder().WithQuotingNecessaryStrings().Build();
        string header = "# Written by attestree-publish (repository mode).\n";
        // The emitter writes Environment.NewLine. A file is committed with LF on every runner, so a re-run on Windows
        // compares equal to what a Linux run committed, and the manifests read the same in winget-pkgs' layout.
        return
        [
            new RepositoryFile($"{folder}/{id}.yaml", header + yaml.Serialize(version).ReplaceLineEndings("\n")),
            new RepositoryFile($"{folder}/{id}.installer.yaml", header + yaml.Serialize(installer).ReplaceLineEndings("\n")),
            new RepositoryFile($"{folder}/{id}.locale.{DefaultLocale}.yaml", header + yaml.Serialize(locale).ReplaceLineEndings("\n")),
        ];
    }

    private static List<RepositoryFile>? Rewrite(
        PublishOptions options,
        IReadOnlyDictionary<string, (string Url, string Sha256)> assets,
        IReadOnlyList<ManifestFile> manifest,
        string folder,
        out string? error)
    {
        error = null;
        if (manifest.Any(m => m.ManifestType == "singleton"))
        {
            error = "Repository mode writes winget-pkgs' multi-file layout: give --manifest a folder with the version, installer and "
                + "defaultLocale files, not a singleton.";
            return null;
        }
        foreach (string type in new[] { "version", "installer", "defaultLocale" })
        {
            if (manifest.Count(m => m.ManifestType == type) != 1)
            {
                error = $"--manifest must hold exactly one {type} file.";
                return null;
            }
        }
        List<YamlMappingNode> entries = [.. ManifestDocuments.InstallerEntries(manifest)];
        HashSet<string> listed = new(entries.Select(e => (ManifestDocuments.Scalar(e, "Architecture") ?? string.Empty).ToLowerInvariant()), StringComparer.Ordinal);
        if (!listed.SetEquals(assets.Keys))
        {
            error = $"The manifest's installers ({string.Join(", ", listed.Order(StringComparer.Ordinal))}) must be the run's "
                + $"({string.Join(", ", assets.Keys.Order(StringComparer.Ordinal))}): each is written with its release asset's URL.";
            return null;
        }
        foreach (YamlMappingNode entry in entries)
        {
            (string url, string sha256) = assets[ManifestDocuments.Scalar(entry, "Architecture")!.ToLowerInvariant()];
            entry.Children[new YamlScalarNode("InstallerUrl")] = new YamlScalarNode(url);
            entry.Children[new YamlScalarNode("InstallerSha256")] = new YamlScalarNode(sha256.ToUpperInvariant());
        }

        List<RepositoryFile> files = [];
        foreach (ManifestFile file in manifest)
        {
            string text = file.Text;
            if (file.ManifestType == "installer")
            {
                using StringWriter writer = new();
                new YamlStream(new YamlDocument(file.Root)).Save(writer, assignAnchors: false);
                string saved = writer.ToString().ReplaceLineEndings("\n");
                // The emitter closes the document with an explicit end marker; a manifest file carries none.
                saved = saved.EndsWith("\n...\n", StringComparison.Ordinal) ? saved[..^4] : saved;
                text = "# Written by attestree-publish (repository mode): each InstallerUrl is the installer's release asset.\n" + saved;
            }
            string fileName = file.ManifestType switch
            {
                "version" => $"{options.PackageId}.yaml",
                "installer" => $"{options.PackageId}.installer.yaml",
                "defaultLocale" or "locale" => $"{options.PackageId}.locale.{ManifestDocuments.Scalar(file.Root, "PackageLocale") ?? DefaultLocale}.yaml",
                _ => file.Name,
            };
            files.Add(new RepositoryFile($"{folder}/{fileName}", text));
        }
        return files;
    }
}

