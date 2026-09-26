using BgFolderAccess_Razor;

namespace BgFolderAccess_Razor.Tests;

/// <summary>
/// The host-supplied caps configuration: construction validates everything the
/// enforcement machinery assumes (so a bad table fails at registration, not
/// mid-pick), the table preserves the host's order (truncation reports read in
/// it), nothing it hands out can be written back into, and the derived figures
/// come from the one stored rule.
/// </summary>
public class FolderPickLimitsTests
{
    private static FolderPickLimits Valid() => new(
        new Dictionary<string, int> { [".xg"] = 3, [".xgp"] = 5 },
        maxFileBytes: 2L * 1024 * 1024);

    /// <summary>
    /// A table whose order is neither ordinal nor length order, handed in as a
    /// plain pair sequence — so an implementation that sorted the table, or
    /// leaned on some keyed collection's enumeration, would read it back in a
    /// different order and fail the order pins.
    /// </summary>
    private static readonly KeyValuePair<string, int>[] UnsortedTable =
    [
        new(".xgp", 5),
        new(".mat", 7),
        new(".xg", 3),
    ];

    [Fact]
    public void Ctor_ValidTable_ExposesCountsInTheGivenOrder()
    {
        var limits = new FolderPickLimits(UnsortedTable, maxFileBytes: 1);

        Assert.Equal<KeyValuePair<string, int>>(UnsortedTable, limits.MaxFileCounts);
        // Position, not just enumeration: entry i is the i-th pair given.
        for (var i = 0; i < UnsortedTable.Length; i++)
        {
            Assert.Equal(UnsortedTable[i], limits.MaxFileCounts[i]);
        }
    }

    [Fact]
    public void Ctor_CopiesTheTable_SoTheCallersSourceCannotReachIt()
    {
        // The host's own collection stays the host's: writing to it after
        // construction must not move a validated cap. An array source, because
        // an array is the one shape an ImmutableArray could wrap without
        // copying — the aliasing this pin exists to refuse.
        var source = (KeyValuePair<string, int>[])UnsortedTable.Clone();
        var limits = new FolderPickLimits(source, maxFileBytes: 1);

        source[0] = new(".xgp", -1);

        Assert.Equal<KeyValuePair<string, int>>(UnsortedTable, limits.MaxFileCounts);
    }

    [Fact]
    public void MaxFileCounts_ImplementsExactlyTheInterfacesTheWriteTestCovers()
    {
        ImmutableExposureAssert.AssertImplementsOnlyCoveredInterfaces<KeyValuePair<string, int>>(
            Valid().MaxFileCounts);
    }

    [Fact]
    public void MaxFileCounts_EveryWriteThroughAnyInterfaceThrows_AndTheTableReadsTheSame()
    {
        // The regression this pins (halheinrich/backgammon#273): the table
        // used to be the private Dictionary behind IReadOnlyDictionary, so a
        // cast wrote caps past the constructor's validation. Every writing
        // member of every interface the exposed object implements is tried;
        // each must refuse, and the table must read exactly as constructed.
        var limits = new FolderPickLimits(UnsortedTable, maxFileBytes: 1);

        ImmutableExposureAssert.AssertEveryWriteRefused(
            limits.MaxFileCounts, new KeyValuePair<string, int>(".xg", -1));

        Assert.Equal<KeyValuePair<string, int>>(UnsortedTable, limits.MaxFileCounts);
        Assert.Equal(5, limits.MaxFileCountFor(".xgp"));
    }

    [Fact]
    public void MaxFileCountFor_KnownExtension_ReturnsItsCap()
    {
        var limits = Valid();

        Assert.Equal(3, limits.MaxFileCountFor(".xg"));
        Assert.Equal(5, limits.MaxFileCountFor(".xgp"));
    }

    [Fact]
    public void MaxFileCountFor_UnknownExtension_Throws()
    {
        // Documented unreachable from a truncation report (the table taught the
        // JS module which extensions exist), but the contract for any other
        // caller is a loud KeyNotFoundException, not a silent default.
        var limits = Valid();

        Assert.Throws<KeyNotFoundException>(() => limits.MaxFileCountFor(".mat"));
    }

    [Fact]
    public void MaxFileMegabytes_IsDerivedFromTheByteCap()
    {
        Assert.Equal(2, Valid().MaxFileMegabytes);
    }

    [Fact]
    public void MaxFileMegabytes_FloorsAPartialMebibyte()
    {
        // The figure is human-facing prose; a non-whole-MiB cap states the
        // floored whole number rather than inventing a fraction.
        var limits = new FolderPickLimits(
            new Dictionary<string, int> { [".xg"] = 1 },
            maxFileBytes: (3L * 1024 * 1024) + 1);

        Assert.Equal(3, limits.MaxFileMegabytes);
    }

    [Fact]
    public void Ctor_NullTable_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new FolderPickLimits(null!, 1));
    }

    [Fact]
    public void Ctor_EmptyTable_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new FolderPickLimits(new Dictionary<string, int>(), 1));
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(".XG")]   // not lower-case
    [InlineData("xg")]    // no leading dot
    [InlineData(".")]     // nothing past the dot
    [InlineData("")]      // empty
    public void Ctor_MalformedExtension_Throws(string extension)
    {
        Assert.Throws<ArgumentException>(() => new FolderPickLimits(
            new[] { new KeyValuePair<string, int>(extension, 1) }, 1));
    }

    [Fact]
    public void Ctor_DuplicateExtension_Throws()
    {
        // A Dictionary source can't express this; the ctor takes any pair
        // sequence, so it has to reject the duplicate itself.
        var ex = Assert.Throws<ArgumentException>(() => new FolderPickLimits(
            [
                new KeyValuePair<string, int>(".xg", 1),
                new KeyValuePair<string, int>(".xg", 2),
            ],
            1));
        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void Ctor_SuffixOverlappingExtensions_Throws()
    {
        // The JS classifier matches by name suffix and takes the first hit, so
        // '.gz' alongside '.tar.gz' would make an 'a.tar.gz' file's kind depend
        // on table order (it name-matches both). This was a comment-only
        // assumption in the module; the ctor makes it a constructed fact.
        var ex = Assert.Throws<ArgumentException>(() => new FolderPickLimits(
            new Dictionary<string, int> { [".gz"] = 1, [".tar.gz"] = 1 }, 1));
        Assert.Contains("suffix", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_NonPositiveCount_Throws(int count)
    {
        Assert.Throws<ArgumentException>(() => new FolderPickLimits(
            new Dictionary<string, int> { [".xg"] = count }, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_NonPositiveByteCap_Throws(long maxFileBytes)
    {
        Assert.Throws<ArgumentException>(() => new FolderPickLimits(
            new Dictionary<string, int> { [".xg"] = 1 }, maxFileBytes));
    }
}
