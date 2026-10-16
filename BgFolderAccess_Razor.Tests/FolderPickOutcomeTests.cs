using BgFolderAccess_Razor;

namespace BgFolderAccess_Razor.Tests;

/// <summary>
/// The outcome value types' stated contracts: the cancelled singleton carries
/// nothing, a sample's collection members refuse a default array and every
/// write, a sample says whether it holds every matching file, and
/// <see cref="SampledKind"/> validates its counts and compares by value.
/// <see cref="PickedFile"/>, <see cref="FolderSample"/> and
/// <see cref="FolderPickOutcome"/> do not compare by value: they compare their
/// arrays (or the sample holding them) by reference, so a test compares their
/// members.
/// </summary>
public class FolderPickOutcomeTests
{
    [Fact]
    public void CancelledOutcome_CarriesNoFolderNoFilesNoKinds()
    {
        var outcome = FolderPickOutcome.CancelledOutcome;

        Assert.True(outcome.Cancelled);
        Assert.Null(outcome.Folder);
        Assert.Empty(outcome.Sample.Files);
        Assert.Empty(outcome.Sample.Kinds);
    }

    [Fact]
    public void Cancelled_IsExactlyHavingNoFolder()
    {
        // One fact, derived: an outcome with a folder is not cancelled, and there is no
        // second flag to disagree with the folder's presence.
        var outcome = new FolderPickOutcome(new StubFolder(), OneFileSample());

        Assert.False(outcome.Cancelled);
        Assert.NotNull(outcome.Folder);
    }

    /// <summary>A context that does nothing: the outcome only carries it.</summary>
    private sealed class StubFolder : IFolderContext
    {
        public string Name => "Corpus";

        public FolderWriteCapability Capability => FolderWriteCapability.Enabled;

        public Task<FolderSample> ResampleAsync() => throw new NotSupportedException();

        public Task<string?> ReadFileAsync(string fileName) => throw new NotSupportedException();

        public Task WriteFileAsync(string fileName, string text) => throw new NotSupportedException();

        public Task<FileWritability> ProbeFileWritabilityAsync(string fileName) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void Sample_Collections_ImplementExactlyTheInterfacesTheWriteTestCovers()
    {
        // The record's collection members are the type-level guarantee; the
        // write refusals themselves are pinned where the library builds real
        // samples (JsFolderAccessTests), which is where the List leaked.
        var sample = OneFileSample();

        ImmutableExposureAssert.AssertImplementsOnlyCoveredInterfaces<PickedFile>(sample.Files);
        ImmutableExposureAssert.AssertImplementsOnlyCoveredInterfaces<SampledKind>(sample.Kinds);
    }

    [Fact]
    public void Outcome_CarriesTheSampleItWasGiven()
    {
        var sample = OneFileSample();

        var folder = new StubFolder();

        var outcome = new FolderPickOutcome(folder, sample);

        Assert.Same(folder, outcome.Folder);
        Assert.Same(sample, outcome.Sample);
    }

    [Fact]
    public void SampledKind_ComparesByValue()
    {
        Assert.Equal(
            new SampledKind(".xg", foundCount: 512, admittedCount: 500, maxFileCount: 500),
            new SampledKind(".xg", foundCount: 512, admittedCount: 500, maxFileCount: 500));
        Assert.NotEqual(
            new SampledKind(".xg", foundCount: 512, admittedCount: 500, maxFileCount: 500),
            new SampledKind(".xg", foundCount: 512, admittedCount: 500, maxFileCount: 501));
        Assert.NotEqual(
            new SampledKind(".xg", foundCount: 3, admittedCount: 3, maxFileCount: 500),
            new SampledKind(".xg", foundCount: 3, admittedCount: 2, maxFileCount: 500));
    }

    [Fact]
    public void SampledKind_KeepsWhatItWasGiven_AndDerivesTheOmittedCount()
    {
        var kind = new SampledKind(".xgp", foundCount: 2340, admittedCount: 2000, maxFileCount: 2000);

        Assert.Equal(".xgp", kind.Extension);
        Assert.Equal(2340, kind.FoundCount);
        Assert.Equal(2000, kind.AdmittedCount);
        Assert.Equal(2000, kind.MaxFileCount);
        Assert.Equal(340, kind.OmittedCount);
    }

    [Theory]
    [InlineData(0, 0, 1)]     // a kind the folder held none of
    [InlineData(3, 3, 5)]     // within the cap: all admitted
    [InlineData(5, 5, 5)]     // exactly at the cap
    [InlineData(9, 5, 5)]     // over the cap: the cap admitted
    public void SampledKind_AcceptsTheBoundaries(int found, int admitted, int max)
    {
        var kind = new SampledKind(".xg", found, admitted, max);

        Assert.Equal(found - admitted, kind.OmittedCount);
    }

    [Fact]
    public void SampledKind_NullExtension_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SampledKind(null!, 1, 1, 1));
    }

    [Theory]
    [InlineData(-1, 0, 1, "foundCount")]     // a negative count found
    [InlineData(1, -1, 1, "admittedCount")]  // a negative count admitted
    [InlineData(1, 0, 0, "maxFileCount")]    // a cap no FolderPickLimits holds
    [InlineData(1, 0, -1, "maxFileCount")]
    [InlineData(2, 3, 5, "admittedCount")]   // admitted a file it did not find
    [InlineData(9, 6, 5, "admittedCount")]   // admitted past the cap
    public void SampledKind_AnImpossibleReport_IsRefused(int found, int admitted, int max, string parameter)
    {
        // An admitted count past the found count or the cap would argue the
        // derived OmittedCount and HoldsEveryMatchingFile into nonsense, so no
        // such report exists, whoever builds it.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new SampledKind(".xg", found, admitted, max));
        Assert.Equal(parameter, ex.ParamName);
    }

    [Fact]
    public void Sample_HoldsEveryMatchingFile_WhenEveryKindIsWhole()
    {
        var sample = new FolderSample(
            Files: [],
            Kinds:
            [
                new SampledKind(".xg", foundCount: 3, admittedCount: 3, maxFileCount: 500),
                new SampledKind(".xgp", foundCount: 0, admittedCount: 0, maxFileCount: 2000),
            ]);

        Assert.True(sample.HoldsEveryMatchingFile);
    }

    [Fact]
    public void Sample_DoesNotHoldEveryMatchingFile_WhenAnyKindIsShort()
    {
        // One kind cut short is enough: another draw at the same limits could
        // hold different files of it, whatever the other kinds did.
        var sample = new FolderSample(
            Files: [],
            Kinds:
            [
                new SampledKind(".xg", foundCount: 3, admittedCount: 3, maxFileCount: 500),
                new SampledKind(".xgp", foundCount: 2001, admittedCount: 2000, maxFileCount: 2000),
            ]);

        Assert.False(sample.HoldsEveryMatchingFile);
    }

    [Fact]
    public void Sample_WithNoKinds_HoldsEveryMatchingFile()
    {
        // The empty report is the cancelled outcome's filler — no draw ran,
        // so no kind was cut short. Pinned so the edge is a stated answer.
        Assert.True(FolderPickOutcome.CancelledOutcome.Sample.HoldsEveryMatchingFile);
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

    private static FolderSample OneFileSample() => new(
        Files: [new PickedFile("a.xg", [1])],
        Kinds: [new SampledKind(".xg", foundCount: 2, admittedCount: 1, maxFileCount: 1)]);

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
    public void FolderSample_DefaultFiles_RefusedByTheConstructorAndByWith()
    {
        var atConstruction = Assert.Throws<ArgumentException>(() => new FolderSample(Files: default, Kinds: []));
        Assert.Equal(nameof(FolderSample.Files), atConstruction.ParamName);

        var viaWith = Assert.Throws<ArgumentException>(() => OneFileSample() with { Files = default });
        Assert.Equal(nameof(FolderSample.Files), viaWith.ParamName);
    }

    [Fact]
    public void FolderSample_DefaultKinds_RefusedByTheConstructorAndByWith()
    {
        var atConstruction = Assert.Throws<ArgumentException>(() => new FolderSample(Files: [], Kinds: default));
        Assert.Equal(nameof(FolderSample.Kinds), atConstruction.ParamName);

        var viaWith = Assert.Throws<ArgumentException>(() => OneFileSample() with { Kinds = default });
        Assert.Equal(nameof(FolderSample.Kinds), viaWith.ParamName);
    }

    [Fact]
    public void EmptyArrays_AreAcceptedOnBothWaysIn()
    {
        // "No bytes", "no files" and "no kinds" are Empty, and the guard must
        // not mistake them for default — through either path.
        Assert.Empty(new PickedFile("empty.xg", []).Bytes);
        Assert.Empty((new PickedFile("a.xg", [1]) with { Bytes = [] }).Bytes);

        var emptied = OneFileSample() with { Files = [], Kinds = [] };
        Assert.Empty(emptied.Files);
        Assert.Empty(emptied.Kinds);
    }

    [Fact]
    public void WithAValidArray_ReplacesTheMember()
    {
        // The init accessors store what they are given, not just refuse.
        var file = new PickedFile("a.xg", [1]) with { Bytes = [7, 8] };
        Assert.Equal<byte>([7, 8], file.Bytes);

        var sample = OneFileSample() with { Files = [new PickedFile("b.xgp", [2])] };
        Assert.Equal("b.xgp", Assert.Single(sample.Files).FileName);
    }
}
