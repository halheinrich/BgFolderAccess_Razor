namespace BgFolderAccess_Razor;

using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

/// <summary>
/// The JS-backed <see cref="IFolderAccess"/>: a thin facade over the <c>folderAccess.js</c>
/// module this library ships as a static web asset. It lands folders and hands each over
/// as a <see cref="JsFolderContext"/>; the module import is lazy and cached. Register
/// Scoped (one per tab) beside the host's one <see cref="FolderPickLimits"/>.
///
/// <para>
/// The per-extension count caps are applied in the module, where the extension is known
/// before any transfer, and truncate; the byte cap is enforced on the C# side before and
/// as bytes move, and fails the whole draw with <see cref="PickedFileTooLargeException"/>.
/// This type hands the caps table down once, in the table's order, and holds no cap logic.
/// </para>
/// </summary>
public sealed class JsFolderAccess : IFolderAccess, IAsyncDisposable
{
    private const string ModulePath = "./_content/BgFolderAccess_Razor/js/folderAccess.js";

    private readonly IJSRuntime _js;
    private readonly FolderPickLimits _limits;
    private readonly JsonElement _wireCaps;
    private Task<IJSObjectReference>? _module;

    /// <summary>Create the facade over <paramref name="js"/>, enforcing <paramref name="limits"/> on every draw.</summary>
    public JsFolderAccess(IJSRuntime js, FolderPickLimits limits)
    {
        _js = js ?? throw new ArgumentNullException(nameof(js));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _wireCaps = ToWireCaps(limits);
    }

    /// <summary>
    /// The caps table as the module receives it: one JSON object, <c>{ ".xg": 500, ".xgp": 2000 }</c>,
    /// members in <see cref="FolderPickLimits.MaxFileCounts"/>' order, since the module's
    /// per-kind report reads in that order and <c>JSON.parse</c> keeps it. Written by hand
    /// so there is no reflection for a trim to break. Internal for the JS-engine suite, which
    /// hands the shipped module this very object.
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

    // The wire DTOs: internal so the bUnit tests can script replies through the context.
    // A WIRE DTO IS NEVER A TYPE ARGUMENT TO INTEROP. Each reply comes back as a JsonElement
    // and is read through BgFolderAccessJsonContext by InvokeDtoAsync: InvokeAsync<T>'s
    // trimmer annotation keeps T but not the records nested in T, and BgQuiz's trimmed
    // publish lost JsPickedFile's constructor that way (halheinrich/backgammon#197).

    /// <summary>The first half of a File System Access pick: what the prompts settled, and the context the folder landed in (0 when cancelled).</summary>
    internal sealed record JsPickStart(string Status, string DirectoryName, bool Writable, int Context);

    /// <summary>A fallback collection: the folder's name and the context its candidates landed in.</summary>
    internal sealed record JsCollected(string DirectoryName, int Context);

    /// <summary>One draw of a folder: the admitted files' metadata, and one <see cref="JsDrawnKind"/> per kind of the caps table.</summary>
    internal sealed record JsDraw(JsPickedFile[] Files, JsDrawnKind[] Kinds);

    /// <summary>One drawn file's metadata, before its bytes are pulled.</summary>
    internal sealed record JsPickedFile(string Name, long Size);

    /// <summary>One kind's found and admitted counts; the cap is not on the wire, it is derived back from the enforced table.</summary>
    internal sealed record JsDrawnKind(string Extension, int FoundCount, int AdmittedCount);

    private Task<IJSObjectReference> ModuleAsync() =>
        _module ??= _js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();

    /// <summary>
    /// The one way a wire DTO comes back from the module: as a <see cref="JsonElement"/>,
    /// deserialized here through the context's compile-time metadata (the trim-safe overload).
    /// </summary>
    /// <exception cref="JsonException">The module answered JSON <c>null</c>, which no reply-returning function does.</exception>
    internal static async ValueTask<TDto> InvokeDtoAsync<TDto>(
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
    /// Two module calls behind one method: <c>beginPick</c> runs the prompts and lands the
    /// folder, then, with <paramref name="onPickAccepted"/> awaited in between so the caller
    /// can paint, the context draws and buffers it. A failure after the landing releases the
    /// context and rethrows, so the host never learns of a folder it could not read.
    /// </summary>
    /// <inheritdoc/>
    public async Task<FolderPickOutcome> PickFolderAsync(Func<Task> onPickAccepted)
    {
        ArgumentNullException.ThrowIfNull(onPickAccepted);

        var module = await ModuleAsync();
        var start = await InvokeDtoAsync(module, "beginPick", BgFolderAccessJsonContext.Default.JsPickStart);
        if (start.Status == "cancelled")
        {
            return FolderPickOutcome.CancelledOutcome;
        }

        var capability = start.Writable ? FolderWriteCapability.Enabled : FolderWriteCapability.PermissionDenied;
        var folder = new JsFolderContext(module, start.Context, start.DirectoryName, capability, _limits, _wireCaps);
        return await DrawLandedAsync(folder, onPickAccepted);
    }

    /// <inheritdoc/>
    public async Task TriggerFallbackPickerAsync(ElementReference fallbackInput)
    {
        var module = await ModuleAsync();
        await module.InvokeVoidAsync("clickElement", fallbackInput);
    }

    /// <summary>
    /// Two module calls, in the shape of <see cref="PickFolderAsync"/>'s: <c>collectFallbackFiles</c>
    /// lands the folder the browser handed over, then the context draws it.
    /// </summary>
    /// <inheritdoc/>
    public async Task<FolderPickOutcome> CollectFallbackAsync(ElementReference fallbackInput)
    {
        var module = await ModuleAsync();
        var collected = await InvokeDtoAsync(
            module, "collectFallbackFiles", BgFolderAccessJsonContext.Default.JsCollected, fallbackInput);
        var folder = new JsFolderContext(
            module, collected.Context, collected.DirectoryName, FolderWriteCapability.BrowserUnsupported, _limits, _wireCaps);
        return await DrawLandedAsync(folder, onPickAccepted: null);
    }

    /// <summary>
    /// The landed folder's first draw, behind the busy hook where there is one. On any
    /// failure the context is released, quietly, and the failure that ended the pick is the
    /// one the caller sees.
    /// </summary>
    private static async Task<FolderPickOutcome> DrawLandedAsync(JsFolderContext folder, Func<Task>? onPickAccepted)
    {
        try
        {
            if (onPickAccepted is not null) await onPickAccepted();
            var sample = await folder.DrawAsync();
            return new FolderPickOutcome(folder, sample);
        }
        catch
        {
            try
            {
                await folder.DisposeAsync();
            }
            catch (JSException)
            {
                // Releasing never misreports: the pick's own failure propagates.
            }

            throw;
        }
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
