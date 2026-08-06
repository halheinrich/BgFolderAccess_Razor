namespace BgFolderAccess_Razor;

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
/// what they left behind rides back as
/// <see cref="FolderPickOutcome.Truncations"/> for the host to report. This
/// type's part in that is to hand the caps table down and map the reply — it
/// holds no cap logic of its own. The per-file byte transfer additionally
/// passes the byte cap to <see cref="IJSStreamReference.OpenReadStreamAsync"/>
/// as a belt-and-braces bound on what actually crosses the boundary.
/// </para>
/// </summary>
public sealed class JsFolderAccess : IFolderAccess, IAsyncDisposable
{
    private const string ModulePath = "./_content/BgFolderAccess_Razor/js/folderAccess.js";

    private readonly IJSRuntime _js;
    private readonly FolderPickLimits _limits;
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
    }

    // The wire DTOs are internal (not private) solely so the bUnit module tests
    // can construct scripted results; nothing outside this type and those tests
    // touches them.

    /// <summary>
    /// The first half of the pick as the JS module shapes it (camelCase on the
    /// wire): what the browser's prompts settled — cancelled or not, the folder
    /// name, and whether write was granted. Carries no files; the enumeration
    /// is the second call (see <see cref="PickFolderAsync"/>).
    /// </summary>
    internal sealed record JsPickStart(string Status, string DirectoryName, bool Writable);

    /// <summary>
    /// The enumeration result: the picked folder's top-level matching files,
    /// metadata only, already truncated to the caps table this call passed in —
    /// plus <see cref="JsOmittedFiles"/> for whatever that truncation dropped.
    /// </summary>
    internal sealed record JsEnumerateResult(JsPickedFile[] Files, JsOmittedFiles[] Omitted);

    /// <summary>The fallback-collection result — no status (nothing to cancel) and no writable claim.</summary>
    internal sealed record JsFallbackResult(string DirectoryName, JsPickedFile[] Files, JsOmittedFiles[] Omitted);

    /// <summary>One enumerated file's metadata, before its bytes are pulled.</summary>
    internal sealed record JsPickedFile(string Name, long Size);

    /// <summary>
    /// One kind's left-behind count as the module reports it. The <i>cap</i> is
    /// not on the wire: it came from this side in the first place, so
    /// <see cref="ToTruncations"/> derives it back rather than trusting a
    /// round-tripped copy.
    /// </summary>
    internal sealed record JsOmittedFiles(string Extension, int OmittedCount);

    private Task<IJSObjectReference> ModuleAsync() =>
        _module ??= _js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();

    /// <inheritdoc/>
    public async ValueTask<bool> SupportsDirectoryPickerAsync()
    {
        var module = await ModuleAsync();
        return await module.InvokeAsync<bool>("supportsDirectoryPicker");
    }

    /// <summary>
    /// Two module calls behind one method, so the stateful half-picked slot the
    /// split creates never escapes this type: <c>beginPick</c> runs the browser's
    /// prompts, then — with <paramref name="onPickAccepted"/> awaited in
    /// between, which is what lets a caller paint before the wait —
    /// <c>enumeratePicked</c> lists the folder and its files are buffered across.
    /// </summary>
    public async Task<FolderPickOutcome> PickFolderAsync(Func<Task> onPickAccepted)
    {
        ArgumentNullException.ThrowIfNull(onPickAccepted);

        var module = await ModuleAsync();
        var start = await module.InvokeAsync<JsPickStart>("beginPick");
        if (start.Status == "cancelled")
        {
            return FolderPickOutcome.CancelledOutcome;
        }

        // The prompts are done and the app's own work starts now. Everything
        // below is the "no feedback" stretch the hook exists to cover.
        await onPickAccepted();

        var enumerated = await module.InvokeAsync<JsEnumerateResult>(
            "enumeratePicked", _limits.MaxFileCounts);
        var files = await BufferFilesAsync(module, enumerated.Files);
        var capability = start.Writable ? FolderWriteCapability.Enabled : FolderWriteCapability.PermissionDenied;
        return new FolderPickOutcome(
            Cancelled: false, start.DirectoryName, files, capability, ToTruncations(enumerated.Omitted));
    }

    /// <inheritdoc/>
    public async Task TriggerFallbackPickerAsync(ElementReference fallbackInput)
    {
        var module = await ModuleAsync();
        await module.InvokeVoidAsync("clickElement", fallbackInput);
    }

    /// <inheritdoc/>
    public async Task<FolderPickOutcome> CollectFallbackAsync(ElementReference fallbackInput)
    {
        var module = await ModuleAsync();
        var result = await module.InvokeAsync<JsFallbackResult>(
            "collectFallbackFiles", fallbackInput, _limits.MaxFileCounts);
        var files = await BufferFilesAsync(module, result.Files);
        return new FolderPickOutcome(
            Cancelled: false, result.DirectoryName, files, FolderWriteCapability.BrowserUnsupported,
            ToTruncations(result.Omitted));
    }

    /// <summary>
    /// Map the module's per-kind left-behind report into
    /// <see cref="PickTruncation"/>s — the module's one shape for both
    /// mechanisms, so this mapping is written once. Order is the module's, which
    /// is <see cref="FolderPickLimits.MaxFileCounts"/>'s, so a multi-kind notice
    /// reads the same way every time. This is the one construction site of a
    /// reported truncation, and where its cap figure is derived from the
    /// enforced table rather than trusted off the wire.
    /// </summary>
    private IReadOnlyList<PickTruncation> ToTruncations(JsOmittedFiles[] omitted) =>
        [.. omitted.Select(o => new PickTruncation(o.Extension, o.OmittedCount, _limits.MaxFileCountFor(o.Extension)))];

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
    private async Task<IReadOnlyList<PickedFile>> BufferFilesAsync(
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

        var picked = new List<PickedFile>(metadata.Length);
        foreach (var file in metadata)
        {
            // Stream the bytes rather than marshaling one giant byte[] result:
            // IJSStreamReference is the supported large-payload path, and its
            // maxAllowedSize re-asserts the byte cap on what actually crosses.
            var streamRef = await module.InvokeAsync<IJSStreamReference>("readFileData", file.Name);
            await using var stream = await streamRef.OpenReadStreamAsync(
                maxAllowedSize: _limits.MaxFileBytes);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            // file.Name carries the extension — the stated PickedFile.FileName
            // contract hosts may discriminate format from.
            picked.Add(new PickedFile(file.Name, ms.ToArray()));
        }

        return picked;
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
