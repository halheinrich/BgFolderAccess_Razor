# BgFolderAccess_Razor

> Collaboration contract: [`../AGENTS.md`](../AGENTS.md)
> Umbrella status & dependency graph: [`../INSTRUCTIONS.md`](../INSTRUCTIONS.md)
> Mission & principles: [`../VISION.md`](../VISION.md)

## What it is

A Blazor WebAssembly host's folder machinery: pick a local folder, buffer its
top-level matching files, and read, write and resample that folder for as
long as the host holds it — across both browser mechanisms, degrading
gracefully. C# / .NET 10 Razor class library with no components: the Razor
SDK exists so `folderAccess.js` ships as a static web asset. Solution
`D:\Users\Hal\Documents\Visual Studio 2026\Projects\backgammon\BgFolderAccess_Razor\BgFolderAccess_Razor.slnx`;
repo https://github.com/halheinrich/BgFolderAccess_Razor, branch `main`.
Standalone, with no reference either way to XgFilter_Razor; a host that uses
both bridges them with an adapter. Its one consumer is `BgQuiz_Blazor.Client`.
The public surface carries XML docs; this file says what the docs cannot.

## Layout

- **The gateway** — `IFolderAccess` / `JsFolderAccess`: the two pick
  mechanisms, and nothing held between calls.
- **The folder context** — `IFolderContext` / `JsFolderContext`: one landed
  folder, held until disposed; resample, named-file read, write and the
  writability probe. The draw, the byte cap and the buffering live here, so a
  pick's sample and a resample are one path.
- **Values** — `FolderPickOutcome` (the context and the pick's draw),
  `FolderSample` (files and the per-kind report), `PickedFile`,
  `SampledKind`, `FolderWriteCapability`, `FileWritability`,
  `FolderPickLimits` (the host's caps, validated), `PickedFileTooLargeException`.
- **The module** — `wwwroot/js/folderAccess.js`: both picks, the one draw,
  the context table. `BgFolderAccessJsonContext` reads its structured replies.
- **Tests** — the C# half pins the values, the caps validation and the two
  seam types' mapping over bUnit's scripted module; the JS half
  (`FolderAccessModuleHost` over `js/moduleHarness.js`) runs the shipped
  module in Jint: the draw's invariants, resample, and the context table. Jint
  is an engine, not a browser: real pickers, prompts, `DOMException` mapping
  and `File` transfer are proved only by BgQuiz's e2e suite.

## How it works

- **Two mechanisms, one outcome.** File System Access: the native picker, a
  readwrite permission request, a draw of the top-level files, full
  buffering; the folder is a directory handle, so documents can be read and
  written and a resample walks it afresh. The `webkitdirectory` fallback: the
  browser hands the whole tree over once; the top-level `File`s are retained
  as candidates (references, never bytes) that a resample redraws; no handle,
  so `FolderWriteCapability.BrowserUnsupported` and no documents.
- **The context table.** A pick lands a folder in the module's table and
  answers its id; the host holds it as an `IFolderContext`, every operation
  names it, and `DisposeAsync` releases it. Two picks are two contexts, so a
  host keeps a run's folder across a later pick of another. A pick that fails
  after its prompts releases its own context and throws. The module records
  nothing else: a draw returns names and sizes, and `readFileData` resolves
  each name against the folder when it is asked, so a failed draw or
  buffering pass leaves nothing behind.
- **Caps: host-owned numbers, library-owned enforcement.** The whole table
  crosses on every draw and the module keeps no copy. Count caps truncate per
  kind, never fail, and every kind is reported (`FolderSample.Kinds`, the
  cap derived from the enforced table, never off the wire); an over-limit
  kind's survivors are drawn uniformly at random (halheinrich/backgammon#106).
  The byte cap fails the whole draw with `PickedFileTooLargeException`, at the
  draw's metadata and again at the length the bytes arrive at.
- **Errors.** Expected outcomes are values: cancelled, `PermissionDenied`, a
  `null` read, `FileWritability.NotWritable`. Unexpected browser failures are
  `JSException`; a released context is `ObjectDisposedException`;
  `PickedFileTooLargeException` derives from `Exception`, so a host reporting
  it never swallows its own bugs.

## DI wiring

```csharp
builder.Services.AddSingleton(new FolderPickLimits(
    new Dictionary<string, int> { [".xg"] = 500, [".xgp"] = 2000 },  // the host's numbers
    maxFileBytes: 50L * 1024 * 1024));
builder.Services.AddScoped<IFolderAccess, JsFolderAccess>();        // one per tab
```

## Pitfalls

- **Two prompts, never `showDirectoryPicker({ mode: 'readwrite' })`.**
  Declining the single readwrite prompt aborts the whole pick (tried and
  reverted 2026-07-24), which destroys the read-only `PermissionDenied` rung.
- **Cancelled and `PermissionDenied` are cause-ambiguous.** A dismissed
  picker and a declined view-files grant both surface as `AbortError`; a
  declined write and a browser that refused to ask (`SecurityError`, Chrome
  for Android, halheinrich/backgammon#109) share a rung. Host copy never says
  the user declined.
- **The busy hook has one truthful raise point**, between the prompts and the
  draw; a resample has no prompt and so no hook.
- **Directory handles never cross interop**; C# addresses them by context id.
- **Count caps truncate, the byte cap throws; don't unify them.** Keep the
  stream-length check before `OpenReadStreamAsync`, and don't translate an
  `ArgumentOutOfRangeException`: other failures wear that type. A zero-byte
  matching file fails the draw that way (the runtime refuses a non-positive
  stream length) and is surfaced untranslated; whether it should buffer as
  empty bytes is an open question.
- **Dispose every `IJSStreamReference`**, not only its stream: the runtime
  roots the buffer until the reference is disposed, a whole sample per draw.
- **The draw sits between the walk and the stats.** Classify by name, draw,
  then `getFile()` the survivors only; a per-entry decision cannot be a
  uniform draw and re-fusing the loops restores the first-N bug.
- **Don't narrow the fallback's candidates** to the draw or to the table's
  matches: the excess would be out of every resample's reach.
- **Only `RepeatedPicks_ReachEveryFileOfAnOverLimitKind` (and its resample
  twin) tells random from first-N**; its failure odds are about 1e-10. A
  "two picks differ" assertion really is flaky.
- **No library type crosses interop as `InvokeAsync<T>`'s type argument**
  (halheinrich/backgammon#197): the annotation keeps `T`, not the records
  nested in it. Replies come back as `JsonElement` and are read through the
  context; the posture test pins every DTO's reachability.
- **The probe aborts its stream and never closes it** (a `close()` would
  replace the file with the empty stream); the `queryPermission` pre-check is
  what keeps it from prompting; `keepExistingData` leaves a `.crswap` behind.
  `Absent` is not `Writable`, and a file another program holds open answers
  `Writable`.
- **Nothing public hands out a writable collection**
  (halheinrich/backgammon#273): `ImmutableArray` members, `OpenRead()` over a
  shared array with no public buffer; `ImmutableExposureAssert` pins it.
- **Suffix-disjoint extension keys are a constructed fact**: the classifier
  takes the first suffix match.
- **The fallback input is reset after collection** (a `change` event fires
  only when the selection differs); an empty FileList lands a folder with
  zero files, not a cancellation.
- **bUnit scripts a call that carries arguments with a matcher**
  (`Setup<T>("draw", _ => true)`); an argument-less setup never matches, and a
  later setup for the same identifier wins over an earlier one.
