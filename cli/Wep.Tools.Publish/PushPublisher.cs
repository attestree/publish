using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Wep.Tools.Publish;

/// <summary>
/// #4830 U13 (KTD9, R13, R15, R36): push mode. One POST names every installer of the version by architecture and the
/// SHA-256 computed here; one PUT per upload slot streams an installer's bytes; and with <c>--wait</c>, the version's
/// status is read until a terminal state. Every request carries a fresh CI token for this publish (KTD8).
/// </summary>
/// <remarks>
/// Only a 503 is retried (a few times, backing off): the instance says it could not record the publish or keep the bytes
/// right now. Every other answer is final for this run, a 429 included: the publish route's rate limit counts refusals,
/// and a pipeline that retries into it only extends the wait.
/// </remarks>
internal sealed class PushPublisher(
    PublishOptions options,
    IReadOnlyList<InstallerFile> installers,
    IReadOnlyList<ManifestFile>? manifest,
    HttpClient http,
    CiTokenSource tokens,
    Output output,
    Func<TimeSpan, CancellationToken, Task> delay,
    TimeProvider clock,
    SbomFile? sbom = null)
{
    /// <summary>The upload slot's token header (the publish API's <c>X-Wep-Upload-Token</c>).</summary>
    public const string UploadTokenHeader = "X-Wep-Upload-Token";

    /// <summary>How long apart the status is read while waiting.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Plan 2026-11-13-0645 U7 (KTD18): where the instance says what its publish API takes. One segment under the publish
    /// prefix, so an instance released before it answers a plain not-found: its status route takes exactly two segments, and
    /// a two-segment read would be validated as a publish, recorded as a refused one, and spend the token's one publish key.
    /// Asked with no token at all.
    /// </summary>
    public const string CapabilitiesPath = "/api/v1/publish/capabilities";

    /// <summary>Plan 2026-11-13-0645 U7 (KTD18): the capability an instance names when its publish takes install switches.</summary>
    public const string TakesSwitches = "switches";

    /// <summary>Plan 2026-11-13-0645 U15 (KTD18): the capability an instance names when its publish takes the build's SBOM.</summary>
    public const string TakesSbom = "sbom";

    /// <summary>Plan 2026-11-13-0645 U15: the reason the instance gives a version it refused for its SBOM alone.</summary>
    public const string SbomRefused = "sbom-refused";

    /// <summary>The backoff between the attempts a 503 earns.</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryBackoff = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    /// <summary>The options every publish-API body is written and read with; the wire-contract test reads with these too.</summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PublishOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly CiTokenSource _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    private readonly Output _output = output ?? throw new ArgumentNullException(nameof(output));

    /// <summary>Publish, upload, and optionally wait. Returns the run's exit code.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        string key = $"{Uri.EscapeDataString(_options.PackageId)}/{Uri.EscapeDataString(_options.Version)}";
        Uri publishUrl = InstanceUrl("/api/v1/publish/" + key);
        // Plan 2026-11-13-0645 U7, U15 (KTD18): an instance that predates switches or the SBOM would drop them with a clean
        // success, so a run that names either asks first, once, and stops before posting when the instance does not say it
        // takes what the run names. A run that names neither sends exactly what 1.0.2 sent, and asks nothing.
        if (_options.NamesSwitches || _options.NamesSbom)
        {
            IReadOnlySet<string> takes = await TakesAsync(ct).ConfigureAwait(false);
            if (_options.NamesSwitches && !takes.Contains(TakesSwitches))
            {
                _output.Error($"{_options.Instance!.Host} does not say it takes install switches in a push, so they would be dropped; nothing was sent. "
                    + "Publish without --silent, --silent-with-progress and --custom-switches, put them in the --manifest, or upgrade the instance.");
                return ExitCodes.Usage;
            }
            if (_options.NamesSbom && !takes.Contains(TakesSbom))
            {
                _output.Error($"{_options.Instance!.Host} does not say it takes an SBOM in a push, so it would be dropped; nothing was sent. "
                    + "Publish without --sbom, or upgrade the instance.");
                return ExitCodes.Usage;
            }
        }
        // KTD5: the switches are named once for the run and sent on every installer. KTD12: the SBOM is declared here, by its
        // SHA-256 and size, and its bytes go through a slot of their own.
        PublishBody body = new(
            [.. installers.Select(i => new PublishInstallerBody(i.Architecture, i.Sha256, i.Url,
                _options.SilentSwitches, _options.SilentWithProgressSwitches, _options.CustomSwitches))],
            manifest is null ? null : [.. manifest.Select(m => m.Text)],
            sbom is null ? null : new PublishSbomBody(sbom.Sha256, sbom.Length));

        Answer posted = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, publishUrl) { Content = JsonContent.Create(body, options: Json) }, ct)
            .ConfigureAwait(false);
        if (posted.ExitCode is { } postFailed)
        {
            return postFailed;
        }
        PublishAnswerBody answer = posted.Read<PublishAnswerBody>()!;
        _output.Info(answer.Repeat
            ? $"{_options.PackageId} {_options.Version} was already published by this pipeline; its state is {Describe(answer.Status)}."
            : $"{_options.PackageId} {_options.Version} accepted by {_options.Instance!.Host}: {Describe(answer.Status)}.");
        if (answer.IgnoredFields is { Count: > 0 } ignored)
        {
            _output.Info($"The instance took the installer type, scope and switches from the manifest, and ignored: {string.Join(", ", ignored)}.");
        }

        PublishStatusBody status = answer.Status;
        // Plan 2026-11-13-0645 U15 (KTD12): the SBOM first, so a refusal ends the run before an installer's bytes are sent.
        if (answer.SbomUpload is { } sbomSlot)
        {
            _output.AddSecret(sbomSlot.Token);
            if (sbom is null)
            {
                _output.Error("The instance asked for the SBOM's bytes, but this run names no --sbom.");
                return ExitCodes.Refused;
            }
            Answer sent = await SendAsync(() => SbomRequest(sbomSlot, sbom), ct).ConfigureAwait(false);
            if (sent.ExitCode is { } sbomFailed)
            {
                return sbomFailed;
            }
            status = sent.Read<PublishAnswerBody>()?.Status ?? status;
            _output.Info($"Uploaded the SBOM ({sbom.Length} bytes, SHA-256 {sbom.Sha256}).");
        }
        foreach (UploadSlotBody slot in answer.Uploads ?? [])
        {
            _output.AddSecret(slot.Token);
            InstallerFile? file = installers.FirstOrDefault(i => string.Equals(i.Architecture, slot.Architecture, StringComparison.OrdinalIgnoreCase));
            if (file?.Path is null)
            {
                _output.Error($"The instance asked for the {slot.Architecture} installer's bytes, but this run names no file for {slot.Architecture}.");
                return ExitCodes.Refused;
            }
            Answer uploaded = await SendAsync(() => UploadRequest(slot, file), ct).ConfigureAwait(false);
            if (uploaded.ExitCode is { } uploadFailed)
            {
                return uploadFailed;
            }
            status = uploaded.Read<PublishAnswerBody>()?.Status ?? status;
            _output.Info($"Uploaded the {file.Architecture} installer ({file.Length} bytes, SHA-256 {file.Sha256}).");
        }

        if (!_options.Wait)
        {
            _output.Info($"{_options.PackageId} {_options.Version}: {Describe(status)}.");
            return ExitCodes.Success;
        }
        return await WaitAsync(publishUrl, status, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Plan 2026-11-13-0645 U7, U15 (KTD18): what the instance says its publish takes. Any answer but a readable 200, no
    /// answer at all included, reads as "takes neither": an older instance answers a plain not-found. Sent with no token, so
    /// no publish key is spent.
    /// </summary>
    private async Task<IReadOnlySet<string>> TakesAsync(CancellationToken ct)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, InstanceUrl(CapabilitiesPath));
            using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _output.Debug($"{CapabilitiesPath} answered {(int)response.StatusCode}: read as an instance that takes neither switches nor an SBOM.");
                return new HashSet<string>(StringComparer.Ordinal);
            }
            PublishCapabilitiesBody? body = await response.Content.ReadFromJsonAsync<PublishCapabilitiesBody>(Json, ct).ConfigureAwait(false);
            return new HashSet<string>(body?.Takes ?? [], StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException or InvalidOperationException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _output.Debug($"{CapabilitiesPath} gave no readable answer: read as an instance that takes neither switches nor an SBOM.");
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private async Task<int> WaitAsync(Uri statusUrl, PublishStatusBody last, CancellationToken ct)
    {
        DateTimeOffset deadline = clock.GetUtcNow() + _options.WaitTimeout;
        string? shown = null;
        while (true)
        {
            string line = Describe(last);
            if (!string.Equals(line, shown, StringComparison.Ordinal))
            {
                _output.Info($"{_options.PackageId} {_options.Version}: {line}.");
                shown = line;
            }
            if (TerminalExitCode(last.State) is { } terminal)
            {
                return terminal;
            }
            if (clock.GetUtcNow() >= deadline)
            {
                _output.Error($"No final state within {(int)_options.WaitTimeout.TotalMinutes} minutes; the version is {line}.");
                return ExitCodes.WaitTimedOut;
            }
            await delay(PollInterval, ct).ConfigureAwait(false);
            Answer read = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, statusUrl), ct, readingStatus: true).ConfigureAwait(false);
            if (read.ExitCode is ExitCodes.Transient)
            {
                // A status read that fails says nothing about the version; read again until the deadline.
                continue;
            }
            if (read.ExitCode is { } failed)
            {
                return failed;
            }
            last = read.Read<PublishStatusBody>() ?? last;
        }
    }

    /// <summary>
    /// The exit code a wait ends on in <paramref name="state"/>, or null while it waits. KTD9's terminal states, plus
    /// attested and refused; <c>not-admitted</c> and <c>halted</c> end the wait because only an admin moves them on.
    /// </summary>
    internal static int? TerminalExitCode(string state) => state switch
    {
        "attested" => ExitCodes.Success,
        "refused" => ExitCodes.Refused,
        "superseded" => ExitCodes.Superseded,
        "not-admitted" => ExitCodes.NotAdmitted,
        "binding-removed" => ExitCodes.BindingRemoved,
        "source-removed" => ExitCodes.SourceRemoved,
        "halted" => ExitCodes.Halted,
        _ => null,
    };

    private static string Describe(PublishStatusBody status) =>
        status.Reason is { Length: > 0 } reason ? $"{status.State} ({reason})" : status.State;

    private HttpRequestMessage UploadRequest(UploadSlotBody slot, InstallerFile file)
    {
        HttpRequestMessage request = new(HttpMethod.Put, InstanceUrl(slot.Location))
        {
            Content = new StreamContent(File.OpenRead(file.Path!)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentLength = file.Length;
        request.Headers.TryAddWithoutValidation(UploadTokenHeader, slot.Token);
        return request;
    }

    /// <summary>Plan 2026-11-13-0645 U15 (KTD12): the SBOM's bytes into its own slot, as an installer's go into theirs.</summary>
    private HttpRequestMessage SbomRequest(SbomUploadBody slot, SbomFile file)
    {
        HttpRequestMessage request = new(HttpMethod.Put, InstanceUrl(slot.Location))
        {
            Content = new StreamContent(File.OpenRead(file.Path)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content.Headers.ContentLength = file.Length;
        request.Headers.TryAddWithoutValidation(UploadTokenHeader, slot.Token);
        return request;
    }

    private Uri InstanceUrl(string path)
    {
        Uri instance = _options.Instance!;
        return new Uri(instance.GetLeftPart(UriPartial.Authority) + instance.AbsolutePath.TrimEnd('/') + "/" + path.TrimStart('/'));
    }

    /// <summary>
    /// Send one publish-API request with this run's CI token, retrying a 503. The answer, or the exit code a failure
    /// ends the run with (after saying why).
    /// </summary>
    private async Task<Answer> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct, bool readingStatus = false)
    {
        bool tokenRenewed = false;
        for (int attempt = 0; ; attempt++)
        {
            CiToken token = await _tokens.GetAsync(ct).ConfigureAwait(false);
            if (token.Token is null)
            {
                _output.Error(token.Message!);
                return Answer.Failed(token.ExitCode);
            }
            using HttpRequestMessage request = build();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                _output.Error($"{_options.Instance!.Host} could not be reached.");
                return Answer.Failed(ExitCodes.Transient);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                _output.Error($"{_options.Instance!.Host} did not answer in time.");
                return Answer.Failed(ExitCodes.Transient);
            }
            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    return Answer.Of(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
                }
                RefusalBody? refusal = await ReadRefusalAsync(response, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < RetryBackoff.Count)
                {
                    _output.Debug($"{(int)response.StatusCode} {refusal?.Reason}: retrying in {RetryBackoff[attempt].TotalSeconds:0} s.");
                    await delay(RetryBackoff[attempt], ct).ConfigureAwait(false);
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.Unauthorized && !tokenRenewed && refusal?.Reason == "token-expired")
                {
                    // The token outlived the wait between two requests: ask the CI system for a fresh one, once.
                    _tokens.Discard();
                    tokenRenewed = true;
                    attempt--;
                    continue;
                }
                return Answer.Failed(Fail(response.StatusCode, refusal, readingStatus));
            }
        }
    }

    private int Fail(HttpStatusCode status, RefusalBody? refusal, bool readingStatus)
    {
        string why = refusal is null ? $"HTTP {(int)status}" : $"{refusal.Reason}: {refusal.Detail}";
        int code = status switch
        {
            HttpStatusCode.Conflict when refusal?.Reason is "version-exists" or "installer-set-differs" or "manifest-differs" or "removed-from-catalog"
                    or "request-switches-differ" or "sbom-differs"
                => ExitCodes.Duplicate,
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout
                => ExitCodes.Transient,
            // The instance could not fetch the issuer's signing keys: the token was not judged, so a later run may pass.
            HttpStatusCode.Unauthorized when refusal?.Reason is "keys-unavailable" => ExitCodes.Transient,
            HttpStatusCode.NotFound when readingStatus => ExitCodes.Transient,
            _ when (int)status >= 500 => ExitCodes.Transient,
            _ => ExitCodes.Refused,
        };
        string message = refusal?.Reason switch
        {
            "digest-mismatch" => $"The instance computed another SHA-256 for the bytes it received than the one this run sent ({why}).",
            // Plan 2026-11-13-0645 U15 (KTD12): the version was refused for its SBOM alone, so its number is not spent.
            SbomRefused when code == ExitCodes.Refused =>
                $"The instance refused the SBOM ({why}). Nothing else was wrong: push {_options.PackageId} {_options.Version} again, the same build with a valid SBOM or none.",
            _ => code switch
            {
                ExitCodes.Duplicate => $"The instance already holds {_options.PackageId} {_options.Version} another way ({why}). A changed build is a new version.",
                ExitCodes.Transient => $"The instance could not take the request now ({why}); retry the job.",
                _ => $"The instance refused the publish ({why}).",
            },
        };
        if (code != ExitCodes.Transient || !readingStatus)
        {
            _output.Error(message);
        }
        return code;
    }

    private static async Task<RefusalBody?> ReadRefusalAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<RefusalBody>(Json, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private sealed record Answer(byte[]? Body, int? ExitCode)
    {
        public static Answer Of(byte[] body) => new(body, null);

        public static Answer Failed(int exitCode) => new(null, exitCode);

        public T? Read<T>() => Body is { Length: > 0 } body ? JsonSerializer.Deserialize<T>(body, Json) : default;
    }
}

/// <summary>One installer with its bytes' SHA-256 computed (or, for a URL, given).</summary>
/// <param name="Architecture">Its architecture.</param>
/// <param name="Path">The file; null for a URL.</param>
/// <param name="Url">The URL; null for a file.</param>
/// <param name="Sha256">Lowercase hex.</param>
/// <param name="Length">The file's length; 0 for a URL.</param>
internal sealed record InstallerFile(string Architecture, string? Path, string? Url, string Sha256, long Length);

/// <summary>One installer of a publish's body; plan 2026-11-13-0645 U7 (KTD5): with the run's switches, the same on every installer.</summary>
/// <remarks>A switch not named is left out of the body, so a run that names none sends exactly what 1.0.2 sent.</remarks>
internal sealed record PublishInstallerBody(
    string Architecture,
    string Sha256,
    string? InstallerUrl,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Silent = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? SilentWithProgress = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Custom = null);

/// <summary>Plan 2026-11-13-0645 U7 (KTD18): what the instance says its publish API takes.</summary>
internal sealed record PublishCapabilitiesBody(IReadOnlyList<string>? Takes);

/// <summary>Plan 2026-11-13-0645 U15 (KTD12): the build's SBOM file, with its SHA-256 and size computed here.</summary>
/// <param name="Path">The file.</param>
/// <param name="Sha256">Lowercase hex.</param>
/// <param name="Length">Its length.</param>
internal sealed record SbomFile(string Path, string Sha256, long Length);

/// <summary>The publish's body; plan 2026-11-13-0645 U15 (KTD12): with the SBOM it declares, left out when the run names none.</summary>
internal sealed record PublishBody(
    IReadOnlyList<PublishInstallerBody> Installers,
    IReadOnlyList<string>? Manifest,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] PublishSbomBody? Sbom = null);

/// <summary>Plan 2026-11-13-0645 U15 (KTD12): the SBOM a publish declares.</summary>
internal sealed record PublishSbomBody(string Sha256, long SizeBytes);

internal sealed record PublishStatusBody(string PackageId, string Version, string State, string? Reason);

internal sealed record UploadSlotBody(string Architecture, string Location, string Token);

/// <summary>Plan 2026-11-13-0645 U15 (KTD12): where the SBOM's bytes go, and the single-use token that lets them in.</summary>
internal sealed record SbomUploadBody(string Location, string Token);

internal sealed record PublishAnswerBody(
    PublishStatusBody Status, string StatusLocation, IReadOnlyList<UploadSlotBody>? Uploads, bool Repeat, IReadOnlyList<string>? IgnoredFields,
    SbomUploadBody? SbomUpload = null);

internal sealed record RefusalBody(string? Reason, string? Detail, string? Claim);
