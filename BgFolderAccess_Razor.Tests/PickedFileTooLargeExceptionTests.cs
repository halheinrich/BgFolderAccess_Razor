namespace BgFolderAccess_Razor.Tests;

/// <summary>
/// The byte cap's exception as a type: what it carries, what it refuses to
/// claim, and — the reason it exists — that it is not the
/// <see cref="InvalidOperationException"/> a caller error throws, so a host
/// handling one cannot catch the other with it.
/// </summary>
public class PickedFileTooLargeExceptionTests
{
    private static readonly FolderPickLimits Limits = new(
        new Dictionary<string, int> { [".xg"] = 3 },
        maxFileBytes: (5L * 1024 * 1024) + 7);

    [Fact]
    public void Constructor_KeepsTheFileAndTakesTheCapFromTheLimits()
    {
        var ex = new PickedFileTooLargeException("huge.xg", Limits.MaxFileBytes + 1, Limits);

        Assert.Equal("huge.xg", ex.FileName);
        Assert.Equal(Limits.MaxFileBytes + 1, ex.FileSize);
        Assert.Equal(Limits.MaxFileBytes, ex.MaxFileBytes);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void Message_NamesTheFileAndStatesTheLimitsOwnMegabyteFigure()
    {
        // The figure is FolderPickLimits.MaxFileMegabytes (floored: a cap of
        // 5 MiB and 7 bytes states 5), not a second computation of it, and the
        // wording is the one hosts already show.
        var ex = new PickedFileTooLargeException("huge.xg", Limits.MaxFileBytes + 1, Limits);

        Assert.Equal($"'huge.xg' is larger than the {Limits.MaxFileMegabytes} MB per-file limit.", ex.Message);
        Assert.Equal(5, Limits.MaxFileMegabytes);
    }

    [Theory]
    [InlineData(0L)]   // exactly at the cap: the cap admits it
    [InlineData(-1L)]  // under the cap
    public void Constructor_RefusesASizeTheCapDoesNotExceed(long overTheCap)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new PickedFileTooLargeException("fine.xg", Limits.MaxFileBytes + overTheCap, Limits));

        Assert.Equal("fileSize", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(
            () => new PickedFileTooLargeException(null!, Limits.MaxFileBytes + 1, Limits));
        Assert.Throws<ArgumentNullException>(
            () => new PickedFileTooLargeException("huge.xg", Limits.MaxFileBytes + 1, null!));
    }

    [Fact]
    public void IsNotAnInvalidOperationException_InEitherDirection()
    {
        // "No folder held" is an InvalidOperationException, a caller bug. A
        // catch for one of the two must never also catch the other, which
        // holds only while neither type derives from the other.
        Assert.False(typeof(InvalidOperationException).IsAssignableFrom(typeof(PickedFileTooLargeException)));
        Assert.False(typeof(PickedFileTooLargeException).IsAssignableFrom(typeof(InvalidOperationException)));
    }

    [Fact]
    public void DerivesFromExceptionDirectly_AndIsSealed()
    {
        // No narrower base fits (it is not an I/O failure either), and no
        // subtype can widen what a catch of it takes in.
        Assert.Equal(typeof(Exception), typeof(PickedFileTooLargeException).BaseType);
        Assert.True(typeof(PickedFileTooLargeException).IsSealed);
    }
}
