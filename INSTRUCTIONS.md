# BgFolderAccess_Razor

> Collaboration contract: [`../AGENTS.md`](../AGENTS.md)
> Umbrella status & dependency graph: [`../INSTRUCTIONS.md`](../INSTRUCTIONS.md)
> Mission & principles: [`../VISION.md`](../VISION.md)
> Open work: the [tracker](https://github.com/halheinrich/backgammon/issues), label `BgFolderAccess_Razor`.

## What it is

A Blazor WebAssembly host's folder machinery: pick a local folder, buffer its
top-level matching files, and read, write and resample that folder for as
long as the host holds it — across both browser mechanisms, degrading
gracefully. C# / .NET 10 Razor class library with no components: the Razor
SDK exists so `folderAccess.js` ships as a static web asset. Standalone, with
no reference either way to XgFilter_Razor; a host that uses both bridges them
with an adapter. Its one consumer is `BgQuiz_Blazor.Client`. The XML docs are
the API reference; this file covers the design, the pitfalls and how to run it.

## Build and test

From this directory; the solution holds the library and its test project, and
warnings are errors (`Directory.Build.props`).

```bash
dotnet build BgFolderAccess_Razor.slnx
```

```bash
dotnet test BgFolderAccess_Razor.slnx
```

The tests run both halves with no browser and no fixture. No CI runs them: the
umbrella's one workflow, `bgquiz.yml`, runs BgQuiz's own suites.

Jint is an engine, not a browser, so this suite proves the module's logic and
nothing of its interop. BgQuiz's e2e suite runs the module in a real Chromium:
the File System Access path through a fake `window.showDirectoryPicker`
(`FsAccessFakeTestBase`) serving a committed fixture's bytes and scripted
outcomes such as a denied permission, and the `webkitdirectory` fallback with
real files set through Playwright's file chooser. That establishes the `File`
transfer, the module's handling of the rejections the fake raises, the
fallback's collection and the page's behaviour while a draw runs. No automated
test drives the native directory picker or its permission dialogs; those are
proved only by hand in a real browser.

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
  `FolderPickLimits` (the host's caps, validated; its `MaxFileCountFor` has no
  production caller, halheinrich/backgammon#409), `PickedFileTooLargeException`.
- **The module** — `wwwroot/js/folderAccess.js`: both picks, the one draw,
  the context table. `BgFolderAccessJsonContext` reads its structured replies.
- **Tests** — the C# half pins the values, the caps validation and the two
  seam types' mapping over bUnit's scripted module; the JS half
  (`FolderAccessModuleHost` over `js/moduleHarness.js`) runs the shipped
  module in Jint: the draw's invariants, resample, and the context table.
  [Build and test](#build-and-test) says what neither half proves.

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
  draw's metadata and again at the length the bytes arrive at. The table's
  keys must be suffix-disjoint, which the constructor enforces, because the
  module's classifier takes the first suffix match.
- **Errors.** Expected outcomes are values: cancelled, `PermissionDenied`, a
  `null` read, `FileWritability.NotWritable`. Unexpected browser failures are
  `JSException`; a released context is `ObjectDisposedException`;
  `PickedFileTooLargeException` derives from `Exception`, so a host reporting
  it never swallows its own bugs.

## Wiring a host

```csharp
builder.Services.AddSingleton(new FolderPickLimits(
    new Dictionary<string, int> { [".xg"] = 500, [".xgp"] = 2000 },  // the host's numbers
    maxFileBytes: 50L * 1024 * 1024));
builder.Services.AddScoped<IFolderAccess, JsFolderAccess>();        // one per tab
```

Each gesture asks `SupportsDirectoryPickerAsync`, then either calls
`PickFolderAsync` with its busy hook, or calls `TriggerFallbackPickerAsync` on
a hidden `<input type="file" webkitdirectory>` the host renders, whose `change`
handler calls `CollectFallbackAsync`. The library never sees that input
dismissed; a host that reports it handles the input's `cancel` event. The host
disposes each `IFolderContext` it stops holding. BgQuiz's `Home.razor` and
`QuizApp` are the worked example.

## Pitfalls

- **Two prompts, never `showDirectoryPicker({ mode: 'readwrite' })`.**
  Declining the single readwrite prompt aborts the whole pick, which destroys
  the read-only `PermissionDenied` rung (tried in real Chrome and reverted
  2026-07-24; the record is BgQuiz_Blazor a496b2f, from before this library
  was hoisted out of it).
- **Cancelled and `PermissionDenied` are cause-ambiguous.** A dismissed
  picker and a declined view-files grant both surface as `AbortError`
  (BgQuiz_Blazor 1a6e7f4); a declined write and a browser that refused to ask
  (`SecurityError`, Chrome for Android, halheinrich/backgammon#109) share a
  rung. Host copy never says the user declined.
- **The busy hook has one truthful raise point**, between the prompts and the
  draw; a resample has no prompt and so no hook.
- **Directory handles never cross interop**; C# addresses them by context id.
- **Count caps truncate, the byte cap throws; don't unify them.** Keep the
  stream-length check before `OpenReadStreamAsync`, so a file that grew since
  the draw surfaces as the cap's exception, and don't translate an
  `ArgumentOutOfRangeException`: other failures wear that type. A zero-byte
  matching file is one, by the runtime's source (it refuses a non-positive
  stream length; not yet run in a browser): it fails the draw, untranslated.
  What it should do instead is booked as halheinrich/backgammon#410.
- **Dispose every `IJSStreamReference`**, not only its stream: the runtime
  roots the buffer until the reference is disposed, a whole sample per draw.
- **The draw sits between the walk and the stats.** Classify by name, draw,
  then `getFile()` the survivors only; a per-entry decision cannot be a
  uniform draw and re-fusing the loops restores the first-N bug.
- **Don't narrow the fallback's candidates** to the draw or to the table's
  matches: the excess would be out of every resample's reach.
- **Only `RepeatedPicks_ReachEveryFileOfAnOverLimitKind` and its twin
  `RepeatedResamples_ReachEveryFileOfAnOverLimitKind` tell random from
  first-N**; each states its failure odds and the arithmetic behind them. A
  "two picks differ" assertion really is flaky.
- **No library type crosses interop as `InvokeAsync<T>`'s type argument**
  (halheinrich/backgammon#197): the annotation keeps `T`, not the records
  nested in it. Replies come back as `JsonElement` and are read through the
  context; `BgFolderAccessRazorTrimPostureTests` pins every DTO's reachability.
- **The probe aborts its stream and never closes it** (a `close()` would
  replace the file with the empty stream); the `queryPermission` pre-check is
  what keeps it from prompting. `Absent` is not `Writable`. A file another
  program holds open answers `Writable`, and `{ keepExistingData: true }`
  stays out though it caught that file, because it left a `.crswap` behind
  (both measured in Chrome 153, 1b6c269, halheinrich/backgammon#261).
- **Nothing public hands out a writable collection**
  (halheinrich/backgammon#273): `ImmutableArray` members, `OpenRead()` over a
  shared array with no public buffer; `ImmutableExposureAssert` pins it.
- **The fallback input is reset after collection** (a `change` event fires
  only when the selection differs); an empty FileList lands a folder with
  zero files, not a cancellation.
- **bUnit scripts a call that carries arguments with a matcher**
  (`Setup<T>("draw", _ => true)`); an argument-less setup never matches, and a
  later setup for the same identifier wins over an earlier one.
