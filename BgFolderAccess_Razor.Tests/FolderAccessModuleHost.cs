using System.Text.Json;
using Jint;
using Jint.Native;

namespace BgFolderAccess_Razor.Tests;

/// <summary>
/// Runs the RCL's shipped <c>folderAccess.js</c> — the real file, staged beside this
/// assembly by the csproj — inside an in-process JS engine, against one fake folder, and
/// hands its results back as typed values.
///
/// <para>
/// The module's count-cap draw is the one piece of genuine algorithm in the library, and it
/// lives in JavaScript, as does the context table a resample and a release act on.
/// Everything else in this suite pins C#, with bUnit scripting the module's replies, which
/// can only prove that <see cref="JsFolderAccess"/> maps what it is told. Jint is an engine,
/// not a browser: these tests prove the module's logic, nothing about the real wire
/// (pickers, prompts, DOMException mapping, File/ArrayBuffer transfer), which is the
/// consuming host's e2e suite's.
/// </para>
///
/// <para>
/// One instance per test: the module keeps its contexts in module state and the harness its
/// fake folder. Each pick lands a context; the harness remembers the latest
/// (<see cref="Context"/>) and the <c>…Of</c> members name one explicitly, so a test can hold
/// two. A rejected or thrown module call surfaces as Jint's own exception
/// (<see cref="Jint.JintException"/>). Public only because xUnit theories are:
/// <see cref="Mechanism"/> is a parameter of public <c>[Theory]</c> methods.
/// </para>
/// </summary>
public sealed class FolderAccessModuleHost
{
    // Module specifiers: moduleHarness.js does `import ... from 'folderAccess'`.
    private const string ModuleSpecifier = "folderAccess";
    private const string HarnessSpecifier = "moduleHarness";

    private static readonly string ModuleSource = ReadStagedJs("folderAccess.js");
    private static readonly string HarnessSource = ReadStagedJs("moduleHarness.js");

    private static readonly JsonSerializerOptions ResultJson =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private readonly JsValue _harness;

    public FolderAccessModuleHost()
    {
        var engine = new Engine();
        engine.Modules.Add(ModuleSpecifier, ModuleSource);
        engine.Modules.Add(HarnessSpecifier, HarnessSource);
        _harness = engine.Modules.Import(HarnessSpecifier);
    }

    /// <summary>
    /// Which pick mechanism a case drives. The count caps are one rule with one
    /// implementation, so every invariant is asserted against both.
    /// </summary>
    public enum Mechanism
    {
        /// <summary>File System Access: <c>beginPick</c> then <c>draw</c>.</summary>
        FileSystemAccess,

        /// <summary>The <c>webkitdirectory</c> fallback: <c>collectFallbackFiles</c> then <c>draw</c>.</summary>
        Fallback,
    }

    /// <summary>One draw's outcome as the module produced it — a pick's or a resample's.</summary>
    /// <param name="Files">The admitted files' names, in the order the module returned them.</param>
    /// <param name="Kinds">The module's per-kind report: one entry per kind of the caps table, in the table's order.</param>
    /// <param name="Stats">How many times the draw called <c>getFile()</c>; <c>null</c> on <see cref="Mechanism.Fallback"/>, whose Files carry their size.</param>
    public sealed record DrawResult(IReadOnlyList<string> Files, IReadOnlyList<DrawnKind> Kinds, int? Stats);

    /// <summary>One kind's found and admitted counts, as they cross the real interop wire. Value-equal.</summary>
    public sealed record DrawnKind(string Extension, int FoundCount, int AdmittedCount);

    /// <summary>The browser calls only a pick may make, counted over the host's life.</summary>
    public sealed record BrowserCalls(int PickerOpenings, int PermissionRequests);

    /// <summary>
    /// Pick a folder whose top-level files are <paramref name="fileNames"/>, in that order,
    /// against <paramref name="caps"/> — the per-extension count-cap table, in its key
    /// order. The pick replaces the fake folder's contents and lands a new context, which
    /// becomes <see cref="Context"/>.
    /// </summary>
    public DrawResult Pick(
        Mechanism mechanism, string[] fileNames, params (string Extension, int Cap)[] caps) =>
        ReadDraw(Call(
            "pickJson",
            mechanism switch
            {
                Mechanism.FileSystemAccess => nameof(Mechanism.FileSystemAccess),
                Mechanism.Fallback => nameof(Mechanism.Fallback),
                _ => throw new ArgumentOutOfRangeException(nameof(mechanism)),
            },
            JsonSerializer.Serialize(fileNames),
            WireCaps(caps)));

    /// <summary>A File System Access pick the user cancels: the picker rejects with <c>AbortError</c>. Answers the module's status.</summary>
    public string CancelPick() => Call("cancelPick").AsString();

    /// <summary>The context the latest pick landed.</summary>
    public int Context => (int)Call("contextId").AsNumber();

    /// <summary>Draw the latest pick's folder again — a resample's module call — with the caps table crossing again.</summary>
    public DrawResult Resample(params (string Extension, int Cap)[] caps) => ResampleOf(Context, caps);

    /// <summary>Draw the folder held under <paramref name="context"/> again.</summary>
    public DrawResult ResampleOf(int context, params (string Extension, int Cap)[] caps) =>
        ReadDraw(Call("drawOfJson", context, WireCaps(caps)));

    /// <summary>
    /// A whole resample of the latest pick's folder as <see cref="JsFolderContext"/> runs it:
    /// the draw, then each drawn file's bytes in turn, stopping at the first read that fails.
    /// Answers <c>"name:byteLength"</c> per file, in order.
    /// </summary>
    public IReadOnlyList<string> ResampleAndBuffer(params (string Extension, int Cap)[] caps) =>
        JsonSerializer.Deserialize<string[]>(Call("bufferJson", WireCaps(caps)).AsString(), ResultJson)
            ?? throw new InvalidOperationException("'bufferJson' returned no result.");

    /// <summary>Add a file to the folder on disk, after the pick.</summary>
    public void AddFile(string name) => Call("addFile", name);

    /// <summary>Make every walk of the folder reject (or stop doing so), as a withdrawn permission does.</summary>
    public void RefuseWalk(bool refuses) => Call("refuseWalk", refuses);

    /// <summary>Make the named file's bytes fail to read (or, with <c>null</c>, none).</summary>
    public void FailReadOf(string? name) => Call("failReadOf", name is null ? JsValue.Null : name);

    /// <summary>How often the picker was opened and permission requested so far.</summary>
    public BrowserCalls BrowserCallCounts() =>
        JsonSerializer.Deserialize<BrowserCalls>(Call("browserCallsJson").AsString(), ResultJson)
            ?? throw new InvalidOperationException("'browserCallsJson' returned no result.");

    /// <summary>The module's <c>readFileData</c> on the latest pick's folder: the byte count read.</summary>
    public int ReadFileData(string name) => (int)Call("readBytes", name).AsNumber();

    /// <summary>The module's <c>release</c> of the latest pick's context.</summary>
    public void Release() => ReleaseOf(Context);

    /// <summary>The module's <c>release</c> of <paramref name="context"/>.</summary>
    public void ReleaseOf(int context) => Call("releaseOf", context);

    /// <summary>The module's <c>readFile</c> on the latest pick's folder: the text, or <c>null</c> when absent.</summary>
    public string? ReadFile(string name) => ReadFileOf(Context, name);

    /// <summary>The module's <c>readFile</c> on <paramref name="context"/>.</summary>
    public string? ReadFileOf(int context, string name) => AsNullableString(Call("readDocOf", context, name));

    /// <summary>The module's <c>writeFile</c> on the latest pick's folder.</summary>
    public void WriteFile(string name, string text) => WriteFileOf(Context, name, text);

    /// <summary>The module's <c>writeFile</c> on <paramref name="context"/>.</summary>
    public void WriteFileOf(int context, string name, string text) => Call("writeDocOf", context, name, text);

    /// <summary>The module's <c>probeFileWritability</c> on the latest pick's folder: its answer string.</summary>
    public string ProbeFile(string name) => Call("probeDocOf", Context, name).AsString();

    /// <summary>
    /// The caps as the module receives them, through production's own path: validated by
    /// <see cref="FolderPickLimits"/>, then written by <see cref="JsFolderAccess.ToWireCaps"/>.
    /// </summary>
    private static string WireCaps((string Extension, int Cap)[] caps)
    {
        // The byte cap never reaches the module; the constructor needs one, and this one constrains nothing.
        var limits = new FolderPickLimits(
            caps.Select(c => KeyValuePair.Create(c.Extension, c.Cap)), maxFileBytes: long.MaxValue);
        return JsFolderAccess.ToWireCaps(limits).GetRawText();
    }

    /// <summary>Call a harness entry point and settle it: a promise is unwrapped (rethrowing its rejection).</summary>
    private JsValue Call(string entryPoint, params JsValue[] arguments) =>
        // Spelled out rather than left to `params` on Jint's side: the overload that also
        // takes a `this` value is otherwise not the one C# picks.
        _harness.Get(entryPoint).Call(JsValue.Undefined, arguments).UnwrapIfPromise();

    private static DrawResult ReadDraw(JsValue returned) =>
        JsonSerializer.Deserialize<DrawResult>(returned.AsString(), ResultJson)
            ?? throw new InvalidOperationException("The draw returned no result.");

    private static string? AsNullableString(JsValue value) => value.IsNull() ? null : value.AsString();

    private static string ReadStagedJs(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "js", fileName);
        return File.ReadAllText(path);
    }
}
