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
/// cross the wire and their left-behind counts cross back with the cap figure
/// derived from the enforced table, and the slot file I/O passes the caller's
/// file name through.
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

    /// <summary>Script <c>enumeratePicked</c>'s reply — the app-work half, nothing truncated.</summary>
    private static void SetupEnumerate(BunitJSModuleInterop module, params JsFolderAccess.JsPickedFile[] files) =>
        SetupEnumerate(module, files, []);

    /// <summary>
    /// Script <c>enumeratePicked</c>'s reply including its left-behind report —
    /// the module's account of a folder its count caps cut short.
    /// </summary>
    private static void SetupEnumerate(
        BunitJSModuleInterop module,
        JsFolderAccess.JsPickedFile[] files,
        JsFolderAccess.JsOmittedFiles[] omitted) =>
        // Matcher, not the argument-less overload: the call carries the caps
        // table, and an exact-argument setup would simply never match it.
        module.Setup<JsonElement>("enumeratePicked", _ => true)
            .SetResult(OnTheWire(
                new JsFolderAccess.JsEnumerateResult(files, omitted),
                BgFolderAccessJsonContext.Default.JsEnumerateResult));

    /// <summary>
    /// Script <c>collectFallbackFiles</c>'s reply — the fallback mechanism's
    /// one-call pick, with the same matcher caveat as <c>enumeratePicked</c>.
    /// </summary>
    private static void SetupCollectFallback(
        BunitJSModuleInterop module,
        string directoryName,
        JsFolderAccess.JsPickedFile[] files,
        JsFolderAccess.JsOmittedFiles[] omitted) =>
        module.Setup<JsonElement>("collectFallbackFiles", _ => true)
            .SetResult(OnTheWire(
                new JsFolderAccess.JsFallbackResult(directoryName, files, omitted),
                BgFolderAccessJsonContext.Default.JsFallbackResult));

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
        Assert.Empty(outcome.Files);
    }

    [Fact]
    public async Task PickFolder_Cancelled_NeverRunsTheAcceptedHook()
    {
        // No folder, no work — so nothing may raise a busy affordance for work
        // that will not happen. (No enumeratePicked setup exists either: a pick
        // that enumerated after a cancellation would fail this test loudly.)
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
        // browser is done asking) and before enumeratePicked (the app's work
        // that leaves the user waiting). Pinned by call order, because that
        // ordering — not the hook's existence — is what makes the busy state
        // both truthful and paintable.
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", true);
        SetupEnumerate(module);
        var sut = CreateSut();

        var hookRan = false;
        await sut.PickFolderAsync(() =>
        {
            hookRan = true;
            // Asserted from inside the hook — the only place the "after the
            // prompts, before the scan" position is observable.
            Assert.Single(module.Invocations["beginPick"]);
            Assert.Empty(module.Invocations["enumeratePicked"]);
            return Task.CompletedTask;
        });

        Assert.True(hookRan); // an unrun hook would vacuously pass the above
        Assert.Single(module.Invocations["enumeratePicked"]);
    }

    [Fact]
    public async Task PickFolder_WritableDenied_BuffersBytesAndMapsPermissionDenied()
    {
        // writable=false from the module (readwrite request not granted) must
        // surface as PermissionDenied while the files still buffer normally —
        // the read-only rung, not a failure.
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", writable: false);
        SetupEnumerate(module, new JsFolderAccess.JsPickedFile("match.xg", 3));
        module.Setup<IJSStreamReference>("readFileData", inv => true)
            .SetResult(new FakeJsStreamReference([1, 2, 3]));
        var sut = CreateSut();

        var outcome = await sut.PickFolderAsync(NoHook);

        Assert.False(outcome.Cancelled);
        Assert.Equal(FolderWriteCapability.PermissionDenied, outcome.Capability);
        Assert.Equal("Corpus", outcome.DirectoryName);
        var file = Assert.Single(outcome.Files);
        Assert.Equal("match.xg", file.FileName); // extension-bearing name preserved
        Assert.Equal([1, 2, 3], file.Bytes);
    }

    [Fact]
    public async Task PickFolder_WritableGranted_MapsEnabled()
    {
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", writable: true);
        SetupEnumerate(module, new JsFolderAccess.JsPickedFile("a.xgp", 1));
        module.Setup<IJSStreamReference>("readFileData", inv => true)
            .SetResult(new FakeJsStreamReference([7]));
        var sut = CreateSut();

        var outcome = await sut.PickFolderAsync(NoHook);

        Assert.Equal(FolderWriteCapability.Enabled, outcome.Capability);
    }

    [Fact]
    public async Task PickFolder_PassesThePerExtensionCapsToTheModule()
    {
        // SSOT, caps edition: the JS module holds no copy of the caps — or of
        // which extensions are matching files — so both jobs it does with that
        // table are only correct if the injected instance's table is handed
        // down on the call. Same pin as the file-name pairs below.
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", true);
        SetupEnumerate(module);
        var sut = CreateSut();

        await sut.PickFolderAsync(NoHook);

        var enumerate = module.VerifyInvoke("enumeratePicked");
        Assert.Same(Limits.MaxFileCounts, enumerate.Arguments[0]);
    }

    [Fact]
    public async Task CollectFallback_PassesThePerExtensionCapsToTheModule()
    {
        // The fallback mechanism is capped identically — same module, same
        // table. The two mechanisms differing on this would be invisible until a
        // Firefox user's 3000-file folder behaved unlike a Chrome user's.
        var module = JSInterop.SetupModule(ModulePath);
        SetupCollectFallback(module, "Corpus", [], []);
        var sut = CreateSut();

        await sut.CollectFallbackAsync(default);

        var collect = module.VerifyInvoke("collectFallbackFiles");
        Assert.Same(Limits.MaxFileCounts, collect.Arguments[1]);
    }

    [Fact]
    public async Task PickFolder_OverTheCounts_TruncatesAndReportsBothKinds_NeverThrows()
    {
        // The truncate-don't-fail contract: the module returns what it took plus
        // a per-kind left-behind report, and this seam carries BOTH kinds up —
        // each capped independently, so a mixed folder can be past both at once.
        // The cap figure on each report is derived from the injected limits, not
        // read off the wire (the wire doesn't carry it).
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Huge", true);
        SetupEnumerate(
            module,
            [new JsFolderAccess.JsPickedFile("kept.xg", 1)],
            [
                new JsFolderAccess.JsOmittedFiles(".xg", 12),
                new JsFolderAccess.JsOmittedFiles(".xgp", 340),
            ]);
        module.Setup<IJSStreamReference>("readFileData", inv => true)
            .SetResult(new FakeJsStreamReference([1]));
        var sut = CreateSut();

        var outcome = await sut.PickFolderAsync(NoHook);

        Assert.False(outcome.Cancelled);
        Assert.Single(outcome.Files); // what was taken still buffers normally
        Assert.Collection(
            outcome.Truncations,
            xg =>
            {
                Assert.Equal(".xg", xg.Extension);
                Assert.Equal(12, xg.OmittedCount);
                // Derived from the injected limits, not round-tripped: the
                // reported cap is by construction the cap the pick enforced.
                Assert.Equal(Limits.MaxFileCountFor(".xg"), xg.MaxFileCount);
            },
            xgp =>
            {
                Assert.Equal(".xgp", xgp.Extension);
                Assert.Equal(340, xgp.OmittedCount);
                Assert.Equal(Limits.MaxFileCountFor(".xgp"), xgp.MaxFileCount);
            });
    }

    [Fact]
    public async Task PickFolder_WithinTheCounts_ReportsNoTruncation()
    {
        // The other half of the contract: a folder that fit says so, so a
        // host's "only for kinds actually truncated" rule has nothing to
        // render. An empty report is the common case, not a missing one.
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", true);
        SetupEnumerate(module, new JsFolderAccess.JsPickedFile("match.xg", 1));
        module.Setup<IJSStreamReference>("readFileData", inv => true)
            .SetResult(new FakeJsStreamReference([1]));
        var sut = CreateSut();

        var outcome = await sut.PickFolderAsync(NoHook);

        Assert.Empty(outcome.Truncations);
    }

    [Fact]
    public async Task CollectFallback_OverOneCount_ReportsThatKindOnly()
    {
        // Truncation rides the fallback's result shape too, and only the kind
        // that was actually cut short appears.
        var module = JSInterop.SetupModule(ModulePath);
        SetupCollectFallback(module, "Huge", [], [new JsFolderAccess.JsOmittedFiles(".xgp", 7)]);
        var sut = CreateSut();

        var outcome = await sut.CollectFallbackAsync(default);

        var only = Assert.Single(outcome.Truncations);
        Assert.Equal(".xgp", only.Extension);
        Assert.Equal(7, only.OmittedCount);
        Assert.Equal(Limits.MaxFileCountFor(".xgp"), only.MaxFileCount);
    }

    [Fact]
    public async Task PickFolder_OversizedFile_FailsBeforeAnyByteTransfer()
    {
        // No readFileData setup exists: a transfer attempted before the
        // metadata size check would fail this test loudly. The message states
        // the derived MB figure, so prose and enforced rule cannot drift.
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", true);
        SetupEnumerate(module,
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
}
