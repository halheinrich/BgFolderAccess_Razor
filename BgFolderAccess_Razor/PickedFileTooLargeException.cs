namespace BgFolderAccess_Razor;

/// <summary>
/// A file a draw took is larger than the host's per-file byte cap
/// (<see cref="FolderPickLimits.MaxFileBytes"/>), so the pick or resample that
/// drew it failed whole: no sample is returned, and nothing held changes (see
/// <see cref="IFolderAccess"/>'s remarks on the held folder).
///
/// <para>
/// <b>One exception for one condition, at both places the cap is enforced.</b>
/// <see cref="JsFolderAccess"/> checks every drawn file's size before any byte
/// moves, and checks each file again by the length its bytes are handed over
/// at, which is larger only if the file grew after the draw. Either way the
/// condition is the same and so is the exception; <see cref="FileSize"/> is
/// the size the failing check saw.
/// </para>
///
/// <para>
/// <b>Not an <see cref="InvalidOperationException"/>, deliberately.</b> That
/// type is what <see cref="IFolderContext.ResampleAsync"/> (and a pick whose
/// folder was released part-way) throws for "no folder is held" — a caller
/// bug. A file over the cap is a property of the folder the user chose,
/// something a host reports, so a host catching this one must not catch its
/// own bug with it. It derives from <see cref="Exception"/> directly: it is
/// not an I/O failure either, so no narrower base would be honest.
/// </para>
///
/// <para>
/// <b>Only the one constructor.</b> The .NET exception guidelines' parameterless,
/// message-only and inner-exception constructors are omitted on purpose: each
/// would make an exception that claims a cap was exceeded without saying which
/// file or which cap. The constructor is public so a host's test fake of
/// <see cref="IFolderAccess"/> can throw the real thing, and it refuses a
/// size the cap does not exceed.
/// </para>
/// </summary>
public sealed class PickedFileTooLargeException : Exception
{
    /// <summary>
    /// Report <paramref name="fileName"/>, seen at <paramref name="fileSize"/>
    /// bytes, as over the byte cap of <paramref name="limits"/>.
    /// </summary>
    /// <param name="fileName">The file's name, extension included, as the draw returned it.</param>
    /// <param name="fileSize">The size in bytes the failing check saw.</param>
    /// <param name="limits">
    /// The limits the draw enforced. The cap figures — <see cref="MaxFileBytes"/>,
    /// and the whole mebibytes the message states — are taken from it, never
    /// restated, so the exception says exactly what was enforced.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="fileName"/> or <paramref name="limits"/> is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="fileSize"/> does not exceed
    /// <see cref="FolderPickLimits.MaxFileBytes"/>.
    /// </exception>
    public PickedFileTooLargeException(string fileName, long fileSize, FolderPickLimits limits)
        : base(MessageFor(fileName, fileSize, limits))
    {
        FileName = fileName;
        FileSize = fileSize;
        MaxFileBytes = limits.MaxFileBytes;
    }

    /// <summary>The file's name, extension included, as the draw returned it.</summary>
    public string FileName { get; }

    /// <summary>
    /// The file's size in bytes as the failing check saw it: its enumerated
    /// size, or the length its bytes were handed over at if it grew after the
    /// draw. Always greater than <see cref="MaxFileBytes"/>.
    /// </summary>
    public long FileSize { get; }

    /// <summary>The per-file byte cap that was enforced (<see cref="FolderPickLimits.MaxFileBytes"/>).</summary>
    public long MaxFileBytes { get; }

    // Validates before the base constructor runs, so no instance exists for a
    // file the cap does not exceed. The wording is the one the byte cap's
    // InvalidOperationException carried, so a host showing the message shows
    // what it showed before.
    private static string MessageFor(string fileName, long fileSize, FolderPickLimits limits)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fileSize, limits.MaxFileBytes);
        return $"'{fileName}' is larger than the {limits.MaxFileMegabytes} MB per-file limit.";
    }
}
