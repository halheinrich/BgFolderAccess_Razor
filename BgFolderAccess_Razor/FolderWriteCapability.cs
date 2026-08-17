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
    /// The browser supports the File System Access API but no write grant came
    /// back — the picked files remain readable, nothing is written.
    ///
    /// <para>
    /// <b>It does not say why, and callers must not claim one.</b> Two causes
    /// land here and are deliberately not distinguished: the user answered no
    /// to the write request, <i>or</i> the browser refused to ask at all
    /// (<c>requestPermission</c> requires transient user activation and throws
    /// a <c>SecurityError</c> without it — the standing outcome on Chrome for
    /// Android, where the picker leaves no live activation behind; see
    /// <c>folderAccess.js</c>'s <c>beginPick</c> and
    /// <c>halheinrich/backgammon#109</c>). On that second path no write prompt
    /// is ever shown, so wording that says the user declined attributes a
    /// decision they never made. Both causes mean the same thing to a host —
    /// no write grant, folder still readable — which is why they share a rung.
    /// </para>
    /// </summary>
    PermissionDenied,
}
