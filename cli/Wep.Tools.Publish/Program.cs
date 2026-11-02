using Wep.Tools.Publish;

// #4830 U13 — attestree-publish, the publish CLI. The GitHub Action (tools/publish-action) and the Azure DevOps task
// (tools/publish-ado-task) run it. The arguments, the exit codes and both modes are in CommandLine.Usage and ExitCodes;
// PublishCommand is the whole run, so the tests drive the same code a pipeline does.

using CancellationTokenSource cancel = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};
using SocketsHttpHandler handler = new() { AllowAutoRedirect = true, MaxAutomaticRedirections = 3 };
return await PublishCommand.RunAsync(
    args, Console.Out, Console.Error, Environment.GetEnvironmentVariable, handler, Task.Delay, TimeProvider.System, cancel.Token);
