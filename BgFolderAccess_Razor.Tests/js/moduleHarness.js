// moduleHarness.js — the test-side wiring that lets the xUnit suite drive the
// RCL's real folderAccess.js (imported below, unmodified) inside Jint.
// FolderAccessModuleHost registers both files as ES modules and calls the two
// entry points here; nothing in the library knows this file exists.
//
// The boundary is deliberately FLAT: names in as JSON, results out as JSON. A
// JS engine's object graph is awkward to assert against from C#, and the caps
// table has to arrive as a plain object with ordered string keys (which is
// exactly what JSON.parse gives) — the same shape FolderPickLimits.MaxFileCounts
// serializes to on the real interop wire.
//
// Every fake here is duck-typed to precisely what the module touches. No
// browser is involved and none is needed: the count-cap rule reads names, and
// the sizes and handles it does touch are stand-ins.
import { beginPick, enumeratePicked, collectFallbackFiles } from 'folderAccess';

// getFile() call count — the cost property enumeratePicked's two-phase draw
// exists to preserve (one stat per file TAKEN, not per file walked past).
let stats = 0;

// FileSystemDirectoryHandle stand-in over `names`, in that order. A directory
// entry rides along in every corpus so the walk's non-file skip stays exercised.
function directoryHandle(names) {
    const entries = names.map(name => ({
        kind: 'file',
        name,
        getFile: () => {
            stats++;
            return { size: name.length };
        },
    }));
    entries.push({ kind: 'directory', name: 'nested' });
    return {
        name: 'corpus',
        requestPermission: () => 'granted',
        values: () => entries[Symbol.iterator](),
    };
}

// The File System Access mechanism, both halves: beginPick() binds the picked
// slot, enumeratePicked() walks, draws, and stats only what it drew.
export async function enumerateJson(namesJson, limitsJson) {
    stats = 0;
    globalThis.window = { showDirectoryPicker: () => directoryHandle(JSON.parse(namesJson)) };
    await beginPick();
    const result = await enumeratePicked(JSON.parse(limitsJson));
    return JSON.stringify({
        files: result.files.map(f => f.name),
        omitted: result.omitted,
        stats,
    });
}

// The fallback mechanism: the browser hands over the whole tree at once, so
// each name gets the "corpus/<name>" path a direct child of the picked folder
// carries. No stat count — the FileList is already in hand on this path.
export function collectFallbackJson(namesJson, limitsJson) {
    const files = JSON.parse(namesJson).map(name => ({
        name,
        size: name.length,
        webkitRelativePath: `corpus/${name}`,
    }));
    const result = collectFallbackFiles({ files, value: 'stale' }, JSON.parse(limitsJson));
    return JSON.stringify({
        files: result.files.map(f => f.name),
        omitted: result.omitted,
    });
}
