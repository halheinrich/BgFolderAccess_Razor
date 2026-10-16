// moduleHarness.js — the test-side wiring that lets the xUnit suite drive the
// RCL's real folderAccess.js (imported below, unmodified) inside Jint.
// FolderAccessModuleHost registers both files as ES modules and calls the entry
// points here; nothing in the library knows this file exists.
//
// The boundary is deliberately FLAT: names in as JSON, results out as JSON or
// primitives. The caps table arrives as the JSON text of the very object
// JsFolderAccess.ToWireCaps writes for the real interop wire.
//
// STATEFUL, ONE FOLDER PER ENGINE. The module keeps its context table between
// calls, and the fake folder below lives as long as the engine — one test's.
// Each pick lands a context; the harness remembers the latest as `current`,
// and the ...Of entry points name one explicitly, so a test can hold two.
//
// Every fake here is duck-typed to precisely what the module touches. No
// browser is involved and none is needed.
import {
    beginPick, collectFallbackFiles, draw, readFileData, release,
    readFile, writeFile, probeFileWritability,
} from 'folderAccess';

// The module tells expected browser answers apart by DOMException name
// (AbortError, NotFoundError); Jint has no DOMException, so the fakes throw
// this stand-in under the global name the module checks against.
class FakeDomException extends Error {
    constructor(message, name) {
        super(message);
        this.name = name;
    }
}
globalThis.DOMException = FakeDomException;

// The folder on "disk": file name -> text, in walk order. A pick fills it with
// the corpus (each file's text is its own name); documents are written into it
// like any other file.
const disk = new Map();
let refusesWalk = false;     // the next walks reject, as a withdrawn permission does
let failsReadOf = null;      // this file's bytes fail to read, after it is drawn
let mechanism = null;        // 'FileSystemAccess' | 'Fallback' — the last pick's
let current = 0;             // the context the last pick landed

// Browser calls the module must never make outside a pick, counted.
let stats = 0;               // getFile() — the FS-Access cost property
let pickerOpenings = 0;      // showDirectoryPicker()
let permissionRequests = 0;  // requestPermission()

function fileOf(name) {
    return {
        name,
        size: name.length,
        webkitRelativePath: `corpus/${name}`,
        arrayBuffer: async () => {
            if (name === failsReadOf) {
                throw new DOMException(`'${name}' could not be read.`, 'NotReadableError');
            }
            return new ArrayBuffer(name.length);
        },
        text: async () => disk.get(name),
    };
}

function fileHandle(name) {
    return {
        kind: 'file',
        name,
        getFile: async () => {
            stats++;
            return fileOf(name);
        },
        queryPermission: async () => 'granted',
        createWritable: async () => {
            let written = '';
            return {
                write: async text => { written += text; },
                close: async () => { disk.set(name, written); },
                abort: async () => {},
            };
        },
    };
}

// FileSystemDirectoryHandle stand-in over the disk, walked as it is at the
// moment of the walk. A directory entry rides along so the walk's non-file
// skip stays exercised.
const directory = {
    name: 'corpus',
    requestPermission: async () => {
        permissionRequests++;
        return 'granted';
    },
    values: () => {
        if (refusesWalk) {
            return {
                [Symbol.iterator]: () => ({
                    next: () => { throw new DOMException('The folder can no longer be read.', 'NotAllowedError'); },
                }),
            };
        }
        return [...[...disk.keys()].map(fileHandle), { kind: 'directory', name: 'nested' }][Symbol.iterator]();
    },
    getFileHandle: async (name, options) => {
        if (!disk.has(name)) {
            if (!options?.create) {
                throw new DOMException(`'${name}' was not found.`, 'NotFoundError');
            }
            disk.set(name, '');
        }
        return fileHandle(name);
    },
};

globalThis.window = {
    showDirectoryPicker: async () => {
        pickerOpenings++;
        return directory;
    },
};

function report(result) {
    return JSON.stringify({
        files: result.files.map(f => f.name),
        kinds: result.kinds,
        // No stat count on the fallback: its Files carry their size, so there is
        // nothing to defer there.
        stats: mechanism === 'FileSystemAccess' ? stats : null,
    });
}

// Pick a folder holding `names`, by either mechanism, and draw it — beginPick
// or collectFallbackFiles, then draw, as JsFolderAccess sequences them. The
// landed context becomes the current one.
export async function pickJson(mechanismName, namesJson, limitsJson) {
    mechanism = mechanismName;
    disk.clear();
    for (const name of JSON.parse(namesJson)) disk.set(name, name);
    if (mechanism === 'FileSystemAccess') {
        current = (await beginPick()).context;
    } else {
        // The browser hands over the folder's tree once, as Files; each gets
        // the "corpus/<name>" path a direct child of the picked folder carries.
        current = collectFallbackFiles({ files: [...disk.keys()].map(fileOf), value: 'stale' }).context;
    }
    return drawJson(limitsJson);
}

// A cancelled File System Access pick: the picker rejects with AbortError.
export async function cancelPick() {
    const picker = globalThis.window.showDirectoryPicker;
    globalThis.window.showDirectoryPicker = async () => {
        throw new DOMException('The user aborted a request.', 'AbortError');
    };
    try {
        return (await beginPick()).status;
    } finally {
        globalThis.window.showDirectoryPicker = picker;
    }
}

export function contextId() { return current; }

// One draw of a landed folder — a resample's module call.
export async function drawOfJson(context, limitsJson) {
    stats = 0;
    return report(await draw(context, JSON.parse(limitsJson)));
}

export async function drawJson(limitsJson) { return drawOfJson(current, limitsJson); }

// A whole resample as JsFolderContext runs it: the draw, then each drawn file's
// bytes in turn. Rejects as soon as any read does, as the C# loop throws.
export async function bufferJson(limitsJson) {
    const result = await draw(current, JSON.parse(limitsJson));
    const files = [];
    for (const file of result.files) {
        const bytes = await readFileData(current, file.name);
        files.push(`${file.name}:${bytes.byteLength}`);
    }
    return JSON.stringify(files);
}

// The folder changing, and the browser misbehaving, between calls.
export function addFile(name) { disk.set(name, name); }
export function refuseWalk(refuses) { refusesWalk = refuses; }
export function failReadOf(name) { failsReadOf = name; }
export function browserCallsJson() { return JSON.stringify({ pickerOpenings, permissionRequests }); }

// The module's other context operations, passed straight through.
export async function readBytes(name) { return (await readFileData(current, name)).byteLength; }
export function releaseOf(context) { release(context); }
export async function readDocOf(context, name) { return await readFile(context, name); }
export async function writeDocOf(context, name, text) { await writeFile(context, name, text); }
export async function probeDocOf(context, name) { return await probeFileWritability(context, name); }
