using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wep.Tools.Publish;

/// <summary>
/// #4830 U13 (KTD26): the package's version index, <c>versions.json</c> beside its version folders:
/// <c>{"schemaVersion":1,"versions":["1.2.0","1.1.0"]}</c>, version strings only. Repository mode rewrites it on every
/// publish, adding the version it wrote and keeping the others, so a copy of the folder on a host that cannot list a
/// directory stays listable.
/// </summary>
internal static class VersionIndex
{
    /// <summary>The index's file name.</summary>
    public const string FileName = "versions.json";

    /// <summary>
    /// The index with <paramref name="version"/> added, newest first, as the file's text; or null with why the existing
    /// index cannot be kept (a version the run would drop).
    /// </summary>
    /// <param name="existing">The index the repository holds now, or null when it holds none.</param>
    /// <param name="version">The version this run writes.</param>
    /// <param name="error">Why <paramref name="existing"/> cannot be read.</param>
    public static string? With(string? existing, string version, out string? error)
    {
        error = null;
        List<string> versions = [];
        if (existing is not null)
        {
            try
            {
                JsonNode? root = JsonNode.Parse(existing);
                if (root is not JsonObject index
                    || index["schemaVersion"] is not JsonValue schema || !schema.TryGetValue(out int schemaVersion) || schemaVersion != 1
                    || index["versions"] is not JsonArray listed)
                {
                    error = $"The repository's {FileName} is not a version index of schema version 1; fix or delete it, then publish again.";
                    return null;
                }
                foreach (JsonNode? entry in listed)
                {
                    if (entry is not JsonValue value || !value.TryGetValue(out string? listedVersion) || !CommandLine.IsValidVersion(listedVersion))
                    {
                        error = $"The repository's {FileName} lists an entry that is not a winget version; fix or delete it, then publish again.";
                        return null;
                    }
                    versions.Add(listedVersion);
                }
            }
            catch (JsonException)
            {
                error = $"The repository's {FileName} is not JSON; fix or delete it, then publish again.";
                return null;
            }
        }
        if (!versions.Contains(version, StringComparer.Ordinal))
        {
            versions.Add(version);
        }
        versions = [.. versions.Distinct(StringComparer.Ordinal)];
        versions.Sort(CompareDescending);

        StringBuilder text = new();
        text.Append("{\n  \"schemaVersion\": 1,\n  \"versions\": [");
        for (int i = 0; i < versions.Count; i++)
        {
            text.Append(i == 0 ? "\n    " : ",\n    ").Append(JsonSerializer.Serialize(versions[i]));
        }
        text.Append(versions.Count == 0 ? "]\n}\n" : "\n  ]\n}\n");
        return text.ToString();
    }

    /// <summary>
    /// Newest first, as the platform orders a listing (<c>WingetVersionLister.CompareVersionsDescending</c>): dotted numeric
    /// segments compared as numbers, a numeric segment above a non-numeric one, and a shorter equal prefix older.
    /// </summary>
    internal static int CompareDescending(string a, string b)
    {
        string[] pa = a.Split('.');
        string[] pb = b.Split('.');
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            string sa = i < pa.Length ? pa[i] : string.Empty;
            string sb = i < pb.Length ? pb[i] : string.Empty;
            bool aNum = long.TryParse(sa, out long na);
            bool bNum = long.TryParse(sb, out long nb);
            int cmp = aNum && bNum ? na.CompareTo(nb) : aNum ? 1 : bNum ? -1 : string.CompareOrdinal(sa, sb);
            if (cmp != 0)
            {
                return -cmp;
            }
        }
        return 0;
    }
}
