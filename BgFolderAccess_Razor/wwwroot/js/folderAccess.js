// folderAccess.js — BgFolderAccess_Razor's folder module, shipped as a static
// web asset and imported by JsFolderAccess alone. A pick lands a folder in the
// context table below and answers its id; every later call names that id, and
// release(id) frees it. The browser's directory handles never cross interop:
// C# sees ids, names, sizes, bytes, text and a few status strings. Expected
// outcomes are values (a cancelled pick, a missing file, a file the browser
// won't write); only unexpected browser failures throw, and those surface as
// JSException on the C# side.

// One entry per landed folder, until release(id): the FileSystemDirectoryHandle
// of a File System Access pick, or the candidates a fallback collection
// retained — every top-level File the browser handed over, by name, as
// references and never bytes, because that mechanism hands the tree over once
// and cannot walk it again. Nothing else is kept between calls: a draw records
// nothing, and readFileData resolves each name against the folder again, so a
// draw or a buffering pass that fails leaves nothing behind to repair.
const contexts = new Map();
let nextContext = 1;

function land(handle, candidates) {
    const id = nextContext++;
    contexts.set(id, { handle, candidates });
    return id;
}

function contextOf(id) {
    const context = contexts.get(id);
    if (context === undefined) {
        throw new Error(`No folder is held under context ${id}.`);
    }
    return context;
}

// The named-file operations need a handle; a fallback folder has none.
function handleOf(id) {
    const { handle } = contextOf(id);
    if (handle === null) {
        throw new Error('This folder has no File System Access handle to read or write files with.');
    }
    return handle;
}

// Which names are matching files, and how many of each kind a draw may take,
// arrive from C# as `limits` on every draw — the object JsFolderAccess writes
// from FolderPickLimits.MaxFileCounts, in the table's order. The module keeps
// no copy of either fact. The keys are disjoint suffixes (FolderPickLimits
// validates that), so the first match is the only match.
function matchingExtensionOf(name, limits) {
    const lower = name.toLowerCase();
    return Object.keys(limits).find(ext => lower.endsWith(ext)) ?? null;
}

// Per-extension admission in two phases: offer() keeps every matching item the
// walk finds, classified by name; draw() chooses each kind's admitted subset
// UNIFORMLY AT RANDOM and reports, for every kind of the table in the table's
// order, how many it found and how many it admitted. Random, not the first N:
// enumeration order is browser-supplied and stable, so a prefix rule put a
// corpus-scale folder's excess permanently out of reach of every re-pick
// (halheinrich/backgammon#106). Collect-then-draw rather than a reservoir,
// because a per-entry decision cannot be a uniform sample before it knows how
// many candidates are still coming. Admitted files keep encounter order across
// kinds, so a pick under every cap reads as one that never sampled. Count caps
// truncate and report; they never fail the pick.
function createCountSampler(limits) {
    const matching = [];
    const positions = new Map();
    return {
        offer(name, item) {
            const extension = matchingExtensionOf(name, limits);
            if (extension === null) return;
            if (!positions.has(extension)) {
                positions.set(extension, []);
            }
            positions.get(extension).push(matching.length);
            matching.push(item);
        },
        draw() {
            const drawn = new Set();
            const kinds = [];
            for (const extension of Object.keys(limits)) {
                const found = positions.get(extension) ?? [];
                const taken = sample(found, limits[extension]);
                for (const index of taken) drawn.add(index);
                kinds.push({ extension, foundCount: found.length, admittedCount: taken.length });
            }
            return { admitted: matching.filter((_, i) => drawn.has(i)), kinds };
        },
    };
}

// `count` of `indices` uniformly at random — all of them, untouched, when there
// are no more than `count`; otherwise a partial Fisher-Yates over a copy.
function sample(indices, count) {
    if (indices.length <= count) {
        return indices;
    }
    const pool = [...indices];
    for (let i = 0; i < count; i++) {
        const j = i + Math.floor(Math.random() * (pool.length - i));
        [pool[i], pool[j]] = [pool[j], pool[i]];
    }
    return pool.slice(0, count);
}

export function supportsDirectoryPicker() {
    return typeof window.showDirectoryPicker === 'function';
}

// File System Access pick, part one of two: the native picker (read mode), then
// a readwrite permission request on the picked handle, then the landing. Split
// from the draw so the caller gets control back exactly when the prompts end
// (JsFolderAccess's onPickAccepted hook) and a busy affordance can be truthful.
//
// Two prompts, deliberately. showDirectoryPicker({ mode: 'readwrite' }) was
// tried and reverted 2026-07-24: declining that single prompt aborts the WHOLE
// pick, which destroys the read-only PermissionDenied rung (decline write, keep
// reading). Declining the readwrite request here resolves 'denied' and the
// handle stays readable. The browser refusing to ask lands on that same rung:
// requestPermission requires transient user activation and throws SecurityError
// without it, which Chrome for Android does on every pick
// (halheinrich/backgammon#109); uncaught, that throw destroyed the pick and the
// read grant with it. Any other failure of the request still fails the pick.
export async function beginPick() {
    let handle;
    try {
        handle = await window.showDirectoryPicker();
    } catch (e) {
        if (e instanceof DOMException && e.name === 'AbortError') {
            return { status: 'cancelled', directoryName: '', writable: false, context: 0 };
        }
        throw e;
    }

    let writable = false;
    try {
        writable = await handle.requestPermission({ mode: 'readwrite' }) === 'granted';
    } catch (e) {
        if (!(e instanceof DOMException && e.name === 'SecurityError')) {
            throw e;
        }
    }

    return { status: 'ok', directoryName: handle.name, writable, context: land(handle, null) };
}

// Fallback gesture: open the hidden webkitdirectory input's native picker. The
// pick arrives through the input's change event (collectFallbackFiles); a
// dismissal fires nothing.
export function clickElement(element) {
    element.click();
}

// Fallback collection: land the folder the browser handed over and answer its
// name and context. The browser hands over the whole tree; only top-level files
// are kept — webkitRelativePath is "folder/file.ext" for direct children (one
// separator) — every one of them, whatever its kind, since which names match is
// decided by each draw against the table it is handed. The input is reset so
// the same folder can be picked again: a change event fires only when the
// selection differs. An empty FileList lands an empty folder.
export function collectFallbackFiles(inputElement) {
    const all = Array.from(inputElement.files ?? []);
    const topLevel = all.filter(f => (f.webkitRelativePath.match(/\//g) ?? []).length === 1);
    const directoryName = all.length > 0 ? all[0].webkitRelativePath.split('/')[0] : '';
    const context = land(null, new Map(topLevel.map(f => [f.name, f])));
    inputElement.value = '';
    return { directoryName, context };
}

// The draw: the one implementation behind a pick's sample and every resample.
// A File System Access folder is walked afresh (top level only), so a resample
// sees files added or removed since the pick; a walk the browser refuses
// rejects, and nothing here asks for permission. A fallback folder's candidates
// are redrawn as collected. The walk classifies by NAME alone and getFile()
// runs only over the drawn files, so a ten-thousand-file folder pays one stat
// per file the draw takes — which is why the draw sits between the two loops.
// The reply carries names and sizes; nothing is recorded.
export async function draw(context, limits) {
    const { handle, candidates } = contextOf(context);
    const sampler = createCountSampler(limits);
    let sizeOf;
    if (handle !== null) {
        for await (const entry of handle.values()) {
            if (entry.kind === 'file') sampler.offer(entry.name, entry);
        }
        sizeOf = async entry => (await entry.getFile()).size;
    } else {
        for (const file of candidates.values()) sampler.offer(file.name, file);
        sizeOf = file => file.size;
    }

    const { admitted, kinds } = sampler.draw();
    const files = [];
    for (const entry of admitted) {
        files.push({ name: entry.name, size: await sizeOf(entry) });
    }
    return { files, kinds };
}

// Byte read of one drawn file, by the name the draw returned, resolved against
// the folder at read time: the handle's own lookup, or the retained candidate.
// A file gone since the draw rejects, which fails the buffering pass as a whole
// on the C# side. The .NET side receives the ArrayBuffer as IJSStreamReference.
export async function readFileData(context, name) {
    const { handle, candidates } = contextOf(context);
    let file;
    if (handle !== null) {
        file = await (await handle.getFileHandle(name)).getFile();
    } else {
        file = candidates.get(name);
        if (file === undefined) {
            throw new Error(`No picked file named '${name}'.`);
        }
    }
    return await file.arrayBuffer();
}

// Text read of a named file; null when it does not exist yet, which is an
// expected absence and not an error.
export async function readFile(context, fileName) {
    const handle = handleOf(context);
    let fileHandle;
    try {
        fileHandle = await handle.getFileHandle(fileName);
    } catch (e) {
        if (e instanceof DOMException && e.name === 'NotFoundError') {
            return null;
        }
        throw e;
    }
    const file = await fileHandle.getFile();
    return await file.text();
}

// Text write of a named file, replacing any existing content.
export async function writeFile(context, fileName, text) {
    const fileHandle = await handleOf(context).getFileHandle(fileName, { create: true });
    const stream = await fileHandle.createWritable();
    await stream.write(text);
    await stream.close();
}

// The createWritable() rejections that mean "this file cannot be written", and
// the probe's only list of them: a read-only file rejects with
// NoModificationAllowedError (measured in Chrome 153, halheinrich/backgammon#261);
// NotAllowedError is permission absent, the backstop behind the pre-check
// below for a grant revoked between the two calls. Anything else propagates.
const NOT_WRITABLE_REJECTIONS = ['NoModificationAllowedError', 'NotAllowedError'];

// Writability probe of a named file: 'absent' | 'writable' | 'notWritable'.
// Absent creates nothing — whether a file COULD be created is the folder's
// write grant, which the caller has from the pick. The queryPermission
// pre-check is what keeps the probe from prompting: createWritable() on a
// handle whose permission is still 'prompt' shows the Edit-files prompt when
// the call carries user activation. ABORT, NEVER CLOSE: Chromium writes to a
// swap file until close(), and abort() leaves the original byte-identical; a
// close() would replace the file with the empty stream. A file another
// program holds open answers 'writable' and fails at the first real write —
// { keepExistingData: true } would catch it but left a .crswap behind.
export async function probeFileWritability(context, fileName) {
    const handle = handleOf(context);
    let fileHandle;
    try {
        fileHandle = await handle.getFileHandle(fileName);
    } catch (e) {
        if (e instanceof DOMException && e.name === 'NotFoundError') {
            return 'absent';
        }
        throw e;
    }
    if (await fileHandle.queryPermission({ mode: 'readwrite' }) !== 'granted') {
        return 'notWritable';
    }
    let stream;
    try {
        stream = await fileHandle.createWritable();
    } catch (e) {
        if (e instanceof DOMException && NOT_WRITABLE_REJECTIONS.includes(e.name)) {
            return 'notWritable';
        }
        throw e;
    }
    await stream.abort();
    return 'writable';
}

// Release a landed folder: its handle, or its retained candidates. Releasing
// an id that is not held does nothing.
export function release(context) {
    contexts.delete(context);
}
