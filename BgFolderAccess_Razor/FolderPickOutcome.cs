namespace BgFolderAccess_Razor;

/// <summary>
/// The result of one folder-pick gesture, whichever mechanism served it: the folder the
/// pick landed, as the context the host now holds, and the pick's draw of its top-level
/// matching files. <see cref="Cancelled"/> — the pick ended with no folder — is an
/// expected outcome, not an error; it carries no folder and an empty sample.
///
/// <para>
/// The folder and its sample are kept apart because different things answer them: the
/// folder and its write capability are settled once by the pick, while the sample is one
/// draw of it, the part <see cref="IFolderContext.ResampleAsync"/> replaces. A cancelled
/// pick has two causes the browser does not tell apart — the picker dismissed, or the
/// view-files permission declined — so a host treats it as "no folder was picked" and
/// nothing more. Compares <see cref="Sample"/> by reference, as that record compares its
/// arrays, so a test compares members.
/// </para>
/// </summary>
/// <param name="Folder">The folder the pick landed, or <c>null</c> when the pick was cancelled.</param>
/// <param name="Sample">The pick's draw: its admitted files, buffered, and the per-kind report of what was found and admitted.</param>
public sealed record FolderPickOutcome(IFolderContext? Folder, FolderSample Sample)
{
    /// <summary>True when the pick ended holding no folder.</summary>
    public bool Cancelled => Folder is null;

    /// <summary>The single cancelled outcome: no folder, and an empty sample with no files and no kinds, since no draw ran.</summary>
    public static FolderPickOutcome CancelledOutcome { get; } =
        new(Folder: null, new FolderSample(Files: [], Kinds: []));
}
