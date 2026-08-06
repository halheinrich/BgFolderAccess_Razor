namespace BgFolderAccess_Razor;

/// <summary>
/// One picked matching file, fully buffered into memory. The bytes are read
/// out of the browser once, at pick time, and never leave it — this is the
/// in-memory payload the host processes locally.
/// </summary>
/// <param name="FileName">
/// The original file name <i>including</i> its extension. The extension is a
/// stated contract of this library, not an accident of the browser API: the
/// name is the only place a file's kind survives buffering, so a host may
/// discriminate format from it (BgQuiz's decision-id stamping does exactly
/// that).
/// </param>
/// <param name="Bytes">
/// The complete file contents. A host that re-enumerates (restart flows) mints
/// a fresh <c>MemoryStream(Bytes)</c> per pass so the source stays
/// re-iterable.
/// </param>
public sealed record PickedFile(string FileName, byte[] Bytes);
