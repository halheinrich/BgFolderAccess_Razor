using System.Collections.Immutable;

namespace BgFolderAccess_Razor;

/// <summary>
/// One draw of a folder's top-level matching files at the host's
/// <see cref="FolderPickLimits"/>: the files it admitted, fully buffered, and
/// its per-kind account of what it found and admitted. A pick hands one back
/// inside its <see cref="FolderPickOutcome"/>; <see cref="IFolderContext.ResampleAsync"/>
/// hands one back alone, drawn from the same held folder.
///
/// <para>
/// <b>Only the sample, nothing of the setup.</b> The folder's name and whether
/// it can be written into belong to the pick that bound the folder
/// (<see cref="IFolderContext.Name"/>,
/// <see cref="IFolderContext.Capability"/>), not to a draw of it, so they
/// are not here. A sample makes no claim about either, which is what lets a
/// resample, which asks the browser nothing, return one.
/// </para>
///
/// <para>
/// <b>An over-limit kind's files are a random draw.</b> Each count cap admits
/// a uniformly random subset of its kind, so two draws of one over-limit
/// folder can hold different files. That is the point: repeated draws reach a
/// corpus larger than the caps admit, where taking a fixed prefix would put
/// the same excess permanently out of reach. <see cref="Files"/> still reads
/// in folder-enumeration order, and a kind within its cap is admitted whole.
/// </para>
///
/// <para>
/// <b>Immutable all the way down</b> (halheinrich/backgammon#273).
/// <see cref="Files"/> and <see cref="Kinds"/> are
/// <see cref="ImmutableArray{T}"/>s, so the type itself guarantees what a
/// read-only interface only suggested: every writing member of every interface
/// the arrays implement throws <see cref="NotSupportedException"/>. Neither
/// member accepts a <c>default</c> array, through the constructor or
/// <c>with</c>. The record compares its arrays by reference, so a test
/// compares its members.
/// </para>
/// </summary>
/// <param name="Files">The admitted files, buffered, in folder-enumeration order.</param>
/// <param name="Kinds">
/// One <see cref="SampledKind"/> per kind of the caps table, in the table's
/// order, each with its found and admitted counts and its cap.
/// </param>
public sealed record FolderSample(ImmutableArray<PickedFile> Files, ImmutableArray<SampledKind> Kinds)
{
    /// <summary>
    /// The admitted files, buffered, in folder-enumeration order (see
    /// <see cref="PickedFile.FileName"/> for the extension-bearing names). Never
    /// a <c>default</c> array: the constructor and <c>with</c> both refuse one
    /// with an <see cref="ArgumentException"/> naming this member.
    /// </summary>
    public ImmutableArray<PickedFile> Files
    {
        get;
        init => field = ImmutableArrayArgument.RequireNotDefault(value, nameof(Files));
    } = ImmutableArrayArgument.RequireNotDefault(Files, nameof(Files));

    /// <summary>
    /// The draw's per-kind report: one <see cref="SampledKind"/> for every kind
    /// of the caps table, in the table's order, a kind the folder held none of
    /// included. It is the library's one account of what the draw found and
    /// admitted; nothing else reports either count. Never a <c>default</c>
    /// array: the constructor and <c>with</c> both refuse one with an
    /// <see cref="ArgumentException"/> naming this member.
    /// </summary>
    public ImmutableArray<SampledKind> Kinds
    {
        get;
        init => field = ImmutableArrayArgument.RequireNotDefault(value, nameof(Kinds));
    } = ImmutableArrayArgument.RequireNotDefault(Kinds, nameof(Kinds));

    /// <summary>
    /// True when the draw admitted every matching file it found: no kind was
    /// over its cap. Such a sample is the only one the limits could draw from
    /// what the walk found, so another draw at the same limits could only hold
    /// the same files; a host decides from this alone whether a fresh draw is
    /// worth offering, without classifying <see cref="Files"/> by name itself.
    /// Derived from <see cref="Kinds"/>, never stored.
    /// </summary>
    public bool HoldsEveryMatchingFile => Kinds.All(kind => kind.OmittedCount == 0);
}
