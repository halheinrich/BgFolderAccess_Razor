namespace BgFolderAccess_Razor;

using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.JSInterop;

/// <summary>
/// The JS-backed <see cref="IFolderContext"/>: one entry of the module's context table,
/// addressed by its id on every call. The draw, the byte cap and the buffering live here,
/// so a pick's draw and a resample are one path by construction; <see cref="JsFolderAccess"/>
/// lands the folder and hands over the context.
/// </summary>
internal sealed class JsFolderContext : IFolderContext
{
    private readonly IJSObjectReference _module;
    private readonly int _id;
    private readonly FolderPickLimits _limits;
    private readonly JsonElement _wireCaps;
    private bool _released;

    internal JsFolderContext(
        IJSObjectReference module, int id, string name, FolderWriteCapability capability,
        FolderPickLimits limits, JsonElement wireCaps)
    {
        _module = module;
        _id = id;
        Name = name;
        Capability = capability;
        _limits = limits;
        _wireCaps = wireCaps;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public FolderWriteCapability Capability { get; }

    /// <inheritdoc/>
    public Task<FolderSample> ResampleAsync() => DrawAsync();

    /// <summary>
    /// Draw the folder and buffer what the draw took: <c>draw</c> with the caps table, the
    /// per-kind report mapped, then every drawn file's bytes pulled across. All or nothing:
    /// any failure throws with nothing returned, and the module's draw changes no state.
    /// </summary>
    /// <exception cref="PickedFileTooLargeException">A drawn file is over the byte cap.</exception>
    /// <exception cref="JsonException">The module's report is not one entry per kind of the caps table, in its order.</exception>
    internal async Task<FolderSample> DrawAsync()
    {
        RequireHeld();
        var draw = await JsFolderAccess.InvokeDtoAsync(
            _module, "draw", BgFolderAccessJsonContext.Default.JsDraw, _id, _wireCaps);
        var kinds = ToKinds(draw.Kinds);
        var files = await BufferFilesAsync(draw.Files);
        return new FolderSample(files, kinds);
    }

    /// <inheritdoc/>
    public async Task<string?> ReadFileAsync(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        RequireHeld();
        return await _module.InvokeAsync<string?>("readFile", _id, fileName);
    }

    /// <inheritdoc/>
    public async Task WriteFileAsync(string fileName, string text)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(text);
        RequireHeld();
        await _module.InvokeVoidAsync("writeFile", _id, fileName, text);
    }

    /// <inheritdoc/>
    /// <exception cref="JsonException">The module answered with a string it does not ship.</exception>
    public async Task<FileWritability> ProbeFileWritabilityAsync(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        RequireHeld();
        return ToWritability(await _module.InvokeAsync<string?>("probeFileWritability", _id, fileName));
    }

    /// <summary>Release the folder's handle; a second call does nothing, and a runtime already torn down (tab close) is tolerated.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_released) return;
        _released = true;
        try
        {
            await _module.InvokeVoidAsync("release", _id);
        }
        catch (JSDisconnectedException)
        {
            // The runtime is gone, and the table with it.
        }
    }

    private void RequireHeld() => ObjectDisposedException.ThrowIf(_released, this);

    /// <summary>
    /// The one place the probe's wire strings are read; which DOMException names mean
    /// "not writable" is decided in the module, never re-interpreted here.
    /// </summary>
    private static FileWritability ToWritability(string? wire) => wire switch
    {
        "absent" => FileWritability.Absent,
        "writable" => FileWritability.Writable,
        "notWritable" => FileWritability.NotWritable,
        _ => throw new JsonException(
            $"'probeFileWritability' returned '{wire ?? "null"}', which is not one of the module's answers."),
    };

    /// <summary>
    /// Map the module's per-kind report, the one construction site of a reported kind. Each
    /// kind's cap comes from the enforced table's entry at the same position, never off the
    /// wire, so the figure a host states is the figure the draw enforced. Runs before any
    /// bytes move.
    /// </summary>
    private ImmutableArray<SampledKind> ToKinds(JsFolderAccess.JsDrawnKind[] drawn)
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

    /// <summary>
    /// Pull every drawn file's bytes across into <see cref="PickedFile"/>s: the metadata is
    /// size-checked first so an oversized file fails before any transfer, and each file again
    /// by the length its bytes arrive at. Every stream reference is disposed, not only the
    /// stream opened from it: the runtime keeps the buffer in its JS object table until the
    /// reference's DisposeAsync removes it (dotnet/aspnetcore v10.0.12), so a dropped reference
    /// would root every buffered file for the life of the tab.
    /// </summary>
    private async Task<ImmutableArray<PickedFile>> BufferFilesAsync(JsFolderAccess.JsPickedFile[] metadata)
    {
        foreach (var file in metadata)
        {
            RequireWithinByteCap(file.Name, file.Size);
        }

        var picked = ImmutableArray.CreateBuilder<PickedFile>(metadata.Length);
        foreach (var file in metadata)
        {
            await using var streamRef = await _module.InvokeAsync<IJSStreamReference>("readFileData", _id, file.Name);
            // Checked before opening, so a file that grew since the draw surfaces as the cap's
            // own exception rather than OpenReadStreamAsync's ArgumentOutOfRangeException.
            RequireWithinByteCap(file.Name, streamRef.Length);
            await using var stream = await streamRef.OpenReadStreamAsync(maxAllowedSize: _limits.MaxFileBytes);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            // ToArray's result is fresh and kept by nothing else, so the array moves in without a copy.
            picked.Add(new PickedFile(file.Name, ImmutableCollectionsMarshal.AsImmutableArray(ms.ToArray())));
        }

        return picked.MoveToImmutable();
    }

    private void RequireWithinByteCap(string fileName, long size)
    {
        if (size > _limits.MaxFileBytes)
        {
            throw new PickedFileTooLargeException(fileName, size, _limits);
        }
    }
}
