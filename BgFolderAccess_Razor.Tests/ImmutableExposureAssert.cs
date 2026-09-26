using System.Collections;
using System.Collections.Immutable;

namespace BgFolderAccess_Razor.Tests;

/// <summary>
/// The one definition of "nothing a caller can write back into"
/// (halheinrich/backgammon#273), shared by every public member that hands out
/// a collection: <see cref="FolderPickLimits.MaxFileCounts"/>,
/// <see cref="PickedFile.Bytes"/>, and <see cref="FolderPickOutcome"/>'s
/// <c>Files</c> and <c>Truncations</c>. Each is an
/// <see cref="ImmutableArray{T}"/>, and these assertions pin what that buys.
///
/// <para>
/// The exposed value is taken as <see cref="object"/> — boxed exactly as a
/// caller's cast would box it — so a member that regressed to some other
/// shape still compiles here and fails on the assertion, rather than failing
/// the build and hiding which promise broke.
/// </para>
/// </summary>
internal static class ImmutableExposureAssert
{
    /// <summary>
    /// The one interface an <see cref="ImmutableArray{T}"/> implements outside
    /// <see cref="CoveredInterfaces{T}"/>: internal to
    /// System.Collections.Immutable, so no code outside that assembly can
    /// name it, and a cast to it cannot be written.
    /// </summary>
    private const string FrameworkInternalInterface = "System.Collections.Immutable.IImmutableArray";

    /// <summary>
    /// Every public interface <see cref="AssertEveryWriteRefused{T}"/> covers.
    /// Pinned by exact set in <see cref="AssertImplementsOnlyCoveredInterfaces{T}"/>:
    /// a future shape that implements something new fails there until its
    /// writing members are attempted too, so "any interface" cannot silently
    /// shrink to "the ones someone remembered".
    /// </summary>
    private static Type[] CoveredInterfaces<T>() =>
    [
        typeof(IList<T>),
        typeof(ICollection<T>),
        typeof(IEnumerable<T>),
        typeof(IReadOnlyList<T>),
        typeof(IReadOnlyCollection<T>),
        typeof(IImmutableList<T>),
        typeof(IEquatable<ImmutableArray<T>>),
        typeof(IList),
        typeof(ICollection),
        typeof(IEnumerable),
        typeof(IStructuralComparable),
        typeof(IStructuralEquatable),
    ];

    /// <summary>
    /// <paramref name="exposed"/> implements exactly the interfaces
    /// <see cref="AssertEveryWriteRefused{T}"/> tries, plus the one
    /// framework-internal interface no caller can cast to.
    /// </summary>
    public static void AssertImplementsOnlyCoveredInterfaces<T>(object exposed)
    {
        var implemented = exposed.GetType().GetInterfaces();

        var frameworkInternal = Assert.Single(implemented, t => !t.IsPublic);
        Assert.Equal(FrameworkInternalInterface, frameworkInternal.FullName);
        Assert.Equal(
            CoveredInterfaces<T>().Select(t => t.FullName).Order(),
            implemented.Where(t => t.IsPublic).Select(t => t.FullName).Order());
    }

    /// <summary>
    /// Every writing member of every interface <paramref name="exposed"/>
    /// implements is tried with <paramref name="intruder"/>, and each refuses.
    /// The caller then reads its member back and asserts it unchanged — the
    /// half that proves the refusals left nothing behind.
    /// </summary>
    /// <param name="exposed">The member's value, non-empty (the index writers need an index).</param>
    /// <param name="intruder">A value that must not end up in the collection.</param>
    public static void AssertEveryWriteRefused<T>(object exposed, T intruder)
    {
        var generic = Assert.IsAssignableFrom<IList<T>>(exposed);
        Assert.NotEmpty(generic);
        Assert.True(generic.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => generic[0] = intruder);
        Assert.Throws<NotSupportedException>(() => generic.Add(intruder));
        Assert.Throws<NotSupportedException>(() => generic.Insert(0, intruder));
        Assert.Throws<NotSupportedException>(() => generic.Remove(generic[0]));
        Assert.Throws<NotSupportedException>(() => generic.RemoveAt(0));
        Assert.Throws<NotSupportedException>(generic.Clear);

        var nonGeneric = Assert.IsAssignableFrom<IList>(exposed);
        Assert.True(nonGeneric.IsReadOnly);
        Assert.True(nonGeneric.IsFixedSize);
        Assert.Throws<NotSupportedException>(() => nonGeneric[0] = intruder);
        Assert.Throws<NotSupportedException>(() => nonGeneric.Add(intruder));
        Assert.Throws<NotSupportedException>(() => nonGeneric.Insert(0, intruder));
        Assert.Throws<NotSupportedException>(() => nonGeneric.Remove(nonGeneric[0]));
        Assert.Throws<NotSupportedException>(() => nonGeneric.RemoveAt(0));
        Assert.Throws<NotSupportedException>(nonGeneric.Clear);

        // ICollection's one writer copies out; it cannot write in.
        var copy = new T[generic.Count];
        Assert.IsAssignableFrom<ICollection>(exposed).CopyTo(copy, 0);
        copy[0] = intruder;

        // IImmutableList's "writers" answer a new list and leave this one be —
        // the caller's read-back is what proves "leave this one be".
        var immutable = Assert.IsAssignableFrom<IImmutableList<T>>(exposed);
        Assert.Equal(intruder, immutable.SetItem(0, intruder)[0]);
        _ = immutable.Add(intruder);
        _ = immutable.Insert(0, intruder);
        _ = immutable.RemoveAt(0);
        _ = immutable.Clear();

        // The remaining interfaces (IEnumerable, IReadOnlyList and their
        // bases, IEquatable, IStructuralComparable, IStructuralEquatable)
        // declare no writing member at all — a write cannot be expressed.
    }
}
