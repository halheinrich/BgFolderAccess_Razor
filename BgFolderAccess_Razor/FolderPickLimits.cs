namespace BgFolderAccess_Razor;

/// <summary>
/// The caps a folder pick enforces on its <see cref="PickedFile"/>s — the
/// per-extension file-count table and the per-file byte cap — supplied by the
/// host as configuration. The library owns <i>enforcement</i> (per-kind
/// truncate-and-report for the count caps, fail-fast for the byte cap) but
/// ships <b>no numbers</b>: each host's values encode its own cost model and
/// stay host-side.
///
/// <para>
/// These are one rule with several consumers — <c>folderAccess.js</c>
/// <i>enforces</i> the count caps against the pick's metadata before any bytes
/// cross the interop boundary (the whole <see cref="MaxFileCounts"/> table is
/// handed to it on every enumeration, so the module holds no copy of its own),
/// <see cref="JsFolderAccess"/> enforces the byte cap and derives each reported
/// truncation's cap figure from this instance, and host pages document and
/// report them. The megabyte figure is <b>derived</b> from
/// <see cref="MaxFileBytes"/>, never restated: raising the byte cap moves every
/// stated figure with it.
/// </para>
///
/// <para>
/// Immutable, validated at construction. Register one instance in DI beside
/// <see cref="IFolderAccess"/>; <see cref="JsFolderAccess"/> takes it as a
/// constructor dependency.
/// </para>
/// </summary>
public sealed class FolderPickLimits
{
    private readonly Dictionary<string, int> _maxFileCounts;

    /// <summary>
    /// Create a validated limits configuration.
    /// </summary>
    /// <param name="maxFileCounts">
    /// The per-extension file-count caps, in the order any per-kind truncation
    /// report should read them (insertion order is preserved). Keys are
    /// lower-case, dot-leading extensions (e.g. <c>".xg"</c>); values are the
    /// maximum number of files of that kind a single pick admits.
    /// </param>
    /// <param name="maxFileBytes">Per-file size cap in bytes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="maxFileCounts"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The table is empty; an extension is not lower-case, dot-leading, and at
    /// least two characters; an extension appears twice; an extension is a
    /// suffix of another (the JS classifier takes the first suffix match, so an
    /// overlapping pair would make classification order-dependent — this
    /// hardens what was a comment-only assumption in the module); a count is
    /// not positive; or <paramref name="maxFileBytes"/> is not positive.
    /// </exception>
    public FolderPickLimits(IEnumerable<KeyValuePair<string, int>> maxFileCounts, long maxFileBytes)
    {
        ArgumentNullException.ThrowIfNull(maxFileCounts);
        if (maxFileBytes <= 0)
        {
            throw new ArgumentException("The per-file byte cap must be positive.", nameof(maxFileBytes));
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (extension, count) in maxFileCounts)
        {
            if (extension is null || extension.Length < 2 || extension[0] != '.'
                || extension != extension.ToLowerInvariant())
            {
                throw new ArgumentException(
                    $"'{extension}' is not a valid extension key — extensions are lower-case, "
                    + "dot-leading, and at least one character past the dot (e.g. \".xg\").",
                    nameof(maxFileCounts));
            }

            if (count <= 0)
            {
                throw new ArgumentException(
                    $"The count cap for '{extension}' must be positive.", nameof(maxFileCounts));
            }

            if (!counts.TryAdd(extension, count))
            {
                throw new ArgumentException(
                    $"'{extension}' appears more than once in the count-cap table.", nameof(maxFileCounts));
            }
        }

        if (counts.Count == 0)
        {
            throw new ArgumentException("The count-cap table must not be empty.", nameof(maxFileCounts));
        }

        // Suffix-disjointness: the JS classifier matches by name suffix and
        // takes the first hit, so a key that is a suffix of another key would
        // make a file's kind depend on table order. Rejecting the pair here
        // turns that comment-only module assumption into a constructed fact.
        foreach (var a in counts.Keys)
        {
            foreach (var b in counts.Keys)
            {
                if (!ReferenceEquals(a, b) && b.EndsWith(a, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"'{a}' is a suffix of '{b}' — extension keys must be suffix-disjoint "
                        + "so a file name matches at most one kind.",
                        nameof(maxFileCounts));
                }
            }
        }

        _maxFileCounts = counts;
        MaxFileBytes = maxFileBytes;
    }

    /// <summary>
    /// The per-extension file-count caps: the pick's matching-file kinds and
    /// what each one admits, in the order any per-kind report reads them.
    ///
    /// <para>
    /// <b>The whole table crosses the interop boundary</b> —
    /// <c>folderAccess.js</c> is handed it on every enumeration and derives
    /// <i>both</i> jobs from it: which names count as matching files, and how
    /// many of each to take. File count is only a cost proxy <i>within</i> one
    /// kind, so each extension truncates at its own cap independently and a
    /// mixed folder can admit its full quota of every kind. Keeping the table
    /// here rather than in the module is what stops the two languages from
    /// disagreeing about either job.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, int> MaxFileCounts => _maxFileCounts;

    /// <summary>
    /// Per-file size cap in bytes. Enforced at pick time by
    /// <see cref="JsFolderAccess"/>: checked against the enumerated metadata up
    /// front (an oversized file fails the whole pick), and re-asserted as the
    /// <c>IJSStreamReference.OpenReadStreamAsync</c> <c>maxAllowedSize</c> on
    /// the actual transfer.
    /// </summary>
    public long MaxFileBytes { get; }

    /// <summary>
    /// <see cref="MaxFileBytes"/> expressed in whole mebibytes (floored) — the
    /// human-facing figure error messages and host copy render. Derived, so
    /// stated prose and enforced rule cannot drift.
    /// </summary>
    public long MaxFileMegabytes => MaxFileBytes / (1024 * 1024);

    /// <summary>
    /// The cap applied to <paramref name="extension"/> — the lookup behind each
    /// reported <see cref="PickTruncation.MaxFileCount"/>, so a reported
    /// truncation states the same figure the pick enforced.
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// <paramref name="extension"/> is not a key of <see cref="MaxFileCounts"/>.
    /// Unreachable from a truncation report: the caps table is what taught the
    /// JS module which extensions exist, so it can only report back a key from
    /// this table.
    /// </exception>
    public int MaxFileCountFor(string extension) => _maxFileCounts[extension];
}
