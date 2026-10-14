using System.Text.Json;
using Jint;
using Jint.Native;

namespace BgFolderAccess_Razor.Tests;

/// <summary>
/// Runs the RCL's shipped <c>folderAccess.js</c> — the real file, staged beside
/// this assembly by the csproj — inside an in-process JS engine, against one
/// fake folder, and hands its results back as typed values.
///
/// <para>
/// <b>Why this exists.</b> The module's count-cap enforcement is the one piece
/// of genuine algorithm in the library, and it lives in JavaScript, as does the
/// state a resample must leave alone. Everything else in this suite pins C#,
/// with bUnit <i>scripting</i> the module's replies — which can only prove
/// that <see cref="JsFolderAccess"/> maps what it is told, never that the
/// module computes it. Without an engine here, the draw would be exercised for
/// the first time in a consuming host's browser suite, one repository away from
/// the code that owns it.
/// </para>
///
/// <para>
/// <b>What this is not.</b> Jint is a JS engine, not a browser. These tests
/// prove the module's <i>logic</i> — classification, the per-kind draw, result
/// ordering, how many times it stats a file, and what its slots hold across
/// calls. They prove nothing about the real wire: actual pickers, permission
/// prompts, <c>DOMException</c> mapping, <c>File</c>/<c>ArrayBuffer</c>
/// transfer. That half is still browser-only and still belongs to the consuming
/// host's e2e suite (see INSTRUCTIONS.md).
/// </para>
///
/// <para>
/// One instance per test: the module keeps its picked/active slots in module
/// state, and the harness its fake folder, so a shared engine would leak one
/// test's pick into the next. Within one instance, calls see each other — which
/// is what a resample test needs.
/// </para>
///
/// <para>
/// A rejected or thrown module call surfaces as Jint's own exception (a
/// <see cref="Jint.JintException"/>), so a test asserts the failure
/// itself, not a translation of it.
/// </para>
///
/// <para>
/// Public only because xUnit theories are: <see cref="Mechanism"/> is a
/// parameter of public <c>[Theory]</c> methods, and C# requires it to be at
/// least as reachable. Nothing outside this test assembly can see it.
/// </para>
/// </summary>
public sealed class FolderAccessModuleHost
{
    // Module specifiers: moduleHarness.js does `import ... from 'folderAccess'`,
    // so the library module has to be registered under exactly that name.
    private const string ModuleSpecifier = "folderAccess";
    private const string HarnessSpecifier = "moduleHarness";

    // Read once: the files are a few KB and identical for every test, but each
    // test still gets its own Engine below.
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
    /// implementation, so every invariant is asserted against both — that the
    /// two agree is itself the claim.
    /// </summary>
    public enum Mechanism
    {
        /// <summary>File System Access: <c>beginPick</c> then <c>drawPicked</c>.</summary>
        FileSystemAccess,

        /// <summary>The <c>webkitdirectory</c> fallback: <c>collectFallbackFiles</c> then <c>drawPicked</c>.</summary>
        Fallback,
    }

    /// <summary>
    /// One draw's outcome as the module produced it — a pick's or a resample's.
    /// </summary>
    /// <param name="Status">
    /// <c>"drawn"</c>, or <c>"noFolder"</c> when nothing was held to draw from.
    /// </param>
    /// <param name="Files">
    /// The admitted files' names, in the order the module returned them.
    /// </param>
    /// <param name="Kinds">
    /// The module's per-kind report: one entry per kind of the caps table, in
    /// the table's order.
    /// </param>
    /// <param name="Stats">
    /// How many times the draw called <c>getFile()</c> — the FS-Access cost
    /// property. <c>null</c> on <see cref="Mechanism.Fallback"/>, whose Files
    /// carry their size and have nothing to defer.
    /// </param>
    public sealed record DrawResult(
        string Status, IReadOnlyList<string> Files, IReadOnlyList<DrawnKind> Kinds, int? Stats);

    /// <summary>
    /// One kind's found and admitted counts, as they cross the real interop
    /// wire. Value-equal, so a whole report is asserted in one comparison.
    /// </summary>
    public sealed record DrawnKind(string Extension, int FoundCount, int AdmittedCount);

    /// <summary>The browser calls only a pick may make, counted over the host's life.</summary>
    public sealed record BrowserCalls(int PickerOpenings, int PermissionRequests);

    /// <summary>
    /// Pick a folder whose top-level files are <paramref name="fileNames"/>, in
    /// that order, against <paramref name="caps"/> — the per-extension count-cap
    /// table, <b>in its key order</b>, which the per-kind report is required to
    /// read in. The pick replaces the fake folder's contents.
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

    /// <summary>
    /// A File System Access pick the user cancels: the picker rejects with
    /// <c>AbortError</c>. Answers the module's status, <c>"cancelled"</c>.
    /// </summary>
    public string CancelPick() => Call("cancelPick").AsString();

    /// <summary>
    /// Draw whatever is held again — the module call a resample makes — with
    /// the caps table crossing again, as it does on every call.
    /// </summary>
    public DrawResult Resample(params (string Extension, int Cap)[] caps) =>
        ReadDraw(Call("drawJson", WireCaps(caps)));

    /// <summary>
    /// A whole resample as <see cref="JsFolderAccess"/> runs it: the draw, then
    /// each drawn file's bytes in turn, stopping at the first read that fails.
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

    /// <summary>The module's <c>readFileData</c> for one name: the byte count read.</summary>
    public int ReadFileData(string name) => (int)Call("readBytes", name).AsNumber();

    /// <summary>The module's <c>clearPicked</c>.</summary>
    public void Clear() => Call("clear");

    /// <summary>The module's <c>readPickedFile</c>: the text, or <c>null</c> when absent.</summary>
    public string? ReadPickedFile(string name) => AsNullableString(Call("readPicked", name));

    /// <summary>The module's <c>writePickedFile</c>.</summary>
    public void WritePickedFile(string name, string text) => Call("writePicked", name, text);

    /// <summary>The module's <c>probePickedFileWritability</c>: its answer string.</summary>
    public string ProbePickedFile(string name) => Call("probePicked", name).AsString();

    /// <summary>The module's <c>promoteToActive</c>.</summary>
    public bool PromoteToActive() => Call("promote").AsBoolean();

    /// <summary>The module's <c>readActiveFile</c>: the text, or <c>null</c> when absent.</summary>
    public string? ReadActiveFile(string name) => AsNullableString(Call("readActive", name));

    /// <summary>
    /// The caps as the module receives them, through production's own path:
    /// validated by <see cref="FolderPickLimits"/>, then written by
    /// <see cref="JsFolderAccess.ToWireCaps"/> — the object the real interop
    /// wire carries, not a second encoding of it built here. A change to the
    /// wire builder is therefore a change to what these tests run, and a table
    /// the constructor would refuse cannot reach the module at all.
    /// </summary>
    private static string WireCaps((string Extension, int Cap)[] caps)
    {
        // The byte cap never reaches the module (JsFolderAccess enforces it on
        // the C# side); the constructor needs one, and this one constrains nothing.
        var limits = new FolderPickLimits(
            caps.Select(c => KeyValuePair.Create(c.Extension, c.Cap)), maxFileBytes: long.MaxValue);
        return JsFolderAccess.ToWireCaps(limits).GetRawText();
    }

    /// <summary>
    /// Call a harness entry point and settle it: the async ones return a
    /// promise, which is unwrapped (rethrowing its rejection); the rest pass
    /// straight through.
    /// </summary>
    private JsValue Call(string entryPoint, params JsValue[] arguments) =>
        // The argument array is spelled out rather than left to `params` on
        // Jint's side: the overload that also takes a `this` value is otherwise
        // not the one C# picks, and the arguments silently shift by one.
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
