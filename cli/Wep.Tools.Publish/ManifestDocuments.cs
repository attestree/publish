using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Wep.Tools.Publish;

/// <summary>One manifest file: its name, its text, and its parsed root.</summary>
/// <param name="Name">The file name.</param>
/// <param name="Text">The file's text, sent as one YAML document (KTD24).</param>
/// <param name="Root">The parsed root mapping.</param>
internal sealed record ManifestFile(string Name, string Text, YamlMappingNode Root)
{
    /// <summary>The document's <c>ManifestType</c>: singleton, version, installer, defaultLocale or locale.</summary>
    public string? ManifestType => ManifestDocuments.Scalar(Root, "ManifestType");
}

/// <summary>
/// #4830 U13 (KTD24, R36): reads <c>--manifest</c>, a singleton file or a folder of manifest files, one YAML document per
/// file, and refuses before anything is sent when the manifest disagrees with the arguments: another id or version, or
/// two installers for one architecture. The instance checks everything else, and lists back the fields it ignored.
/// </summary>
internal static class ManifestDocuments
{
    /// <summary>The largest manifest file read: the publish body holding all of them is capped at a megabyte.</summary>
    public const long MaxFileBytes = 256 * 1024;

    /// <summary>The manifest files at <paramref name="path"/>, by name, or why they cannot be read.</summary>
    /// <param name="path">A manifest file, or a folder whose <c>.yaml</c> and <c>.yml</c> files are the manifest.</param>
    /// <param name="files">The files; null on an error.</param>
    /// <param name="error">Why they cannot be read; null when they can.</param>
    public static bool TryRead(string path, out IReadOnlyList<ManifestFile>? files, out string? error)
    {
        files = null;
        error = null;
        string[] names;
        if (Directory.Exists(path))
        {
            names = [.. Directory.EnumerateFiles(path)
                .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)];
            if (names.Length == 0)
            {
                error = $"--manifest {path} holds no .yaml file.";
                return false;
            }
        }
        else if (File.Exists(path))
        {
            names = [path];
        }
        else
        {
            error = $"--manifest {path} is neither a file nor a folder.";
            return false;
        }

        List<ManifestFile> read = [];
        foreach (string name in names)
        {
            FileInfo info = new(name);
            if (info.Length > MaxFileBytes)
            {
                error = $"{info.Name} is larger than a manifest file can be ({MaxFileBytes / 1024} KiB).";
                return false;
            }
            string text = File.ReadAllText(name);
            YamlStream stream = [];
            try
            {
                stream.Load(new StringReader(text));
            }
            catch (YamlException ex)
            {
                error = $"{info.Name} is not YAML (line {ex.Start.Line}).";
                return false;
            }
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                error = $"{info.Name} must hold exactly one YAML document, a mapping.";
                return false;
            }
            read.Add(new ManifestFile(info.Name, text, root));
        }
        files = read;
        return true;
    }

    /// <summary>
    /// Why <paramref name="files"/> cannot accompany a publish of <paramref name="packageId"/>@<paramref name="version"/>,
    /// or null when they can: each document names that id and version, and no architecture has two installers.
    /// </summary>
    public static string? Check(IReadOnlyList<ManifestFile> files, string packageId, string version)
    {
        ArgumentNullException.ThrowIfNull(files);
        foreach (ManifestFile file in files)
        {
            string? id = Scalar(file.Root, "PackageIdentifier");
            if (!string.Equals(id, packageId, StringComparison.OrdinalIgnoreCase))
            {
                return $"{file.Name} names package '{id}', not --package-id {packageId}.";
            }
            string? declared = Scalar(file.Root, "PackageVersion");
            if (!string.Equals(declared, version, StringComparison.Ordinal))
            {
                return $"{file.Name} names version '{declared}', not --version {version}.";
            }
        }
        List<string> architectures = [.. InstallerEntries(files).Select(e => (Scalar(e, "Architecture") ?? string.Empty).ToLowerInvariant())];
        if (architectures.GroupBy(a => a, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            return $"The manifest lists two installers for {twice.Key}; a version has one installer per architecture.";
        }
        return null;
    }

    /// <summary>The installer entries of the installer or singleton document.</summary>
    public static IEnumerable<YamlMappingNode> InstallerEntries(IReadOnlyList<ManifestFile> files) =>
        files.Where(f => f.ManifestType is "installer" or "singleton")
            .SelectMany(f => f.Root.Children.TryGetValue(new YamlScalarNode("Installers"), out YamlNode? node) && node is YamlSequenceNode list
                ? list.Children.OfType<YamlMappingNode>()
                : []);

    /// <summary>The scalar value under <paramref name="key"/>, or null.</summary>
    public static string? Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value) && value is YamlScalarNode scalar ? scalar.Value : null;
}
