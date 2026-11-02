using System.Security.Cryptography;

namespace Wep.Tools.Publish;

/// <summary>
/// #4830 U13: one run of <c>attestree-publish</c>. Program.cs is one call to <see cref="RunAsync"/>, so the tests drive
/// what a pipeline drives, with the network, the environment and the clock passed in.
/// </summary>
internal static class PublishCommand
{
    /// <summary>Parse, check, hash, then push or write into the repository. Returns the exit code (<see cref="ExitCodes"/>).</summary>
    /// <param name="args">The arguments.</param>
    /// <param name="stdout">Standard output.</param>
    /// <param name="stderr">Standard error.</param>
    /// <param name="environment">Reads an environment variable.</param>
    /// <param name="handler">The HTTP transport.</param>
    /// <param name="delay">Waits between status reads and retries.</param>
    /// <param name="clock">The clock a wait's deadline and a token's freshness read.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        Func<string, string?> environment,
        HttpMessageHandler handler,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeProvider clock,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        if (!CommandLine.TryParse(args, out PublishOptions? options, out string? error))
        {
            if (error is null)
            {
                stdout.WriteLine(CommandLine.Usage);
                return ExitCodes.Success;
            }
            stderr.WriteLine(error);
            stderr.WriteLine("Run attestree-publish --help for the options.");
            return ExitCodes.Usage;
        }

        Output output = new(stdout, stderr, options!.Verbose);
        IReadOnlyList<ManifestFile>? manifest = null;
        if (options.ManifestPath is { } manifestPath)
        {
            if (!ManifestDocuments.TryRead(manifestPath, out manifest, out string? readError))
            {
                output.Error(readError!);
                return ExitCodes.Usage;
            }
            if (ManifestDocuments.Check(manifest!, options.PackageId, options.Version) is { } disagreement)
            {
                output.Error(disagreement + " Nothing was sent.");
                return ExitCodes.Usage;
            }
        }

        List<InstallerFile> installers = [];
        foreach (InstallerArgument installer in options.Installers)
        {
            if (installer.Url is not null)
            {
                installers.Add(new InstallerFile(installer.Architecture, null, installer.Url, installer.Sha256!, 0));
                continue;
            }
            if (!File.Exists(installer.Path))
            {
                output.Error($"--installer {installer.Architecture}={installer.Path}: no such file.");
                return ExitCodes.Usage;
            }
            installers.Add(await HashAsync(installer.Architecture, installer.Path!, ct).ConfigureAwait(false));
        }

        using HttpClient http = new(new RequestLog(output) { InnerHandler = handler }, disposeHandler: false)
        {
            Timeout = TimeSpan.FromMinutes(30),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("attestree-publish/1");

        if (options.ToRepository)
        {
            return await new RepositoryPublisher(options, installers, manifest, http, environment, output, delay)
                .RunAsync(ct).ConfigureAwait(false);
        }
        CiTokenSource tokens = new(options, environment, http, output, clock);
        return await new PushPublisher(options, installers, manifest, http, tokens, output, delay, clock)
            .RunAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The installer at <paramref name="path"/> with its SHA-256, streamed.</summary>
    internal static async Task<InstallerFile> HashAsync(string architecture, string path, CancellationToken ct)
    {
        FileStream stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            byte[] digest = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            return new InstallerFile(architecture, path, null, Convert.ToHexStringLower(digest), stream.Length);
        }
    }
}
