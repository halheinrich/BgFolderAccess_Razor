namespace BgFolderAccess_Razor;

/// <summary>
/// The result of one folder-pick gesture, whichever mechanism served it.
/// <see cref="Cancelled"/> <c>true</c> means the pick ended with no folder — an
/// expected outcome, not an error, carrying no other data. Otherwise
/// <see cref="Files"/> holds the folder's top-level matching files fully
/// buffered (extension-bearing names — see <see cref="PickedFile.FileName"/>),
/// <see cref="Capability"/> says whether the folder can be written into, and
/// <see cref="Truncations"/> reports any kind the count caps cut short.
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
/// </summary>
/// <param name="Cancelled">True when the pick ended holding no folder — see the type remarks.</param>
/// <param name="DirectoryName">The picked folder's leaf name (empty when cancelled).</param>
/// <param name="Files">Top-level matching files, buffered (empty when cancelled).</param>
/// <param name="Capability">Whether the host can write files into this folder.</param>
/// <param name="Truncations">
/// The matching-file kinds a count cap cut short, one entry per truncated kind
/// and empty when the folder fit — the pick's own account of what it did not
/// read. Deliberately <b>not</b> optional: a caller that renders the pick has
/// to be handed the fact that it is partial, and every construction site is a
/// place where "nothing was left behind" is a claim worth stating out loud.
/// </param>
public sealed record FolderPickOutcome(
    bool Cancelled,
    string DirectoryName,
    IReadOnlyList<PickedFile> Files,
    FolderWriteCapability Capability,
    IReadOnlyList<PickTruncation> Truncations)
{
    /// <summary>
    /// The single cancelled outcome — no folder, no files, no capability claim
    /// (the <see cref="FolderWriteCapability.BrowserUnsupported"/> it carries is
    /// filler, meaningless while <see cref="Cancelled"/> is true), nothing left
    /// behind.
    /// </summary>
    public static FolderPickOutcome CancelledOutcome { get; } =
        new(Cancelled: true, DirectoryName: "", Files: [], FolderWriteCapability.BrowserUnsupported, Truncations: []);
}
