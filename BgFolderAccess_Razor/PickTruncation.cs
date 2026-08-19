namespace BgFolderAccess_Razor;

/// <summary>
/// One matching-file kind the pick had to truncate: more files of
/// <paramref name="Extension"/> were in the folder than the host's
/// <see cref="FolderPickLimits"/> admits, so a randomly drawn
/// <paramref name="MaxFileCount"/> of them were taken and
/// <paramref name="OmittedCount"/> were left unread.
///
/// <para>
/// <b>Which files survive is drawn uniformly at random, not taken in folder
/// order</b> (<c>folderAccess.js</c> enforces the count caps). Enumeration
/// order is browser-supplied and stable, so admitting a prefix would put a
/// corpus-scale folder's excess permanently out of reach — the same files
/// would be lost to every re-pick. A random draw lets repeated picks of one
/// folder cover the whole corpus over time. This record is unaffected by that
/// choice: it reports <i>how much</i> was left behind, never <i>which</i>
/// files — but host copy that says "the first N" is no longer true.
/// </para>
///
/// <para>
/// Reported per kind, and <b>only for kinds actually truncated</b> — each
/// extension is capped independently, so a folder can hit one cap while
/// another is nowhere near it. A pick that fit reports none of these.
/// </para>
/// </summary>
/// <param name="Extension">
/// The truncated kind, as a lower-case dot-bearing extension — always a key of
/// <see cref="FolderPickLimits.MaxFileCounts"/>, which is the table the pick
/// was enforced against.
/// </param>
/// <param name="OmittedCount">How many files of that kind were left unread; always positive.</param>
/// <param name="MaxFileCount">
/// The cap that was applied. <b>Derived, never round-tripped</b>: the figure is
/// looked up (<see cref="FolderPickLimits.MaxFileCountFor"/>) at the library's
/// one construction site — <see cref="JsFolderAccess"/>'s truncation mapping —
/// on the same <see cref="FolderPickLimits"/> instance the enumeration
/// enforced, rather than carried back across the interop boundary. The figure
/// a notice states is therefore the figure the pick enforced.
/// </param>
public sealed record PickTruncation(string Extension, int OmittedCount, int MaxFileCount);
