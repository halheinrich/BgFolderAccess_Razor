using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace BgFolderAccess_Razor;

/// <summary>
/// One picked matching file, fully buffered into memory. The bytes are read
/// out of the browser once, at pick time, and never leave it — this is the
/// in-memory payload the host processes locally.
///
/// <para>
/// <b>Immutable, bytes included</b> (halheinrich/backgammon#273). The contents
/// are an <see cref="ImmutableArray{T}"/>, not a <c>byte[]</c>: one picked
/// file is shared by everything that holds the outcome, and a writable array
/// would let any one holder rewrite what every other one reads. Every writing
/// member of every interface the array implements throws
/// <see cref="NotSupportedException"/>, and <see cref="OpenRead"/> gives the
/// stream-shaped read without handing the buffer out.
/// </para>
/// </summary>
/// <param name="FileName">
/// The original file name <i>including</i> its extension. The extension is a
/// stated contract of this library, not an accident of the browser API: the
/// name is the only place a file's kind survives buffering, so a host may
/// discriminate format from it (BgQuiz's decision-id stamping does exactly
/// that).
/// </param>
/// <param name="Bytes">
/// The complete file contents. A host that re-enumerates (restart flows) calls
/// <see cref="OpenRead"/> once per pass so the source stays re-iterable.
/// </param>
public sealed record PickedFile(string FileName, ImmutableArray<byte> Bytes)
{
    /// <summary>
    /// A fresh stream over <see cref="Bytes"/>, positioned at the start: each
    /// call is independent, so one pass reading to the end leaves the next
    /// pass's stream untouched. No copy is made. The stream is a
    /// <see cref="MemoryStream"/> built with <c>writable: false</c> and no
    /// public buffer, so it cannot write, and <c>GetBuffer</c> refuses and
    /// <c>TryGetBuffer</c> declines. A cast to <see cref="MemoryStream"/> gets
    /// no closer to the array than <see cref="Bytes"/> does.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Bytes"/> is a <c>default</c> <see cref="ImmutableArray{T}"/>,
    /// which holds no array at all (not an empty one). The library never
    /// constructs one; only a host passing <c>default</c> can.
    /// </exception>
    public Stream OpenRead() =>
        new MemoryStream(
            ImmutableCollectionsMarshal.AsArray(Bytes)
                ?? throw new InvalidOperationException(
                    $"'{FileName}' holds a default ImmutableArray<byte>, which has no contents to read."),
            writable: false);
}
