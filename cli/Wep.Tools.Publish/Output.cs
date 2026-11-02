using System.Text.RegularExpressions;

namespace Wep.Tools.Publish;

/// <summary>
/// #4830 U13: everything <c>attestree-publish</c> prints goes through here, and nothing it prints carries a token. The
/// CI token, the upload tokens and the GitHub token are registered as they are obtained and replaced wherever they
/// would appear; anything shaped like a JWT or a GitHub token is replaced too, whoever's it is.
/// </summary>
internal sealed partial class Output(TextWriter stdout, TextWriter stderr, bool verbose)
{
    private const string Redacted = "[redacted]";

    private readonly TextWriter _stdout = stdout ?? throw new ArgumentNullException(nameof(stdout));
    private readonly TextWriter _stderr = stderr ?? throw new ArgumentNullException(nameof(stderr));
    private readonly List<string> _secrets = [];
    private readonly Lock _gate = new();

    /// <summary>True when <c>--verbose</c> was given.</summary>
    public bool Verbose { get; } = verbose;

    /// <summary>Never print <paramref name="secret"/>.</summary>
    /// <param name="secret">A token this run holds.</param>
    public void AddSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return;
        }
        lock (_gate)
        {
            if (!_secrets.Contains(secret, StringComparer.Ordinal))
            {
                _secrets.Add(secret);
            }
        }
    }

    /// <summary>One line to standard output.</summary>
    public void Info(string line) => _stdout.WriteLine(Redact(line));

    /// <summary>One line to standard error.</summary>
    public void Error(string line) => _stderr.WriteLine(Redact(line));

    /// <summary>One line to standard output, only with <c>--verbose</c>.</summary>
    public void Debug(string line)
    {
        if (Verbose)
        {
            _stdout.WriteLine(Redact(line));
        }
    }

    /// <summary><paramref name="text"/> with every registered secret, JWT and GitHub token replaced.</summary>
    public string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            foreach (string secret in _secrets)
            {
                text = text.Replace(secret, Redacted, StringComparison.Ordinal);
            }
        }
        text = JwtShape().Replace(text, Redacted);
        return GitHubTokenShape().Replace(text, Redacted);
    }

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]*", RegexOptions.CultureInvariant)]
    private static partial Regex JwtShape();

    [GeneratedRegex(@"\b(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenShape();
}

/// <summary>
/// #4830 U13: with <c>--verbose</c>, each request's method, host and path, and each answer's status. Never a header and
/// never a query: the token endpoints' queries and the upload slots' tokens stay out of the log.
/// </summary>
internal sealed class RequestLog(Output output) : DelegatingHandler
{
    private readonly Output _output = output ?? throw new ArgumentNullException(nameof(output));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string target = request.RequestUri is { } uri ? uri.Host + uri.AbsolutePath : "(no URL)";
        _output.Debug($"-> {request.Method} {target}");
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        _output.Debug($"<- {(int)response.StatusCode} {target}");
        return response;
    }
}
