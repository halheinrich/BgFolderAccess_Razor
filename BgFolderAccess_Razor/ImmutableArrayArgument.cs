using System.Collections.Immutable;

namespace BgFolderAccess_Razor;

/// <summary>
/// The one refusal of a <c>default</c> <see cref="ImmutableArray{T}"/> handed
/// to a public record member (halheinrich/backgammon#273).
///
/// <para>
/// <b>Why it exists.</b> <see cref="PickedFile.Bytes"/>,
/// <see cref="FolderPickOutcome.Files"/> and
/// <see cref="FolderPickOutcome.Truncations"/> were non-nullable reference
/// types, so under this repo's nullable warnings-as-errors a <c>null</c> for
/// them failed the build. As <see cref="ImmutableArray{T}"/>s they are structs,
/// which nullable analysis does not see, and <c>default</c> compiles
/// silently. It is no domain state either: "no files" and "no bytes" are
/// <see cref="ImmutableArray{T}.Empty"/>. A default array holds no array at
/// all and fails wherever it is first used, far from the code that built it.
/// Refusing it at construction restores the invariant the reference types
/// carried.
/// </para>
/// </summary>
internal static class ImmutableArrayArgument
{
    /// <summary>
    /// <paramref name="value"/> itself, unless it is a default array.
    /// </summary>
    /// <param name="value">The array being stored.</param>
    /// <param name="memberName">The record member it is stored in, named by the exception.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> is a default array.</exception>
    public static ImmutableArray<T> RequireNotDefault<T>(ImmutableArray<T> value, string memberName) =>
        value.IsDefault
            ? throw new ArgumentException(
                $"{memberName} must not be a default ImmutableArray<{typeof(T).Name}>, which holds no array "
                + "at all; pass an empty array ([]) for none.",
                memberName)
            : value;
}
