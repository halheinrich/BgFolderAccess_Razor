namespace BgFolderAccess_Razor;

/// <summary>
/// Whether the host can write files into the folder the user picked,
/// determined once at pick time and carried on
/// <see cref="FolderPickOutcome.Capability"/>. Only <see cref="Enabled"/>
/// permits writing; the other two are the read-only degrade reasons a host's
/// pick-time status notice distinguishes.
/// </summary>
public enum FolderWriteCapability
{
    /// <summary>
    /// The folder was picked via the File System Access API and the user
    /// granted readwrite permission — the host can write files beside the
    /// picked content.
    /// </summary>
    Enabled,

    /// <summary>
    /// The browser has no <c>showDirectoryPicker</c> (the
    /// <c>webkitdirectory</c> fallback was used) — files are readable but no
    /// writable handle exists, so the host runs read-only.
    /// </summary>
    BrowserUnsupported,

    /// <summary>
    /// The browser supports the File System Access API but the user declined
    /// write permission — the picked files remain readable, nothing is
    /// written.
    /// </summary>
    PermissionDenied,
}
