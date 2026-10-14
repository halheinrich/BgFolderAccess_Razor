namespace BgFolderAccess_Razor;

using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

/// <summary>
/// The JS-backed <see cref="IFolderAccess"/>: a thin, typed facade over the
/// <c>folderAccess.js</c> ES module this library ships as a static web asset.
/// The one type in the library that holds an <see cref="IJSObjectReference"/> —
/// everything above it (host pages, host stores) sees only the interface, and
/// the browser-side directory handles never leave the module's own state.
///
/// <para>
/// Lifetime: register <b>Scoped</b> (one per tab), beside the host's one
/// <see cref="FolderPickLimits"/> instance. The module import is lazy and
/// cached — first use pays the fetch, later calls reuse it.
/// </para>
///
/// <para>
/// Caps are enforced against the pick's metadata, <i>before</i> any bytes move,
/// and the two caps end differently. A file larger than
/// <see cref="FolderPickLimits.MaxFileBytes"/> fails the whole pick here with
/// an <see cref="InvalidOperationException"/> for the host to surface as its
/// pick-error notice. The per-extension <i>count</i> caps
/// (<see cref="FolderPickLimits.MaxFileCounts"/>) instead <b>truncate</b>: they
/// are applied JS-side, where the extension is known before any transfer, and
/// the module's per-kind account of what it found and admitted rides back as
/// <see cref="FolderSample.Kinds"/> for the host to report. This type's part in
/// that is to hand the caps table down and map the reply — it holds no cap
/// logic of its own. The per-file byte transfer additionally passes the byte
/// cap to <see cref="IJSStreamReference.OpenReadStreamAsync"/> as a
/// belt-and-braces bound on what actually crosses the boundary.
/// </para>
/// </summary>
public sealed class JsFolderAccess : IFolderAccess, IAsyncDisposable
{
    private const string ModulePath = "./_content/BgFolderAccess_Razor/js/folderAccess.js";

    private readonly IJSRuntime _js;
    private readonly FolderPickLimits _limits;
    private readonly JsonElement _wireCaps;
    private Task<IJSObjectReference>? _module;

    /// <summary>
    /// Create the facade over <paramref name="js"/>, enforcing
    /// <paramref name="limits"/> — the host's caps configuration, injected once
    /// rather than carried on each call.
    /// </summary>
    public JsFolderAccess(IJSRuntime js, FolderPickLimits limits)
    {
        _js = js ?? throw new ArgumentNullException(nameof(js));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _wireCaps = ToWireCaps(limits);
    }

    /// <summary>
    /// The caps table as the module receives it: one JSON object,
    /// <c>{ ".xg": 500, ".xgp": 2000 }</c>, whose members are written in
    /// <see cref="FolderPickLimits.MaxFileCounts"/>' order — derived here, once,
    /// from the one table, so the module still holds no copy of its own.
    ///
    /// <para>
    /// <b>Why written by hand.</b> The order is the table's contract and the
    /// module's per-kind report reads in it, so the wire has to carry it —
    /// and <see cref="Utf8JsonWriter"/> writes members in exactly the order it
    /// is told, with no reflection for a trim to break. The JS side keeps it:
    /// <c>JSON.parse</c> creates properties in text order, and
    /// <c>Object.keys</c> lists string keys in creation order for every key
    /// that is not an array index — a dot-leading extension never is.
    /// </para>
    ///
    /// <para>
    /// A <see cref="JsonElement"/> crosses as an argument the way it crosses as
    /// a reply: a framework type the JS runtime writes with its own converter
    /// and nothing of this assembly's.
    /// </para>
    ///
    /// <para>
    /// <b>Internal, for one reader.</b> The JS-engine suite
    /// (<c>FolderAccessModuleHost</c>) hands the shipped module exactly this
    /// object, so it runs the wire this type sends rather than a second
    /// encoding of it. It takes the whole <see cref="FolderPickLimits"/>, not
    /// its array, so no wire object can be derived from a table the
    /// constructor has not validated.
    /// </para>
    /// </summary>
    internal static JsonElement ToWireCaps(FolderPickLimits limits)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (extension, cap) in limits.MaxFileCounts)
            {
                writer.WriteNumber(extension, cap);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    // The wire DTOs are internal (not private) solely so the bUnit module tests
    // can construct scripted results; nothing outside this type, the context
    // and those tests touches them.
    //
    // A WIRE DTO IS NEVER A TYPE ARGUMENT TO INTEROP. Each reply is taken back
    // as a JsonElement and read through BgFolderAccessJsonContext by
    // InvokeDtoAsync below — the one spelling of the rule, which the two
    // DTO-returning calls (beginPick, drawPicked) use. InvokeAsync<T>'s
    // trimmer annotation keeps T through a trim but not the records nested in
    // T (halheinrich/backgammon#197:
    // BgQuiz's trimmed publish lost JsPickedFile's constructor that way, with
    // the analyzer silent because the call it inspects is annotated). The
    // context names the whole graph at compile time, so the trimmer has
    // nothing to decide. The context's doc comment carries the full account.

    /// <summary>
    /// The first half of the pick as the JS module shapes it (camelCase on the
    /// wire): what the browser's prompts settled — cancelled or not, the folder
    /// name, and whether write was granted. Carries no files; the draw is the
    /// second call (see <see cref="PickFolderAsync"/>).
    /// </summary>
    internal sealed record JsPickStart(string Status, string DirectoryName, bool Writable);

    /// <summary>
    /// One draw of the held folder, the reply to <c>drawPicked</c> on every
    /// path — pick, fallback collection and resample alike. <see cref="Status"/>
    /// is <c>"drawn"</c>, with the admitted files' metadata (drawn down to the
    /// caps table the call passed in) and one <see cref="JsDrawnKind"/> per kind
    /// of that table; or <c>"noFolder"</c>, with both arrays empty, when the
    /// module held no folder to draw from.
    /// </summary>
    internal sealed record JsDraw(string Status, JsPickedFile[] Files, JsDrawnKind[] Kinds);

    /// <summary>One enumerated file's metadata, before its bytes are pulled.</summary>
    internal sealed record JsPickedFile(string Name, long Size);

    /// <summary>
    /// One kind's found and admitted counts as the module reports them. The
    /// <i>cap</i> is not on the wire: it came from this side in the first place,
    /// so <see cref="ToKinds"/> derives it back rather than trusting a
    /// round-tripped copy.
    /// </summary>
    internal sealed record JsDrawnKind(string Extension, int FoundCount, int AdmittedCount);

    private Task<IJSObjectReference> ModuleAsync() =>
        _module ??= _js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();

    /// <summary>
    /// The one way a wire DTO comes back from the module: the call takes a
    /// <see cref="JsonElement"/> — a framework type the JS runtime reads with
    /// nothing of this assembly's — and the reply is deserialized here through
    /// <paramref name="wireShape"/>, the context's compile-time metadata for
    /// <typeparamref name="TDto"/>. Never <c>InvokeAsync&lt;TDto&gt;</c>: see the
    /// rule above the DTOs. The trim-safe overload,
    /// <c>JsonSerializer.Deserialize(JsonElement, JsonTypeInfo&lt;T&gt;)</c>, is
    /// the one the analyzer accepts without a suppression.
    /// </summary>
    /// <exception cref="JsonException">
    /// The module answered with JSON <c>null</c>, which no reply-returning
    /// function of the shipped module does — a wire-contract violation, not an
    /// expected absence, so it is not folded into a value.
    /// </exception>
    private static async ValueTask<TDto> InvokeDtoAsync<TDto>(
        IJSObjectReference module, string identifier, JsonTypeInfo<TDto> wireShape, params object?[] args)
    {
        var reply = await module.InvokeAsync<JsonElement>(identifier, args);
        return reply.Deserialize(wireShape)
            ?? throw new JsonException($"'{identifier}' returned JSON null where a {typeof(TDto).Name} was expected.");
    }

    /// <inheritdoc/>
    public async ValueTask<bool> SupportsDirectoryPickerAsync()
    {
        var module = await ModuleAsync();
        return await module.InvokeAsync<bool>("supportsDirectoryPicker");
    }

    /// <summary>
    /// Two module calls behind one method: <c>beginPick</c> runs the browser's
    /// prompts and binds the folder, then — with
    /// <paramref name="onPickAccepted"/> awaited in between, which is what lets a
    /// caller paint before the wait — <see cref="DrawSampleAsync"/> draws it and
    /// buffers what it drew.
    /// </summary>
    /// <inheritdoc/>
    public async Task<FolderPickOutcome> PickFolderAsync(Func<Task> onPickAccepted)
    {
        ArgumentNullException.ThrowIfNull(onPickAccepted);

        var module = await ModuleAsync();
        var start = await InvokeDtoAsync(
            module, "beginPick", BgFolderAccessJsonContext.Default.JsPickStart);
        if (start.Status == "cancelled")
        {
            return FolderPickOutcome.CancelledOutcome;
        }

        // The prompts are done and the app's own work starts now. Everything
        // below is the "no feedback" stretch the hook exists to cover.
        await onPickAccepted();

        var sample = await DrawSampleAsync(module);
        var capability = start.Writable ? FolderWriteCapability.Enabled : FolderWriteCapability.PermissionDenied;
        return new FolderPickOutcome(Cancelled: false, start.DirectoryName, capability, sample);
    }

    /// <inheritdoc/>
    public async Task TriggerFallbackPickerAsync(ElementReference fallbackInput)
    {
        var module = await ModuleAsync();
        await module.InvokeVoidAsync("clickElement", fallbackInput);
    }

    /// <summary>
    /// Two module calls, in the shape of <see cref="PickFolderAsync"/>'s:
    /// <c>collectFallbackFiles</c> binds the folder the browser handed over and
    /// answers its name, then <see cref="DrawSampleAsync"/> draws it.
    /// </summary>
    /// <inheritdoc/>
    /// <exception cref="JsonException">The module answered the collection with JSON <c>null</c>.</exception>
    public async Task<FolderPickOutcome> CollectFallbackAsync(ElementReference fallbackInput)
    {
        var module = await ModuleAsync();
        var directoryName = await module.InvokeAsync<string?>("collectFallbackFiles", fallbackInput)
            ?? throw new JsonException("'collectFallbackFiles' returned JSON null where a folder name was expected.");
        var sample = await DrawSampleAsync(module);
        return new FolderPickOutcome(
            Cancelled: false, directoryName, FolderWriteCapability.BrowserUnsupported, sample);
    }

    /// <summary>
    /// One module call, the draw both pick paths end in, and nothing else: no
    /// picker, no permission request, no busy hook.
    /// </summary>
    /// <inheritdoc/>
    public async Task<FolderSample> ResampleAsync()
    {
        var module = await ModuleAsync();
        return await DrawSampleAsync(module);
    }

    /// <summary>
    /// Draw the held folder and buffer what the draw took: <c>drawPicked</c>
    /// with the caps table, then the per-kind report mapped
    /// (<see cref="ToKinds"/>), then every drawn file's bytes pulled across
    /// (<see cref="BufferFilesAsync"/>). The one path from a held folder to a
    /// <see cref="FolderSample"/>, so a pick, a fallback collection and a
    /// resample share the draw, the byte cap and the buffering by construction.
    ///
    /// <para>
    /// <b>All or nothing.</b> The sample is built only once every byte is in;
    /// any failure on the way — the draw refused, the byte cap, a read — throws
    /// out of here with nothing returned. Nothing is staged on either side to
    /// undo: this type keeps no state of its own, and the module's draw and
    /// reads change none of its state (see <c>drawPicked</c>).
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The module held no folder to draw from; or a drawn file is larger than
    /// <see cref="FolderPickLimits.MaxFileBytes"/>.
    /// </exception>
    /// <exception cref="JsonException">The reply's status is not one the module ships.</exception>
    private async Task<FolderSample> DrawSampleAsync(IJSObjectReference module)
    {
        var draw = await InvokeDtoAsync(
            module, "drawPicked", BgFolderAccessJsonContext.Default.JsDraw, _wireCaps);
        switch (draw.Status)
        {
            case "drawn":
                break;
            case "noFolder":
                throw new InvalidOperationException(
                    "No folder is held to draw from: none has been picked, or ClearPickedAsync released it.");
            default:
                throw new JsonException(
                    $"'drawPicked' returned status '{draw.Status}', which is not one of the module's answers.");
        }

        var kinds = ToKinds(draw.Kinds);
        var files = await BufferFilesAsync(module, draw.Files);
        return new FolderSample(files, kinds);
    }

    /// <summary>
    /// Map the module's per-kind report into <see cref="SampledKind"/>s — the
    /// module's one shape for both mechanisms, so this mapping is written once,
    /// and the one construction site of a reported kind. Each kind's cap is
    /// taken from the enforced table's entry at the same position, never off
    /// the wire, so the figure a host states is the figure the draw enforced.
    /// Called before any bytes move, so a malformed report costs no transfer.
    /// </summary>
    /// <exception cref="JsonException">
    /// The report is not one entry per kind of
    /// <see cref="FolderPickLimits.MaxFileCounts"/>, in the table's order — a
    /// wire-contract violation by the shipped module, since the table it was
    /// handed is what it reports over.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A kind's counts are ones <see cref="SampledKind"/> refuses (more
    /// admitted than found or than the cap) — the same violation, caught by
    /// the record's own validation rather than restated here.
    /// </exception>
    private ImmutableArray<SampledKind> ToKinds(JsDrawnKind[] drawn)
    {
        var table = _limits.MaxFileCounts;
        if (drawn.Length != table.Length)
        {
            throw new JsonException(
                $"The module reported {drawn.Length} kinds for a caps table of {table.Length}.");
        }

        var kinds = ImmutableArray.CreateBuilder<SampledKind>(table.Length);
        for (var i = 0; i < table.Length; i++)
        {
            var (extension, maxFileCount) = table[i];
            if (!string.Equals(drawn[i].Extension, extension, StringComparison.Ordinal))
            {
                throw new JsonException(
                    $"The module reported '{drawn[i].Extension}' where the caps table's entry {i} is '{extension}'.");
            }

            kinds.Add(new SampledKind(extension, drawn[i].FoundCount, drawn[i].AdmittedCount, maxFileCount));
        }

        return kinds.MoveToImmutable();
    }

    /// <inheritdoc/>
    public async ValueTask<bool> PromoteToActiveAsync()
    {
        var module = await ModuleAsync();
        return await module.InvokeAsync<bool>("promoteToActive");
    }

    /// <inheritdoc/>
    public async Task<string?> ReadPickedFileAsync(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var module = await ModuleAsync();
        return await module.InvokeAsync<string?>("readPickedFile", fileName);
    }

    /// <inheritdoc/>
    public async Task WritePickedFileAsync(string fileName, string json)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(json);
        var module = await ModuleAsync();
        await module.InvokeVoidAsync("writePickedFile", fileName, json);
    }

    /// <inheritdoc/>
    /// <exception cref="JsonException">
    /// The module answered with a string it does not ship (see
    /// <see cref="ToWritability"/>).
    /// </exception>
    public async Task<PickedFileWritability> ProbePickedFileWritabilityAsync(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var module = await ModuleAsync();
        return ToWritability(await module.InvokeAsync<string?>("probePickedFileWritability", fileName));
    }

    /// <summary>
    /// Map <c>probePickedFileWritability</c>'s answer onto
    /// <see cref="PickedFileWritability"/> — the one place the wire strings are
    /// read. The module has already turned the browser's answer into one of
    /// three strings; which <c>DOMException</c> names mean "not writable" is
    /// decided there, in one array, and never re-interpreted here.
    /// </summary>
    /// <exception cref="JsonException">
    /// Any other answer, <c>null</c> included — a wire-contract violation by the
    /// shipped module, not an expected outcome, so it is not folded into a value.
    /// </exception>
    private static PickedFileWritability ToWritability(string? wire) => wire switch
    {
        "absent" => PickedFileWritability.Absent,
        "writable" => PickedFileWritability.Writable,
        "notWritable" => PickedFileWritability.NotWritable,
        _ => throw new JsonException(
            $"'probePickedFileWritability' returned '{wire ?? "null"}', which is not one of the module's answers."),
    };

    /// <inheritdoc/>
    public async Task<string?> ReadActiveFileAsync(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var module = await ModuleAsync();
        return await module.InvokeAsync<string?>("readActiveFile", fileName);
    }

    /// <inheritdoc/>
    public async Task WriteActiveFileAsync(string fileName, string json)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(json);
        var module = await ModuleAsync();
        await module.InvokeVoidAsync("writeActiveFile", fileName, json);
    }

    /// <inheritdoc/>
    public async ValueTask ClearPickedAsync()
    {
        var module = await ModuleAsync();
        await module.InvokeVoidAsync("clearPicked");
    }

    /// <summary>
    /// Pull every enumerated file's bytes across the boundary into
    /// <see cref="PickedFile"/>s, size-checking the metadata first so an
    /// oversized file fails fast before any transfer starts. The count caps have
    /// already been applied JS-side — what arrives here is what the pick took.
    /// </summary>
    private async Task<ImmutableArray<PickedFile>> BufferFilesAsync(
        IJSObjectReference module, JsPickedFile[] metadata)
    {
        foreach (var file in metadata)
        {
            if (file.Size > _limits.MaxFileBytes)
            {
                throw new InvalidOperationException(
                    $"'{file.Name}' is larger than the {_limits.MaxFileMegabytes} MB per-file limit.");
            }
        }

        var picked = ImmutableArray.CreateBuilder<PickedFile>(metadata.Length);
        foreach (var file in metadata)
        {
            // Stream the bytes rather than marshaling one giant byte[] result:
            // IJSStreamReference is the supported large-payload path, and its
            // maxAllowedSize re-asserts the byte cap on what actually crosses.
            //
            // THE REFERENCE IS DISPOSED, not just the stream opened from it.
            // The runtime keeps the file's buffer in its JS object table until
            // the reference's DisposeAsync removes it (Microsoft.JSInterop.ts,
            // createJSStreamReference; JSObjectReference.DisposeAsync), and the
            // stream does not dispose the reference (PullFromJSDataStream;
            // dotnet/aspnetcore v10.0.12). Left to the stream alone, every
            // buffered file stayed rooted in the browser for the life of the
            // tab, and every resample added a sample's worth. `await using`
            // releases it once the copy is done, and on any failure to open or
            // copy; declared first, it is released last, after the stream.
            await using var streamRef = await module.InvokeAsync<IJSStreamReference>("readFileData", file.Name);
            await using var stream = await streamRef.OpenReadStreamAsync(
                maxAllowedSize: _limits.MaxFileBytes);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            // file.Name carries the extension — the stated PickedFile.FileName
            // contract hosts may discriminate format from. The bytes move into
            // the immutable array without a copy: ToArray's result is fresh and
            // this method keeps no reference to it, so the PickedFile is its
            // only owner from here on.
            picked.Add(new PickedFile(
                file.Name, ImmutableCollectionsMarshal.AsImmutableArray(ms.ToArray())));
        }

        // One slot per metadata entry, all filled: the builder's array becomes
        // the outcome's without a copy, and the builder keeps none of it.
        return picked.MoveToImmutable();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try
        {
            var module = await _module;
            await module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // Runtime already torn down (tab close / reload) — nothing to release.
        }
    }
}
