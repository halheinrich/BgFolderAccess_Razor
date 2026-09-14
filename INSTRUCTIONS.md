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
`IFilterDocumentStorage` adapter over `IFolderAccess`'s picked-slot file I/O.)

## Directory tree

```
BgFolderAccess_Razor.slnx
Directory.Build.props
Directory.Packages.props
BgFolderAccess_Razor/
  BgFolderAccess_Razor.csproj
  FolderWriteCapability.cs   — pick-time write-capability taxonomy
  FolderPickLimits.cs        — host-supplied caps configuration (validated)
  PickedFile.cs              — one buffered matching file (name + bytes)
  PickTruncation.cs          — one kind's left-behind report
  FolderPickOutcome.cs       — the result of one pick gesture
  IFolderAccess.cs           — the host-facing interface
  JsFolderAccess.cs          — the JS-backed implementation (the only interop type)
  wwwroot/js/folderAccess.js — the ES module (static web asset)
BgFolderAccess_Razor.Tests/
  BgFolderAccess_Razor.Tests.csproj
  FolderPickLimitsTests.cs   — ctor validation matrix, order, derived figures
  FolderPickOutcomeTests.cs  — cancelled-singleton + value-type contracts
  JsFolderAccessTests.cs     — the interop seam over bUnit's scripted module
  FolderAccessModuleHost.cs  — runs the shipped folderAccess.js in Jint
  FolderAccessSamplingTests.cs — the count-cap draw, as the module computes it
  js/moduleHarness.js        — test-side fakes the module is driven through
```

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
  readwrite permission request → top-level enumeration → full buffering.
  Yields a directory handle, so named-file read/write works.
- **`webkitdirectory` fallback** (everything else): a hidden `<input>`'s
  native picker; the browser hands over the whole tree, the module keeps only
  top-level matching files. No writable handle —
  `FolderWriteCapability.BrowserUnsupported` always.

Both land in the same `FolderPickOutcome`; the capability and truncation
contracts are identical across mechanisms (same module, same caps table).
`SupportsDirectoryPickerAsync` is probed per gesture, not at boot — capability
is a property of the moment.

### Two-slot state model (in the JS module, never crossing interop)

- **picked slot** — populated by a pick gesture; the directory handle
  (FS-Access) or null (fallback) plus a name → handle/File map for byte reads.
  Serves setup-time named documents (`ReadPickedFileAsync` /
  `WritePickedFileAsync`).
- **active slot** — bound only by `PromoteToActiveAsync()`; the folder a
  running session records through (`ReadActiveFileAsync` /
  `WriteActiveFileAsync`).

The split is the isolation guarantee: `ClearPickedAsync` and a re-pick touch
only the picked slot, so work bound to the active slot keeps its handle until
the next promotion re-binds. Handles live in JS module state; C# sees names,
sizes, bytes, and booleans.

### Caps: host-owned numbers, library-owned enforcement

`FolderPickLimits` (host-supplied, DI-registered, validated at construction)
carries the per-extension count-cap table and the per-file byte cap. The
library ships **no numbers** — each host's values encode its own cost model
(BgQuiz's `PickedFileLimits` stays host-side; Extract Web will bring its own).

- The **whole count table crosses the interop boundary on every enumeration**;
  the JS module derives both of its jobs from it — which names are matching
  files, and how many of each to take — and keeps no copy (SSOT).
- **Count caps truncate, never fail**, per extension independently, in the JS
  module (the only place an extension is known before transfer). What was left
  behind rides back as `FolderPickOutcome.Truncations`.
- **An over-limit kind's survivors are drawn uniformly at random**, not taken
  in enumeration order (umbrella issue halheinrich/backgammon#106). That order
  is browser-supplied and stable, so a prefix rule put a corpus-scale folder's
  excess permanently out of reach — the same files lost to every re-pick. The
  draw is the module's only behavior, deliberately **not** a `FolderPickLimits`
  knob: the table is host-owned *numbers*, and how a cap picks its survivors is
  library enforcement. Files still come back in folder order, and a pick under
  every cap is unchanged from a pick that never sampled.
- The **byte cap fails the whole pick** (`InvalidOperationException` from
  `JsFolderAccess`, checked against enumerated metadata before any transfer)
  and is re-asserted as `OpenReadStreamAsync(maxAllowedSize:)` on the actual
  bytes.
- Each `PickTruncation.MaxFileCount` is **derived** from the enforced
  `FolderPickLimits` instance at the one lib-side construction site
  (`JsFolderAccess.ToTruncations`) — never round-tripped across interop — so
  the figure a host's notice states is by construction the figure the pick
  enforced.

### Error contract

Expected outcomes are values, never exceptions: a pick that ended with no
folder is `FolderPickOutcome.Cancelled`, a write denial is
`FolderWriteCapability.PermissionDenied`, a missing named file is a `null`
read. Unexpected browser failures surface as `JSException` for hosts to catch
and degrade on. The one deliberate throw is the byte-cap
`InvalidOperationException` above.

### Test posture (honest scope)

The xUnit suite pins the C# side: `FolderPickLimits` validation, the outcome
value types, and `JsFolderAccess`'s mapping seam over bUnit's scripted module
interop (`SetupModule` — no real JS runs there).

It also **runs the shipped `folderAccess.js` itself**, in Jint, via
`FolderAccessModuleHost` — the real file, staged beside the test assembly by
the csproj so a rename breaks the build rather than a test. That covers the
module's *logic*: classification, the per-kind random draw and its left-behind
report, result ordering, and how many times a pick calls `getFile()`. It exists
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
wire DTOs are `internal` (test-only `InternalsVisibleTo`).

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
ValueTask<bool> PromoteToActiveAsync();                          // false = no-write signal
Task<string?> ReadPickedFileAsync(string fileName);              // null = absent, not error
Task WritePickedFileAsync(string fileName, string json);
Task<string?> ReadActiveFileAsync(string fileName);              // null = absent, not error
Task WriteActiveFileAsync(string fileName, string json);
ValueTask ClearPickedAsync();                                    // picked slot only
```

`onPickAccepted` is awaited after the browser's prompts settle and before the
enumeration/buffering — the host's one truthful place to raise a busy
affordance (see Pitfalls). File-name constants stay host-side; the slot file
I/O is name-parameterized by design.

### `FolderPickLimits`

```csharp
FolderPickLimits(IEnumerable<KeyValuePair<string, int>> maxFileCounts, long maxFileBytes);
IReadOnlyDictionary<string, int> MaxFileCounts { get; }  // insertion-ordered
long MaxFileBytes { get; }
long MaxFileMegabytes { get; }                           // derived, floored MiB
int MaxFileCountFor(string extension);
```

Ctor validates: non-empty table; lower-case dot-leading keys; no duplicates;
suffix-disjoint keys; positive counts; positive byte cap.

### Outcome types

```csharp
sealed record FolderPickOutcome(
    bool Cancelled, string DirectoryName, IReadOnlyList<PickedFile> Files,
    FolderWriteCapability Capability, IReadOnlyList<PickTruncation> Truncations)
{ static FolderPickOutcome CancelledOutcome { get; } }

sealed record PickedFile(string FileName, byte[] Bytes);           // name keeps its extension
sealed record PickTruncation(string Extension, int OmittedCount, int MaxFileCount);
enum FolderWriteCapability { Enabled, BrowserUnsupported, PermissionDenied }
```

Host tests fake `IFolderAccess` and construct outcomes directly — the records
are public and value-equal for exactly that.

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
  everything in `enumeratePicked` is the app working. The hook is awaited
  between them: raised any earlier, a busy affordance would lie over a modal
  prompt; raised any later, it could never paint, because the scan that
  follows never yields to the renderer on WebAssembly's single thread. The
  hook is not invoked for a cancelled pick (no work to report). The seam
  ordering is pinned by test
  (`PickFolder_RunsTheAcceptedHook_AfterThePromptsAndBeforeTheScan`).
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
- **Suffix-disjoint extension keys are a constructed fact now.** The JS
  classifier takes the first suffix match; `FolderPickLimits`'s ctor rejects
  a key that is a suffix of another key (e.g. `.gz` alongside `.tar.gz`).
  This was a comment-only assumption in BgQuiz's module — behavior-identical
  for its `.xg`/`.xgp` table. Don't weaken the validation; the classifier's
  correctness rides on it.
- **Count caps truncate; the byte cap throws — that asymmetry is
  deliberate.** A folder past a count cap still yields a useful partial pick
  plus a truncation report; an oversized file fails the whole pick before any
  transfer. Don't "unify" them either way: truncating on bytes would silently
  drop a file the user can see, and throwing on counts re-opens the
  lose-the-whole-pick failure the truncation contract closed.
- **`Truncations` is deliberately non-optional and `PickTruncation.MaxFileCount`
  is derived, never round-tripped.** Every outcome construction site states
  "nothing was left behind" explicitly (empty list), and the cap figure comes
  from the enforced `FolderPickLimits` instance at `ToTruncations` — so a
  host notice can never state a figure the pick didn't enforce.
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
- **The draw sits BETWEEN the walk and the stats — don't fold it back in.**
  `enumeratePicked` classifies and collects by name (free), draws each kind's
  survivors, and only then calls `getFile()`, so a ten-thousand-file folder
  still pays one stat per file it *takes*. A per-entry admission decision made
  on the way past cannot be a uniform draw — it does not yet know how many
  candidates are still coming — so re-fusing the two loops silently restores
  the first-N bug halheinrich/backgammon#106 closed.
  `Enumeration_StatsOnlyTheFilesItTakes` is what guards the cost half.
- **Only one sampling test tells random from first-N.** Every other invariant
  in `FolderAccessSamplingTests` (`|admitted| = min(cap, matching)`, subset,
  per-kind independence, encounter order, report order) holds under *both*
  rules — verified by running the suite against the module from before
  halheinrich/backgammon#106, where exactly
  `RepeatedPicks_ReachEveryFileOfAnOverLimitKind` fails. Don't delete it as
  "the flaky-looking one": its stated failure odds are ~1e-10, and without it
  the change is untested. Equally, don't replace it with a "two picks differ"
  assertion — that one really is flaky (1-in-501 on BgQuiz's `.xg` cap).
- **bUnit test trap: scripted setups for calls that carry the caps table need
  a matcher** (`Setup<T>("enumeratePicked", _ => true)`) — an argument-less
  exact setup never matches, and the resulting "no setup" failure looks like
  a bunit bug rather than what it is.

## Subproject-internal next steps

- None. (The consuming legs — BgQuiz's migration onto this library and the
  Extract Web adoption, halheinrich/backgammon#80 — are umbrella-tracked
  arc work, not internal items.)
