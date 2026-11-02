using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wep.Tools.Publish;

/// <summary>
/// #4830 U13 (KTD25, KTD26, R37): repository mode, the pull bridge. With the job's own GitHub token it attaches each
/// installer to a release of the customer's repository, then commits the version's manifest folder and the package's
/// <c>versions.json</c> in one commit. The instance, which reads that repository as a folder source signed in with a
/// GitHub token, finds the version on its next freshness check: no inbound route, no click.
/// </summary>
/// <remarks>
/// <para>
/// It never asks for an OIDC token and never calls the instance: what the instance trusts is the repository's write
/// access and the admin's consent to read it (KTD25).
/// </para>
/// <para>
/// The assets go up before the manifests, so the instance never lists a version whose installer is missing. A re-run of
/// the same publish reuses the release's assets and finds its manifests already written; a changed build under the same
/// version is refused as a duplicate.
/// </para>
/// </remarks>
internal sealed class RepositoryPublisher(
    PublishOptions options,
    IReadOnlyList<InstallerFile> installers,
    IReadOnlyList<ManifestFile>? manifest,
    HttpClient http,
    Func<string, string?> environment,
    Output output,
    Func<TimeSpan, CancellationToken, Task> delay)
{
    private const int CommitAttempts = 3;

    private readonly PublishOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly Output _output = output ?? throw new ArgumentNullException(nameof(output));
    private string _apiBase = "https://api.github.com";
    private string _token = string.Empty;

    /// <summary>Attach, write, commit. Returns the run's exit code.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        string? token = environment("GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            _output.Error("Repository mode writes with the job's GitHub token: pass it as GITHUB_TOKEN (env: GITHUB_TOKEN: ${{ github.token }}), "
                + "with 'permissions: contents: write'.");
            return ExitCodes.Usage;
        }
        _token = token;
        _output.AddSecret(token);
        _apiBase = (environment("GITHUB_API_URL") is { Length: > 0 } api ? api : _apiBase).TrimEnd('/');
        string repo = _options.Repository!;
        string tag = _options.ReleaseTag ?? $"{_options.PackageId}-{_options.Version}";

        Reply release = await GetOrCreateReleaseAsync(repo, tag, ct).ConfigureAwait(false);
        if (release.ExitCode is { } releaseFailed)
        {
            return releaseFailed;
        }
        Dictionary<string, (string Url, string Sha256)> assets = new(StringComparer.Ordinal);
        foreach (InstallerFile installer in installers)
        {
            (string? url, int? failed) = await AttachAsync(release.Json!, installer, ct).ConfigureAwait(false);
            if (failed is { } attachFailed)
            {
                return attachFailed;
            }
            assets[installer.Architecture] = (url!, installer.Sha256);
        }

        IReadOnlyList<RepositoryFile>? files = ManifestWriter.Write(_options, assets, manifest, out string? writeError);
        if (files is null)
        {
            _output.Error(writeError!);
            return ExitCodes.Usage;
        }
        return await CommitAsync(repo, files, tag, ct).ConfigureAwait(false);
    }

    private async Task<Reply> GetOrCreateReleaseAsync(string repo, string tag, CancellationToken ct)
    {
        Reply found = await SendAsync(HttpMethod.Get, $"/repos/{repo}/releases/tags/{Uri.EscapeDataString(tag)}", null, ct, allowNotFound: true)
            .ConfigureAwait(false);
        if (found.ExitCode is not null || found.Status != HttpStatusCode.NotFound)
        {
            return found;
        }
        JsonObject create = new()
        {
            ["tag_name"] = tag,
            ["target_commitish"] = _options.Branch,
            ["name"] = $"{_options.PackageId} {_options.Version}",
            ["body"] = "Installers of this version, published by attestree-publish. Attestree reads them through the manifests in this repository.",
            ["draft"] = false,
            ["prerelease"] = false,
        };
        Reply created = await SendAsync(HttpMethod.Post, $"/repos/{repo}/releases", create, ct).ConfigureAwait(false);
        if (created.ExitCode is null)
        {
            _output.Info($"Created release {tag} in {repo}.");
        }
        return created;
    }

    /// <summary>The installer's asset API URL on the release (uploading it unless the same bytes are there), or the exit code.</summary>
    private async Task<(string? Url, int? ExitCode)> AttachAsync(JsonNode release, InstallerFile installer, CancellationToken ct)
    {
        string name = AssetName(installer);
        if (release["assets"] is JsonArray existing
            && existing.OfType<JsonObject>().FirstOrDefault(a => string.Equals((string?)a["name"], name, StringComparison.Ordinal)) is { } asset)
        {
            string? digest = (string?)asset["digest"];
            long size = asset["size"]?.GetValue<long>() ?? -1;
            bool same = digest is not null
                ? string.Equals(digest, "sha256:" + installer.Sha256, StringComparison.OrdinalIgnoreCase)
                : size == installer.Length;
            if (!same)
            {
                _output.Error($"The release already holds a different {name}. A changed build is a new version.");
                return (null, ExitCodes.Duplicate);
            }
            _output.Info($"The release already holds {name}; reusing it.");
            return ((string?)asset["url"], null);
        }

        string uploadUrl = ((string?)release["upload_url"] ?? string.Empty).Split('{')[0];
        if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out Uri? upload))
        {
            _output.Error("GitHub's release answer named no upload URL.");
            return (null, ExitCodes.Transient);
        }
        FileStream stream = File.OpenRead(installer.Path!);
        await using (stream.ConfigureAwait(false))
        {
            StreamContent content = new(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = installer.Length;
            Reply uploaded = await SendAsync(HttpMethod.Post, new Uri(upload + "?name=" + Uri.EscapeDataString(name)).ToString(), content, ct)
                .ConfigureAwait(false);
            if (uploaded.ExitCode is { } failed)
            {
                return (null, failed);
            }
            _output.Info($"Attached the {installer.Architecture} installer as {name} ({installer.Length} bytes, SHA-256 {installer.Sha256}).");
            return ((string?)uploaded.Json!["url"], null);
        }
    }

    private string AssetName(InstallerFile installer)
    {
        string fileName = Path.GetFileName(installer.Path!);
        bool shared = installers.Count(i => string.Equals(Path.GetFileName(i.Path!), fileName, StringComparison.OrdinalIgnoreCase)) > 1;
        return shared ? $"{installer.Architecture}-{fileName}" : fileName;
    }

    private async Task<int> CommitAsync(string repo, IReadOnlyList<RepositoryFile> files, string tag, CancellationToken ct)
    {
        string indexPath = $"{ManifestWriter.PackageFolder(_options.ManifestsPath, _options.PackageId)}/{VersionIndex.FileName}";
        string versionFolder = files[0].Path[..files[0].Path.LastIndexOf('/')];
        for (int attempt = 1; ; attempt++)
        {
            Reply reference = await SendAsync(HttpMethod.Get, $"/repos/{repo}/git/ref/heads/{_options.Branch}", null, ct).ConfigureAwait(false);
            if (reference.ExitCode is { } refFailed)
            {
                return refFailed;
            }
            string head = (string)reference.Json!["object"]!["sha"]!;

            List<RepositoryFile> changed = [];
            foreach (RepositoryFile file in files)
            {
                (string? current, int? failed) = await ReadFileAsync(repo, file.Path, head, ct).ConfigureAwait(false);
                if (failed is { } readFailed)
                {
                    return readFailed;
                }
                if (current is null)
                {
                    changed.Add(file);
                }
                else if (!string.Equals(current.ReplaceLineEndings("\n"), file.Text, StringComparison.Ordinal))
                {
                    _output.Error($"{repo} already holds {file.Path} with other content. A changed build is a new version.");
                    return ExitCodes.Duplicate;
                }
            }
            (string? index, int? indexFailed) = await ReadFileAsync(repo, indexPath, head, ct).ConfigureAwait(false);
            if (indexFailed is { } failedIndex)
            {
                return failedIndex;
            }
            string? newIndex = VersionIndex.With(index, _options.Version, out string? indexError);
            if (newIndex is null)
            {
                _output.Error(indexError!);
                return ExitCodes.Usage;
            }
            if (!string.Equals(index, newIndex, StringComparison.Ordinal))
            {
                changed.Add(new RepositoryFile(indexPath, newIndex));
            }
            if (changed.Count == 0)
            {
                _output.Info($"{repo}@{_options.Branch} already holds {_options.PackageId} {_options.Version} at {versionFolder}/; nothing to commit.");
                return ExitCodes.Success;
            }

            Reply commit = await SendAsync(HttpMethod.Get, $"/repos/{repo}/git/commits/{head}", null, ct).ConfigureAwait(false);
            if (commit.ExitCode is { } commitReadFailed)
            {
                return commitReadFailed;
            }
            JsonArray entries = [];
            foreach (RepositoryFile file in changed)
            {
                entries.Add(new JsonObject { ["path"] = file.Path, ["mode"] = "100644", ["type"] = "blob", ["content"] = file.Text });
            }
            Reply tree = await SendAsync(
                HttpMethod.Post, $"/repos/{repo}/git/trees", new JsonObject { ["base_tree"] = (string)commit.Json!["tree"]!["sha"]!, ["tree"] = entries }, ct)
                .ConfigureAwait(false);
            if (tree.ExitCode is { } treeFailed)
            {
                return treeFailed;
            }
            Reply created = await SendAsync(
                HttpMethod.Post,
                $"/repos/{repo}/git/commits",
                new JsonObject
                {
                    ["message"] = $"Publish {_options.PackageId} {_options.Version}\n\nWritten by attestree-publish: the manifests and versions.json; "
                        + $"the installers are on release {tag}.",
                    ["tree"] = (string)tree.Json!["sha"]!,
                    ["parents"] = new JsonArray(head),
                },
                ct).ConfigureAwait(false);
            if (created.ExitCode is { } createFailed)
            {
                return createFailed;
            }
            string sha = (string)created.Json!["sha"]!;
            Reply moved = await SendAsync(
                new HttpMethod("PATCH"), $"/repos/{repo}/git/refs/heads/{_options.Branch}", new JsonObject { ["sha"] = sha, ["force"] = false }, ct,
                allowUnprocessable: attempt < CommitAttempts).ConfigureAwait(false);
            if (moved.ExitCode is { } moveFailed)
            {
                return moveFailed;
            }
            if (moved.Status == HttpStatusCode.UnprocessableEntity)
            {
                // The branch moved while this run wrote: write again on its new head.
                _output.Debug($"{_options.Branch} moved; writing again on its new head.");
                continue;
            }
            _output.Info($"Committed {_options.PackageId} {_options.Version} to {repo}@{_options.Branch} ({sha[..Math.Min(12, sha.Length)]}): "
                + $"{versionFolder}/ and {indexPath}. The instance reads it on its next freshness check.");
            return ExitCodes.Success;
        }
    }

    /// <summary>The file at <paramref name="path"/> on <paramref name="commit"/>, null when there is none, or the exit code.</summary>
    private async Task<(string? Text, int? ExitCode)> ReadFileAsync(string repo, string path, string commit, CancellationToken ct)
    {
        Reply read = await SendAsync(
            HttpMethod.Get, $"/repos/{repo}/contents/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}?ref={commit}", null, ct,
            allowNotFound: true).ConfigureAwait(false);
        if (read.ExitCode is { } failed)
        {
            return (null, failed);
        }
        if (read.Status == HttpStatusCode.NotFound)
        {
            return (null, null);
        }
        if (read.Json is not JsonObject file || (string?)file["encoding"] != "base64" || (string?)file["content"] is not { } content)
        {
            _output.Error($"{repo} holds {path}, but not as a file this tool can read.");
            return (null, ExitCodes.Refused);
        }
        return (Encoding.UTF8.GetString(Convert.FromBase64String(content.Replace("\n", string.Empty, StringComparison.Ordinal))), null);
    }

    private async Task<Reply> SendAsync(
        HttpMethod method, string pathOrUrl, object? body, CancellationToken ct, bool allowNotFound = false, bool allowUnprocessable = false)
    {
        string url = pathOrUrl.StartsWith("https://", StringComparison.Ordinal) ? pathOrUrl : _apiBase + pathOrUrl;
        for (int attempt = 0; ; attempt++)
        {
            using HttpRequestMessage request = new(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            request.Content = body switch
            {
                null => null,
                HttpContent content => content,
                JsonNode node => JsonContent.Create(node),
                _ => throw new ArgumentException("A request body is content or JSON.", nameof(body)),
            };
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                _output.Error($"{new Uri(url).Host} could not be reached.");
                return Reply.Failed(ExitCodes.Transient);
            }
            using (response)
            {
                string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return new Reply(response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text), null);
                }
                if ((response.StatusCode == HttpStatusCode.NotFound && allowNotFound)
                    || (response.StatusCode == HttpStatusCode.UnprocessableEntity && allowUnprocessable))
                {
                    return new Reply(response.StatusCode, null, null);
                }
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < PushPublisher.RetryBackoff.Count && body is not HttpContent)
                {
                    await delay(PushPublisher.RetryBackoff[attempt], ct).ConfigureAwait(false);
                    continue;
                }
                string message = MessageOf(text);
                int code = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden when response.Headers.TryGetValues("x-ratelimit-remaining", out IEnumerable<string>? left)
                        && left.FirstOrDefault() == "0" => ExitCodes.Transient,
                    HttpStatusCode.TooManyRequests => ExitCodes.Transient,
                    _ when (int)response.StatusCode >= 500 => ExitCodes.Transient,
                    _ => ExitCodes.Refused,
                };
                _output.Error(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden when code == ExitCodes.Refused =>
                        $"GitHub refused the job's token for {method} {new Uri(url).AbsolutePath} ({message}): it needs 'permissions: contents: write' on {_options.Repository}.",
                    HttpStatusCode.NotFound => $"GitHub found nothing at {new Uri(url).AbsolutePath} ({message}): check --to-repository and --branch, and that the token can see the repository.",
                    _ => $"GitHub answered {(int)response.StatusCode} to {method} {new Uri(url).AbsolutePath} ({message}).",
                });
                return Reply.Failed(code);
            }
        }
    }

    private static string MessageOf(string text)
    {
        try
        {
            return JsonNode.Parse(text) is JsonObject error && (string?)error["message"] is { } message ? message : "no message";
        }
        catch (JsonException)
        {
            return "no message";
        }
    }

    private sealed record Reply(HttpStatusCode Status, JsonNode? Json, int? ExitCode)
    {
        public static Reply Failed(int exitCode) => new(0, null, exitCode);
    }
}
