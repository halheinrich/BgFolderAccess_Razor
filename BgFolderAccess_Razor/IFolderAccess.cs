namespace BgFolderAccess_Razor;

using Microsoft.AspNetCore.Components;

/// <summary>
/// The host's one gateway to the browser's folder facilities — directory
/// picking (both mechanisms), buffered file reads, and named-file read/write
/// on each slot — backed by the <c>folderAccess.js</c> module. Host pages and
/// stores depend on this interface; only the <see cref="JsFolderAccess"/>
/// implementation touches JS interop, and the browser-side directory handles
/// never cross the boundary at all (they live in JS module state).
///
/// <para>
/// <b>Two-slot model.</b> A pick populates the JS module's <i>picked</i> slot;
/// promoting it (<see cref="PromoteToActiveAsync"/>) binds the <i>active</i>
/// slot, which the active-slot read/write pair then operates on. The split is
/// what isolates a running session from a mid-session Clear or re-pick:
/// <see cref="ClearPickedAsync"/> resets only the picked slot, so work bound to
/// the active slot keeps its handle until the next promotion re-binds. The
/// picked slot additionally serves setup-time documents on the folder being
/// configured (<see cref="ReadPickedFileAsync"/> /
/// <see cref="WritePickedFileAsync"/>) — never touching the active slot a
/// running session records through.
/// </para>
///
/// <para>
/// <b>Error signaling.</b> Expected outcomes are values, never exceptions: a
/// pick that ended with no folder — dismissed picker or declined read — is
/// <see cref="FolderPickOutcome.Cancelled"/>, a write denial is
/// <see cref="FolderWriteCapability.PermissionDenied"/>, a missing named file
/// is a <c>null</c> read. Unexpected browser failures surface as
/// <see cref="Microsoft.JSInterop.JSException"/> for callers to catch and
/// degrade on.
/// </para>
/// </summary>
public interface IFolderAccess
{
    /// <summary>
    /// True when the browser offers <c>showDirectoryPicker</c> (the File
    /// System Access path). Probed at pick time, per gesture — capability is a
    /// property of the moment, not of app boot.
    /// </summary>
    ValueTask<bool> SupportsDirectoryPickerAsync();

    /// <summary>
    /// Run the File System Access pick: native directory picker, readwrite
    /// permission request, top-level enumeration, and full buffering of every
    /// matching file (the kinds the configured
    /// <see cref="FolderPickLimits.MaxFileCounts"/> names). Call only when
    /// <see cref="SupportsDirectoryPickerAsync"/> is true.
    /// </summary>
    /// <param name="onPickAccepted">
    /// Invoked — and awaited — once the browser has finished asking the user
    /// (picker dismissed with a folder, permission request answered) and
    /// <i>before</i> the enumeration and buffering begin. It is the seam between
    /// "the user is deciding" and "the app is working", which is the only place
    /// a busy affordance can be raised truthfully: raised any earlier it would
    /// claim the app was working while a modal waited on the user; raised any
    /// later it could not paint, because the scan that follows never yields to
    /// the renderer on WebAssembly's single thread. Hosts pass the hook that
    /// raises and paints their busy state.
    /// <b>Not</b> invoked for a cancelled pick — there is no work to report.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// A matching file is larger than the configured
    /// <see cref="FolderPickLimits.MaxFileBytes"/>. A folder past the
    /// <i>count</i> caps does not throw — it truncates per kind and reports it
    /// (<see cref="FolderPickOutcome.Truncations"/>).
    /// </exception>
    Task<FolderPickOutcome> PickFolderAsync(Func<Task> onPickAccepted);

    /// <summary>
    /// Open the hidden <c>webkitdirectory</c> input's native picker — the
    /// fallback gesture for browsers without File System Access. The eventual
    /// pick arrives via the input's own <c>change</c> event, handled with
    /// <see cref="CollectFallbackAsync"/>; a dismissal fires nothing.
    /// </summary>
    Task TriggerFallbackPickerAsync(ElementReference fallbackInput);

    /// <summary>
    /// Collect the fallback pick from the hidden input's FileList: filter to
    /// top-level matching entries (by <c>webkitRelativePath</c> depth — the
    /// browser hands over the whole tree) and buffer them. The outcome's
    /// capability is always
    /// <see cref="FolderWriteCapability.BrowserUnsupported"/>: this mechanism
    /// yields no writable handle. An empty FileList is a non-cancelled outcome
    /// with zero files. The count caps apply here exactly as on the other
    /// mechanism — same module, same table, same per-kind truncation report.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A matching file is larger than the configured
    /// <see cref="FolderPickLimits.MaxFileBytes"/> (the count caps truncate
    /// rather than throw — see <see cref="PickFolderAsync"/>).
    /// </exception>
    Task<FolderPickOutcome> CollectFallbackAsync(ElementReference fallbackInput);

    /// <summary>
    /// Promote the picked slot's directory handle to the active slot. Returns
    /// false when no File System Access handle is picked (fallback pick,
    /// cleared slot, or never picked) — the caller's no-write signal for the
    /// work being started. Call from the host's begin-work bind.
    /// </summary>
    ValueTask<bool> PromoteToActiveAsync();

    /// <summary>
    /// Read the named file's text from the <i>picked</i> slot's folder — the
    /// setup-time document surface, read on the folder being configured before
    /// any promotion binds the active slot. <c>null</c> means the file doesn't
    /// exist yet — an expected absence, not an error. Reading the picked slot,
    /// not the active one, is deliberate: setup-time documents belong to the
    /// folder being configured and are isolated from whatever a running
    /// session records through the active slot.
    /// </summary>
    Task<string?> ReadPickedFileAsync(string fileName);

    /// <summary>
    /// Write <paramref name="json"/> as the named file into the <i>picked</i>
    /// slot's folder, replacing any existing content.
    /// </summary>
    Task WritePickedFileAsync(string fileName, string json);

    /// <summary>
    /// Read the named file's text from the <i>active</i> slot's folder — the
    /// surface a running session records through. <c>null</c> means the file
    /// doesn't exist yet — an expected absence (a fresh folder), not an error;
    /// the same contract as <see cref="ReadPickedFileAsync"/>.
    /// </summary>
    Task<string?> ReadActiveFileAsync(string fileName);

    /// <summary>
    /// Write <paramref name="json"/> as the named file into the <i>active</i>
    /// slot's folder, replacing any existing content.
    /// </summary>
    Task WriteActiveFileAsync(string fileName, string json);

    /// <summary>
    /// Clear the <i>picked</i> slot only — the active slot persists so work
    /// bound to it keeps recording. Pairs with a host's Clear affordance.
    /// </summary>
    ValueTask ClearPickedAsync();
}
