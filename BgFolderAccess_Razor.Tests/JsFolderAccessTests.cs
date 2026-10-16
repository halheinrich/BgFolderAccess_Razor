using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bunit;
using Microsoft.JSInterop;

namespace BgFolderAccess_Razor.Tests;

/// <summary>
/// The C# half of the interop seam: <see cref="JsFolderAccess"/>'s mapping of the module's
/// replies into a <see cref="FolderPickOutcome"/> and its <see cref="IFolderContext"/>, driven
/// through bUnit's module interop — no real JS runs. The JS half is exercised on the real
/// module by the Jint suites and, for the real wire, by the consuming host's e2e suite.
/// These pin the result-shape contract: cancelled is a value not an exception, writable
/// maps to capability, the byte cap fails the draw before bytes move, the caps cross the
/// wire and every kind's counts cross back with the cap derived from the enforced table, a
/// resample is the same draw on the folder's own context, every context operation names
/// its context and forwards the caller's file name, a landed folder whose draw fails is
/// released, and a released context refuses every operation before interop.
///
/// <para>
/// The module's replies are scripted the way they arrive: as the <see cref="JsonElement"/>
/// the JS runtime hands back, built from the reply DTO through
/// <see cref="BgFolderAccessJsonContext"/> (<see cref="OnTheWire{TDto}"/>). Scripting the
/// DTO itself would script the very shape halheinrich/backgammon#197 forbids crossing interop.
/// </para>
/// </summary>
public class JsFolderAccessTests : BunitContext
{
    private const string ModulePath = "./_content/BgFolderAccess_Razor/js/folderAccess.js";

    /// <summary>Arbitrary test caps — the library ships no numbers.</summary>
    private static readonly FolderPickLimits Limits = new(
        new Dictionary<string, int> { [".xg"] = 3, [".xgp"] = 5 },
        maxFileBytes: 2L * 1024 * 1024);

    /// <summary>
    /// An <see cref="IJSStreamReference"/> over in-memory bytes that behaves as the runtime's
    /// <c>JSStreamReference</c> does where the seam depends on it, and records what the seam
    /// does with it: <see cref="OpenReadStreamAsync"/> refuses a <see cref="Length"/> past
    /// <c>maxAllowedSize</c> with an <see cref="ArgumentOutOfRangeException"/> naming that
    /// parameter (dotnet/aspnetcore v10.0.12, JSStreamReference.cs), and only
    /// <see cref="DisposeAsync"/> releases the reference — the stream it opens does not.
    /// </summary>
    private sealed class FakeJsStreamReference(byte[] bytes) : IJSStreamReference
    {
        public long Length { get; init; } = bytes.Length;

        public Exception? OpenFailure { get; init; }

        public int? FailAfterBytes { get; init; }

        public int DisposeCount { get; private set; }

        public List<TrackedStream> OpenedStreams { get; } = [];

        public ValueTask<Stream> OpenReadStreamAsync(
            long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            if (Length > maxAllowedSize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxAllowedSize),
                    $"The incoming data stream of length {Length} exceeds the maximum allowed length {maxAllowedSize}.");
            }

            if (OpenFailure is not null)
            {
                return ValueTask.FromException<Stream>(OpenFailure);
            }

            var stream = new TrackedStream(bytes, FailAfterBytes);
            OpenedStreams.Add(stream);
            return ValueTask.FromResult<Stream>(stream);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A read-only stream that records its own disposal and, given <c>failAfterBytes</c>,
    /// breaks off with an <see cref="IOException"/> once that many bytes are read. Not a
    /// <see cref="MemoryStream"/>: that copies its whole buffer in <c>CopyToAsync</c> without
    /// reading, so it could not fail part-way.
    /// </summary>
    private sealed class TrackedStream(byte[] bytes, int? failAfterBytes) : Stream
    {
        private int _position;

        public bool Disposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => bytes.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var limit = failAfterBytes ?? bytes.Length;
            if (_position >= limit && _position < bytes.Length)
            {
                throw new IOException("The transfer broke off part-way.");
            }

            var read = Math.Min(count, limit - _position);
            Array.Copy(bytes, _position, buffer, offset, read);
            _position += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private JsFolderAccess CreateSut() => new(JSInterop.JSRuntime, Limits);

    /// <summary>
    /// A scripted reply as it crosses the seam: the module's plain object, arriving as the
    /// <see cref="JsonElement"/> the JS runtime hands back, serialized through the library's
    /// own context so the seam reads it back the way production does.
    /// </summary>
    private static JsonElement OnTheWire<TDto>(TDto reply, JsonTypeInfo<TDto> wireShape) =>
        JsonSerializer.SerializeToElement(reply, wireShape);

    /// <summary>Script <c>beginPick</c>'s reply — the browser-prompt half of the pick, landing the folder in <paramref name="context"/>.</summary>
    private static void SetupBeginPick(
        BunitJSModuleInterop module, string status, string directoryName, bool writable, int context = 1) =>
        module.Setup<JsonElement>("beginPick")
            .SetResult(OnTheWire(
                new JsFolderAccess.JsPickStart(status, directoryName, writable, context),
                BgFolderAccessJsonContext.Default.JsPickStart));

    /// <summary>
    /// The per-kind report of a draw that took every file it found, as the module would send
    /// it for <paramref name="files"/> under <paramref name="limits"/>.
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

    /// <summary>Script <c>draw</c>'s reply for a folder within every one of <see cref="Limits"/>' caps.</summary>
    private static JSRuntimeInvocationHandler<JsonElement> SetupDraw(
        BunitJSModuleInterop module, params JsFolderAccess.JsPickedFile[] files) =>
        SetupDraw(module, files, WholeDraw(Limits, files));

    /// <summary>Script <c>draw</c>'s reply with the per-kind report given.</summary>
    private static JSRuntimeInvocationHandler<JsonElement> SetupDraw(
        BunitJSModuleInterop module,
        JsFolderAccess.JsPickedFile[] files,
        JsFolderAccess.JsDrawnKind[] kinds) =>
        // Matcher, not the argument-less overload: the call carries the context and the caps
        // table, and an exact-argument setup would never match it. A later setup for the
        // same identifier wins over an earlier one, which is how a test scripts a pick's
        // draw and then a resample's.
        module.Setup<JsonElement>("draw", _ => true)
            .SetResult(OnTheWire(new JsFolderAccess.JsDraw(files, kinds), BgFolderAccessJsonContext.Default.JsDraw));

    /// <summary>Script the fallback mechanism's pick: <c>collectFallbackFiles</c> landing the folder, then its draw.</summary>
    private static void SetupCollectFallback(
        BunitJSModuleInterop module,
        string directoryName,
        JsFolderAccess.JsPickedFile[] files,
        JsFolderAccess.JsDrawnKind[] kinds,
        int context = 2)
    {
        module.Setup<JsonElement>("collectFallbackFiles", _ => true)
            .SetResult(OnTheWire(
                new JsFolderAccess.JsCollected(directoryName, context),
                BgFolderAccessJsonContext.Default.JsCollected));
        SetupDraw(module, files, kinds);
    }

    /// <summary>Script <c>release</c>, which a disposed context invokes.</summary>
    private static void SetupRelease(BunitJSModuleInterop module) =>
        module.SetupVoid("release", _ => true).SetVoidResult();

    /// <summary>
    /// A File System Access pick of an empty folder landed in <paramref name="context"/>: the
    /// folder's context, for the tests of what a context does once held.
    /// </summary>
    private async Task<IFolderContext> PickedFolderAsync(BunitJSModuleInterop module, int context = 1, bool writable = true)
    {
        SetupBeginPick(module, "ok", "Corpus", writable, context);
        SetupDraw(module, []);
        var outcome = await CreateSut().PickFolderAsync(NoHook);
        return outcome.Folder!;
    }

    private static void AssertKinds(ImmutableArray<SampledKind> actual, params SampledKind[] expected) =>
        Assert.Equal(expected, actual);

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
        SetupBeginPick(module, "cancelled", "", false, context: 0);
        var sut = CreateSut();

        var outcome = await sut.PickFolderAsync(NoHook);

        Assert.True(outcome.Cancelled);
        Assert.Null(outcome.Folder);
        Assert.Empty(outcome.Sample.Files);
    }

    [Fact]
    public async Task PickFolder_Cancelled_NeverRunsTheAcceptedHook()
    {
        // No folder, no work — so nothing may raise a busy affordance for work that will not
        // happen. (No draw setup exists either: a pick that drew after a cancellation would
        // fail this test loudly.)
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "cancelled", "", false, context: 0);
        var sut = CreateSut();
        var hookRan = false;

        await sut.PickFolderAsync(() => { hookRan = true; return Task.CompletedTask; });

        Assert.False(hookRan);
    }

    [Fact]
    public async Task PickFolder_RunsTheAcceptedHook_AfterThePromptsAndBeforeTheScan()
    {
        // The seam the split exists for: the hook lands after beginPick (the browser is done
        // asking) and before the draw (the work that leaves the user waiting).
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", true);
        SetupDraw(module, []);
        var sut = CreateSut();

        var hookRan = false;
        await sut.PickFolderAsync(() =>
        {
            hookRan = true;
            Assert.Single(module.Invocations["beginPick"]);
            Assert.Empty(module.Invocations["draw"]);
            return Task.CompletedTask;
        });

        Assert.True(hookRan);
        Assert.Single(module.Invocations["draw"]);
    }

    [Fact]
    public async Task PickFolder_WritableDenied_BuffersBytesAndMapsPermissionDenied()
    {
        // writable=false from the module (readwrite request not granted) surfaces as
        // PermissionDenied on the folder while the files still buffer normally.
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", writable: false, context: 5);
        SetupDraw(module, new JsFolderAccess.JsPickedFile("match.xg", 3));
        module.Setup<IJSStreamReference>("readFileData", inv => true)
            .SetResult(new FakeJsStreamReference([1, 2, 3]));
        var sut = CreateSut();

        var outcome = await sut.PickFolderAsync(NoHook);

        Assert.False(outcome.Cancelled);
        var folder = Assert.IsAssignableFrom<IFolderContext>(outcome.Folder);
        Assert.Equal(FolderWriteCapability.PermissionDenied, folder.Capability);
        Assert.Equal("Corpus", folder.Name);
        var file = Assert.Single(outcome.Sample.Files);
        Assert.Equal("match.xg", file.FileName); // extension-bearing name preserved
        Assert.Equal<byte>([1, 2, 3], file.Bytes);
        // The draw and the byte read name the context the pick landed.
        Assert.Equal(5, module.VerifyInvoke("draw").Arguments[0]);
        Assert.Equal([5, "match.xg"], module.VerifyInvoke("readFileData").Arguments);
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

        Assert.Equal(FolderWriteCapability.Enabled, outcome.Folder!.Capability);
    }

    [Fact]
    public async Task CollectFallback_LandsAFolderWithoutWriteCapability_UnderTheCollectedContext()
    {
        var module = JSInterop.SetupModule(ModulePath);
        SetupCollectFallback(module, "Dropped", [], WholeDraw(Limits), context: 9);
        var sut = CreateSut();

        var outcome = await sut.CollectFallbackAsync(default);

        Assert.False(outcome.Cancelled);
        Assert.Equal("Dropped", outcome.Folder!.Name);
        Assert.Equal(FolderWriteCapability.BrowserUnsupported, outcome.Folder.Capability);
        Assert.Equal(9, module.VerifyInvoke("draw").Arguments[0]);
    }

    /// <summary>
    /// Caps in an order that is neither ordinal nor length order, for the wire pins: a
    /// table sorted on its way down would reach the module in a different order.
    /// </summary>
    private static readonly FolderPickLimits UnsortedLimits = new(
        [new(".xgp", 5), new(".mat", 7), new(".xg", 3)],
        maxFileBytes: 1);

    /// <summary>
    /// The caps argument is the module's plain object — every extension of the enforced
    /// table with its cap, members in the table's order — pinned as exact JSON text, since
    /// the text's member order is what the module's per-kind report reads in.
    /// </summary>
    private static void AssertIsTheWireCaps(object? argument)
    {
        var wire = Assert.IsType<JsonElement>(argument);
        Assert.Equal("""{".xgp":5,".mat":7,".xg":3}""", wire.GetRawText());
    }

    [Fact]
    public async Task PickFolder_PassesThePerExtensionCapsToTheModule_InTableOrder()
    {
        // SSOT, caps edition: the module holds no copy of the caps, so both jobs it does with
        // the table are only correct if the injected instance's table is handed down on the
        // call, after the context, in the table's order.
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", true, context: 3);
        SetupDraw(module, [], WholeDraw(UnsortedLimits));
        var sut = new JsFolderAccess(JSInterop.JSRuntime, UnsortedLimits);

        await sut.PickFolderAsync(NoHook);

        var draw = module.VerifyInvoke("draw");
        Assert.Equal(3, draw.Arguments[0]);
        AssertIsTheWireCaps(draw.Arguments[1]);
    }

    [Fact]
    public async Task CollectFallback_PassesThePerExtensionCapsToTheModule_InTableOrder()
    {
        // The fallback mechanism is capped identically — same module, same table.
        var module = JSInterop.SetupModule(ModulePath);
        SetupCollectFallback(module, "Corpus", [], WholeDraw(UnsortedLimits));
        var sut = new JsFolderAccess(JSInterop.JSRuntime, UnsortedLimits);

        await sut.CollectFallbackAsync(default);

        AssertIsTheWireCaps(module.VerifyInvoke("draw").Arguments[1]);
    }

    /// <summary><paramref name="count"/> scripted files of <paramref name="extension"/>, one byte each.</summary>
    private static IEnumerable<JsFolderAccess.JsPickedFile> FilesOf(string extension, int count) =>
        Enumerable.Range(0, count).Select(i => new JsFolderAccess.JsPickedFile($"f{i}{extension}", 1));

    [Fact]
    public async Task PickFolder_OverTheCounts_ReportsEveryKindsFoundAndAdmitted_NeverThrows()
    {
        // The truncate-don't-fail contract: the module returns what it took plus its per-kind
        // account, and this seam carries every kind up — each capped independently.
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
        Assert.Equal(8, outcome.Sample.Files.Length);
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
        // Each kind's figure is the entry the module was handed for that kind, and a kind
        // the folder holds none of keeps its place.
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
        // A kind in the wrong place is the shipped module breaking its contract — refused,
        // not re-sorted. No readFileData setup exists, so a transfer attempted before the
        // check would fail differently.
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", true);
        SetupDraw(
            module,
            [new JsFolderAccess.JsPickedFile("a.xg", 1)],
            [
                new JsFolderAccess.JsDrawnKind(".xgp", FoundCount: 0, AdmittedCount: 0),
                new JsFolderAccess.JsDrawnKind(".xg", FoundCount: 1, AdmittedCount: 1),
            ]);
        SetupRelease(module);
        var sut = CreateSut();

        await Assert.ThrowsAsync<JsonException>(() => sut.PickFolderAsync(NoHook));
    }

    [Fact]
    public async Task PickFolder_AReportMissingAKind_IsAWireContractViolation_BeforeAnyByteMoves()
    {
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", true);
        SetupDraw(
            module,
            [new JsFolderAccess.JsPickedFile("a.xg", 1)],
            [new JsFolderAccess.JsDrawnKind(".xg", FoundCount: 1, AdmittedCount: 1)]);
        SetupRelease(module);
        var sut = CreateSut();

        await Assert.ThrowsAsync<JsonException>(() => sut.PickFolderAsync(NoHook));
    }

    [Fact]
    public async Task PickFolder_SampleCollections_RefuseEveryWrite_AndReadTheSame()
    {
        // The regression this pins (halheinrich/backgammon#273): the pick's files used to be
        // the List this seam buffered into, handed out behind IReadOnlyList.
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
        var module = JSInterop.SetupModule(ModulePath);
        var kept = new JsFolderAccess.JsPickedFile("kept.xg", 1);
        SetupCollectFallback(module, "Huge", [kept], WholeDraw(Limits, kept));
        module.Setup<IJSStreamReference>("readFileData", inv => true)
            .SetResult(new FakeJsStreamReference([1]));
        var sut = CreateSut();

        var outcome = await sut.CollectFallbackAsync(default);

        AssertSampleCollectionsRefuseEveryWrite(outcome.Sample, keptFile: "kept.xg");
    }

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
    public async Task PickFolder_OversizedFile_FailsBeforeAnyByteTransfer_AndReleasesTheFolder()
    {
        // No readFileData setup exists: a transfer attempted before the metadata size check
        // would fail this test loudly. The landed folder is released with the failure, so the
        // host holds nothing.
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", true, context: 4);
        SetupDraw(module,
            new JsFolderAccess.JsPickedFile("huge.xg", Limits.MaxFileBytes + 1));
        SetupRelease(module);
        var sut = CreateSut();

        var ex = await Assert.ThrowsAsync<PickedFileTooLargeException>(
            () => sut.PickFolderAsync(NoHook));

        AssertOverTheCap(ex, "huge.xg", Limits.MaxFileBytes + 1);
        Assert.Equal(4, module.VerifyInvoke("release").Arguments[0]);
    }

    /// <summary>
    /// <paramref name="ex"/> reports <paramref name="fileName"/> at <paramref name="fileSize"/>
    /// bytes against <see cref="Limits"/>' cap, and its message states the derived MB figure.
    /// </summary>
    private static void AssertOverTheCap(PickedFileTooLargeException ex, string fileName, long fileSize)
    {
        Assert.Equal(fileName, ex.FileName);
        Assert.Equal(fileSize, ex.FileSize);
        Assert.Equal(Limits.MaxFileBytes, ex.MaxFileBytes);
        Assert.Contains(fileName, ex.Message);
        Assert.Contains($"{Limits.MaxFileMegabytes} MB", ex.Message);
    }

    [Fact]
    public async Task PickFolder_AFailingHook_ReleasesTheLandedFolder_AndThrowsTheHooksFailure()
    {
        // The folder is landed before the hook runs; a hook that throws is a host bug, and the
        // folder must not leak behind it.
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", true, context: 6);
        SetupRelease(module);
        var sut = CreateSut();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.PickFolderAsync(() => throw new InvalidOperationException("the host's own fault")));

        Assert.Equal(6, module.VerifyInvoke("release").Arguments[0]);
        Assert.Empty(module.Invocations["draw"]);
    }

    [Fact]
    public async Task CollectFallback_ADrawThatFails_ReleasesTheLandedFolder_AndThrowsTheDrawsFailure()
    {
        var module = JSInterop.SetupModule(ModulePath);
        module.Setup<JsonElement>("collectFallbackFiles", _ => true)
            .SetResult(OnTheWire(new JsFolderAccess.JsCollected("Corpus", 8), BgFolderAccessJsonContext.Default.JsCollected));
        module.Setup<JsonElement>("draw", _ => true).SetException(new JSException("The folder could not be walked."));
        SetupRelease(module);
        var sut = CreateSut();

        await Assert.ThrowsAsync<JSException>(() => sut.CollectFallbackAsync(default));

        Assert.Equal(8, module.VerifyInvoke("release").Arguments[0]);
    }

    [Fact]
    public async Task PickFolder_AReleaseThatFailsAfterAFailedDraw_StillThrowsTheDrawsFailure()
    {
        // Releasing never misreports: the failure that ended the pick is the one the caller sees.
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", true);
        module.Setup<JsonElement>("draw", _ => true).SetException(new JSException("the draw's own failure"));
        module.SetupVoid("release", _ => true).SetException(new JSException("and the release failed too"));
        var sut = CreateSut();

        var ex = await Assert.ThrowsAsync<JSException>(() => sut.PickFolderAsync(NoHook));

        Assert.Contains("the draw's own failure", ex.Message);
    }

    // The context's named-file operations: each names its context and forwards the caller's
    // file name verbatim — file-name constants stay host-side.

    [Fact]
    public async Task ReadAndWrite_PassTheContextAndTheCallerSuppliedNameToJs()
    {
        var module = JSInterop.SetupModule(ModulePath);
        var folder = await PickedFolderAsync(module, context: 42);
        module.Setup<string?>("readFile", _ => true).SetResult(null);
        module.SetupVoid("writeFile", _ => true).SetVoidResult();

        await folder.ReadFileAsync("setup.json");
        await folder.WriteFileAsync("setup.json", "{}");

        Assert.Equal([42, "setup.json"], module.VerifyInvoke("readFile").Arguments);
        Assert.Equal([42, "setup.json", "{}"], module.VerifyInvoke("writeFile").Arguments);
    }

    [Fact]
    public async Task ReadOfAnAbsentFile_ReturnsNull()
    {
        // null = the file doesn't exist yet — an expected absence, not an error.
        var module = JSInterop.SetupModule(ModulePath);
        var folder = await PickedFolderAsync(module);
        module.Setup<string?>("readFile", _ => true).SetResult(null);

        Assert.Null(await folder.ReadFileAsync("absent.json"));
    }

    [Fact]
    public async Task ReadAndWrite_NullArguments_ThrowBeforeInterop()
    {
        var module = JSInterop.SetupModule(ModulePath);
        var folder = await PickedFolderAsync(module);

        await Assert.ThrowsAsync<ArgumentNullException>(() => folder.ReadFileAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => folder.WriteFileAsync(null!, "{}"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => folder.WriteFileAsync("setup.json", null!));
        Assert.Empty(module.Invocations["readFile"]);
        Assert.Empty(module.Invocations["writeFile"]);
    }

    [Theory]
    [InlineData("absent", FileWritability.Absent)]
    [InlineData("writable", FileWritability.Writable)]
    [InlineData("notWritable", FileWritability.NotWritable)]
    public async Task ProbeFile_MapsEachModuleAnswer(string wire, FileWritability expected)
    {
        // The module turns the browser's answer into one of three strings; this seam is the
        // one place they become the enum.
        var module = JSInterop.SetupModule(ModulePath);
        var folder = await PickedFolderAsync(module, context: 3);
        module.Setup<string?>("probeFileWritability", _ => true).SetResult(wire);

        Assert.Equal(expected, await folder.ProbeFileWritabilityAsync("stats.json"));
        Assert.Equal([3, "stats.json"], module.VerifyInvoke("probeFileWritability").Arguments);
    }

    [Theory]
    [InlineData("Writable")]
    [InlineData("readOnly")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ProbeFile_AnAnswerTheModuleDoesNotShip_Throws(string? wire)
    {
        // An unknown answer is a wire-contract violation, not a fourth outcome to guess at —
        // a case-variant of a real one included, since the module's spelling is a fact.
        var module = JSInterop.SetupModule(ModulePath);
        var folder = await PickedFolderAsync(module);
        module.Setup<string?>("probeFileWritability", _ => true).SetResult(wire);

        await Assert.ThrowsAsync<JsonException>(() => folder.ProbeFileWritabilityAsync("stats.json"));
    }

    [Fact]
    public async Task ProbeFile_NullName_ThrowsBeforeInterop()
    {
        var module = JSInterop.SetupModule(ModulePath);
        var folder = await PickedFolderAsync(module);

        await Assert.ThrowsAsync<ArgumentNullException>(() => folder.ProbeFileWritabilityAsync(null!));
        Assert.Empty(module.Invocations["probeFileWritability"]);
    }

    // Release: a context frees its handle once, and refuses everything afterwards.

    [Fact]
    public async Task Dispose_ReleasesTheContext_Once()
    {
        var module = JSInterop.SetupModule(ModulePath);
        var folder = await PickedFolderAsync(module, context: 11);
        SetupRelease(module);

        await folder.DisposeAsync();
        await folder.DisposeAsync();

        Assert.Equal(11, module.VerifyInvoke("release").Arguments[0]);
    }

    [Fact]
    public async Task Dispose_ToleratesARuntimeAlreadyTornDown()
    {
        // A tab close or reload tears the runtime down before the context releases: nothing
        // is left to free, and the disposal must not throw.
        var module = JSInterop.SetupModule(ModulePath);
        var folder = await PickedFolderAsync(module);
        module.SetupVoid("release", _ => true).SetException(new JSDisconnectedException("gone"));

        await folder.DisposeAsync();
    }

    [Fact]
    public async Task AReleasedContext_RefusesEveryOperation_BeforeInterop()
    {
        var module = JSInterop.SetupModule(ModulePath);
        var folder = await PickedFolderAsync(module);
        SetupRelease(module);
        await folder.DisposeAsync();
        var before = module.Invocations.Count;

        await Assert.ThrowsAsync<ObjectDisposedException>(() => folder.ResampleAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => folder.ReadFileAsync("a.json"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => folder.WriteFileAsync("a.json", "{}"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => folder.ProbeFileWritabilityAsync("a.json"));

        Assert.Equal(before, module.Invocations.Count);
    }

    // Resample: the draw a pick ends in, on the folder's own context. The module's side —
    // what it walks, and that a draw and its reads change none of its state — is pinned on
    // the real module in FolderAccessResampleTests; these pin the C# mapping.

    [Fact]
    public async Task Resample_ReportsEveryKind_WithTheCapFromTheEnforcedTable()
    {
        var module = JSInterop.SetupModule(ModulePath);
        var folder = await PickedFolderAsync(module);
        SetupDraw(
            module,
            [.. FilesOf(".xg", 3), .. FilesOf(".xgp", 2)],
            [
                new JsFolderAccess.JsDrawnKind(".xg", FoundCount: 7, AdmittedCount: 3),
                new JsFolderAccess.JsDrawnKind(".xgp", FoundCount: 2, AdmittedCount: 2),
            ]);
        module.Setup<IJSStreamReference>("readFileData", inv => true)
            .SetResult(new FakeJsStreamReference([1]));

        var sample = await folder.ResampleAsync();

        Assert.Equal(5, sample.Files.Length);
        AssertKinds(
            sample.Kinds,
            new SampledKind(".xg", foundCount: 7, admittedCount: 3, maxFileCount: Limits.MaxFileCountFor(".xg")),
            new SampledKind(".xgp", foundCount: 2, admittedCount: 2, maxFileCount: Limits.MaxFileCountFor(".xgp")));
        Assert.False(sample.HoldsEveryMatchingFile);
    }

    [Fact]
    public async Task Resample_NamesItsContext_AndPassesThePerExtensionCapsToTheModule_InTableOrder()
    {
        // The module keeps no copy of the table between a pick and a resample: the resample
        // hands it down again, whole and in order, for the folder it is a resample of.
        var module = JSInterop.SetupModule(ModulePath);
        SetupBeginPick(module, "ok", "Corpus", true, context: 12);
        SetupDraw(module, [], WholeDraw(UnsortedLimits));
        var sut = new JsFolderAccess(JSInterop.JSRuntime, UnsortedLimits);
        var folder = (await sut.PickFolderAsync(NoHook)).Folder!;

        await folder.ResampleAsync();

        var draws = module.Invocations["draw"];
        Assert.Equal(2, draws.Count);
        Assert.Equal(12, draws[1].Arguments[0]);
        AssertIsTheWireCaps(draws[1].Arguments[1]);
    }

    [Fact]
    public async Task Resample_CallsOnlyTheDrawAndTheByteReads()
    {
        // No picker, no permission request, no fallback collection: nothing a resample does
        // can show the user a prompt.
        var module = JSInterop.SetupModule(ModulePath);
        var folder = await PickedFolderAsync(module);
        var before = module.Invocations.Count;
        SetupDraw(module, new JsFolderAccess.JsPickedFile("a.xg", 1));
        module.Setup<IJSStreamReference>("readFileData", inv => true)
            .SetResult(new FakeJsStreamReference([1]));

        await folder.ResampleAsync();

        Assert.Equal(["draw", "readFileData"], module.Invocations.Skip(before).Select(i => i.Identifier).Distinct().Order());
    }

    [Fact]
    public async Task Resample_OversizedFile_FailsBeforeAnyByteTransfer()
    {
        var module = JSInterop.SetupModule(ModulePath);
        var folder = await PickedFolderAsync(module);
        SetupDraw(
            module,
            new JsFolderAccess.JsPickedFile("small.xg", 1),
            new JsFolderAccess.JsPickedFile("huge.xg", Limits.MaxFileBytes + 1));

        var ex = await Assert.ThrowsAsync<PickedFileTooLargeException>(() => folder.ResampleAsync());
        AssertOverTheCap(ex, "huge.xg", Limits.MaxFileBytes + 1);
        Assert.Empty(module.Invocations["readFileData"]);
    }

    [Fact]
    public async Task Resample_AReadFailingPartWayThroughBuffering_ReturnsNoPart_AndALaterResampleSucceeds()
    {
        // One file buffered, then the next file's read fails: the caller gets the exception
        // and nothing else, and the context keeps nothing that stops the next resample or the
        // folder's documents.
        var module = JSInterop.SetupModule(ModulePath);
        var folder = await PickedFolderAsync(module);
        SetupDraw(
            module,
            new JsFolderAccess.JsPickedFile("a.xg", 1),
            new JsFolderAccess.JsPickedFile("b.xg", 1));
        module.Setup<IJSStreamReference>("readFileData", inv => Equals(inv.Arguments[1], "a.xg"))
            .SetResult(new FakeJsStreamReference([1]));
        var bRead = module.Setup<IJSStreamReference>("readFileData", inv => Equals(inv.Arguments[1], "b.xg"));
        bRead.SetException(new JSException("'b.xg' could not be read."));
        module.Setup<string?>("readFile", _ => true).SetResult("{}");
        module.Setup<string?>("probeFileWritability", _ => true).SetResult("writable");

        FolderSample? partial = null;
        await Assert.ThrowsAsync<JSException>(async () => partial = await folder.ResampleAsync());

        Assert.Null(partial);
        Assert.Equal(["a.xg", "b.xg"], module.Invocations["readFileData"].Select(i => i.Arguments[1]));

        bRead.SetResult(new FakeJsStreamReference([2]));
        var sample = await folder.ResampleAsync();

        Assert.Equal(["a.xg", "b.xg"], sample.Files.Select(file => file.FileName));
        Assert.Equal<byte>([2], sample.Files[1].Bytes);
        Assert.Equal("{}", await folder.ReadFileAsync("setup.json"));
        Assert.Equal(FileWritability.Writable, await folder.ProbeFileWritabilityAsync("setup.json"));
    }

    // Every buffered file's IJSStreamReference is released. The runtime keeps each returned
    // buffer in its JS object table until the reference is disposed (dotnet/aspnetcore
    // v10.0.12), and the stream opened from it does not dispose it.

    /// <summary>
    /// Buffer one file, <c>a.xg</c> (3 bytes by its metadata), read through <paramref name="read"/>,
    /// by <paramref name="operation"/> — <c>"pick"</c>, <c>"fallback"</c> or <c>"resample"</c>:
    /// the three operations that reach the one buffering path.
    /// </summary>
    private async Task<FolderSample> BufferOneFileAsync(string operation, FakeJsStreamReference read)
    {
        var module = JSInterop.SetupModule(ModulePath);
        var file = new JsFolderAccess.JsPickedFile("a.xg", 3);
        module.Setup<IJSStreamReference>("readFileData", _ => true).SetResult(read);
        SetupRelease(module);
        var sut = CreateSut();
        switch (operation)
        {
            case "pick":
                SetupBeginPick(module, "ok", "Corpus", true);
                SetupDraw(module, file);
                return (await sut.PickFolderAsync(NoHook)).Sample;
            case "fallback":
                SetupCollectFallback(module, "Corpus", [file], WholeDraw(Limits, file));
                return (await sut.CollectFallbackAsync(default)).Sample;
            case "resample":
                var folder = await PickedFolderAsync(module);
                SetupDraw(module, file);
                return await folder.ResampleAsync();
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, "Not an operation that buffers.");
        }
    }

    [Theory]
    [InlineData("pick")]
    [InlineData("fallback")]
    [InlineData("resample")]
    public async Task BufferedFile_StreamReferenceIsReleased_OnceItsBytesAreCopied(string operation)
    {
        var read = new FakeJsStreamReference([1, 2, 3]);

        var sample = await BufferOneFileAsync(operation, read);

        Assert.Equal<byte>([1, 2, 3], Assert.Single(sample.Files).Bytes);
        Assert.Equal(1, read.DisposeCount);
        Assert.True(Assert.Single(read.OpenedStreams).Disposed);
    }

    [Fact]
    public async Task BufferedFile_StreamReferenceIsReleased_WhenOpeningTheStreamFails()
    {
        var read = new FakeJsStreamReference([1, 2, 3]) { OpenFailure = new JSException("The stream could not be opened.") };

        await Assert.ThrowsAsync<JSException>(() => BufferOneFileAsync("resample", read));

        Assert.Equal(1, read.DisposeCount);
        Assert.Empty(read.OpenedStreams);
    }

    [Fact]
    public async Task BufferedFile_StreamReferenceIsReleased_WhenCopyingFailsPartWay()
    {
        var read = new FakeJsStreamReference([1, 2, 3]) { FailAfterBytes = 2 };

        await Assert.ThrowsAsync<IOException>(() => BufferOneFileAsync("resample", read));

        Assert.Equal(1, read.DisposeCount);
        Assert.True(Assert.Single(read.OpenedStreams).Disposed);
    }

    // The byte cap's second enforcement point, and what it must not swallow.

    [Theory]
    [InlineData("pick")]
    [InlineData("fallback")]
    [InlineData("resample")]
    public async Task AFileThatGrewPastTheCapAfterTheDraw_ThrowsTheCapsException_AndReleasesItsReference(string operation)
    {
        // Within the cap by its metadata, over it by the length its bytes are handed over at:
        // the same exception as the metadata check, with the size the second check saw. The
        // stream is never opened, so the runtime's own ArgumentOutOfRangeException never arises.
        var read = new FakeJsStreamReference([1, 2, 3]) { Length = Limits.MaxFileBytes + 1 };

        var ex = await Assert.ThrowsAsync<PickedFileTooLargeException>(() => BufferOneFileAsync(operation, read));

        AssertOverTheCap(ex, "a.xg", Limits.MaxFileBytes + 1);
        Assert.Equal(1, read.DisposeCount);
        Assert.Empty(read.OpenedStreams);
    }

    [Fact]
    public async Task AFileExactlyAtTheCap_IsBuffered()
    {
        var read = new FakeJsStreamReference([1, 2, 3]) { Length = Limits.MaxFileBytes };

        var sample = await BufferOneFileAsync("resample", read);

        Assert.Equal("a.xg", Assert.Single(sample.Files).FileName);
        Assert.Equal(1, read.DisposeCount);
    }

    [Fact]
    public async Task AnArgumentOutOfRangeFromOpeningTheStream_IsNotTranslated()
    {
        // Within the cap, the stream fails to open for some other reason that happens to be an
        // ArgumentOutOfRangeException: it is not the byte cap and is not reported as one.
        var read = new FakeJsStreamReference([1, 2, 3])
        {
            OpenFailure = new ArgumentOutOfRangeException("offset", "Something else entirely."),
        };

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => BufferOneFileAsync("resample", read));

        Assert.Equal("offset", ex.ParamName);
        Assert.Equal(1, read.DisposeCount);
    }

    [Fact]
    public async Task AnArgumentOutOfRangeFromReadingTheReference_IsNotTranslated()
    {
        // The runtime's reference converter refuses a length that is not positive with an
        // ArgumentOutOfRangeException (dotnet/aspnetcore v10.0.12, JSStreamReference.cs), so
        // readFileData itself can fail that way, before this side holds any reference.
        var module = JSInterop.SetupModule(ModulePath);
        var folder = await PickedFolderAsync(module);
        SetupDraw(module, new JsFolderAccess.JsPickedFile("a.xg", 3));
        module.Setup<IJSStreamReference>("readFileData", _ => true)
            .SetException(new ArgumentOutOfRangeException("totalLength", 0L, "Length must be a positive value."));

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => folder.ResampleAsync());

        Assert.Equal("totalLength", ex.ParamName);
    }
}
