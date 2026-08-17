// folderAccess.js — BgFolderAccess_Razor's folder-picking + slot-file module,
// shipped as a static web asset (consumers import
// ./_content/BgFolderAccess_Razor/js/folderAccess.js). Loaded as an ES module
// by JsFolderAccess; nothing else imports it.
//
// Two-slot state model:
//   picked slot — populated by a pick gesture (either mechanism); holds the
//     FileSystemDirectoryHandle (FS-Access) or null (fallback), plus a
//     name → FileSystemFileHandle | File map for byte reads.
//   active slot — the directory handle promoted for a *running session's*
//     writes. Bound only by promoteToActive(), so a mid-session Clear or
//     re-pick (which only touches the picked slot) never affects work
//     recording through the active slot.
//
// Handles never cross the interop boundary — C# sees names, sizes, bytes, and
// booleans. Expected outcomes are result values (cancelled pick, missing named
// file, denied permission); only unexpected browser failures throw, surfacing
// as JSException on the C# side.

let pickedHandle = null;      // FileSystemDirectoryHandle | null (fallback picks have none)
let pickedFiles = new Map();  // name -> FileSystemFileHandle | File
let activeHandle = null;      // FileSystemDirectoryHandle | null — the running session's write folder

// Which names are matching files, and how many of each kind a pick may take,
// both arrive from C# as the `limits` argument on every enumeration: the object
// FolderPickLimits.MaxFileCounts serializes to, e.g. { '.xg': 500, '.xgp': 2000 }.
// The module deliberately keeps NO copy of either fact — a second list here is a
// second thing to update when the caps or the kinds change.
function matchingExtensionOf(name, limits) {
    const lower = name.toLowerCase();
    // The extensions are disjoint suffixes — FolderPickLimits validates that no
    // key is a suffix of another — so the first match is the only match.
    return Object.keys(limits).find(ext => lower.endsWith(ext)) ?? null;
}

// Per-extension admission against the caps table. admit() takes the first
// limits[ext] files of each kind in enumeration order and tallies the rest;
// left() reports only the kinds actually truncated, in the table's own order so
// a multi-line notice is deterministic.
//
// TRUNCATE, DON'T FAIL — and enforce it HERE. An over-limit folder used to lose
// the whole pick to a throw on the C# side (JsFolderAccess counted the
// enumerated metadata), including the files that were perfectly readable. The
// caps are a cost ceiling, not an admissions test, so the pick takes what it
// can and says what it left; downstream derives from the partial pool with no
// special-casing at all. The count check lives in this module because it is
// PER EXTENSION, and this is the only place a file's extension is known before
// anything is transferred.
//
// Each kind is capped INDEPENDENTLY, because file count is only a cost proxy
// within one kind (the host's table encodes its own per-kind cost model). A
// mixed folder can therefore admit its full quota of every kind.
function createCountLimiter(limits) {
    const taken = new Map();
    const skipped = new Map();
    return {
        admit(extension) {
            const soFar = taken.get(extension) ?? 0;
            if (soFar < limits[extension]) {
                taken.set(extension, soFar + 1);
                return true;
            }
            skipped.set(extension, (skipped.get(extension) ?? 0) + 1);
            return false;
        },
        left() {
            return Object.keys(limits)
                .filter(ext => skipped.has(ext))
                .map(ext => ({ extension: ext, omittedCount: skipped.get(ext) }));
        },
    };
}

export function supportsDirectoryPicker() {
    return typeof window.showDirectoryPicker === 'function';
}

// FS-Access pick, PART ONE of two: native picker (read mode), then a readwrite
// permission request on the picked handle. AbortError is the user dismissing
// the picker — an expected outcome, returned as { status: 'cancelled' }.
//
// SPLIT FROM THE ENUMERATION DELIBERATELY. Everything here is the browser
// asking the *user* something; everything in enumeratePicked() is the app
// working. Fused, the app could not tell the two apart, so it could not raise
// a busy affordance that was true — a progress cursor would have been up while
// a modal picker waited on the user, and absent during the scan that actually
// makes them wait. Split, the caller gets control back at exactly the moment
// the prompts end (JsFolderAccess.PickFolderAsync's onPickAccepted hook) and
// the affordance means one thing on both mechanisms: the app is processing a
// selection the user has made.
//
// The handle lands in the PICKED slot here, with pickedFiles left empty until
// enumeratePicked() fills it. That intermediate state is not observable outside
// the single C# call that spans both — no third slot is introduced for it.
//
// Declining the readwrite request is NOT an abort: requestPermission resolves
// 'denied', the picked handle stays READABLE, and the pick returns
// writable:false — the PermissionDenied rung, where the file list still loads
// and the host runs read-only.
//
// THE BROWSER REFUSING TO ASK LANDS ON THAT SAME RUNG, and getting there takes
// the catch below. requestPermission REQUIRES transient user activation and
// REJECTS when there is none — File System Access spec: "If global does not
// have transient activation, then throw a SecurityError DOMException"; the
// message Chromium puts on it is "User activation is required to request
// permissions." showDirectoryPicker performs the activation notification steps
// as it resolves, which is what leaves desktop Chrome with live activation on
// the line below. On Chrome for Android it is not live by the time this runs,
// so the request is refused on EVERY pick — deterministically, a second fresh
// gesture included (halheinrich/backgammon#109, Galaxy Tab S10 FE).
//
// This was previously described here as an "auto-deny" that degraded on its
// own. It does not auto-deny, it THROWS, and the throw was uncaught: it escaped
// beginPick as a JSException and destroyed the entire pick — including the read
// grant the user had ALREADY given, which is strictly worse than any degrade.
// Catching it is what makes the documented graceful degrade actually exist. A
// refusal to ask and a user's own "no" are deliberately not distinguished: both
// mean "no write grant", the picked folder stays readable in both, and no
// caller has a reason to act differently. Anything else the request throws is a
// real browser failure and still fails the pick loudly.
//
// DON'T collapse this into one prompt. showDirectoryPicker({ mode: 'readwrite' })
// looks like a free UX win — one prompt instead of two — and was tried and
// reverted on 2026-07-24 (in BgQuiz, this module's origin). Observed in real
// Chrome: declining that single readwrite prompt ABORTS THE WHOLE PICK. It
// surfaces as AbortError, which this function maps to 'cancelled', so the app
// returns to its initial no-folder state with no read handle at all. That
// destroys the read-only PermissionDenied rung above — a deliberate, valued
// degrade (decline write, keep reading). One fewer prompt is not worth
// silently losing it, so the two-prompt flow is retained deliberately. (The
// underlying concern — the second prompt being missed in a busy UI — is a
// host-copy problem: progressive disclosure and in-page "check your browser"
// guidance, not a prompt-collapse.)
export async function beginPick() {
    let handle;
    try {
        handle = await window.showDirectoryPicker();
    } catch (e) {
        if (e instanceof DOMException && e.name === 'AbortError') {
            return { status: 'cancelled', directoryName: '', writable: false };
        }
        throw e;
    }

    let writable = false;
    try {
        writable = await handle.requestPermission({ mode: 'readwrite' }) === 'granted';
    } catch (e) {
        // SecurityError is the refuse-to-ask above (no transient activation, or
        // a cross-origin frame) — read-only is the honest outcome for both.
        if (!(e instanceof DOMException && e.name === 'SecurityError')) {
            throw e;
        }
    }

    pickedHandle = handle;
    pickedFiles = new Map();  // filled by enumeratePicked
    return { status: 'ok', directoryName: handle.name, writable };
}

// FS-Access pick, PART TWO: the app's own work — top-level enumeration only
// (user-settled): values() yields direct children; subdirectories are not
// descended into. Names keep their extension — the extension-bearing-name
// contract (PickedFile.FileName). Called once, immediately after a
// non-cancelled beginPick(); the caller's busy affordance is already up and
// painted by the time this runs.
//
// `limits` is FolderPickLimits.MaxFileCounts (see matchingExtensionOf). The
// admission check runs BEFORE getFile(), so a folder holding ten thousand files
// pays one stat per file it actually takes, not per file it walks past.
export async function enumeratePicked(limits) {
    if (pickedHandle === null) {
        throw new Error('No picked folder to enumerate.');
    }

    const limiter = createCountLimiter(limits);
    const files = [];
    const map = new Map();
    for await (const entry of pickedHandle.values()) {
        if (entry.kind !== 'file') continue;
        const extension = matchingExtensionOf(entry.name, limits);
        if (extension === null || !limiter.admit(extension)) continue;
        const file = await entry.getFile();
        map.set(entry.name, entry);
        files.push({ name: entry.name, size: file.size });
    }

    // Only the admitted files land in the picked slot: readFileData serves the
    // buffering pass, and what was left behind is not readable by this pick.
    pickedFiles = map;
    return { files, omitted: limiter.left() };
}

// Fallback gesture: open the hidden webkitdirectory input's native picker.
// The eventual pick arrives via the input's own change event (collected with
// collectFallbackFiles); a dismissal fires nothing.
export function clickElement(element) {
    element.click();
}

// Fallback collection: the browser hands over the folder's whole tree; keep
// only top-level matching files — webkitRelativePath is "folder/file.ext" for
// direct children (exactly one separator). Blazor's InputFile can't see
// webkitRelativePath, which is why this module reads the FileList itself.
export function collectFallbackFiles(inputElement, limits) {
    const all = Array.from(inputElement.files ?? []);
    const limiter = createCountLimiter(limits);
    const topLevel = [];
    for (const f of all) {
        if ((f.webkitRelativePath.match(/\//g) ?? []).length !== 1) continue;
        const extension = matchingExtensionOf(f.name, limits);
        if (extension === null || !limiter.admit(extension)) continue;
        topLevel.push(f);
    }

    const directoryName = all.length > 0 ? all[0].webkitRelativePath.split('/')[0] : '';

    pickedHandle = null;  // no writable handle on this mechanism
    pickedFiles = new Map(topLevel.map(f => [f.name, f]));
    // Allow the same folder to be re-picked later: a change event only fires
    // when the selection differs, so reset the input now that it's collected.
    inputElement.value = '';
    return {
        directoryName,
        files: topLevel.map(f => ({ name: f.name, size: f.size })),
        omitted: limiter.left(),
    };
}

// Byte read from the picked slot's enumerated files. Returns the raw
// ArrayBuffer — the .NET side receives it as IJSStreamReference and enforces
// its own size cap when opening the stream.
export async function readFileData(name) {
    const entry = pickedFiles.get(name);
    if (entry === undefined) {
        throw new Error(`No picked file named '${name}'.`);
    }
    const file = entry instanceof File ? entry : await entry.getFile();
    return await file.arrayBuffer();
}

// Text read of a named file from the PICKED slot — the setup-time document
// surface, read on the folder being configured before any promotion binds the
// active slot. null = the file doesn't exist yet (an expected absence, not an
// error), mirroring readActiveFile's missing-file contract. Anything else
// unexpected throws. Distinct from readFileData (which returns an enumerated
// file's bytes as an ArrayBuffer); this is small UTF-8 text returned as text.
export async function readPickedFile(fileName) {
    if (pickedHandle === null) {
        throw new Error('No picked folder to read from.');
    }
    let fileHandle;
    try {
        fileHandle = await pickedHandle.getFileHandle(fileName);
    } catch (e) {
        if (e instanceof DOMException && e.name === 'NotFoundError') {
            return null;
        }
        throw e;
    }
    const file = await fileHandle.getFile();
    return await file.text();
}

// Text write of a named file into the PICKED slot, replacing any existing
// content. Deliberately on the picked slot, never the active one: setup-time
// documents belong to the folder the user is configuring, isolated from
// whatever a running session records through the active slot.
export async function writePickedFile(fileName, text) {
    if (pickedHandle === null) {
        throw new Error('No picked folder to write to.');
    }
    const fileHandle = await pickedHandle.getFileHandle(fileName, { create: true });
    const stream = await fileHandle.createWritable();
    await stream.write(text);
    await stream.close();
}

// Begin-work bind: promote the picked slot's handle to the active slot.
// False = no FS-Access handle picked (fallback pick, cleared, or never
// picked) — the caller's no-write signal.
export function promoteToActive() {
    activeHandle = pickedHandle;
    return activeHandle !== null;
}

// Text read of a named file via the ACTIVE slot. null = the file doesn't exist
// yet (a fresh folder — not an error). Anything else unexpected throws.
export async function readActiveFile(fileName) {
    if (activeHandle === null) {
        throw new Error('No active folder to read from.');
    }
    let fileHandle;
    try {
        fileHandle = await activeHandle.getFileHandle(fileName);
    } catch (e) {
        if (e instanceof DOMException && e.name === 'NotFoundError') {
            return null;
        }
        throw e;
    }
    const file = await fileHandle.getFile();
    return await file.text();
}

// Text write of a named file via the ACTIVE slot, replacing any existing content.
export async function writeActiveFile(fileName, text) {
    if (activeHandle === null) {
        throw new Error('No active folder to write to.');
    }
    const fileHandle = await activeHandle.getFileHandle(fileName, { create: true });
    const stream = await fileHandle.createWritable();
    await stream.write(text);
    await stream.close();
}

// Clear affordance: reset the PICKED slot only. The active slot persists so
// work bound to it keeps recording until the next promotion re-binds it.
export function clearPicked() {
    pickedHandle = null;
    pickedFiles = new Map();
}
