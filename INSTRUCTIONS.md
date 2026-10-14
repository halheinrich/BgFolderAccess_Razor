# BgFolderAccess_Razor

> Collaboration contract: [`../AGENTS.md`](../AGENTS.md)
> Umbrella status & dependency graph: [`../INSTRUCTIONS.md`](../INSTRUCTIONS.md)
> Mission & principles: [`../VISION.md`](../VISION.md)

## Stack

C# / .NET 10 / Razor class library (`Microsoft.NET.Sdk.Razor`) / xUnit + bUnit
(JS-module interop scripting only — no components render) + Jint (runs the
shipped `folderAccess.js` in-process; see Test posture).
Visual Studio 2026 on Windows.

## Solution

`D:\Users\Hal\Documents\Visual Studio 2026\Projects\backgammon\BgFolderAccess_Razor\BgFolderAccess_Razor.slnx`

## Repo

https://github.com/halheinrich/BgFolderAccess_Razor — branch `main`.

## Depends on

Standalone. (Deliberately no reference either direction with `XgFilter_Razor` —
hosts that use both bridge them with one-line adapter glue, e.g. an
`IDocumentStorage` adapter over `IFolderAccess`'s picked-slot file I/O.)

## Layout

Two projects under `BgFolderAccess_Razor.slnx`, governed by repo-root
`Directory.Build.props` (TFM, `TreatWarningsAsErrors`, XML doc generation)
and `Directory.Packages.props` (Central Package Management).

**`BgFolderAccess_Razor/`** — the Razor class library. It renders no
components. Three areas:

- **The host-facing seam** — `IFolderAccess`, the host's one gateway to the
  browser's folder facilities; `JsFolderAccess`, its implementation and the
  library's only interop type; and `BgFolderAccessJsonContext`, the
  source-generated metadata its wire replies are read through (see Test
  posture for why a context, not the runtime's own deserializer).
- **The pick's values** — `FolderPickOutcome`, the result of one pick
  gesture; `FolderSample`, one draw of the folder (its files and its
  per-kind report), which a pick carries and a resample returns alone;
  `PickedFile`, one buffered matching file; `SampledKind`, one file kind's
  found and admitted counts; `FolderWriteCapability`, the pick-time
  write-capability taxonomy; `PickedFileWritability`, one named file's
  writability as the setup-time probe answers it; `FolderPickLimits`, the host-supplied caps
  configuration, validated at construction.
- **The JS module** — `wwwroot/js/folderAccess.js`, the ES module shipped as
  a static web asset: both pick mechanisms, the one draw they and Resample
  share, and the two-slot state, with directory handles that never cross
  interop.

**`BgFolderAccess_Razor.Tests/`** — xUnit, in the two halves § Test posture
describes. The C# half: the value types' contracts, the caps validation,
and `JsFolderAccess`'s mapping over bUnit's scripted module. The JS half:
`FolderAccessModuleHost` runs the shipped `folderAccess.js` in Jint through
the stateful test-side fakes in `js/moduleHarness.js`; the sampling tests pin
the count-cap draw as the module itself computes it, and the resample tests
pin a second draw of the held folder and what the module holds across a
failed one.

## Architecture

### What this library is

BgQuiz_Blazor's File System Access folder machinery, rehomed (umbrella arc
issue halheinrich/backgammon#79) so a second consumer (Extract Web, deferred
issue halheinrich/backgammon#80) can adopt it instead of copying it. One
concern: let a Blazor WebAssembly host pick a local folder, buffer its
top-level matching files, and read/write named text files in that folder —
across both browser mechanisms, with graceful degrades. No `.razor`
components; the Razor SDK exists solely so `folderAccess.js` ships as a static
web asset (`./_content/BgFolderAccess_Razor/js/folderAccess.js`).

### Two pick mechanisms, one outcome shape

- **File System Access** (`showDirectoryPicker`, Chromium): native picker →
  readwrite permission request → draw of the top-level files → full
  buffering. Yields a directory handle, so named-file read/write works, and a
  resample walks it afresh.
- **`webkitdirectory` fallback** (everything else): a hidden `<input>`'s
  native picker; the browser hands over the whole tree once, the module
  retains its top-level files as the folder's candidates, then draws from
  them — as a resample later does again. No writable handle —
  `FolderWriteCapability.BrowserUnsupported` always.

Both land in the same `FolderPickOutcome`, through the same draw
(`drawPicked`, which `ResampleAsync` calls alone); the capability and
per-kind report contracts are identical across mechanisms (same module,
same caps table).
`SupportsDirectoryPickerAsync` is probed per gesture, not at boot — capability
is a property of the moment.

### Two-slot state model (in the JS module, never crossing interop)

- **picked slot** — the **held folder**: the directory handle of a File
  System Access pick, or the **retained candidates** of a fallback
  collection (every top-level `File` the browser handed over, by name — file
  references, never bytes; the draw classifies them against each call's caps
  table, so nothing retained encodes the table). Every draw and every byte
  read resolves against it. Serves setup-time named documents
  (`ReadPickedFileAsync` / `WritePickedFileAsync`, File System Access only),
  and answers whether one can be written before the first write tries
  (`ProbePickedFileWritabilityAsync`).
- **active slot** — bound only by `PromoteToActiveAsync()`; the folder a
  running session records through (`ReadActiveFileAsync` /
  `WriteActiveFileAsync`).

The split is the isolation guarantee: `ClearPickedAsync` and a re-pick touch
only the picked slot, so work bound to the active slot keeps its handle until
the next promotion re-binds. Handles live in JS module state; C# sees names,
sizes, bytes, and booleans.

**What "held" means** is defined once, on `IFolderAccess`'s type remarks, and
the module's three writers of the picked slot are its whole implementation:
a File System Access pick replaces the held folder once its prompts settle
with a folder (so a pick that then fails still leaves the new folder held); a
cancelled one leaves it as it was; a fallback collection replaces it, an
empty FileList included; `ClearPickedAsync` releases it with its candidates.
Nothing else writes the slot — not a draw, not a byte read, not a resample
that fails part-way. **The module records nothing between calls**: a draw
returns names and sizes, and `readFileData` resolves each name against the
held folder when the buffering pass asks for it, so there is no drawn-file
map to leave half-replaced.

### Caps: host-owned numbers, library-owned enforcement

`FolderPickLimits` (host-supplied, DI-registered, validated at construction)
carries the per-extension count-cap table and the per-file byte cap. The
library ships **no numbers** — each host's values encode its own cost model
(BgQuiz's `PickedFileLimits` stays host-side; Extract Web will bring its own).

- The **whole count table crosses the interop boundary on every draw** (a
  pick's, a fallback collection's, a resample's); the JS module derives both
  of its jobs from it — which names are matching files, and how many of each
  to take — and keeps no copy (SSOT). It crosses
  as one JSON object that `JsFolderAccess.ToWireCaps` writes, members in the
  table's order, so the module's `Object.keys` order (and with it the
  per-kind report's order) is the host's. The Jint suite hands the module
  that same object (`FolderAccessModuleHost` calls `ToWireCaps`), so it runs
  the wire production sends, not a second encoding of it.
- **Count caps truncate, never fail**, per extension independently, in the JS
  module (the only place an extension is known before transfer).
- **Every draw reports every kind** (`FolderSample.Kinds`): one `SampledKind`
  per entry of the table, in its order, with how many matching files the
  draw found and how many it admitted — a kind the folder held none of
  included, at zero. It is the only report of those counts, and
  `FolderSample.HoldsEveryMatchingFile` derives from it whether the sample is
  the only one the limits could draw, so a host never classifies `Files` by
  name to find out.
- **An over-limit kind's survivors are drawn uniformly at random**, not taken
  in enumeration order (umbrella issue halheinrich/backgammon#106). That order
  is browser-supplied and stable, so a prefix rule put a corpus-scale folder's
  excess permanently out of reach — the same files lost to every re-pick. The
  draw is the module's only behavior, deliberately **not** a `FolderPickLimits`
  knob: the table is host-owned *numbers*, and how a cap picks its survivors is
  library enforcement. Files still come back in folder order, and a pick under
  every cap is unchanged from a pick that never sampled.
- The **byte cap fails the whole pick or resample**
  (`InvalidOperationException` from `JsFolderAccess`, checked against the
  drawn files' metadata before any transfer) and is re-asserted as
  `OpenReadStreamAsync(maxAllowedSize:)` on the actual bytes.
- Each `SampledKind.MaxFileCount` is **derived** from the enforced
  `FolderPickLimits` instance at the one lib-side construction site
  (`JsFolderAccess.ToKinds`, which takes it from the table entry at the same
  position) — never round-tripped across interop — so the figure a host's
  notice states is by construction the figure the draw enforced. The found
  and admitted counts are the module's, carried back; `ToKinds` refuses a
  report that is not one entry per table kind in the table's order
  (`JsonException`), and `SampledKind`'s constructor refuses counts no draw
  could produce.
- **Resample** (`IFolderAccess.ResampleAsync`) draws the held folder again at
  the same limits, with no picker and no permission prompt or request, and
  returns a `FolderSample` — the files and the report, and nothing of the
  setup: it makes no write-capability claim, since it never asks the browser
  for one. It is `JsFolderAccess.DrawSampleAsync`, the very path both pick
  mechanisms end in, called alone: one draw, one byte cap, one buffering
  pass. All or nothing — a refused walk, the byte cap or a read failing
  part-way through buffering throws and returns no part of a sample, and
  leaves everything held as it was (see the two-slot model above), so a later
  resample can succeed. No busy hook: the host raises its affordance before
  calling.

### Error contract

Expected outcomes are values, never exceptions: a pick that ended with no
folder is `FolderPickOutcome.Cancelled`, a write denial is
`FolderWriteCapability.PermissionDenied`, a missing named file is a `null`
read. Unexpected browser failures surface as `JSException` for hosts to catch
and degrade on. Two throws are deliberate, both `InvalidOperationException`:
the byte cap above, and a draw with no folder held — the module answers
`noFolder` and `DrawSampleAsync` throws, which is the caller error
`ResampleAsync` documents (and what a pick throws if `ClearPickedAsync`
released its folder while its hook was awaited).

The writability probe (`ProbePickedFileWritabilityAsync`, halheinrich/backgammon#261)
answers `PickedFileWritability` values the same way. A missing file is
`Absent`. `NotWritable` is a `readwrite` `queryPermission` that is not
`'granted'` — checked first, so the probe never prompts — or a
`createWritable()` rejecting with one of the two names in the module's
`NOT_WRITABLE_REJECTIONS`: `NoModificationAllowedError` (the read-only
attribute, measured in Chrome 153) and `NotAllowedError` (permission absent;
the backstop behind the pre-check). Every other rejection propagates as
`JSException`, as does probing with no File System Access folder picked. A
module answer outside the three strings is a wire-contract violation and
throws `JsonException` in `JsFolderAccess.ToWritability`, the one mapping
point.

**The probe's limit, measured:** it catches a file the browser cannot open for
writing (read-only attribute, no permission), not one another program holds
open. Plain `createWritable()` does not open the original, so a locked file
answers `Writable` and is still discovered at the first real write — hosts keep
their write-failure notices for that reason.

### Test posture (honest scope)

The xUnit suite pins the C# side: `FolderPickLimits` validation, the outcome
value types, and `JsFolderAccess`'s mapping seam over bUnit's scripted module
interop (`SetupModule` — no real JS runs there).

The library's trim posture (halheinrich/backgammon#197) is pinned by
`BgFolderAccessRazorTrimPostureTests`: the built assembly carries the
SDK-emitted `IsTrimmable` metadata (the one trace of the csproj setting a
test can read), `BgFolderAccessJsonContext` generates metadata-only, and
every wire DTO resolves through that context. The analyzer switch leaves no
such trace: the trim analyzer runs in this library's own build under
`TreatWarningsAsErrors`, so a *direct* trim-unsafe call — a reflection-bound
serializer overload — is a build error here, not a test failure.

That is the analyzer's whole reach, and it was over-claimed until the arc's
proof (2026-09-15) said otherwise. `IJSObjectReference.InvokeAsync<T>`
annotates `T` with `DynamicallyAccessedMembers`, which keeps `T`'s own
constructor and members through a trim but is not transitive: a record
reached only as the element type of `T`'s array property is named nowhere
the trimmer looks, and BgQuiz's partial trim removed `JsPickedFile`'s
constructor while the analyzer — which inspects the annotated call and finds
it clean — reported nothing. The transitive DTO graph is therefore protected
by a rule, not a flag: **no library type crosses interop as a type
argument**. Every reply comes back as a `JsonElement` and is read through
the context by one private helper (`JsFolderAccess.InvokeDtoAsync`), so the
generator, not the trimmer, decides what the graph needs. The posture test
catches a DTO the context does not reach; a DTO handed to interop *again*
is caught only at runtime, by BgQuiz's trimmed e2e
(`EnvironmentFidelityTests`, whose fixture publishes the trimmed AOT
artifact) — that suite is the gate for the runtime half, and this repo's
green build is not.

It also **runs the shipped `folderAccess.js` itself**, in Jint, via
`FolderAccessModuleHost` — the real file, staged beside the test assembly by
the csproj so a rename breaks the build rather than a test. That covers the
module's *logic*: classification, the per-kind random draw and its per-kind
report, result ordering, how many times a draw calls `getFile()`, and — over
the harness's stateful fake folder — what the picked and active slots hold
across a pick, a resample, a refused walk, a failed read and a Clear. It exists
because the draw is the library's one real algorithm and it is written in
JavaScript; without an engine here it would first execute one repository away,
in a consumer's browser suite.

**Jint is an engine, not a browser.** The module's real-wire proof — actual
pickers, permission prompts, `DOMException` mapping, `File`/`ArrayBuffer`
transfer — is still browser-only and is NOT covered in this repo; it arrives
with the consuming host's e2e suite (BgQuiz's migration leg carries it). Do not
read this repo's green tests as coverage of the browser behavior.

## Public API

All types `public`, root namespace `BgFolderAccess_Razor`. `JsFolderAccess`'s
wire DTOs and `BgFolderAccessJsonContext` are `internal` (test-only
`InternalsVisibleTo`).

### DI wiring (host `Program.cs`)

```csharp
builder.Services.AddSingleton(new FolderPickLimits(
    new Dictionary<string, int> { [".xg"] = 500, [".xgp"] = 2000 },  // host's numbers
    maxFileBytes: 50L * 1024 * 1024));
builder.Services.AddScoped<IFolderAccess, JsFolderAccess>();
```

Scoped, one per tab (the JS module import is lazy and cached per instance).
The module loads from `./_content/BgFolderAccess_Razor/js/folderAccess.js` —
no host script tag needed; the RCL static-web-asset pipeline serves it.

### `IFolderAccess`

```csharp
ValueTask<bool> SupportsDirectoryPickerAsync();                  // per-gesture probe
Task<FolderPickOutcome> PickFolderAsync(Func<Task> onPickAccepted);
Task TriggerFallbackPickerAsync(ElementReference fallbackInput);
Task<FolderPickOutcome> CollectFallbackAsync(ElementReference fallbackInput);
Task<FolderSample> ResampleAsync();                              // the held folder again; no prompt
ValueTask<bool> PromoteToActiveAsync();                          // false = no-write signal
Task<string?> ReadPickedFileAsync(string fileName);              // null = absent, not error
Task WritePickedFileAsync(string fileName, string json);
Task<PickedFileWritability> ProbePickedFileWritabilityAsync(string fileName); // never prompts, never writes
Task<string?> ReadActiveFileAsync(string fileName);              // null = absent, not error
Task WriteActiveFileAsync(string fileName, string json);
ValueTask ClearPickedAsync();                                    // picked slot only
```

`onPickAccepted` is awaited after the browser's prompts settle and before the
draw and buffering — the host's one truthful place to raise a busy
affordance (see Pitfalls). `ResampleAsync` takes no hook: it shows no prompt,
so the host raises its busy affordance before calling. It throws
`InvalidOperationException` when no folder is held (a caller error) or a drawn
file is over the byte cap, and `JSException` when the walk is refused or a
read fails; either way it returns no part of a sample and leaves everything
held as it was. File-name constants stay host-side; the slot file I/O is
name-parameterized by design.

### `FolderPickLimits`

```csharp
FolderPickLimits(IEnumerable<KeyValuePair<string, int>> maxFileCounts, long maxFileBytes);
ImmutableArray<KeyValuePair<string, int>> MaxFileCounts { get; }  // the ctor's order, by position
long MaxFileBytes { get; }
long MaxFileMegabytes { get; }                           // derived, floored MiB
int MaxFileCountFor(string extension);
```

Ctor validates: non-empty table; lower-case dot-leading keys; no duplicates;
suffix-disjoint keys; positive counts; positive byte cap. The table is copied
in, so the host's source collection cannot reach it afterwards.

### Outcome types

```csharp
sealed record FolderPickOutcome(
    bool Cancelled, string DirectoryName, FolderWriteCapability Capability, FolderSample Sample)
{ static FolderPickOutcome CancelledOutcome { get; } }              // empty sample: no files, no kinds

sealed record FolderSample(ImmutableArray<PickedFile> Files, ImmutableArray<SampledKind> Kinds)
{ bool HoldsEveryMatchingFile { get; } }                           // derived: no kind cut short

sealed record SampledKind                                           // validated; no `with` setters
{
    SampledKind(string extension, int foundCount, int admittedCount, int maxFileCount);
    string Extension { get; }
    int FoundCount { get; }
    int AdmittedCount { get; }
    int MaxFileCount { get; }                                      // the enforced table's figure
    int OmittedCount { get; }                                      // derived: found - admitted
}

sealed record PickedFile(string FileName, ImmutableArray<byte> Bytes)  // name keeps its extension
{ Stream OpenRead(); }                                             // fresh, read-only, no copy
enum FolderWriteCapability { Enabled, BrowserUnsupported, PermissionDenied }
enum PickedFileWritability { Absent, Writable, NotWritable }        // one named file, per probe
```

Host tests fake `IFolderAccess` and construct outcomes directly — the records
are public for exactly that. Only `SampledKind` is value-equal: `PickedFile`
and `FolderSample` compare their arrays by reference, and `FolderPickOutcome`
its sample, so a test compares their members. The array members refuse a
`default` array at construction and in `with` (`ArgumentException` naming the
member); "none" is an empty `[]`. `SampledKind`'s constructor refuses a
negative count, a non-positive cap, and an admitted count past the found count
or the cap (`ArgumentOutOfRangeException`).

## Pitfalls

This section is the permanent home of the File System Access lore this
machinery was built on. Most of it was learned live in BgQuiz; losing any of
it re-opens a closed trap.

- **The two-prompt shape is deliberate — do NOT collapse it into
  `showDirectoryPicker({ mode: 'readwrite' })`.** Tried and reverted
  2026-07-24 (in BgQuiz, this module's origin): declining the single readwrite
  prompt **aborts the whole pick** (AbortError → mapped to cancelled), which
  destroys the read-only `PermissionDenied` rung — a deliberate, valued
  degrade (decline write, keep reading). One fewer prompt is not worth
  silently losing it. This is a closed won't-do; the underlying "second prompt
  gets missed" concern is a host-copy problem (progressive disclosure,
  in-page "check your browser" guidance), not a prompt-collapse.
- **Cancelled is cause-ambiguous, and host copy must respect that.** A
  dismissed picker and a declined *first* (view-files) permission both surface
  as `AbortError`; nothing downstream can tell them apart. Host copy must
  never say "you declined" — and must never promise a prompt count either
  (see the auto-deny below). Treat cancelled as "no folder was picked",
  nothing more.
- **The write capability is cause-ambiguous too.** `PermissionDenied` covers
  the user declining the readwrite prompt *and* some Chromium versions
  auto-denying it (the transient user activation gets consumed by the
  picker, so the second request resolves 'denied' with no prompt shown).
  Copy for the read-only degrade must not claim the user made a choice.
- **The busy-affordance seam (`onPickAccepted`) has exactly one truthful
  raise point.** Everything in `beginPick` is the browser asking the user;
  everything in `drawPicked` is the app working. The hook is awaited
  between them: raised any earlier, a busy affordance would lie over a modal
  prompt; raised any later, it could never paint, because the scan that
  follows never yields to the renderer on WebAssembly's single thread. The
  hook is not invoked for a cancelled pick (no work to report). The seam
  ordering is pinned by test
  (`PickFolder_RunsTheAcceptedHook_AfterThePromptsAndBeforeTheScan`).
  **Don't add a hook to `ResampleAsync`:** it has no prompt, so there is no
  boundary for one to mark; the host raises and paints its affordance before
  the call, which is the one truthful point there.
- **The slot split is the isolation guarantee — don't "simplify" to one
  slot.** Clearing or re-picking touches only the picked slot; the active
  slot persists so work recording through it survives a mid-session Clear.
  Fusing the slots re-opens the mid-session-clear data-loss trap the split
  was built for.
- **Directory handles never cross the interop boundary.** They can't be
  serialized; they live in JS module state, and C# addresses them only
  through the module's functions. Don't add an API that tries to hand one
  out.
- **The caps table crosses whole, every call — the JS module holds no
  copy.** Both of the module's jobs (which names match, how many to take)
  derive from the passed table. Adding a constant or default in the JS is a
  second encoding that will drift (SSOT).
- **Nothing public hands out a collection a caller can write back into**
  (halheinrich/backgammon#273). `MaxFileCounts` used to be the private
  `Dictionary` behind `IReadOnlyDictionary`, and a cast wrote caps past the
  constructor's validation. It is an `ImmutableArray` now, because an array's
  order is its position, a documented fact. Neither `Dictionary` nor
  `ReadOnlyDictionary` documents an enumeration order, and "insertion order is
  preserved" was an implementation detail. Don't swap in a dictionary for
  lookup convenience; `MaxFileCountFor` is the lookup. `PickedFile.Bytes` was
  a public `byte[]`, writable in place under every holder of the outcome; it
  is an `ImmutableArray<byte>` now. `OpenRead()` is the per-pass stream that
  `new MemoryStream(Bytes)` used to be, sharing the array without copying it.
  So the stream is the one door left to that array: it must stay
  `writable: false` with no public buffer, and a copy-free `AsImmutableArray`
  wrap is only sound over an array nothing else holds (`BufferFilesAsync`'s
  fresh `ToArray`). The pick's files were the `List` that
  `BufferFilesAsync` filled, handed out behind `IReadOnlyList`.
  `FolderSample.Files` and `FolderSample.Kinds` are `ImmutableArray`s, so the
  record's own type makes the promise and no construction site has to
  remember it.
  `ImmutableExposureAssert` is the shared test definition:
  an exact-set pin on the implemented interfaces fails any new shape until
  every writing member of every interface it implements has been tried.
- **Suffix-disjoint extension keys are a constructed fact now.** The JS
  classifier takes the first suffix match; `FolderPickLimits`'s ctor rejects
  a key that is a suffix of another key (e.g. `.gz` alongside `.tar.gz`).
  This was a comment-only assumption in BgQuiz's module — behavior-identical
  for its `.xg`/`.xgp` table. Don't weaken the validation; the classifier's
  correctness rides on it.
- **Count caps truncate; the byte cap throws — that asymmetry is
  deliberate.** A folder past a count cap still yields a useful partial pick
  plus a per-kind report saying so; an oversized file fails the whole pick
  before any transfer. Don't "unify" them either way: truncating on bytes
  would silently drop a file the user can see, and throwing on counts
  re-opens the lose-the-whole-pick failure the truncation contract closed.
- **The report covers every kind, and `SampledKind.MaxFileCount` is derived,
  never round-tripped.** Don't trim the report back to the kinds a cap cut
  short: "is this the only sample the limits could draw?" is answered by
  every kind being whole (`FolderSample.HoldsEveryMatchingFile`), and a gap
  cannot say "none found". Don't put the cap on the wire either: it comes
  from the enforced `FolderPickLimits` instance at `ToKinds`, so a host
  notice can never state a figure the draw didn't enforce. Nor derive the
  admitted count from the cap on the C# side — that would restate the draw's
  rule in a second language; the module reports what it admitted.
- **Extension-bearing names are a stated contract.** `PickedFile.FileName`
  keeps its extension; the JS enumeration preserves it. Hosts discriminate
  file kind from the name (BgQuiz's decision-id stamping does). Don't strip
  or normalize names in the module or the seam.
- **The fallback input must be reset after collection.** A `change` event
  only fires when the selection differs, so `collectFallbackFiles` clears
  `inputElement.value` — removing that breaks re-picking the same folder.
- **An empty fallback FileList is a non-cancelled outcome with zero files**,
  not `CancelledOutcome`. The fallback has no cancel signal at all (a
  dismissal fires nothing); don't invent one.
- **Dispose tolerates `JSDisconnectedException`** (tab close / reload tears
  the runtime down before the module reference releases). Keep the catch;
  removing it turns every tab close into an unhandled exception.
- **Dispose every `IJSStreamReference`, not only the stream opened from it.**
  The runtime keeps each returned buffer in its JS object table until the
  reference's `DisposeAsync` removes it, and the stream does not dispose its
  reference (dotnet/aspnetcore v10.0.12: `Microsoft.JSInterop.ts`
  `createJSStreamReference`, `JSObjectReference.DisposeAsync`,
  `PullFromJSDataStream`). `BufferFilesAsync` declares the reference with
  `await using`, so it is released after the copy and on any failure to open
  or copy. Dropping that leaks every buffered file's bytes for the life of the
  tab — a whole sample per resample. The `BufferedFile_StreamReferenceIsReleased_*`
  seam tests pin all three exits, counting disposal on a fake that, like the
  runtime's reference, is released by nothing else.
- **The draw sits BETWEEN the walk and the stats — don't fold it back in.**
  `drawPicked` classifies and collects by name (free), draws each kind's
  survivors, and only then calls `getFile()`, so a ten-thousand-file folder
  still pays one stat per file it *takes*. A per-entry admission decision made
  on the way past cannot be a uniform draw — it does not yet know how many
  candidates are still coming — so re-fusing the two loops silently restores
  the first-N bug halheinrich/backgammon#106 closed.
  `Enumeration_StatsOnlyTheFilesItTakes` and
  `FileSystemAccessResample_StatsOnlyTheFilesItTakes` guard the cost half.
- **One draw, and it records nothing — don't give Resample its own path, or
  the module a drawn-file map.** Both pick mechanisms and `ResampleAsync`
  end in `drawPicked` and `JsFolderAccess.DrawSampleAsync`; a second copy of
  either is a second draw rule, byte cap or buffering pass to keep in step.
  And `readFileData` resolves names against the held folder, not against a
  map the draw left behind: such a map is state a failed buffering pass can
  leave pointing at a sample nobody received, which is what made "a failed
  resample changes nothing" a property to defend rather than a fact.
- **Don't narrow the fallback's candidates.** They are every top-level file
  the collection saw, not the pick's matches and never the pick's draw:
  narrowing to the draw puts the excess out of every later resample's reach
  (the halheinrich/backgammon#106 failure again, on the resample path), and narrowing to the
  matches bakes the collection-time caps table into module state.
  `RepeatedResamples_ReachEveryFileOfAnOverLimitKind` (fallback) and
  `FallbackResample_ClassifiesTheCandidatesAgainstTheTableItIsHanded` guard
  the two.
- **A pick that fails after its prompts still leaves the new folder held.**
  `beginPick` binds the folder before the draw, and `collectFallbackFiles`
  before its draw, so an oversized file or a failed read throws out of the
  pick with the module holding the folder the pick was for, not the one held
  before. That is the documented "held" (see `IFolderAccess`); a host that
  keeps its previous sample across a failed pick must not then resample, or
  it draws a different folder from the one its sample came from — release
  the folder (`ClearPickedAsync`) or treat the pick's failure as ending the
  setup.
- **Only one sampling test tells random from first-N.** Every other invariant
  in `FolderAccessSamplingTests` (`|admitted| = min(cap, matching)`, subset,
  per-kind independence, encounter order, report order) holds under *both*
  rules — verified by running the suite against the module from before
  halheinrich/backgammon#106, where exactly
  `RepeatedPicks_ReachEveryFileOfAnOverLimitKind` fails. Don't delete it as
  "the flaky-looking one": its stated failure odds are ~1e-10, and without it
  the change is untested. Equally, don't replace it with a "two picks differ"
  assertion — that one really is flaky (1-in-501 on BgQuiz's `.xg` cap).
  `RepeatedResamples_ReachEveryFileOfAnOverLimitKind` in
  `FolderAccessResampleTests` is the same property on the resample path, with
  the same standing.
- **bUnit test trap: scripted setups for calls that carry the caps table need
  a matcher** (`Setup<T>("drawPicked", _ => true)`) — an argument-less
  exact setup never matches, and the resulting "no setup" failure looks like
  a bunit bug rather than what it is.
- **No library type crosses interop as `InvokeAsync<T>`'s type argument —
  take a `JsonElement` back and read it through `BgFolderAccessJsonContext`
  (`JsFolderAccess.InvokeDtoAsync`).** The annotation on `T` keeps `T`
  through BgQuiz's trim, not the records nested in `T`; `JsPickedFile` lost
  its constructor that way and the pick failed on the first file
  (halheinrich/backgammon#197, measured 2026-09-15), with this repo's trim
  analyzer silent because the call it inspects is annotated. A new reply
  type goes on the context's `[JsonSerializable]` roots and through the
  helper; the posture test fails a DTO the context cannot reach, and only
  BgQuiz's trimmed e2e catches one handed to interop again. bUnit scripts
  replies as `JsonElement`s serialized through the context for the same
  reason — scripting the DTO would script the forbidden shape.
- **The probe aborts a writable stream — never `close()` a probe stream.**
  Chromium writes to a swap file until `close()`, so an aborted stream leaves
  the original byte-identical (measured, halheinrich/backgammon#261); a
  `close()` would replace the file with the empty stream. Nor reach for
  `createWritable({ keepExistingData: true })` to catch locked files: it does
  catch them, but on the locked file it left a `.crswap` in the folder, and a
  probe that leaves a file behind is a write.
- **`Absent` is not `Writable`.** The probe never creates the file, so a
  missing one says nothing about whether it *could* be created — a caller that
  wants to know has only the folder permission (`FolderWriteCapability`) to go
  on.
- **The probe never prompts, and the `queryPermission` pre-check is why.**
  `createWritable()` on a handle whose write permission is still `'prompt'`
  shows the Edit-files prompt when the call carries user activation, so a
  host probing from a click handler would get a prompt instead of an answer.
  Don't drop the pre-check as redundant with the `NotAllowedError` entry —
  that entry is only the backstop for a grant revoked between the two calls.

## Subproject-internal next steps

- None. (The consuming legs — BgQuiz's migration onto this library and the
  Extract Web adoption, halheinrich/backgammon#80 — are umbrella-tracked
  arc work, not internal items.)
