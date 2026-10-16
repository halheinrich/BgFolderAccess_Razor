using Jint;

namespace BgFolderAccess_Razor.Tests;

using static BgFolderAccess_Razor.Tests.FolderAccessModuleHost;

/// <summary>
/// Resample as <c>folderAccess.js</c> actually runs it, through
/// <see cref="FolderAccessModuleHost"/>: a second draw of a landed folder, by
/// the same <c>draw</c> a pick ends in, with no picker and no permission
/// request — and what the module still holds afterwards, whether the resample
/// succeeded or failed part-way; and the context table itself: two picks are
/// two folders, each drawn and released on its own.
///
/// <para>
/// The draw's own invariants (subset, per-kind independence, encounter order,
/// the report) are pinned on picks in <see cref="FolderAccessSamplingTests"/>.
/// A resample runs the same function, so these tests pin what is particular
/// to drawing <i>again</i>: that repeated draws reach every file, that the
/// File System Access walk is fresh while the fallback redraws its retained
/// candidates, that the walk still stats only what it takes, and that nothing
/// a failed resample touched is left changed.
/// </para>
/// </summary>
public class FolderAccessResampleTests
{
    /// <summary>Both pick mechanisms: a resample works on either.</summary>
    public static TheoryData<Mechanism> Mechanisms =>
        [Mechanism.FileSystemAccess, Mechanism.Fallback];

    [Theory]
    [MemberData(nameof(Mechanisms))]
    public void RepeatedResamples_ReachEveryFileOfAnOverLimitKind(Mechanism mechanism)
    {
        string[] folder = ["a.xg", "b.xg", "c.xg"];
        var host = new FolderAccessModuleHost();
        var seen = new HashSet<string>(host.Pick(mechanism, folder, (".xg", 1)).Files);

        // The property that tells a random draw from a first-N rule, on the
        // resample path: one pick, then draws of the SAME held folder. Under a
        // first-N rule the set never grows past { "a.xg" }. Each draw takes 1
        // of 3, so one file is missed by all 61 draws with probability
        // (2/3)^61, about 2e-11 — under 1e-10 across all three.
        for (var draw = 0; draw < 60; draw++)
        {
            seen.UnionWith(host.Resample((".xg", 1)).Files);
        }

        Assert.Equal([.. folder], [.. seen.Order()]);
    }

    [Theory]
    [MemberData(nameof(Mechanisms))]
    public void Resample_AdmitsAKindWithinItsLimitWholeAgain_InFolderOrder(Mechanism mechanism)
    {
        string[] folder = ["a.xg", "b.xgp", "c.xg", "d.xgp", "e.xg", "f.xg"];
        var host = new FolderAccessModuleHost();
        host.Pick(mechanism, folder, (".xg", 2), (".xgp", 5));

        var result = host.Resample((".xg", 2), (".xgp", 5));

        // The under-limit kind is whole and in folder order on the resample as
        // on the pick, the over-limit kind is drawn down to its cap, and the
        // admitted files are a subsequence of the folder across kinds.
        Assert.Equal(["b.xgp", "d.xgp"], [.. result.Files.Where(name => name.EndsWith(".xgp"))]);
        Assert.Equal(2, result.Files.Count(name => name.EndsWith(".xg")));
        Assert.Equal([.. folder.Where(result.Files.Contains)], result.Files);
        Assert.Equal<DrawnKind>([new(".xg", 4, 2), new(".xgp", 2, 2)], result.Kinds);
    }

    [Theory]
    [MemberData(nameof(Mechanisms))]
    public void Resample_OfAFolderWithinEveryCap_DrawsTheSameFilesInTheSameOrder(Mechanism mechanism)
    {
        string[] folder = ["a.xg", "b.xgp", "c.xg"];
        var host = new FolderAccessModuleHost();
        var picked = host.Pick(mechanism, folder, (".xg", 3), (".xgp", 5));

        var resampled = host.Resample((".xg", 3), (".xgp", 5));

        // The only sample the limits could draw, drawn again.
        Assert.Equal(picked.Files, resampled.Files);
        Assert.Equal(picked.Kinds, resampled.Kinds);
        Assert.Equal(folder, resampled.Files);
    }

    [Theory]
    [MemberData(nameof(Mechanisms))]
    public void Resample_NeverOpensThePickerOrRequestsPermission(Mechanism mechanism)
    {
        var host = new FolderAccessModuleHost();
        host.Pick(mechanism, ["a.xg", "b.xg"], (".xg", 1));
        var afterThePick = host.BrowserCallCounts();

        host.Resample((".xg", 1));
        host.ResampleAndBuffer((".xg", 1));

        // Whatever the pick asked the browser (the fallback asks nothing here),
        // a resample asks nothing more.
        Assert.Equal(afterThePick, host.BrowserCallCounts());
    }

    [Fact]
    public void FileSystemAccessResample_WalksAfresh_SoSeesAFileAddedAfterThePick()
    {
        var host = new FolderAccessModuleHost();
        host.Pick(Mechanism.FileSystemAccess, ["a.xg", "c.xg"], (".xg", 5));

        host.AddFile("b.xgp");
        host.AddFile("d.xg");
        var result = host.Resample((".xg", 5), (".xgp", 5));

        Assert.Equal(["a.xg", "c.xg", "b.xgp", "d.xg"], result.Files);
        Assert.Equal<DrawnKind>([new(".xg", 3, 3), new(".xgp", 1, 1)], result.Kinds);
    }

    [Fact]
    public void FallbackResample_RedrawsTheCandidatesAsCollected_SoSeesNothingAddedSince()
    {
        // The fallback has no handle to walk again: the browser handed the
        // folder over once, and a resample redraws that list.
        var host = new FolderAccessModuleHost();
        host.Pick(Mechanism.Fallback, ["a.xg", "c.xg"], (".xg", 5));

        host.AddFile("d.xg");
        var result = host.Resample((".xg", 5));

        Assert.Equal(["a.xg", "c.xg"], result.Files);
        Assert.Equal<DrawnKind>([new(".xg", 2, 2)], result.Kinds);
    }

    [Fact]
    public void FallbackResample_ClassifiesTheCandidatesAgainstTheTableItIsHanded()
    {
        // The candidates are every top-level file, not the pick's matches: the
        // module keeps nothing derived from a caps table, so a kind the pick's
        // table did not name is found by a draw whose table does.
        var host = new FolderAccessModuleHost();
        host.Pick(Mechanism.Fallback, ["a.xg", "b.xgp"], (".xg", 5));

        var result = host.Resample((".xg", 5), (".xgp", 5));

        Assert.Equal(["a.xg", "b.xgp"], result.Files);
        Assert.Equal<DrawnKind>([new(".xg", 1, 1), new(".xgp", 1, 1)], result.Kinds);
    }

    [Fact]
    public void FileSystemAccessResample_StatsOnlyTheFilesItTakes()
    {
        var host = new FolderAccessModuleHost();
        host.Pick(
            Mechanism.FileSystemAccess,
            [.. Enumerable.Range(0, 9).Select(i => $"f{i}.xg"), "a.xgp", "b.xgp"],
            (".xg", 3),
            (".xgp", 5));

        var result = host.Resample((".xg", 3), (".xgp", 5));

        // The pick's cost property, on the resample's walk: eleven files walked
        // past, five drawn, five getFile() calls.
        Assert.Equal(5, result.Files.Count);
        Assert.Equal(5, result.Stats);
    }

    [Fact]
    public void ARefusedWalk_FailsTheResample_AndLeavesTheFolderResamplable()
    {
        var host = new FolderAccessModuleHost();
        host.Pick(Mechanism.FileSystemAccess, ["a.xg", "b.xg"], (".xg", 5));

        // The permission withdrawn, or the folder gone: the walk rejects, and
        // the rejection is the resample's failure.
        host.RefuseWalk(true);
        var refused = Assert.ThrowsAny<JintException>(() => host.Resample((".xg", 5)));
        Assert.Contains("NotAllowedError", refused.ToString());

        // The folder was never let go of: once the browser lets the walk
        // through again, the same folder draws.
        host.RefuseWalk(false);
        var result = host.Resample((".xg", 5));
        Assert.Equal(["a.xg", "b.xg"], result.Files);
    }

    [Fact]
    public void FileSystemAccess_AReadFailingAfterTheDraw_LeavesTheFolderAndItsDocuments()
    {
        var host = new FolderAccessModuleHost();
        host.Pick(Mechanism.FileSystemAccess, ["a.xg", "b.xg", "c.xg"], (".xg", 5));
        host.WriteFile("setup.json", "{\"kept\":true}");

        // a.xg buffers, then b.xg's bytes fail to read: the whole resample
        // fails — the harness loop stops where JsFolderAccess's would, and
        // nothing comes back.
        host.FailReadOf("b.xg");
        Assert.ThrowsAny<JintException>(() => host.ResampleAndBuffer((".xg", 5)));

        // Everything a later operation reads is as it was: the folder's
        // document and probe, and the folder itself, which the next resample
        // draws whole.
        Assert.Equal("{\"kept\":true}", host.ReadFile("setup.json"));
        Assert.Equal("writable", host.ProbeFile("setup.json"));
        host.FailReadOf(null);
        Assert.Equal(["a.xg:4", "b.xg:4", "c.xg:4"], host.ResampleAndBuffer((".xg", 5)));
    }

    [Fact]
    public void Fallback_AReadFailingAfterTheDraw_LeavesTheCandidates()
    {
        // The .xg files are within their cap, so a.xg is buffered and b.xg is
        // the second read, every time; the .xgp kind is over its cap, so a
        // module that narrowed its candidates to what it drew would show it.
        (string, int)[] caps = [(".xg", 5), (".xgp", 1)];
        var host = new FolderAccessModuleHost();
        host.Pick(Mechanism.Fallback, ["a.xg", "b.xg", "p1.xgp", "p2.xgp", "p3.xgp"], caps);

        host.FailReadOf("b.xg");
        Assert.ThrowsAny<JintException>(() => host.ResampleAndBuffer(caps));

        // Every candidate is still there to draw and to read, and the
        // fallback's folder still offers no handle for named files, as before.
        host.FailReadOf(null);
        Assert.Equal<DrawnKind>([new(".xg", 2, 2), new(".xgp", 3, 1)], host.Resample(caps).Kinds);
        var buffered = host.ResampleAndBuffer(caps);
        Assert.Equal(["a.xg:4", "b.xg:4"], buffered.Take(2));
        Assert.Equal(3, buffered.Count);
        Assert.ThrowsAny<JintException>(() => host.ReadFile("setup.json"));
    }

    [Theory]
    [MemberData(nameof(Mechanisms))]
    public void Release_FreesTheFolder_AndTheRetainedCandidates(Mechanism mechanism)
    {
        var host = new FolderAccessModuleHost();
        host.Pick(mechanism, ["a.xg", "b.xg"], (".xg", 5));
        Assert.Equal(4, host.ReadFileData("a.xg")); // readable while held

        host.Release();

        // Nothing is left to draw from, or to read a drawn name against: a
        // released context is a caller error on every operation.
        Assert.ThrowsAny<JintException>(() => host.Resample((".xg", 5)));
        Assert.ThrowsAny<JintException>(() => host.ReadFileData("a.xg"));
        Assert.ThrowsAny<JintException>(() => host.ReadFile("setup.json"));
    }

    [Fact]
    public void ADrawOfAContextNeverLanded_Throws()
    {
        var host = new FolderAccessModuleHost();

        Assert.ThrowsAny<JintException>(() => host.ResampleOf(99, (".xg", 5)));
    }

    [Theory]
    [MemberData(nameof(Mechanisms))]
    public void ReleasingAContextTwice_IsHarmless(Mechanism mechanism)
    {
        var host = new FolderAccessModuleHost();
        host.Pick(mechanism, ["a.xg"], (".xg", 5));

        host.Release();
        host.Release();
    }

    [Fact]
    public void ACancelledFileSystemAccessPick_LeavesThePreviousFolderHeld()
    {
        var host = new FolderAccessModuleHost();
        host.Pick(Mechanism.FileSystemAccess, ["a.xg", "b.xg"], (".xg", 5));

        Assert.Equal("cancelled", host.CancelPick());

        Assert.Equal(["a.xg", "b.xg"], host.Resample((".xg", 5)).Files);
    }

    [Fact]
    public void TwoPicks_AreTwoFolders_EachHeldDrawnAndReleasedOnItsOwn()
    {
        // A File System Access folder landed, then a fallback collection: both
        // are held, each draws on its own, the first still serves its
        // documents, and releasing it leaves the second untouched. (The fake
        // disk is one folder that a pick refills, so the handle's fresh walk
        // sees the second pick's files and the document is written after it;
        // the fallback's candidates were retained at collection.)
        var host = new FolderAccessModuleHost();
        host.Pick(Mechanism.FileSystemAccess, ["a.xg"], (".xg", 5));
        var first = host.Context;

        host.Pick(Mechanism.Fallback, ["z.xg"], (".xg", 5));
        var second = host.Context;

        Assert.NotEqual(first, second);
        Assert.Equal(["z.xg"], host.Resample((".xg", 5)).Files);
        Assert.Equal(["z.xg"], host.ResampleOf(first, (".xg", 5)).Files);
        host.WriteFileOf(first, "setup.json", "{\"kept\":true}");
        Assert.Equal("{\"kept\":true}", host.ReadFileOf(first, "setup.json"));
        Assert.ThrowsAny<JintException>(() => host.ReadFile("setup.json")); // the fallback has no handle

        host.ReleaseOf(first);

        Assert.ThrowsAny<JintException>(() => host.ResampleOf(first, (".xg", 5)));
        Assert.Equal(["z.xg"], host.Resample((".xg", 5)).Files);
    }
}
