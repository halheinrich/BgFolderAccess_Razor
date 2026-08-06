using BgFolderAccess_Razor;

namespace BgFolderAccess_Razor.Tests;

/// <summary>
/// The host-supplied caps configuration: construction validates everything the
/// enforcement machinery assumes (so a bad table fails at registration, not
/// mid-pick), the table preserves the host's order (truncation reports read in
/// it), and the derived figures come from the one stored rule.
/// </summary>
public class FolderPickLimitsTests
{
    private static FolderPickLimits Valid() => new(
        new Dictionary<string, int> { [".xg"] = 3, [".xgp"] = 5 },
        maxFileBytes: 2L * 1024 * 1024);

    [Fact]
    public void Ctor_ValidTable_ExposesCountsInInsertionOrder()
    {
        var limits = Valid();

        Assert.Collection(
            limits.MaxFileCounts,
            first =>
            {
                Assert.Equal(".xg", first.Key);
                Assert.Equal(3, first.Value);
            },
            second =>
            {
                Assert.Equal(".xgp", second.Key);
                Assert.Equal(5, second.Value);
            });
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
