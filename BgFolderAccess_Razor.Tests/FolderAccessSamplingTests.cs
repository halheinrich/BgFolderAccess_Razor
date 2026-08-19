namespace BgFolderAccess_Razor.Tests;

using static BgFolderAccess_Razor.Tests.FolderAccessModuleHost;

/// <summary>
/// The count-cap rule as <c>folderAccess.js</c> actually computes it, run
/// through <see cref="FolderAccessModuleHost"/> — the module's own logic, not a
/// scripted stand-in for it (contrast <see cref="JsFolderAccessTests"/>, which
/// pins the C# mapping over bUnit's scripted replies).
///
/// <para>
/// The caps are enforced by a <b>uniformly random draw</b> per kind
/// (halheinrich/backgammon#106): admitting the first N in enumeration order put
/// a corpus-scale folder's excess permanently out of reach, since that order is
/// browser-supplied and stable. Everything below is asserted against
/// <b>both</b> pick mechanisms, because the rule is one rule with one
/// implementation and the two agreeing is part of the claim.
/// </para>
///
/// <para>
/// Randomness is pinned by invariants that hold for <i>every</i> draw, never by
/// comparing two picks — that comparison is inherently flaky (a 501-of-500 draw
/// repeats itself once in 501 runs). The one probabilistic assertion here is
/// <see cref="RepeatedPicks_ReachEveryFileOfAnOverLimitKind"/>, whose failure
/// odds are stated on it and sit around 1e-10.
/// </para>
/// </summary>
public class FolderAccessSamplingTests
{
    /// <summary>
    /// Both pick mechanisms, for every invariant below — the count caps are one
    /// rule with one implementation.
    /// </summary>
    public static TheoryData<Mechanism> Mechanisms =>
        [Mechanism.FileSystemAccess, Mechanism.Fallback];

    [Theory]
    [MemberData(nameof(Mechanisms))]
    public void PickUnderEveryCap_TakesEveryMatchingFileInFolderOrder(Mechanism mechanism)
    {
        var result = new FolderAccessModuleHost().Pick(
            mechanism, ["a.xg", "b.xgp", "c.xg"], (".xg", 3), (".xgp", 5));

        // The no-op case: nothing to choose between, so the draw has to be
        // indistinguishable from a pick that never sampled at all.
        Assert.Equal(["a.xg", "b.xgp", "c.xg"], result.Files);
        Assert.Empty(result.Omitted);
    }

    [Theory]
    [MemberData(nameof(Mechanisms))]
    public void PickExactlyAtACap_TakesEveryFileAndOmitsNothing(Mechanism mechanism)
    {
        var result = new FolderAccessModuleHost().Pick(
            mechanism, ["a.xg", "b.xg", "c.xg"], (".xg", 3));

        Assert.Equal(["a.xg", "b.xg", "c.xg"], result.Files);
        Assert.Empty(result.Omitted);
    }

    [Theory]
    [MemberData(nameof(Mechanisms))]
    public void PickOverACap_AdmitsExactlyTheCapAndCountsTheRest(Mechanism mechanism)
    {
        var result = new FolderAccessModuleHost().Pick(
            mechanism, ["a.xg", "b.xg", "c.xg", "d.xg", "e.xg"], (".xg", 2));

        // |admitted| = min(cap, matching), and the report accounts for the
        // difference exactly — no file is both taken and counted as omitted.
        Assert.Equal(2, result.Files.Count);
        var omitted = Assert.Single(result.Omitted);
        Assert.Equal(".xg", omitted.Extension);
        Assert.Equal(3, omitted.OmittedCount);
    }

    [Theory]
    [MemberData(nameof(Mechanisms))]
    public void PickOverACap_AdmitsOnlyDistinctFilesTheFolderHeld(Mechanism mechanism)
    {
        string[] folder = ["a.xg", "b.xg", "c.xg", "d.xg", "e.xg"];

        var result = new FolderAccessModuleHost().Pick(mechanism, folder, (".xg", 3));

        // admitted is a subset of the candidates, with no file drawn twice — the
        // sampler works in positions, and a repeat would be a duplicated read.
        Assert.All(result.Files, name => Assert.Contains(name, folder));
        Assert.Equal(result.Files.Count, result.Files.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Mechanisms))]
    public void PickOverOneCap_LeavesTheUnderLimitKindsWhole(Mechanism mechanism)
    {
        var result = new FolderAccessModuleHost().Pick(
            mechanism,
            ["a.xg", "b.xgp", "c.xg", "d.xgp", "e.xg", "f.xg"],
            (".xg", 2),
            (".xgp", 5));

        // Per-kind independence: file count is only a cost proxy within one
        // kind, so a kind nowhere near its cap survives intact and stays out of
        // the report even while another kind is being cut down.
        Assert.Equal(["b.xgp", "d.xgp"], [.. result.Files.Where(name => name.EndsWith(".xgp"))]);
        Assert.Equal(2, result.Files.Count(name => name.EndsWith(".xg")));
        Assert.Equal(".xg", Assert.Single(result.Omitted).Extension);
    }

    [Theory]
    [MemberData(nameof(Mechanisms))]
    public void AdmittedFiles_KeepFolderEnumerationOrderAcrossKinds(Mechanism mechanism)
    {
        string[] folder = ["a.xg", "b.xgp", "c.xg", "d.xgp", "e.xg", "f.xg"];

        var result = new FolderAccessModuleHost().Pick(mechanism, folder, (".xg", 2), (".xgp", 5));

        // The admitted files are a subsequence of the folder — so the draw is
        // re-ordered back into encounter order, and kinds are not grouped
        // (grouping would float both .xgp files to one end of the list).
        Assert.Equal([.. folder.Where(result.Files.Contains)], result.Files);
    }

    [Theory]
    [MemberData(nameof(Mechanisms))]
    public void NamesOutsideTheCapsTable_AreNeitherAdmittedNorOmitted(Mechanism mechanism)
    {
        var result = new FolderAccessModuleHost().Pick(
            mechanism, ["a.xg", "notes.txt", "b.xg", "readme.md"], (".xg", 1));

        // Non-matching names are not files the pick declined to take; they were
        // never candidates, so they must not inflate the left-behind count.
        Assert.Single(result.Files);
        Assert.EndsWith(".xg", result.Files[0]);
        Assert.Equal(1, Assert.Single(result.Omitted).OmittedCount);
    }

    [Theory]
    [MemberData(nameof(Mechanisms))]
    public void SeveralKindsOverTheirCaps_AreReportedInTheCapsTablesOwnOrder(Mechanism mechanism)
    {
        var result = new FolderAccessModuleHost().Pick(
            mechanism,
            ["a.xgp", "b.xg", "c.xgp", "d.xg", "e.xgp"],
            (".xg", 1),
            (".xgp", 2));

        // The report reads in table order, not in the order the folder happened
        // to hit each cap — a multi-line host notice has to be deterministic.
        Assert.Equal([".xg", ".xgp"], [.. result.Omitted.Select(kind => kind.Extension)]);
        Assert.Equal([1, 1], [.. result.Omitted.Select(kind => kind.OmittedCount)]);
    }

    [Theory]
    [MemberData(nameof(Mechanisms))]
    public void RepeatedPicks_ReachEveryFileOfAnOverLimitKind(Mechanism mechanism)
    {
        string[] folder = ["a.xg", "b.xg", "c.xg"];
        var seen = new HashSet<string>();

        // The whole point of halheinrich/backgammon#106: a cap must not put the
        // same files permanently out of reach. Under the old first-N rule this
        // set never grows past { "a.xg" }, however many picks are made.
        //
        // The one probabilistic assertion in the suite, and deliberately not a
        // "two picks differ" comparison: each pick draws 1 of 3, so one file is
        // missed by all 60 picks with probability (2/3)^60, about 3e-11 — under
        // 1e-10 across all three. That is not a flake budget worth a seam.
        for (var pick = 0; pick < 60; pick++)
        {
            seen.UnionWith(new FolderAccessModuleHost().Pick(mechanism, folder, (".xg", 1)).Files);
        }

        Assert.Equal([.. folder], [.. seen.Order()]);
    }

    [Fact]
    public void Enumeration_StatsOnlyTheFilesItTakes()
    {
        var result = new FolderAccessModuleHost().Pick(
            Mechanism.FileSystemAccess,
            [.. Enumerable.Range(0, 9).Select(i => $"f{i}.xg"), "a.xgp", "b.xgp"],
            (".xg", 3),
            (".xgp", 5));

        // The cost property the two-phase draw exists to preserve: the walk
        // classifies by name (free) and only the drawn files are stat'd, so an
        // eleven-file folder pays five getFile() calls, not eleven. Folding the
        // draw back into the walk would break this silently — nothing else in
        // the result would change.
        Assert.Equal(5, result.Files.Count);
        Assert.Equal(5, result.Stats);
    }
}
