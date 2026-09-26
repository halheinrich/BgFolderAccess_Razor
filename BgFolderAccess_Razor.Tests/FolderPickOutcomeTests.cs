using BgFolderAccess_Razor;

namespace BgFolderAccess_Razor.Tests;

/// <summary>
/// The outcome value types' stated contracts: the cancelled singleton carries
/// nothing, and the records compare by value — what a host's fake-backed tests
/// lean on when asserting against constructed outcomes.
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

    [Fact]
    public void PickedFile_OpenRead_OverDefaultBytes_ThrowsNamingTheFile()
    {
        // default(ImmutableArray<byte>) holds no array, not an empty one — a
        // host-only construction the library never makes, refused by name
        // rather than as an anonymous null buffer.
        var file = new PickedFile("match.xg", default);

        var ex = Assert.Throws<InvalidOperationException>(file.OpenRead);
        Assert.Contains("match.xg", ex.Message);
    }
}
