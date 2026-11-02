using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Wep.Tools.Publish;

/// <summary>A CI token, or why none could be had and the exit code that says so.</summary>
/// <param name="Token">The token; null on a failure.</param>
/// <param name="ExitCode">On a failure, <see cref="ExitCodes.Usage"/> or <see cref="ExitCodes.Transient"/>.</param>
/// <param name="Message">On a failure, what to do about it.</param>
internal sealed record CiToken(string? Token, int ExitCode, string? Message)
{
    public static CiToken Of(string token) => new(token, ExitCodes.Success, null);

    public static CiToken Failed(int exitCode, string message) => new(null, exitCode, message);
}

/// <summary>
/// #4830 U13 (KTD8): the pipeline's federated identity. GitHub Actions mints a token for the instance's audience
/// (<c>ACTIONS_ID_TOKEN_REQUEST_URL</c>, which <c>permissions: id-token: write</c> provides); Azure Pipelines mints one
/// for a workload-identity service connection through <c>System.OidcRequestUri</c>, with the job's access token. No
/// token is ever stored or printed: it lives for this run, and the run asks again before it expires.
/// </summary>
/// <remarks>
/// One token, one publish (KTD8): the instance binds each token it accepts to the first id and version it authorizes,
/// so a run's tokens are only ever used for its own publish. A wait that outlives a token (GitHub's last five minutes)
/// asks for a fresh one, for the same publish.
/// </remarks>
internal sealed class CiTokenSource(PublishOptions options, Func<string, string?> environment, HttpClient http, Output output, TimeProvider clock)
{
    /// <summary>The variable the Azure DevOps task sets to its service connection input, when no option names one.</summary>
    public const string ServiceConnectionVariable = "ATTESTREE_SERVICE_CONNECTION";

    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan UnreadableExpiryLifetime = TimeSpan.FromMinutes(4);

    private readonly PublishOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly Func<string, string?> _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly Output _output = output ?? throw new ArgumentNullException(nameof(output));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private string? _token;
    private DateTimeOffset _refreshAt;

    /// <summary>A token for this run's publish: the one held while it is fresh, else a new one.</summary>
    public async Task<CiToken> GetAsync(CancellationToken ct)
    {
        if (_token is not null && _clock.GetUtcNow() < _refreshAt)
        {
            return CiToken.Of(_token);
        }
        CiToken minted = await MintAsync(ct).ConfigureAwait(false);
        if (minted.Token is { } token)
        {
            _output.AddSecret(token);
            _token = token;
            _refreshAt = ExpiryOf(token) is { } expiry ? expiry - RefreshMargin : _clock.GetUtcNow() + UnreadableExpiryLifetime;
        }
        return minted;
    }

    /// <summary>Forget the token held, so the next <see cref="GetAsync"/> asks for a new one (the instance said it expired).</summary>
    public void Discard() => _token = null;

    private Task<CiToken> MintAsync(CancellationToken ct)
    {
        string? gitHubUrl = _environment("ACTIONS_ID_TOKEN_REQUEST_URL");
        string? gitHubBearer = _environment("ACTIONS_ID_TOKEN_REQUEST_TOKEN");
        if (!string.IsNullOrEmpty(gitHubUrl) && !string.IsNullOrEmpty(gitHubBearer))
        {
            return GitHubAsync(gitHubUrl, gitHubBearer, ct);
        }
        if (_environment("SYSTEM_OIDCREQUESTURI") is { Length: > 0 } adoUrl)
        {
            return AzureDevOpsAsync(adoUrl, ct);
        }
        return Task.FromResult(CiToken.Failed(
            ExitCodes.Usage,
            "No CI identity to publish with. On GitHub Actions, give the job 'permissions: id-token: write'. On Azure Pipelines, "
            + "publish through the Attestree task, which ties the job to its service connection."));
    }

    private async Task<CiToken> GitHubAsync(string requestUrl, string bearer, CancellationToken ct)
    {
        _output.AddSecret(bearer);
        if (string.IsNullOrWhiteSpace(_options.Audience))
        {
            return CiToken.Failed(
                ExitCodes.Usage,
                "On GitHub Actions, pass --audience: the instance's audience, attestree:<instance-id>, as the binding screen shows it.");
        }
        string separator = requestUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        using HttpRequestMessage request = new(HttpMethod.Get, requestUrl + separator + "audience=" + Uri.EscapeDataString(_options.Audience));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await ReadAsync(request, "value", "GitHub's OIDC token endpoint", ct).ConfigureAwait(false);
    }

    private async Task<CiToken> AzureDevOpsAsync(string requestUrl, CancellationToken ct)
    {
        string? connection = _options.ServiceConnectionId ?? _environment(ServiceConnectionVariable);
        string? accessToken = _environment("SYSTEM_ACCESSTOKEN");
        _output.AddSecret(accessToken);
        if (string.IsNullOrWhiteSpace(connection))
        {
            return CiToken.Failed(
                ExitCodes.Usage,
                "On Azure Pipelines, name the workload-identity service connection bound on the instance: --service-connection <id>, "
                + "or publish through the Attestree task.");
        }
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return CiToken.Failed(
                ExitCodes.Usage,
                "On Azure Pipelines the job's access token is needed to ask for the OIDC token: map it into the step as SYSTEM_ACCESSTOKEN.");
        }
        string separator = requestUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        using HttpRequestMessage request = new(
            HttpMethod.Post, requestUrl + separator + "api-version=7.1&serviceConnectionId=" + Uri.EscapeDataString(connection));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        return await ReadAsync(request, "oidcToken", "Azure DevOps' OIDC token endpoint", ct).ConfigureAwait(false);
    }

    private async Task<CiToken> ReadAsync(HttpRequestMessage request, string property, string endpoint, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return CiToken.Failed(ExitCodes.Transient, $"{endpoint} could not be reached; retry the job.");
        }
        using (response)
        {
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return CiToken.Failed(ExitCodes.Transient, $"{endpoint} answered {(int)response.StatusCode}; retry the job.");
            }
            if (!response.IsSuccessStatusCode)
            {
                return CiToken.Failed(
                    ExitCodes.Usage,
                    $"{endpoint} refused to mint a token ({(int)response.StatusCode}). On Azure Pipelines, the service connection must be a "
                    + "workload-identity connection the step references; on GitHub Actions, the job needs 'permissions: id-token: write'.");
            }
            try
            {
                using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(property, out JsonElement value)
                    && value.GetString() is { Length: > 0 } token)
                {
                    return CiToken.Of(token);
                }
            }
            catch (JsonException)
            {
                // Falls through to the failure below; the body is never printed, since it may hold a token.
            }
            return CiToken.Failed(ExitCodes.Transient, $"{endpoint} answered without a token; retry the job.");
        }
    }

    /// <summary>The <c>exp</c> claim of a JWT, read without verifying it (only to know when to ask again); null when unreadable.</summary>
    internal static DateTimeOffset? ExpiryOf(string jwt)
    {
        string[] parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }
        try
        {
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            using JsonDocument doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("exp", out JsonElement exp)
                && exp.TryGetInt64(out long seconds)
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                    : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
