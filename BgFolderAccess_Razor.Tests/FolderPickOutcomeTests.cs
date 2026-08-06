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
        Assert.Equal([1, 2, 3], file.Bytes);
    }
}
