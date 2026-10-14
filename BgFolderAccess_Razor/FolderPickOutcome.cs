namespace BgFolderAccess_Razor;

/// <summary>
/// The result of one folder-pick gesture, whichever mechanism served it.
/// <see cref="Cancelled"/> <c>true</c> means the pick ended with no folder — an
/// expected outcome, not an error, carrying no other data. Otherwise
/// <see cref="DirectoryName"/> names the folder, <see cref="Capability"/> says
/// whether it can be written into, and <see cref="Sample"/> holds the pick's
/// draw of its top-level matching files: the files, fully buffered, and the
/// per-kind account of what was found and admitted.
///
/// <para>
/// <b>The pick's facts and its sample are kept apart</b> because they are
/// answered by different things. The folder and its write capability are
/// settled once, by the pick that bound the folder; the sample is one draw of
/// that folder, the part a later draw (<see cref="IFolderAccess.ResampleAsync"/>)
/// replaces.
/// </para>
///
/// <para>
/// <b>Cancelled has two causes, and does not say which.</b> The user dismissed
/// the native picker, <i>or</i> declined the load-bearing view-files permission
/// the File System Access mechanism requests first — the browser reports both as
/// <c>AbortError</c>, so nothing downstream can tell them apart. Callers must
/// treat it as "no folder was picked", never as "the user changed their mind".
/// (Nothing that happens to the <i>second</i>, readwrite request is
/// cancellation — neither declining it nor the browser refusing to ask it. Both
/// keep the readable handle and land on
/// <see cref="FolderWriteCapability.PermissionDenied"/>; see that member.)
/// </para>
///
/// <para>
/// Compares <see cref="Sample"/> by reference, as that record compares its
/// arrays, so a test compares members.
/// </para>
/// </summary>
/// <param name="Cancelled">True when the pick ended holding no folder — see the type remarks.</param>
/// <param name="DirectoryName">The picked folder's leaf name (empty when cancelled).</param>
/// <param name="Capability">Whether the host can write files into this folder.</param>
/// <param name="Sample">
/// The pick's draw of the folder: its admitted files, buffered, and its
/// per-kind report (<see cref="FolderSample.Kinds"/>). Deliberately <b>not</b>
/// optional, and the report deliberately covers every kind: a caller that
/// renders the pick has to be handed whether it is partial, and a kind the
/// caps cut short is reported beside the kinds they did not.
/// </param>
public sealed record FolderPickOutcome(
    bool Cancelled,
    string DirectoryName,
    FolderWriteCapability Capability,
    FolderSample Sample)
{
    /// <summary>
    /// The single cancelled outcome — no folder, no capability claim (the
    /// <see cref="FolderWriteCapability.BrowserUnsupported"/> it carries is
    /// filler, meaningless while <see cref="Cancelled"/> is true), and an empty
    /// <see cref="Sample"/> with no files and no kinds: no draw ran, so it
    /// reports none, and it is filler too.
    /// </summary>
    public static FolderPickOutcome CancelledOutcome { get; } =
        new(Cancelled: true, DirectoryName: "", FolderWriteCapability.BrowserUnsupported, new FolderSample(Files: [], Kinds: []));
}
