using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bunit;
using Microsoft.JSInterop;

namespace BgFolderAccess_Razor.Tests;

/// <summary>
/// The C# half of the interop seam: <see cref="JsFolderAccess"/>'s mapping of
/// the JS module's results into <see cref="FolderPickOutcome"/>s, driven
/// through bUnit's module interop (<c>SetupModule</c>) — no real JS runs. The
/// JS half (and the real byte path) is browser-only and is exercised by the
/// consuming host's e2e suite; these tests pin the result-shape contract:
/// cancelled is a value not an exception, writable maps to capability, the
/// byte cap fails the pick before bytes move, the per-extension count caps
/// cross the wire and every kind's found and admitted counts cross back with
/// the cap figure derived from the enforced table, a resample is that same
/// draw alone (no picker, no permission request, the caller error when nothing
/// is held, and no part of a sample when a read fails), the slot file I/O
/// passes the caller's file name through, and the writability probe's three
/// answers map one-to-one with anything else refused.
///
/// <para>
/// The module's replies are scripted the way they arrive: as the
/// <see cref="JsonElement"/> the JS runtime hands back, built from the reply
/// DTO through <see cref="BgFolderAccessJsonContext"/> (see
/// <see cref="OnTheWire{TDto}"/>). Scripting the DTO itself would script the
/// very shape halheinrich/backgammon#197 forbids crossing interop, and would
/// leave the context's deserialization — the half the fix added — unexercised.
/// </para>
/// </summary>
public class JsFolderAccessTests : BunitContext
{
    private const string ModulePath = "./_content/BgFolderAccess_Razor/js/folderAccess.js";

    /// <summary>
    /// Arbitrary test caps — the library ships no numbers, so the tests supply
    /// their own small ones.
    /// </summary>
    private static readonly FolderPickLimits Limits = new(
        new Dictionary<string, int> { [".xg"] = 3, [".xgp"] = 5 },
        maxFileBytes: 2L * 1024 * 1024);

    /// <summary>Minimal <see cref="IJSStreamReference"/> over in-memory bytes.</summary>
    private sealed class FakeJsStreamReference(byte[] bytes) : IJSStreamReference
    {
        public long Length => bytes.Length;

        public ValueTask<Stream> OpenReadStreamAsync(
            long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(new MemoryStream(bytes));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private JsFolderAccess CreateSut() => new(JSInterop.JSRuntime, Limits);

    /// <summary>
    /// A scripted reply as it crosses the seam: the module's plain object,
    /// arriving as the <see cref="JsonElement"/> the JS runtime hands back.
    /// Built by serializing the DTO through the library's own context, so the
    /// element carries the wire's camelCase shape and the seam under test reads
    /// it back the way production does — the round trip is proved by every
    /// test that then asserts on the mapped outcome.
    /// </summary>
    private static JsonElement OnTheWire<TDto>(TDto reply, JsonTypeInfo<TDto> wireShape) =>
        JsonSerializer.SerializeToElement(reply, wireShape);

    /// <summary>Script <c>beginPick</c>'s reply — the browser-prompt half of the pick.</summary>
    private static void SetupBeginPick(BunitJSModuleInterop module, string status, string directoryName, bool writable) =>
        module.Setup<JsonElement>("beginPick")
            .SetResult(OnTheWire(
                new JsFolderAccess.JsPickStart(status, directoryName, writable),
                BgFolderAccessJsonContext.Default.JsPickStart));

    /// <summary>
    /// The per-kind report of a draw that took every file it found, as the
    /// module would send it for <paramref name="files"/> under
    /// <paramref name="limits"/>: one entry per kind of the table, in its
    /// order, found and admitted both the number of files of that kind.
    /// </summary>
    private static JsFolderAccess.JsDrawnKind[] WholeDraw(
        FolderPickLimits limits, params JsFolderAccess.JsPickedFile[] files) =>
        [
            .. limits.MaxFileCounts.Select(entry =>
            {
                var count = files.Count(file => file.Name.EndsWith(entry.Key, StringComparison.Ordinal));
                return new JsFolderAccess.JsDrawnKind(entry.Key, count, count);
            }),
        ];

    /// <summary>
    /// Script <c>drawPicked</c>'s reply — the app-work half of every path —
    /// for a folder within every one of <see cref="Limits"/>' caps.
    /// </summary>
    private static void SetupDraw(BunitJSModuleInterop module, params JsFolderAccess.JsPickedFile[] files) =>
        SetupDraw(module, files, WholeDraw(Limits, files));

    /// <summary>
    /// Script <c>drawPicked</c>'s reply with the per-kind report given — the
    /// module's account of what it found and admitted.
    /// </summary>
    private static void SetupDraw(
        BunitJSModuleInterop module,
        JsFolderAccess.JsPickedFile[] files,
        JsFolderAccess.JsDrawnKind[] kinds) =>
        SetupDrawReply(module, new JsFolderAccess.JsDraw("drawn", files, kinds));

    /// <summary>Script <c>drawPicked</c>'s answer when the module holds no folder.</summary>
    private static void SetupNoFolder(BunitJSModuleInterop module) =>
        SetupDrawReply(module, new JsFolderAccess.JsDraw("noFolder", [], []));

    private static void SetupDrawReply(BunitJSModuleInterop module, JsFolderAccess.JsDraw reply) =>
        // Matcher, not the argument-less overload: the call carries the caps
        // table, and an exact-argument setup would simply never match it.
        module.Setup<JsonElement>("drawPicked", _ => true)
            .SetResult(OnTheWire(reply, BgFolderAccessJsonContext.Default.JsDraw));

    /// <summary>
    /// Script the fallback mechanism's pick: <c>collectFallbackFiles</c>
    /// answering the folder's name, then the draw that follows it.
    /// </summary>
    private static void SetupCollectFallback(
        BunitJSModuleInterop module,
        string directoryName,
        JsFolderAccess.JsPickedFile[] files,
        JsFolderAccess.JsDrawnKind[] kinds)
    {
        module.Setup<string?>("collectFallbackFiles", _ => true).SetResult(directoryName);
        SetupDraw(module, files, kinds);
    }

    /// <summary>
    /// <paramref name="actual"/> is the report <paramref name="expected"/>
    /// spells, kind for kind and in order — compared by value, since
    /// <see cref="SampledKind"/> is value-equal.
    /// </summary>
    private static void AssertKinds(ImmutableArray<SampledKind> actual, params SampledKind[] expected) =>
        Assert.Equal(expected, actual);

    /// <summary>A hook that records nothing — for the cases the hook isn't what's under test.</summary>
    private static Func<Task> NoHook => () => Task.CompletedTask;

    [Fact]
    public void Ctor_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new JsFolderAccess(null!, Limits));
        Assert.Throws<ArgumentNullException>(() => new JsFolderAccess(JSInterop.JSRuntime, null!));
    }

    [Fact]
    public async Task PickFolder_NullHook_Throws()
    {
        var sut = CreateSut();

        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.PickFolderAsync(null!));
    }

    [Fact]
    public async Task PickFolder_CancelledStatus_MapsToCancelledOutcomeNotException()
    {
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "cancelled", "", false);
        var sut = CreateSut();

        var outcome = await sut.PickFolderAsync(NoHook);

        Assert.True(outcome.Cancelled);
        Assert.Empty(outcome.Sample.Files);
    }

    [Fact]
    public async Task PickFolder_Cancelled_NeverRunsTheAcceptedHook()
    {
        // No folder, no work — so nothing may raise a busy affordance for work
        // that will not happen. (No drawPicked setup exists either: a pick that
        // drew after a cancellation would fail this test loudly.)
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "cancelled", "", false);
        var sut = CreateSut();
        var hookRan = false;

        await sut.PickFolderAsync(() => { hookRan = true; return Task.CompletedTask; });

        Assert.False(hookRan);
    }

    [Fact]
    public async Task PickFolder_RunsTheAcceptedHook_AfterThePromptsAndBeforeTheScan()
    {
        // The seam the split exists for: the hook must land after beginPick (the
        // browser is done asking) and before drawPicked (the app's work that
        // leaves the user waiting). Pinned by call order, because that
        // ordering — not the hook's existence — is what makes the busy state
        // both truthful and paintable.
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", true);
        SetupDraw(module, []);
        var sut = CreateSut();

        var hookRan = false;
        await sut.PickFolderAsync(() =>
        {
            hookRan = true;
            // Asserted from inside the hook — the only place the "after the
            // prompts, before the scan" position is observable.
            Assert.Single(module.Invocations["beginPick"]);
            Assert.Empty(module.Invocations["drawPicked"]);
            return Task.CompletedTask;
        });

        Assert.True(hookRan); // an unrun hook would vacuously pass the above
        Assert.Single(module.Invocations["drawPicked"]);
    }

    [Fact]
    public async Task PickFolder_WritableDenied_BuffersBytesAndMapsPermissionDenied()
    {
        // writable=false from the module (readwrite request not granted) must
        // surface as PermissionDenied while the files still buffer normally —
        // the read-only rung, not a failure.
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", writable: false);
        SetupDraw(module, new JsFolderAccess.JsPickedFile("match.xg", 3));
        module.Setup<IJSStreamReference>("readFileData", inv => true)
            .SetResult(new FakeJsStreamReference([1, 2, 3]));
        var sut = CreateSut();

        var outcome = await sut.PickFolderAsync(NoHook);

        Assert.False(outcome.Cancelled);
        Assert.Equal(FolderWriteCapability.PermissionDenied, outcome.Capability);
        Assert.Equal("Corpus", outcome.DirectoryName);
        var file = Assert.Single(outcome.Sample.Files);
        Assert.Equal("match.xg", file.FileName); // extension-bearing name preserved
        Assert.Equal<byte>([1, 2, 3], file.Bytes);
    }

    [Fact]
    public async Task PickFolder_WritableGranted_MapsEnabled()
    {
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", writable: true);
        SetupDraw(module, new JsFolderAccess.JsPickedFile("a.xgp", 1));
        module.Setup<IJSStreamReference>("readFileData", inv => true)
            .SetResult(new FakeJsStreamReference([7]));
        var sut = CreateSut();

        var outcome = await sut.PickFolderAsync(NoHook);

        Assert.Equal(FolderWriteCapability.Enabled, outcome.Capability);
    }

    /// <summary>
    /// Caps in an order that is neither ordinal nor length order, for the wire
    /// pins: a table sorted, or re-keyed through an unordered collection, on
    /// its way down would reach the module in a different order and fail them.
    /// </summary>
    private static readonly FolderPickLimits UnsortedLimits = new(
        [new(".xgp", 5), new(".mat", 7), new(".xg", 3)],
        maxFileBytes: 1);

    /// <summary>
    /// The caps argument is the module's plain object — every extension of the
    /// enforced table, each with its cap, members in the table's order. Pinned
    /// as exact JSON text: the text's member order is what <c>JSON.parse</c>
    /// turns into the module's <c>Object.keys</c> order, the order its
    /// per-kind report reads in.
    /// </summary>
    private static void AssertIsTheWireCaps(object? argument)
    {
        var wire = Assert.IsType<JsonElement>(argument);
        Assert.Equal("""{".xgp":5,".mat":7,".xg":3}""", wire.GetRawText());
    }

    [Fact]
    public async Task PickFolder_PassesThePerExtensionCapsToTheModule_InTableOrder()
    {
        // SSOT, caps edition: the JS module holds no copy of the caps — or of
        // which extensions are matching files — so both jobs it does with that
        // table are only correct if the injected instance's table is handed
        // down on the call, in the table's order. Same pin as the file-name
        // pairs below.
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", true);
        SetupDraw(module, [], WholeDraw(UnsortedLimits));
        var sut = new JsFolderAccess(JSInterop.JSRuntime, UnsortedLimits);

        await sut.PickFolderAsync(NoHook);

        AssertIsTheWireCaps(module.VerifyInvoke("drawPicked").Arguments[0]);
    }

    [Fact]
    public async Task CollectFallback_PassesThePerExtensionCapsToTheModule_InTableOrder()
    {
        // The fallback mechanism is capped identically — same module, same
        // table. The two mechanisms differing on this would be invisible until a
        // Firefox user's 3000-file folder behaved unlike a Chrome user's.
        var module = JSInterop.SetupModule(ModulePath);
        SetupCollectFallback(module, "Corpus", [], WholeDraw(UnsortedLimits));
        var sut = new JsFolderAccess(JSInterop.JSRuntime, UnsortedLimits);

        await sut.CollectFallbackAsync(default);

        AssertIsTheWireCaps(module.VerifyInvoke("drawPicked").Arguments[0]);
    }

    /// <summary><paramref name="count"/> scripted files of <paramref name="extension"/>, one byte each.</summary>
    private static IEnumerable<JsFolderAccess.JsPickedFile> FilesOf(string extension, int count) =>
        Enumerable.Range(0, count).Select(i => new JsFolderAccess.JsPickedFile($"f{i}{extension}", 1));

    [Fact]
    public async Task PickFolder_OverTheCounts_ReportsEveryKindsFoundAndAdmitted_NeverThrows()
    {
        // The truncate-don't-fail contract: the module returns what it took plus
        // its per-kind account, and this seam carries every kind up — each
        // capped independently, so a mixed folder can be past both at once.
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Huge", true);
        SetupDraw(
            module,
            [.. FilesOf(".xg", 3), .. FilesOf(".xgp", 5)],
            [
                new JsFolderAccess.JsDrawnKind(".xg", FoundCount: 15, AdmittedCount: 3),
                new JsFolderAccess.JsDrawnKind(".xgp", FoundCount: 345, AdmittedCount: 5),
            ]);
        module.Setup<IJSStreamReference>("readFileData", inv => true)
            .SetResult(new FakeJsStreamReference([1]));
        var sut = CreateSut();

        var outcome = await sut.PickFolderAsync(NoHook);

        Assert.False(outcome.Cancelled);
        Assert.Equal(8, outcome.Sample.Files.Length); // what was taken still buffers normally
        // Each cap is the injected limits' figure: the wire does not carry it.
        AssertKinds(
            outcome.Sample.Kinds,
            new SampledKind(".xg", foundCount: 15, admittedCount: 3, maxFileCount: Limits.MaxFileCountFor(".xg")),
            new SampledKind(".xgp", foundCount: 345, admittedCount: 5, maxFileCount: Limits.MaxFileCountFor(".xgp")));
        Assert.Equal([12, 340], outcome.Sample.Kinds.Select(kind => kind.OmittedCount));
        Assert.False(outcome.Sample.HoldsEveryMatchingFile);
    }

    [Fact]
    public async Task PickFolder_EachKindsCap_IsTheEnforcedTablesEntry_InTheTablesOrder()
    {
        // The derivation, on a table whose caps all differ and whose order is
        // neither ordinal nor length order: each kind's figure is the entry
        // the module was handed for that kind, and a kind the folder holds
        // none of keeps its place.
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", true);
        SetupDraw(
            module,
            [.. FilesOf(".xgp", 5), .. FilesOf(".xg", 2)],
            [
                new JsFolderAccess.JsDrawnKind(".xgp", FoundCount: 9, AdmittedCount: 5),
                new JsFolderAccess.JsDrawnKind(".mat", FoundCount: 0, AdmittedCount: 0),
                new JsFolderAccess.JsDrawnKind(".xg", FoundCount: 2, AdmittedCount: 2),
            ]);
        module.Setup<IJSStreamReference>("readFileData", inv => true)
            .SetResult(new FakeJsStreamReference([1]));
        var sut = new JsFolderAccess(JSInterop.JSRuntime, UnsortedLimits);

        var outcome = await sut.PickFolderAsync(NoHook);

        AssertKinds(
            outcome.Sample.Kinds,
            new SampledKind(".xgp", foundCount: 9, admittedCount: 5, maxFileCount: 5),
            new SampledKind(".mat", foundCount: 0, admittedCount: 0, maxFileCount: 7),
            new SampledKind(".xg", foundCount: 2, admittedCount: 2, maxFileCount: 3));
    }

    [Fact]
    public async Task PickFolder_AReportOutOfTheTablesOrder_IsAWireContractViolation_BeforeAnyByteMoves()
    {
        // The report is the module's account over the table it was handed, so
        // a kind in the wrong place is the shipped module breaking its
        // contract — refused, not re-sorted. No readFileData setup exists, so
        // a transfer attempted before the check would fail differently.
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", true);
        SetupDraw(
            module,
            [new JsFolderAccess.JsPickedFile("a.xg", 1)],
            [
                new JsFolderAccess.JsDrawnKind(".xgp", FoundCount: 0, AdmittedCount: 0),
                new JsFolderAccess.JsDrawnKind(".xg", FoundCount: 1, AdmittedCount: 1),
            ]);
        var sut = CreateSut();

        await Assert.ThrowsAsync<JsonException>(() => sut.PickFolderAsync(NoHook));
    }

    [Fact]
    public async Task PickFolder_AReportMissingAKind_IsAWireContractViolation_BeforeAnyByteMoves()
    {
        // Every kind, or the report cannot say whether the sample is whole: a
        // missing kind is refused rather than read as "none found".
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", true);
        SetupDraw(
            module,
            [new JsFolderAccess.JsPickedFile("a.xg", 1)],
            [new JsFolderAccess.JsDrawnKind(".xg", FoundCount: 1, AdmittedCount: 1)]);
        var sut = CreateSut();

        await Assert.ThrowsAsync<JsonException>(() => sut.PickFolderAsync(NoHook));
    }

    [Fact]
    public async Task PickFolder_SampleCollections_RefuseEveryWrite_AndReadTheSame()
    {
        // The regression this pins (halheinrich/backgammon#273): the pick's
        // files used to be the List this seam buffered into, handed out behind
        // IReadOnlyList — a cast added, removed or replaced files under every
        // other holder of the outcome. Both of the sample's collections are
        // tried through every interface they implement.
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Huge", true);
        SetupDraw(module, new JsFolderAccess.JsPickedFile("kept.xg", 1));
        module.Setup<IJSStreamReference>("readFileData", inv => true)
            .SetResult(new FakeJsStreamReference([1]));
        var sut = CreateSut();

        var outcome = await sut.PickFolderAsync(NoHook);

        AssertSampleCollectionsRefuseEveryWrite(outcome.Sample, keptFile: "kept.xg");
    }

    [Fact]
    public async Task CollectFallback_SampleCollections_RefuseEveryWrite_AndReadTheSame()
    {
        // The fallback builds its sample through the same two mappings; pinned
        // separately so a mechanism that grew its own construction path could
        // not quietly hand out a mutable list again.
        var module = JSInterop.SetupModule(ModulePath);
        var kept = new JsFolderAccess.JsPickedFile("kept.xg", 1);
        SetupCollectFallback(module, "Huge", [kept], WholeDraw(Limits, kept));
        module.Setup<IJSStreamReference>("readFileData", inv => true)
            .SetResult(new FakeJsStreamReference([1]));
        var sut = CreateSut();

        var outcome = await sut.CollectFallbackAsync(default);

        AssertSampleCollectionsRefuseEveryWrite(outcome.Sample, keptFile: "kept.xg");
    }

    /// <summary>
    /// Every write to either of <paramref name="sample"/>'s collections is
    /// refused, and afterwards the files still hold exactly the one file the
    /// draw took and the report still reads every kind of <see cref="Limits"/>.
    /// </summary>
    private static void AssertSampleCollectionsRefuseEveryWrite(FolderSample sample, string keptFile)
    {
        ImmutableExposureAssert.AssertImplementsOnlyCoveredInterfaces<PickedFile>(sample.Files);
        ImmutableExposureAssert.AssertEveryWriteRefused(
            sample.Files, new PickedFile("intruder.xg", [0]));
        ImmutableExposureAssert.AssertImplementsOnlyCoveredInterfaces<SampledKind>(sample.Kinds);
        ImmutableExposureAssert.AssertEveryWriteRefused(
            sample.Kinds, new SampledKind(".xg", foundCount: 1, admittedCount: 1, maxFileCount: 1));

        Assert.Equal(keptFile, Assert.Single(sample.Files).FileName);
        Assert.Equal([".xg", ".xgp"], sample.Kinds.Select(kind => kind.Extension));
    }

    [Fact]
    public async Task PickFolder_WithinTheCounts_ReportsEveryKindWhole()
    {
        // The other half of the contract: a folder that fit says so, kind by
        // kind, so a host decides from the report alone that this sample is
        // the only one the limits could draw. A kind the folder holds none of
        // is reported too, at zero.
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", true);
        SetupDraw(module, new JsFolderAccess.JsPickedFile("match.xg", 1));
        module.Setup<IJSStreamReference>("readFileData", inv => true)
            .SetResult(new FakeJsStreamReference([1]));
        var sut = CreateSut();

        var outcome = await sut.PickFolderAsync(NoHook);

        AssertKinds(
            outcome.Sample.Kinds,
            new SampledKind(".xg", foundCount: 1, admittedCount: 1, maxFileCount: Limits.MaxFileCountFor(".xg")),
            new SampledKind(".xgp", foundCount: 0, admittedCount: 0, maxFileCount: Limits.MaxFileCountFor(".xgp")));
        Assert.True(outcome.Sample.HoldsEveryMatchingFile);
    }

    [Fact]
    public async Task CollectFallback_OverOneCount_ReportsEveryKind_WithOnlyThatOneShort()
    {
        // The report rides the fallback's result shape too: the kind cut short
        // and the kind that was not, side by side.
        var module = JSInterop.SetupModule(ModulePath);
        SetupCollectFallback(
            module, "Huge",
            [.. FilesOf(".xgp", 5)],
            [
                new JsFolderAccess.JsDrawnKind(".xg", FoundCount: 0, AdmittedCount: 0),
                new JsFolderAccess.JsDrawnKind(".xgp", FoundCount: 12, AdmittedCount: 5),
            ]);
        module.Setup<IJSStreamReference>("readFileData", inv => true)
            .SetResult(new FakeJsStreamReference([1]));
        var sut = CreateSut();

        var outcome = await sut.CollectFallbackAsync(default);

        AssertKinds(
            outcome.Sample.Kinds,
            new SampledKind(".xg", foundCount: 0, admittedCount: 0, maxFileCount: Limits.MaxFileCountFor(".xg")),
            new SampledKind(".xgp", foundCount: 12, admittedCount: 5, maxFileCount: Limits.MaxFileCountFor(".xgp")));
        Assert.Equal(7, outcome.Sample.Kinds[1].OmittedCount);
        Assert.False(outcome.Sample.HoldsEveryMatchingFile);
    }

    [Fact]
    public async Task PickFolder_OversizedFile_FailsBeforeAnyByteTransfer()
    {
        // No readFileData setup exists: a transfer attempted before the
        // metadata size check would fail this test loudly. The message states
        // the derived MB figure, so prose and enforced rule cannot drift.
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", true);
        SetupDraw(module,
            new JsFolderAccess.JsPickedFile("huge.xg", Limits.MaxFileBytes + 1));
        var sut = CreateSut();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.PickFolderAsync(NoHook));
        Assert.Contains("huge.xg", ex.Message);
        Assert.Contains($"{Limits.MaxFileMegabytes} MB", ex.Message);
    }

    [Fact]
    public async Task PickedReadAndWrite_PassTheCallerSuppliedNameToJs()
    {
        // The slot file I/O is name-parameterized — file-name constants stay
        // host-side, and this seam forwards whatever the caller supplies to the
        // picked-slot JS ops verbatim.
        var module = JSInterop.SetupModule(ModulePath);
        module.Setup<string?>("readPickedFile", inv => true).SetResult(null);
        module.SetupVoid("writePickedFile", inv => true).SetVoidResult();
        var sut = CreateSut();

        await sut.ReadPickedFileAsync("setup.json");
        await sut.WritePickedFileAsync("setup.json", "{}");

        var read = module.VerifyInvoke("readPickedFile");
        Assert.Equal("setup.json", read.Arguments[0]);
        var write = module.VerifyInvoke("writePickedFile");
        Assert.Equal("setup.json", write.Arguments[0]);
        Assert.Equal("{}", write.Arguments[1]);
    }

    [Fact]
    public async Task ActiveReadAndWrite_PassTheCallerSuppliedNameToJs()
    {
        // Same pin for the active slot — the pair a running session records
        // through routes to the active-slot JS ops with the caller's name.
        var module = JSInterop.SetupModule(ModulePath);
        module.Setup<string?>("readActiveFile", inv => true).SetResult(null);
        module.SetupVoid("writeActiveFile", inv => true).SetVoidResult();
        var sut = CreateSut();

        await sut.ReadActiveFileAsync("record.json");
        await sut.WriteActiveFileAsync("record.json", "{}");

        var read = module.VerifyInvoke("readActiveFile");
        Assert.Equal("record.json", read.Arguments[0]);
        var write = module.VerifyInvoke("writeActiveFile");
        Assert.Equal("record.json", write.Arguments[0]);
        Assert.Equal("{}", write.Arguments[1]);
    }

    [Fact]
    public async Task ReadsOfAbsentFiles_ReturnNull_OnBothSlots()
    {
        // null = the file doesn't exist yet — an expected absence, not an
        // error, on either slot.
        var module = JSInterop.SetupModule(ModulePath);
        module.Setup<string?>("readPickedFile", inv => true).SetResult(null);
        module.Setup<string?>("readActiveFile", inv => true).SetResult(null);
        var sut = CreateSut();

        Assert.Null(await sut.ReadPickedFileAsync("absent.json"));
        Assert.Null(await sut.ReadActiveFileAsync("absent.json"));
    }

    [Theory]
    [InlineData("absent", PickedFileWritability.Absent)]
    [InlineData("writable", PickedFileWritability.Writable)]
    [InlineData("notWritable", PickedFileWritability.NotWritable)]
    public async Task ProbePickedFile_MapsEachModuleAnswer(string wire, PickedFileWritability expected)
    {
        // The module turns the browser's answer into one of three strings;
        // this seam is the one place they become the enum. The caller's name
        // is forwarded verbatim, as for the sibling picked-slot reads.
        var module = JSInterop.SetupModule(ModulePath);
        module.Setup<string?>("probePickedFileWritability", _ => true).SetResult(wire);
        var sut = CreateSut();

        Assert.Equal(expected, await sut.ProbePickedFileWritabilityAsync("stats.json"));
        Assert.Equal("stats.json", module.VerifyInvoke("probePickedFileWritability").Arguments[0]);
    }

    [Theory]
    [InlineData("Writable")]
    [InlineData("readOnly")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ProbePickedFile_AnAnswerTheModuleDoesNotShip_Throws(string? wire)
    {
        // An unknown answer is a wire-contract violation, not a fourth outcome
        // to guess at — including a case-variant of a real one, since the
        // module's spelling is a fact, not a tolerance.
        var module = JSInterop.SetupModule(ModulePath);
        module.Setup<string?>("probePickedFileWritability", _ => true).SetResult(wire);
        var sut = CreateSut();

        await Assert.ThrowsAsync<JsonException>(() => sut.ProbePickedFileWritabilityAsync("stats.json"));
    }

    [Fact]
    public async Task ProbePickedFile_NullName_ThrowsBeforeInterop()
    {
        // Not even the module import runs — the root interop saw no call.
        JSInterop.SetupModule(ModulePath);
        var sut = CreateSut();

        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.ProbePickedFileWritabilityAsync(null!));
        Assert.Empty(JSInterop.Invocations);
    }

    // Resample: the draw both pick paths end in, alone. The module's side —
    // what it walks, and that a draw and its reads change none of its state —
    // is pinned on the real module in FolderAccessResampleTests; these pin the
    // C# mapping over scripted replies.

    [Fact]
    public async Task Resample_ReportsEveryKind_WithTheCapFromTheEnforcedTable()
    {
        var module = JSInterop.SetupModule(ModulePath);
        SetupDraw(
            module,
            [.. FilesOf(".xg", 3), .. FilesOf(".xgp", 2)],
            [
                new JsFolderAccess.JsDrawnKind(".xg", FoundCount: 7, AdmittedCount: 3),
                new JsFolderAccess.JsDrawnKind(".xgp", FoundCount: 2, AdmittedCount: 2),
            ]);
        module.Setup<IJSStreamReference>("readFileData", inv => true)
            .SetResult(new FakeJsStreamReference([1]));
        var sut = CreateSut();

        var sample = await sut.ResampleAsync();

        Assert.Equal(5, sample.Files.Length);
        AssertKinds(
            sample.Kinds,
            new SampledKind(".xg", foundCount: 7, admittedCount: 3, maxFileCount: Limits.MaxFileCountFor(".xg")),
            new SampledKind(".xgp", foundCount: 2, admittedCount: 2, maxFileCount: Limits.MaxFileCountFor(".xgp")));
        Assert.False(sample.HoldsEveryMatchingFile);
    }

    [Fact]
    public async Task Resample_PassesThePerExtensionCapsToTheModule_InTableOrder()
    {
        // The module keeps no copy of the table between a pick and a resample:
        // the resample hands it down again, whole and in order.
        var module = JSInterop.SetupModule(ModulePath);
        SetupDraw(module, [], WholeDraw(UnsortedLimits));
        var sut = new JsFolderAccess(JSInterop.JSRuntime, UnsortedLimits);

        await sut.ResampleAsync();

        AssertIsTheWireCaps(module.VerifyInvoke("drawPicked").Arguments[0]);
    }

    [Fact]
    public async Task Resample_CallsOnlyTheDrawAndTheByteReads()
    {
        // No picker, no permission request, no fallback collection: nothing a
        // resample does can show the user a prompt. (The interop is strict, so
        // an unscripted call would fail the test anyway; this states the set.)
        var module = JSInterop.SetupModule(ModulePath);
        SetupDraw(module, new JsFolderAccess.JsPickedFile("a.xg", 1));
        module.Setup<IJSStreamReference>("readFileData", inv => true)
            .SetResult(new FakeJsStreamReference([1]));
        var sut = CreateSut();

        await sut.ResampleAsync();

        Assert.Equal(["drawPicked", "readFileData"], module.Invocations.Select(i => i.Identifier).Distinct().Order());
    }

    [Fact]
    public async Task Resample_OversizedFile_FailsBeforeAnyByteTransfer()
    {
        // The pick's byte cap, by the pick's own code path: no readFileData
        // setup exists, so a transfer attempted before the check would fail
        // differently.
        var module = JSInterop.SetupModule(ModulePath);
        SetupDraw(
            module,
            new JsFolderAccess.JsPickedFile("small.xg", 1),
            new JsFolderAccess.JsPickedFile("huge.xg", Limits.MaxFileBytes + 1));
        var sut = CreateSut();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ResampleAsync());
        Assert.Contains("huge.xg", ex.Message);
        Assert.Contains($"{Limits.MaxFileMegabytes} MB", ex.Message);
    }

    [Fact]
    public async Task Resample_WithNoFolderHeld_IsACallerError()
    {
        // Nothing picked, or the pick cleared: the module answers "noFolder",
        // and the seam turns it into the documented InvalidOperationException
        // rather than an empty sample a caller might install.
        var module = JSInterop.SetupModule(ModulePath);
        SetupNoFolder(module);
        var sut = CreateSut();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ResampleAsync());
        Assert.Contains("No folder is held", ex.Message);
        Assert.Empty(module.Invocations["readFileData"]);
    }

    [Fact]
    public async Task PickFolder_FolderReleasedWhileTheHookRan_IsTheSameCallerError()
    {
        // The hook yields to the renderer, so a Clear can land inside it; the
        // draw then finds nothing held, as documented on PickFolderAsync.
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", true);
        SetupNoFolder(module);
        var sut = CreateSut();

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.PickFolderAsync(NoHook));
    }

    [Fact]
    public async Task Resample_AStatusTheModuleDoesNotShip_IsAWireContractViolation()
    {
        var module = JSInterop.SetupModule(ModulePath);
        SetupDrawReply(module, new JsFolderAccess.JsDraw("Drawn", [], WholeDraw(Limits)));
        var sut = CreateSut();

        await Assert.ThrowsAsync<JsonException>(() => sut.ResampleAsync());
    }

    [Fact]
    public async Task Resample_AReadFailingPartWayThroughBuffering_ReturnsNoPart_AndALaterResampleSucceeds()
    {
        // One file buffered, then the next file's read fails: the caller gets
        // the exception and nothing else — no sample holding the first file —
        // and the seam keeps nothing that stops the next resample. (That the
        // module's own state survives is pinned on the real module, in
        // FolderAccessResampleTests; here the replies are scripted.)
        var module = JSInterop.SetupModule(ModulePath);
        SetupDraw(
            module,
            new JsFolderAccess.JsPickedFile("a.xg", 1),
            new JsFolderAccess.JsPickedFile("b.xg", 1));
        module.Setup<IJSStreamReference>("readFileData", inv => Equals(inv.Arguments[0], "a.xg"))
            .SetResult(new FakeJsStreamReference([1]));
        var bRead = module.Setup<IJSStreamReference>("readFileData", inv => Equals(inv.Arguments[0], "b.xg"));
        bRead.SetException(new JSException("'b.xg' could not be read."));
        module.Setup<string?>("readPickedFile", _ => true).SetResult("{}");
        module.Setup<string?>("probePickedFileWritability", _ => true).SetResult("writable");
        var sut = CreateSut();

        FolderSample? partial = null;
        await Assert.ThrowsAsync<JSException>(async () => partial = await sut.ResampleAsync());

        Assert.Null(partial);
        Assert.Equal(["a.xg", "b.xg"], module.Invocations["readFileData"].Select(i => i.Arguments[0]));

        bRead.SetResult(new FakeJsStreamReference([2]));
        var sample = await sut.ResampleAsync();

        Assert.Equal(["a.xg", "b.xg"], sample.Files.Select(file => file.FileName));
        Assert.Equal<byte>([2], sample.Files[1].Bytes);
        Assert.Equal("{}", await sut.ReadPickedFileAsync("setup.json"));
        Assert.Equal(PickedFileWritability.Writable, await sut.ProbePickedFileWritabilityAsync("setup.json"));
    }
}
