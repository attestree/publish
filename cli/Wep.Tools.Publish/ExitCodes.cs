namespace Wep.Tools.Publish;

/// <summary>
/// #4830 U13 (KTD9): <c>attestree-publish</c>'s exit codes. A pipeline branches on them, so each outcome a step may
/// handle differently has its own code, and a code never changes meaning once shipped.
/// </summary>
/// <remarks>
/// <para>
/// 0 to 4 are the publish itself. A repeat of the same publish (the same installers, manifest and switches) is accepted:
/// the instance answers the version's existing state, so re-running a pipeline step is safe. A <i>conflicting</i>
/// duplicate (the version exists with other installers, another manifest, through another way in, or was removed from
/// the catalog) is <see cref="Duplicate"/>.
/// </para>
/// <para>
/// 10 to 15 are the terminal states a <c>--wait</c> ends on (KTD9), each its own code. <c>not-admitted</c> ends a wait
/// even though the instance reads it again on every status read: the precondition it names (the app's subscription,
/// the update grant, the firing switch, a carve-out) waits on an admin, not on time.
/// </para>
/// </remarks>
internal static class ExitCodes
{
    /// <summary>The publish was accepted (a repeat included); with <c>--wait</c>, the version was attested.</summary>
    public const int Success = 0;

    /// <summary>The arguments, an installer file, or the manifest cannot be used; nothing was sent.</summary>
    public const int Usage = 1;

    /// <summary>The instance refused the publish or the pipeline's token, or (with <c>--wait</c>) refused the version.</summary>
    public const int Refused = 2;

    /// <summary>A conflicting duplicate: the version exists with other content, arrived another way, or was removed.</summary>
    public const int Duplicate = 3;

    /// <summary>A failure that may pass on a retry: the network, a rate limit, or a 503 that outlasted the retries.</summary>
    public const int Transient = 4;

    /// <summary>With <c>--wait</c>: superseded, a newer version of the app will fly instead.</summary>
    public const int Superseded = 10;

    /// <summary>With <c>--wait</c>: not admitted, the update pipeline will not take it until an admin acts.</summary>
    public const int NotAdmitted = 11;

    /// <summary>With <c>--wait</c>: the binding that pushed it was removed or re-pinned before it was attested.</summary>
    public const int BindingRemoved = 12;

    /// <summary>With <c>--wait</c>: the way in it arrived through was removed before it was attested.</summary>
    public const int SourceRemoved = 13;

    /// <summary>With <c>--wait</c>: its flight is halted or stalled and waits on an operator.</summary>
    public const int Halted = 14;

    /// <summary>With <c>--wait</c>: no terminal state within the wait's timeout (still waiting for review, say).</summary>
    public const int WaitTimedOut = 15;
}
