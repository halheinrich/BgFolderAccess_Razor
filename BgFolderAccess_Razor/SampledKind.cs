namespace BgFolderAccess_Razor;

/// <summary>
/// One matching-file kind as a draw took it: how many files of
/// <see cref="Extension"/> the folder held (<see cref="FoundCount"/>), how
/// many the draw admitted into the sample (<see cref="AdmittedCount"/>), and
/// the cap it enforced (<see cref="MaxFileCount"/>). Every draw reports one of
/// these for <i>every</i> kind of the host's <see cref="FolderPickLimits"/>, in
/// the table's order, a kind the folder held none of included
/// (<see cref="FolderSample.Kinds"/>).
///
/// <para>
/// <b>Which files survive a cap is drawn uniformly at random, not taken in
/// folder order</b> (<c>folderAccess.js</c> enforces the count caps).
/// Enumeration order is browser-supplied and stable, so admitting a prefix
/// would put a corpus-scale folder's excess permanently out of reach: the same
/// files would be lost to every re-pick. A random draw lets repeated draws of
/// one folder cover the whole corpus over time. This record reports <i>how
/// many</i>, never <i>which</i>, so host copy that says "the first N" is not
/// true.
/// </para>
///
/// <para>
/// <b>Validated at construction</b>, so the derived <see cref="OmittedCount"/>
/// and <see cref="FolderSample.HoldsEveryMatchingFile"/> cannot be argued out
/// of range by an impossible report: nothing is admitted that was not found,
/// and nothing past the cap. The properties have no <c>init</c> accessor, so
/// <c>with</c> cannot set one past that check. Compares by value.
/// </para>
/// </summary>
public sealed record SampledKind
{
    /// <summary>
    /// Create one kind's report.
    /// </summary>
    /// <param name="extension">The kind, as a key of <see cref="FolderPickLimits.MaxFileCounts"/>.</param>
    /// <param name="foundCount">How many files of the kind the folder held.</param>
    /// <param name="admittedCount">How many of them the draw admitted.</param>
    /// <param name="maxFileCount">The cap the draw enforced on the kind.</param>
    /// <exception cref="ArgumentNullException"><paramref name="extension"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="foundCount"/> or <paramref name="admittedCount"/> is
    /// negative; <paramref name="maxFileCount"/> is not positive (a
    /// <see cref="FolderPickLimits"/> cap never is); or
    /// <paramref name="admittedCount"/> exceeds <paramref name="foundCount"/>
    /// or <paramref name="maxFileCount"/>.
    /// </exception>
    public SampledKind(string extension, int foundCount, int admittedCount, int maxFileCount)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentOutOfRangeException.ThrowIfNegative(foundCount);
        ArgumentOutOfRangeException.ThrowIfNegative(admittedCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(admittedCount, foundCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(admittedCount, maxFileCount);

        Extension = extension;
        FoundCount = foundCount;
        AdmittedCount = admittedCount;
        MaxFileCount = maxFileCount;
    }

    /// <summary>
    /// The kind, as a lower-case dot-bearing extension: always a key of
    /// <see cref="FolderPickLimits.MaxFileCounts"/> in a report the library
    /// built, which is the table the draw was enforced against.
    /// </summary>
    public string Extension { get; }

    /// <summary>
    /// How many top-level files of this kind the draw found in the folder.
    /// The module's own count, carried back across interop.
    /// </summary>
    public int FoundCount { get; }

    /// <summary>
    /// How many of them the draw admitted into the sample: all of them when
    /// <see cref="FoundCount"/> is within <see cref="MaxFileCount"/>, otherwise
    /// a uniformly random <see cref="MaxFileCount"/> of them. The module's own
    /// count, carried back across interop, never inferred here from the cap.
    /// </summary>
    public int AdmittedCount { get; }

    /// <summary>
    /// The cap the draw enforced on this kind. <b>Derived, never
    /// round-tripped</b>: it is not on the wire. The library's one
    /// construction site, <see cref="JsFolderAccess"/>'s report mapping, takes
    /// it from the same <see cref="FolderPickLimits"/> instance whose table it
    /// handed the module, so the figure a notice states is by construction the
    /// figure the draw enforced.
    /// </summary>
    public int MaxFileCount { get; }

    /// <summary>
    /// How many files of this kind the draw left unread:
    /// <see cref="FoundCount"/> minus <see cref="AdmittedCount"/>. Zero when the
    /// kind was within its cap; never negative.
    /// </summary>
    public int OmittedCount => FoundCount - AdmittedCount;
}
