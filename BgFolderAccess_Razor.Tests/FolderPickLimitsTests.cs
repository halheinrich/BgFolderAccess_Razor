using System.Collections;
using System.Collections.Immutable;
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

    /// <summary>
    /// Every interface the exposed table implements, as the one list the write
    /// test below must cover. Pinned by exact set: a future shape that
    /// implements something new fails here until its writing members are
    /// attempted too, so "any interface" cannot silently shrink to "the ones
    /// someone remembered".
    /// </summary>
    private static readonly Type[] TableInterfaces =
    [
        typeof(IList<KeyValuePair<string, int>>),
        typeof(ICollection<KeyValuePair<string, int>>),
        typeof(IEnumerable<KeyValuePair<string, int>>),
        typeof(IReadOnlyList<KeyValuePair<string, int>>),
        typeof(IReadOnlyCollection<KeyValuePair<string, int>>),
        typeof(IImmutableList<KeyValuePair<string, int>>),
        typeof(IEquatable<ImmutableArray<KeyValuePair<string, int>>>),
        typeof(IList),
        typeof(ICollection),
        typeof(IEnumerable),
        typeof(IStructuralComparable),
        typeof(IStructuralEquatable),
    ];

    /// <summary>
    /// The one interface outside <see cref="TableInterfaces"/>: internal to
    /// System.Collections.Immutable, so no code outside that assembly can
    /// name it, and a cast to it cannot be written.
    /// </summary>
    private const string FrameworkInternalInterface = "System.Collections.Immutable.IImmutableArray";

    [Fact]
    public void MaxFileCounts_ImplementsExactlyTheInterfacesTheWriteTestCovers()
    {
        object table = Valid().MaxFileCounts;
        var implemented = table.GetType().GetInterfaces();

        var frameworkInternal = Assert.Single(implemented, t => !t.IsPublic);
        Assert.Equal(FrameworkInternalInterface, frameworkInternal.FullName);
        Assert.Equal(
            TableInterfaces.Select(t => t.FullName).Order(),
            implemented.Where(t => t.IsPublic).Select(t => t.FullName).Order());
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
        object table = limits.MaxFileCounts;
        var intruder = new KeyValuePair<string, int>(".xg", -1);

        var generic = Assert.IsAssignableFrom<IList<KeyValuePair<string, int>>>(table);
        Assert.True(generic.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => generic[0] = intruder);
        Assert.Throws<NotSupportedException>(() => generic.Add(intruder));
        Assert.Throws<NotSupportedException>(() => generic.Insert(0, intruder));
        Assert.Throws<NotSupportedException>(() => generic.Remove(generic[0]));
        Assert.Throws<NotSupportedException>(() => generic.RemoveAt(0));
        Assert.Throws<NotSupportedException>(generic.Clear);

        var nonGeneric = Assert.IsAssignableFrom<IList>(table);
        Assert.True(nonGeneric.IsReadOnly);
        Assert.True(nonGeneric.IsFixedSize);
        Assert.Throws<NotSupportedException>(() => nonGeneric[0] = intruder);
        Assert.Throws<NotSupportedException>(() => nonGeneric.Add(intruder));
        Assert.Throws<NotSupportedException>(() => nonGeneric.Insert(0, intruder));
        Assert.Throws<NotSupportedException>(() => nonGeneric.Remove(nonGeneric[0]));
        Assert.Throws<NotSupportedException>(() => nonGeneric.RemoveAt(0));
        Assert.Throws<NotSupportedException>(nonGeneric.Clear);

        // ICollection's one writer copies out; it cannot write in.
        var copy = new KeyValuePair<string, int>[UnsortedTable.Length];
        Assert.IsAssignableFrom<ICollection>(table).CopyTo(copy, 0);
        copy[0] = intruder;

        // IImmutableList's "writers" answer a new list and leave this one be —
        // the read-back below is what proves "leave this one be".
        var immutable = Assert.IsAssignableFrom<IImmutableList<KeyValuePair<string, int>>>(table);
        Assert.Equal(intruder, immutable.SetItem(0, intruder)[0]);
        _ = immutable.Add(intruder);
        _ = immutable.Insert(0, intruder);
        _ = immutable.RemoveAt(0);
        _ = immutable.Clear();

        // The remaining interfaces (IEnumerable, IReadOnlyList and their
        // bases, IEquatable, IStructuralComparable, IStructuralEquatable)
        // declare no writing member at all — a write cannot be expressed.
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
