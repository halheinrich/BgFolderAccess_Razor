using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;

namespace BgFolderAccess_Razor.Tests;

/// <summary>
/// Runs the RCL's shipped <c>folderAccess.js</c> — the real file, staged beside
/// this assembly by the csproj — inside an in-process JS engine, and hands its
/// results back as typed <see cref="PickResult"/>s.
///
/// <para>
/// <b>Why this exists.</b> The module's count-cap enforcement is the one piece
/// of genuine algorithm in the library, and it lives in JavaScript. Everything
/// else in this suite pins C#, with bUnit <i>scripting</i> the module's replies
/// — which can only prove that <see cref="JsFolderAccess"/> maps what it is
/// told, never that the module computes it. Without an engine here, the draw
/// would be exercised for the first time in a consuming host's browser suite,
/// one repository away from the code that owns it.
/// </para>
///
/// <para>
/// <b>What this is not.</b> Jint is a JS engine, not a browser. These tests
/// prove the module's <i>logic</i> — classification, the per-kind draw, result
/// ordering, and how many times it stats a file. They prove nothing about the
/// real wire: actual pickers, permission prompts, <c>DOMException</c> mapping,
/// <c>File</c>/<c>ArrayBuffer</c> transfer. That half is still browser-only and
/// still belongs to the consuming host's e2e suite (see INSTRUCTIONS.md).
/// </para>
///
/// <para>
/// One instance per test: the module keeps its picked/active slots in module
/// state, so a shared engine would leak one test's pick into the next.
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
        /// <summary>File System Access: <c>beginPick</c> then <c>enumeratePicked</c>.</summary>
        FileSystemAccess,

        /// <summary>The <c>webkitdirectory</c> fallback: <c>collectFallbackFiles</c>.</summary>
        Fallback,
    }

    /// <summary>
    /// One pick's outcome as the module produced it.
    /// </summary>
    /// <param name="Files">
    /// The admitted files' names, in the order the module returned them.
    /// </param>
    /// <param name="Omitted">The module's per-kind left-behind report.</param>
    /// <param name="Stats">
    /// How many times the pick called <c>getFile()</c> — the FS-Access cost
    /// property. <c>null</c> on <see cref="Mechanism.Fallback"/>, which is
    /// handed the whole FileList up front and has nothing to defer.
    /// </param>
    public sealed record PickResult(
        IReadOnlyList<string> Files, IReadOnlyList<OmittedKind> Omitted, int? Stats);

    /// <summary>One kind's left-behind count, as it crosses the real interop wire.</summary>
    public sealed record OmittedKind(string Extension, int OmittedCount);

    /// <summary>
    /// Pick a folder whose top-level files are <paramref name="fileNames"/>, in
    /// that order, against <paramref name="caps"/> — the per-extension count-cap
    /// table in the shape <see cref="FolderPickLimits.MaxFileCounts"/> reaches
    /// the module in, <b>including its key order</b>, which the left-behind
    /// report is required to read in.
    /// </summary>
    public PickResult Pick(
        Mechanism mechanism, string[] fileNames, params (string Extension, int Cap)[] caps)
    {
        var limits = new JsonObject();
        foreach (var (extension, cap) in caps)
        {
            limits[extension] = cap;
        }

        var entryPoint = mechanism switch
        {
            Mechanism.FileSystemAccess => "enumerateJson",
            Mechanism.Fallback => "collectFallbackJson",
            _ => throw new ArgumentOutOfRangeException(nameof(mechanism)),
        };

        // The argument array is spelled out rather than left to `params`: the
        // overload that also takes a `this` value is otherwise not the one C#
        // picks, and the arguments silently shift by one.
        JsValue[] arguments = [JsonSerializer.Serialize(fileNames), limits.ToJsonString()];

        // enumerateJson is async; collectFallbackJson is not. Unwrapping covers
        // both — a non-promise passes straight through.
        var returned = _harness.Get(entryPoint).Call(JsValue.Undefined, arguments).UnwrapIfPromise();

        return JsonSerializer.Deserialize<PickResult>(returned.AsString(), ResultJson)
            ?? throw new InvalidOperationException($"'{entryPoint}' returned no result.");
    }

    private static string ReadStagedJs(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "js", fileName);
        return File.ReadAllText(path);
    }
}
