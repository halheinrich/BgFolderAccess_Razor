using BgFolderAccess_Razor;

namespace BgFolderAccess_Razor.Tests;

/// <summary>
/// The outcome value types' stated contracts: the cancelled singleton carries
/// nothing, the collection members refuse a default array and every write,
/// and <see cref="PickTruncation"/> compares by value. <see cref="PickedFile"/>
/// and <see cref="FolderPickOutcome"/> do not: they compare their arrays by
/// reference, so a test compares their members.
/// </summary>
public class FolderPickOutcomeTests
{
    [Fact]
    public void CancelledOutcome_CarriesNoFolderNoFilesNoTruncations()
    {
        var outcome = FolderPickOutcome.CancelledOutcome;

        Assert.True(outcome.Cancelled);
        Assert.Equal("", outcome.DirectoryName);
        Assert.Empty(outcome.Files);
        Assert.Empty(outcome.Truncations);
    }

    [Fact]
    public void Outcome_Collections_ImplementExactlyTheInterfacesTheWriteTestCovers()
    {
        // The records' collection members are the type-level guarantee; the
        // write refusals themselves are pinned where the library builds real
        // outcomes (JsFolderAccessTests), which is where the List leaked.
        var outcome = new FolderPickOutcome(
            Cancelled: false, "Corpus", [new PickedFile("a.xg", [1])], FolderWriteCapability.Enabled,
            [new PickTruncation(".xg", OmittedCount: 1, MaxFileCount: 1)]);

        ImmutableExposureAssert.AssertImplementsOnlyCoveredInterfaces<PickedFile>(outcome.Files);
        ImmutableExposureAssert.AssertImplementsOnlyCoveredInterfaces<PickTruncation>(outcome.Truncations);
    }

    [Fact]
    public void PickTruncation_ComparesByValue()
    {
        Assert.Equal(
            new PickTruncation(".xg", OmittedCount: 12, MaxFileCount: 500),
            new PickTruncation(".xg", OmittedCount: 12, MaxFileCount: 500));
        Assert.NotEqual(
            new PickTruncation(".xg", OmittedCount: 12, MaxFileCount: 500),
            new PickTruncation(".xg", OmittedCount: 12, MaxFileCount: 501));
    }

    [Fact]
    public void PickedFile_KeepsTheExtensionBearingName()
    {
        // The stated FileName contract: the extension survives buffering, so a
        // host may discriminate format from it.
        var file = new PickedFile("match.xg", [1, 2, 3]);

        Assert.Equal("match.xg", file.FileName);
        Assert.Equal<byte>([1, 2, 3], file.Bytes);
    }

    [Fact]
    public void PickedFile_Bytes_ImplementsExactlyTheInterfacesTheWriteTestCovers()
    {
        ImmutableExposureAssert.AssertImplementsOnlyCoveredInterfaces<byte>(
            new PickedFile("match.xg", [1, 2, 3]).Bytes);
    }

    [Fact]
    public void PickedFile_Bytes_EveryWriteThroughAnyInterfaceThrows_AndTheBytesReadTheSame()
    {
        // The contents used to be a public byte[] — writable in place, with no
        // cast needed, under every other holder of the same outcome
        // (halheinrich/backgammon#273).
        var file = new PickedFile("match.xg", [1, 2, 3]);

        ImmutableExposureAssert.AssertEveryWriteRefused<byte>(file.Bytes, 0xFF);

        Assert.Equal<byte>([1, 2, 3], file.Bytes);
    }

    [Fact]
    public void PickedFile_OpenRead_ReadsTheBytes_AndEachCallIsFresh()
    {
        // The re-enumeration contract: one pass draining its stream must leave
        // the next pass's stream at the start.
        var file = new PickedFile("match.xg", [1, 2, 3]);

        using var first = file.OpenRead();
        using var second = file.OpenRead();
        using var drained = new MemoryStream();
        first.CopyTo(drained);

        Assert.Equal<byte>([1, 2, 3], drained.ToArray());
        Assert.Equal(0, second.Position);
        Assert.Equal(3, second.Length);
    }

    [Fact]
    public void PickedFile_OpenRead_CannotWriteOrHandOutItsBuffer_AndTheBytesReadTheSame()
    {
        // OpenRead shares the array rather than copying it, so the stream is
        // the one door left to it: it must neither write nor hand the buffer
        // out, whether it is used as a Stream or cast to what it is.
        var file = new PickedFile("match.xg", [1, 2, 3]);
        using var stream = file.OpenRead();

        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Write([9], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.WriteByte(9));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));

        var memory = Assert.IsType<MemoryStream>(stream);
        Assert.Throws<UnauthorizedAccessException>(() => memory.GetBuffer());
        Assert.False(memory.TryGetBuffer(out _));

        Assert.Equal<byte>([1, 2, 3], file.Bytes);
    }

    // A default ImmutableArray holds no array at all — not an empty one — and
    // compiles silently where a null for the reference types these members
    // used to be failed the build under nullable warnings-as-errors. Each
    // member refuses one on both ways in: the primary constructor, and `with`.
    // One test per member, each path asserted separately, so dropping any one
    // of the six refusals fails exactly one test.

    private static FolderPickOutcome OneFileOutcome() => new(
        Cancelled: false, "Corpus", [new PickedFile("a.xg", [1])], FolderWriteCapability.Enabled,
        [new PickTruncation(".xg", OmittedCount: 1, MaxFileCount: 1)]);

    [Fact]
    public void PickedFile_DefaultBytes_RefusedByTheConstructorAndByWith()
    {
        var atConstruction = Assert.Throws<ArgumentException>(() => new PickedFile("match.xg", default));
        Assert.Equal(nameof(PickedFile.Bytes), atConstruction.ParamName);

        var file = new PickedFile("match.xg", [1, 2, 3]);
        var viaWith = Assert.Throws<ArgumentException>(() => file with { Bytes = default });
        Assert.Equal(nameof(PickedFile.Bytes), viaWith.ParamName);
    }

    [Fact]
    public void FolderPickOutcome_DefaultFiles_RefusedByTheConstructorAndByWith()
    {
        var atConstruction = Assert.Throws<ArgumentException>(() => new FolderPickOutcome(
            Cancelled: false, "Corpus", Files: default, FolderWriteCapability.Enabled, Truncations: []));
        Assert.Equal(nameof(FolderPickOutcome.Files), atConstruction.ParamName);

        var viaWith = Assert.Throws<ArgumentException>(() => OneFileOutcome() with { Files = default });
        Assert.Equal(nameof(FolderPickOutcome.Files), viaWith.ParamName);
    }

    [Fact]
    public void FolderPickOutcome_DefaultTruncations_RefusedByTheConstructorAndByWith()
    {
        var atConstruction = Assert.Throws<ArgumentException>(() => new FolderPickOutcome(
            Cancelled: false, "Corpus", Files: [], FolderWriteCapability.Enabled, Truncations: default));
        Assert.Equal(nameof(FolderPickOutcome.Truncations), atConstruction.ParamName);

        var viaWith = Assert.Throws<ArgumentException>(() => OneFileOutcome() with { Truncations = default });
        Assert.Equal(nameof(FolderPickOutcome.Truncations), viaWith.ParamName);
    }

    [Fact]
    public void EmptyArrays_AreAcceptedOnBothWaysIn()
    {
        // "No bytes", "no files" and "nothing left behind" are Empty, and the
        // guard must not mistake them for default — through either path.
        Assert.Empty(new PickedFile("empty.xg", []).Bytes);
        Assert.Empty((new PickedFile("a.xg", [1]) with { Bytes = [] }).Bytes);

        var emptied = OneFileOutcome() with { Files = [], Truncations = [] };
        Assert.Empty(emptied.Files);
        Assert.Empty(emptied.Truncations);
    }

    [Fact]
    public void WithAValidArray_ReplacesTheMember()
    {
        // The init accessors store what they are given, not just refuse.
        var file = new PickedFile("a.xg", [1]) with { Bytes = [7, 8] };
        Assert.Equal<byte>([7, 8], file.Bytes);

        var outcome = OneFileOutcome() with { Files = [new PickedFile("b.xgp", [2])] };
        Assert.Equal("b.xgp", Assert.Single(outcome.Files).FileName);
    }
}
