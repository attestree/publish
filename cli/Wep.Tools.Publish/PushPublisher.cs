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
    TimeProvider clock)
{
    /// <summary>The upload slot's token header (the publish API's <c>X-Wep-Upload-Token</c>).</summary>
    public const string UploadTokenHeader = "X-Wep-Upload-Token";

    /// <summary>How long apart the status is read while waiting.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    /// <summary>The backoff between the attempts a 503 earns.</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryBackoff = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PublishOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly CiTokenSource _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    private readonly Output _output = output ?? throw new ArgumentNullException(nameof(output));

    /// <summary>Publish, upload, and optionally wait. Returns the run's exit code.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        string key = $"{Uri.EscapeDataString(_options.PackageId)}/{Uri.EscapeDataString(_options.Version)}";
        Uri publishUrl = InstanceUrl("/api/v1/publish/" + key);
        PublishBody body = new(
            [.. installers.Select(i => new PublishInstallerBody(i.Architecture, i.Sha256, i.Url))],
            manifest is null ? null : [.. manifest.Select(m => m.Text)]);

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

internal sealed record PublishInstallerBody(string Architecture, string Sha256, string? InstallerUrl);

internal sealed record PublishBody(IReadOnlyList<PublishInstallerBody> Installers, IReadOnlyList<string>? Manifest);

internal sealed record PublishStatusBody(string PackageId, string Version, string State, string? Reason);

internal sealed record UploadSlotBody(string Architecture, string Location, string Token);

internal sealed record PublishAnswerBody(
    PublishStatusBody Status, string StatusLocation, IReadOnlyList<UploadSlotBody>? Uploads, bool Repeat, IReadOnlyList<string>? IgnoredFields);

internal sealed record RefusalBody(string? Reason, string? Detail, string? Claim);
