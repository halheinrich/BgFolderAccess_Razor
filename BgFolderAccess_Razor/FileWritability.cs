namespace BgFolderAccess_Razor;

/// <summary>
/// Whether one named file in a picked folder can be written, as
/// <see cref="IFolderContext.ProbeFileWritabilityAsync"/> answers it. Where
/// <see cref="FolderWriteCapability"/> is about the folder, this is about one file in it.
/// The probe catches a file the browser refuses to open for writing (the read-only
/// attribute, no write grant), not a file another program holds open: the browser writes
/// to a swap file and touches the original only when the stream closes, so that file
/// answers <see cref="Writable"/> and fails at the first real write (measured in Chrome 153,
/// halheinrich/backgammon#261).
/// </summary>
public enum FileWritability
{
    /// <summary>The file does not exist; nothing was probed or created. Not a claim that it could be created.</summary>
    Absent,

    /// <summary>The browser opened the file for writing; the probe aborted the stream, so the file is unchanged.</summary>
    Writable,

    /// <summary>The browser will not write the file: its read-only attribute is set, or the folder has no write grant. The two are not distinguished.</summary>
    NotWritable,
}
