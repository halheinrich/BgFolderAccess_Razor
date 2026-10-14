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
/// <see cref="WritePickedFileAsync"/>, and
/// <see cref="ProbePickedFileWritabilityAsync"/> ahead of a write) — never touching the active slot a
/// running session records through.
/// </para>
///
/// <para>
/// <b>The held folder.</b> The picked slot holds at most one folder, and
/// <see cref="ResampleAsync"/> and the picked slot's named-file reads, writes
/// and probe act on it. These transitions, and no others, change it:
/// </para>
/// <list type="bullet">
/// <item><description>
/// A File System Access pick (<see cref="PickFolderAsync"/>) replaces it once
/// the browser's prompts settle with a folder — before the draw, so a pick that
/// then fails (an oversized file, a refused read) still leaves the new folder
/// held. A <i>cancelled</i> File System Access pick changes nothing: the
/// previously held folder stays held.
/// </description></item>
/// <item><description>
/// A fallback collection (<see cref="CollectFallbackAsync"/>) replaces it, an
/// empty FileList included, and retains the folder's top-level files (as file
/// references, never their bytes) as the candidates a resample redraws from.
/// Opening the fallback picker (<see cref="TriggerFallbackPickerAsync"/>)
/// changes nothing until the collection.
/// </description></item>
/// <item><description>
/// <see cref="ClearPickedAsync"/> releases it, and with it the retained
/// candidates; nothing is held afterwards.
/// </description></item>
/// </list>
/// <para>
/// A resample never changes it, whether it succeeds or fails, and nor does
/// <see cref="PromoteToActiveAsync"/>, which copies the File System Access
/// handle into the active slot and leaves the picked slot as it was.
/// </para>
///
/// <para>
/// <b>Error signaling.</b> Expected outcomes are values, never exceptions: a
/// pick that ended with no folder — dismissed picker or declined read — is
/// <see cref="FolderPickOutcome.Cancelled"/>, a write denial is
/// <see cref="FolderWriteCapability.PermissionDenied"/>, a missing named file
/// is a <c>null</c> read, a named file the browser won't write is
/// <see cref="PickedFileWritability.NotWritable"/>. Unexpected browser
/// failures surface as <see cref="Microsoft.JSInterop.JSException"/> for
/// callers to catch and degrade on. Two exceptions are deliberate and kept
/// apart: a drawn file over the byte cap is a
/// <see cref="PickedFileTooLargeException"/>, a fact about the folder for a
/// host to report; a draw with no folder held is an
/// <see cref="InvalidOperationException"/>, a caller error.
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
    /// permission request, then a draw of the folder's top-level matching files
    /// (the kinds the configured <see cref="FolderPickLimits.MaxFileCounts"/>
    /// names, each up to its cap) and full buffering of what it drew. A
    /// non-cancelled pick makes the folder the held one (see the type remarks).
    /// Call only when <see cref="SupportsDirectoryPickerAsync"/> is true.
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
    /// <exception cref="PickedFileTooLargeException">
    /// A drawn file is larger than the configured
    /// <see cref="FolderPickLimits.MaxFileBytes"/> — by its enumerated size,
    /// before any byte moves, or by the length its bytes arrive at, if it grew
    /// after the draw. A folder past the <i>count</i> caps does not throw — it
    /// truncates per kind and reports it (<see cref="FolderSample.Kinds"/>).
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="ClearPickedAsync"/> released the folder while
    /// <paramref name="onPickAccepted"/> was awaited, leaving nothing to draw —
    /// a caller error, never a property of the folder.
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
    /// Collect the fallback pick from the hidden input's FileList: make its
    /// folder the held one, retaining its top-level files (by
    /// <c>webkitRelativePath</c> depth — the browser hands over the whole tree)
    /// as the candidates, then draw and buffer them exactly as the other
    /// mechanism does — same module, same table, same draw, same per-kind
    /// report. The outcome's capability is always
    /// <see cref="FolderWriteCapability.BrowserUnsupported"/>: this mechanism
    /// yields no writable handle. An empty FileList is a non-cancelled outcome
    /// with zero files.
    /// </summary>
    /// <exception cref="PickedFileTooLargeException">
    /// A drawn file is larger than the configured
    /// <see cref="FolderPickLimits.MaxFileBytes"/>, by its size or by the length
    /// its bytes arrive at, as for <see cref="PickFolderAsync"/> (the count caps
    /// truncate rather than throw).
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="ClearPickedAsync"/> released the folder between the collection
    /// and the draw — a caller error.
    /// </exception>
    Task<FolderPickOutcome> CollectFallbackAsync(ElementReference fallbackInput);

    /// <summary>
    /// Draw the held folder again at the configured
    /// <see cref="FolderPickLimits"/>, without the picker and without any
    /// permission prompt or permission request, and return the new sample: its
    /// files fully buffered and its per-kind report
    /// (<see cref="FolderSample.Kinds"/>). The draw, the byte cap and the
    /// buffering are the pick's own, implemented once: a kind over its cap is a
    /// fresh uniform random draw, a kind within it is admitted whole again, and
    /// the files read in folder order.
    ///
    /// <para>
    /// <b>What is drawn.</b> After a File System Access pick, the held directory
    /// is walked afresh, so files added or removed since the pick are seen.
    /// After a fallback collection, the retained candidates are redrawn — the
    /// folder as the browser handed it over, with nothing added since; a
    /// retained file changed or removed on disk since may fail to read, which
    /// fails the resample as any read does.
    /// </para>
    ///
    /// <para>
    /// <b>What is held stays held</b>, whether the resample succeeds or fails:
    /// the held folder (see the type remarks), the fallback's candidates, the
    /// picked slot's named-file reads, writes and probe, and the active slot.
    /// The write capability is not asked again, and the sample makes no claim
    /// about it — the pick's <see cref="FolderPickOutcome.Capability"/> is the
    /// only statement of it. A failure throws and returns no part of a sample;
    /// nothing is left for the caller to repair, and a later resample can
    /// succeed.
    /// </para>
    ///
    /// <para>
    /// <b>No busy hook.</b> A resample shows no prompt, so all of it is the
    /// app's own work: a host raises its busy affordance, and lets it paint,
    /// <i>before</i> calling — unlike <see cref="PickFolderAsync"/>, whose
    /// <c>onPickAccepted</c> exists because the prompts come first.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No folder is held — never picked, or released by
    /// <see cref="ClearPickedAsync"/> (see the type remarks). A caller error:
    /// offer a resample only while a folder is held.
    /// </exception>
    /// <exception cref="PickedFileTooLargeException">
    /// As for a pick, a drawn file is larger than the configured
    /// <see cref="FolderPickLimits.MaxFileBytes"/> — by its size, before any
    /// byte moves, or by the length its bytes arrive at, if it grew since the
    /// draw. Not an <see cref="InvalidOperationException"/>, so a host handling
    /// it does not also catch the caller error above.
    /// </exception>
    /// <exception cref="Microsoft.JSInterop.JSException">
    /// The walk was refused — the folder's permission withdrawn, or the folder
    /// gone; a drawn file's bytes failed to read, part-way through buffering
    /// included; or the browser failed in any other way.
    /// </exception>
    Task<FolderSample> ResampleAsync();

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
    /// Ask whether the named file in the <i>picked</i> slot's folder can be
    /// written — at setup time, beside <see cref="ReadPickedFileAsync"/>, so a
    /// host learns at the pick what it would otherwise learn at its first real
    /// write. The probe opens the file for writing and aborts the stream: no
    /// byte changes, nothing is created, and it never shows a permission
    /// prompt, so it is safe to call without checking the pick's
    /// <see cref="FolderWriteCapability"/> first (no write grant answers
    /// <see cref="PickedFileWritability.NotWritable"/>).
    ///
    /// <para>
    /// A file another program holds open is <b>not</b> caught — it answers
    /// <see cref="PickedFileWritability.Writable"/>; see
    /// <see cref="PickedFileWritability"/> for the measured limit.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="fileName"/> is null.</exception>
    /// <exception cref="Microsoft.JSInterop.JSException">
    /// No File System Access folder is picked (fallback pick, cleared slot, or
    /// never picked), as for <see cref="ReadPickedFileAsync"/>; or the browser
    /// failed the probe in a way that is not one of the expected answers.
    /// </exception>
    Task<PickedFileWritability> ProbePickedFileWritabilityAsync(string fileName);

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
