namespace BgFolderAccess_Razor;

/// <summary>
/// Whether one named file in the <i>picked</i> slot's folder can be written,
/// as answered by <see cref="IFolderAccess.ProbePickedFileWritabilityAsync"/>
/// at setup time, before the host's first real write would otherwise
/// discover it. Where <see cref="FolderWriteCapability"/> is about the folder
/// (was a write grant given?), this is about one file in it (will the browser
/// open it for writing?).
///
/// <para>
/// <b>The answer is the browser's, and it has a limit.</b> The probe catches a
/// file the browser refuses to open for writing — the read-only attribute, or
/// no write permission. It does <i>not</i> catch a file another program holds
/// open: the browser writes to a swap file and only touches the original when
/// the stream closes, so a locked file answers <see cref="Writable"/> and is
/// still discovered at the first real write (measured in Chrome 153,
/// halheinrich/backgammon#261). A host keeps its write-failure handling for
/// that reason.
/// </para>
/// </summary>
public enum PickedFileWritability
{
    /// <summary>
    /// The file does not exist in the picked folder. Nothing was probed and
    /// nothing was created. <b>Not a claim that the file could be
    /// created</b> — for that the caller has only the folder's
    /// <see cref="FolderWriteCapability"/> to go on.
    /// </summary>
    Absent,

    /// <summary>
    /// The browser opened the file for writing; the probe then aborted the
    /// stream, so the file is byte-identical to before.
    /// </summary>
    Writable,

    /// <summary>
    /// The browser will not write the file: its read-only attribute is set, or
    /// the picked folder has no write grant. The two causes are deliberately
    /// not distinguished.
    /// </summary>
    NotWritable,
}
