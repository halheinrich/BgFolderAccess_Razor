namespace BgFolderAccess_Razor;

using Microsoft.AspNetCore.Components;

/// <summary>
/// The host's one gateway to the browser's folder picking, both mechanisms, backed by the
/// <c>folderAccess.js</c> module. A pick that lands a folder answers it as an
/// <see cref="IFolderContext"/>, through which the host reads, writes, resamples and finally
/// releases that folder; this interface itself holds nothing between calls. Only
/// <see cref="JsFolderAccess"/> touches JS interop, and the browser's directory handles never
/// cross the boundary.
///
/// <para>
/// Expected outcomes are values, never exceptions: a pick that ended with no folder is
/// <see cref="FolderPickOutcome.Cancelled"/>, a write denial is
/// <see cref="FolderWriteCapability.PermissionDenied"/>. A drawn file over the byte cap is a
/// <see cref="PickedFileTooLargeException"/>, a fact about the folder for the host to report;
/// unexpected browser failures surface as <see cref="Microsoft.JSInterop.JSException"/>.
/// </para>
/// </summary>
public interface IFolderAccess
{
    /// <summary>
    /// True when the browser offers <c>showDirectoryPicker</c>, the File System Access path.
    /// Probed per gesture: capability is a property of the moment, not of app boot.
    /// </summary>
    ValueTask<bool> SupportsDirectoryPickerAsync();

    /// <summary>
    /// Run the File System Access pick: the native directory picker, a readwrite permission
    /// request, then a draw of the folder's top-level matching files (each kind of
    /// <see cref="FolderPickLimits.MaxFileCounts"/> up to its cap) and full buffering of what
    /// it drew. A pick that fails after its prompts releases the folder it had landed and
    /// throws; the host holds nothing. Call only when <see cref="SupportsDirectoryPickerAsync"/>
    /// is true.
    /// </summary>
    /// <param name="onPickAccepted">
    /// Awaited once the browser has finished asking the user and before the draw begins: the
    /// one truthful place to raise a busy affordance, since earlier it would cover a modal
    /// prompt and later it could not paint before the scan. Not invoked for a cancelled pick.
    /// </param>
    /// <exception cref="PickedFileTooLargeException">A drawn file is over <see cref="FolderPickLimits.MaxFileBytes"/>; the count caps truncate and report instead.</exception>
    /// <exception cref="Microsoft.JSInterop.JSException">The browser failed the prompts, the walk or a read.</exception>
    Task<FolderPickOutcome> PickFolderAsync(Func<Task> onPickAccepted);

    /// <summary>
    /// Open the hidden <c>webkitdirectory</c> input's native picker, the fallback gesture for
    /// browsers without File System Access. The pick arrives through the input's own
    /// <c>change</c> event, handled with <see cref="CollectFallbackAsync"/>; a dismissal fires nothing.
    /// </summary>
    Task TriggerFallbackPickerAsync(ElementReference fallbackInput);

    /// <summary>
    /// Collect the fallback pick from the hidden input's FileList: the folder's top-level
    /// files become the landed folder's candidates, drawn and buffered exactly as the other
    /// mechanism's are. The folder's capability is always
    /// <see cref="FolderWriteCapability.BrowserUnsupported"/>, since this mechanism yields no
    /// writable handle. An empty FileList lands a folder with zero files, not a cancellation.
    /// A collection that fails after landing releases its folder and throws.
    /// </summary>
    /// <exception cref="PickedFileTooLargeException">A drawn file is over <see cref="FolderPickLimits.MaxFileBytes"/>.</exception>
    /// <exception cref="Microsoft.JSInterop.JSException">The browser failed a read.</exception>
    Task<FolderPickOutcome> CollectFallbackAsync(ElementReference fallbackInput);
}
