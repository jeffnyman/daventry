// The browser side of Daventry.Host.Browser: one canvas, one audio context, the input
// listeners and the animation frame loop, plus the game files (a picked folder or the games kept
// in IndexedDB) and downloads. The .NET side calls these through [JSImport] and hands over three
// callbacks in start(); apart from the files of the folder being read, nothing here keeps state
// the host does not know about.

let canvas = null;
let context = null;
let image = null;
let onFrame = null;
let onKey = null;
let onMouse = null;
let animationFrame = 0;
let listeners = [];

let audio = null;
let audioNext = 0;
let audioRate = 0;

// visible from the console for diagnosing sound problems: window.daventryAudio
const audioStats = { state: "none", rate: 0, pushed: 0, audible: 0, peak: 0, underruns: 0 };
globalThis.daventryAudio = audioStats;

const modifierFlags = (e) =>
    (e.ctrlKey ? 1 : 0) | (e.altKey ? 2 : 0) | (e.shiftKey ? 4 : 0) | (e.metaKey ? 8 : 0) |
    (e.repeat ? 16 : 0) | (e.getModifierState("CapsLock") ? 32 : 0) | (e.getModifierState("NumLock") ? 64 : 0);

function framebufferPoint(e) {
    const r = canvas.getBoundingClientRect();
    const x = Math.floor((e.clientX - r.left) * canvas.width / r.width);
    const y = Math.floor((e.clientY - r.top) * canvas.height / r.height);
    return [Math.max(0, Math.min(canvas.width - 1, x)), Math.max(0, Math.min(canvas.height - 1, y))];
}

function listen(target, type, handler, options) {
    target.addEventListener(type, handler, options);
    listeners.push([target, type, handler, options]);
}

function frame(timestamp) {
    animationFrame = requestAnimationFrame(frame);
    onFrame(timestamp);
}

export function start(canvasId, frameCallback, keyCallback, mouseCallback) {
    stop();
    canvas = document.getElementById(canvasId);
    if (!canvas) {
        throw new Error(`No canvas with id ${canvasId}`);
    }

    context = canvas.getContext("2d", { alpha: false });
    context.imageSmoothingEnabled = false;
    onFrame = frameCallback;
    onKey = keyCallback;
    onMouse = mouseCallback;

    listen(canvas, "keydown", (e) => {
        resumeAudio();
        onKey(true, e.key + "\n" + e.code, modifierFlags(e));
        e.preventDefault();
    });
    listen(canvas, "keyup", (e) => {
        onKey(false, e.key + "\n" + e.code, modifierFlags(e));
        e.preventDefault();
    });
    listen(canvas, "mousemove", (e) => {
        const [x, y] = framebufferPoint(e);
        onMouse(0, x, y);
    });
    listen(canvas, "mousedown", (e) => {
        resumeAudio();
        canvas.focus();
        const [x, y] = framebufferPoint(e);
        onMouse(1 + e.button * 2, x, y);
        e.preventDefault();
    });
    listen(canvas, "mouseup", (e) => {
        const [x, y] = framebufferPoint(e);
        onMouse(2 + e.button * 2, x, y);
        e.preventDefault();
    });
    listen(canvas, "contextmenu", (e) => e.preventDefault());

    animationFrame = requestAnimationFrame(frame);
}

export function stop() {
    if (animationFrame) {
        cancelAnimationFrame(animationFrame);
        animationFrame = 0;
    }

    for (const [target, type, handler, options] of listeners) {
        target.removeEventListener(type, handler, options);
    }

    listeners = [];
    audioStop();
    canvas = null;
    context = null;
    image = null;
}

export function setMode(width, height, aspectRatioCorrection) {
    canvas.width = width;
    canvas.height = height;
    canvas.style.aspectRatio = aspectRatioCorrection ? "4 / 3" : `${width} / ${height}`;
    context = canvas.getContext("2d", { alpha: false });
    context.imageSmoothingEnabled = false;
    image = context.createImageData(width, height);
}

export function blit(width, height, rgba) {
    if (!image || image.width !== width || image.height !== height) {
        setMode(width, height, canvas.style.aspectRatio === "4 / 3");
    }

    // the view points into the WebAssembly heap and is only valid during this call
    image.data.set(rgba.slice());
    context.putImageData(image, 0, 0);
}

export function setTitle(title) {
    document.title = title;
}

export function focus() {
    canvas?.focus();
}

export function toggleFullscreen() {
    if (document.fullscreenElement) {
        document.exitFullscreen();
    } else if (canvas) {
        canvas.requestFullscreen();
    }
}

function resumeAudio() {
    if (audio && audio.state === "suspended") {
        audio.resume();
    }
}

export function audioStart(sampleRate) {
    audioStop();
    audio = new AudioContext({ sampleRate });
    audioRate = audio.sampleRate;
    audioNext = 0;
    audioStats.rate = audioRate;
    audioStats.pushed = 0;
    audioStats.audible = 0;
    audioStats.peak = 0;
    audioStats.underruns = 0;
    audio.onstatechange = () => (audioStats.state = audio.state);
    audioStats.state = audio.state;
    resumeAudio();
}

export function audioStop() {
    if (audio) {
        audio.close();
        audio = null;
    }
}

export function audioQueued() {
    if (!audio) {
        return 0;
    }

    return Math.max(0, audioNext - audio.currentTime);
}

export function audioPush(pcm, frames) {
    if (!audio) {
        return;
    }

    const bytes = pcm.slice();
    const samples = new Int16Array(bytes.buffer, bytes.byteOffset, frames * 2);
    const buffer = audio.createBuffer(2, frames, audioRate);
    const left = buffer.getChannelData(0);
    const right = buffer.getChannelData(1);
    let peak = 0;
    for (let i = 0; i < frames; i++) {
        left[i] = samples[i * 2] / 32768;
        right[i] = samples[i * 2 + 1] / 32768;
        peak = Math.max(peak, Math.abs(samples[i * 2]), Math.abs(samples[i * 2 + 1]));
    }

    audioStats.pushed++;
    if (peak > 0) {
        audioStats.audible++;
    }

    audioStats.peak = Math.max(audioStats.peak, peak);

    const source = audio.createBufferSource();
    source.buffer = buffer;
    source.connect(audio.destination);

    // a small lead keeps the first chunk from starting in the past after a stall
    const now = audio.currentTime;
    if (audioNext < now + 0.005) {
        if (audioNext > 0) {
            audioStats.underruns++;
        }

        audioNext = now + 0.02;
    }

    source.start(audioNext);
    audioNext += frames / audioRate;
}

export function storageGet(key) {
    try {
        return localStorage.getItem(key);
    } catch {
        return null;
    }
}

export function storageSet(key, value) {
    localStorage.setItem(key, value);
}

export function storageRemove(key) {
    try {
        localStorage.removeItem(key);
    } catch {
    }
}

export function storageKeys(prefix) {
    const keys = [];
    try {
        for (let i = 0; i < localStorage.length; i++) {
            const key = localStorage.key(i);
            if (key.startsWith(prefix)) {
                keys.push(key);
            }
        }
    } catch {
    }

    return keys.join("\n");
}

// ---- game files: a picked folder, or a game kept in the library ----
// The files of the last folder read stay here as Blobs; .NET loads them one at a time with
// sourceLoad and copies each into its own memory with sourceCopy.

let source = [];
let loaded = null;

function describeSource(folder) {
    return JSON.stringify({ folder, files: source.map((e) => ({ path: e.path, size: e.blob.size })) });
}

// webkitRelativePath starts with the name of the folder that was picked; the paths handed back
// are relative to it, with forward slashes
export function pickedFiles(inputId) {
    const input = document.getElementById(inputId);
    const files = Array.from(input?.files ?? []);
    let folder = "";
    source = files.map((file) => {
        const relative = file.webkitRelativePath || file.name;
        const slash = relative.indexOf("/");
        if (slash >= 0 && !folder) {
            folder = relative.slice(0, slash);
        }

        return { path: slash >= 0 ? relative.slice(slash + 1) : relative, blob: file };
    });

    // let the same folder be picked again
    if (input) {
        input.value = "";
    }

    return describeSource(folder);
}

export async function sourceLoad(index) {
    loaded = new Uint8Array(await source[index].blob.arrayBuffer());
    return loaded.length;
}

export function sourceCopy(destination) {
    destination.set(loaded);
    loaded = null;
}

// The library: games kept in IndexedDB, a record per game and a Blob per file

const libraryName = "daventry-library";

function openLibrary() {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(libraryName, 2);
        request.onupgradeneeded = (e) => {
            const db = request.result;
            if (e.oldVersion < 1) {
                db.createObjectStore("games", { keyPath: "id" });
                db.createObjectStore("files");
            }

            // version 2: single files the page keeps, such as the SoundFont, as { name, blob }
            if (e.oldVersion < 2) {
                db.createObjectStore("assets");
            }
        };
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });
}

const completed = (tx) => new Promise((resolve, reject) => {
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
    tx.onabort = () => reject(tx.error ?? new Error("The transaction was aborted"));
});

const result = (request) => new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
});

const fileKey = (id, path) => id + "\n" + path;
const fileRange = (id) => IDBKeyRange.bound(id + "\n", id + "\n￿");

export async function libraryList() {
    const db = await openLibrary();
    try {
        return JSON.stringify(await result(db.transaction("games").objectStore("games").getAll()));
    } finally {
        db.close();
    }
}

// stores the record and the files of the last folder read, replacing a game with the same id
export async function libraryStore(recordJson) {
    const record = JSON.parse(recordJson);
    const db = await openLibrary();
    try {
        const tx = db.transaction(["games", "files"], "readwrite");
        const files = tx.objectStore("files");
        files.delete(fileRange(record.id));
        for (const entry of source) {
            files.put(entry.blob, fileKey(record.id, entry.path));
        }

        tx.objectStore("games").put(record);
        await completed(tx);
    } finally {
        db.close();
    }
}

export async function libraryUpdate(recordJson) {
    const db = await openLibrary();
    try {
        const tx = db.transaction("games", "readwrite");
        tx.objectStore("games").put(JSON.parse(recordJson));
        await completed(tx);
    } finally {
        db.close();
    }
}

// makes a kept game's files the current source
export async function libraryOpen(id) {
    const db = await openLibrary();
    try {
        const store = db.transaction("files").objectStore("files");
        const [keys, blobs] = await Promise.all([result(store.getAllKeys(fileRange(id))), result(store.getAll(fileRange(id)))]);
        source = keys.map((key, i) => ({ path: key.slice(id.length + 1), blob: blobs[i] }));
        return describeSource("");
    } finally {
        db.close();
    }
}

export async function libraryDelete(id) {
    const db = await openLibrary();
    try {
        const tx = db.transaction(["games", "files"], "readwrite");
        tx.objectStore("files").delete(fileRange(id));
        tx.objectStore("games").delete(id);
        await completed(tx);
    } finally {
        db.close();
    }
}

// keeps the first file of a file input under a key; resolves to its name and size as JSON
export async function assetKeep(key, inputId) {
    const input = document.getElementById(inputId);
    const file = input?.files?.[0];
    if (!file) {
        return "";
    }

    if (input) {
        input.value = "";
    }

    const db = await openLibrary();
    try {
        const tx = db.transaction("assets", "readwrite");
        tx.objectStore("assets").put({ name: file.name, blob: file }, key);
        await completed(tx);
    } finally {
        db.close();
    }

    return JSON.stringify({ name: file.name, size: file.size });
}

// keeps bytes from .NET under a key, in two steps because the bytes are only readable during a
// synchronous call: assetStage copies them, assetKeepStaged stores the copy
let staged = null;

export function assetStage(name, bytes) {
    staged = { name, blob: new Blob([bytes.slice()]) };
}

export async function assetKeepStaged(key) {
    const asset = staged;
    staged = null;
    const db = await openLibrary();
    try {
        const tx = db.transaction("assets", "readwrite");
        tx.objectStore("assets").put(asset, key);
        await completed(tx);
    } finally {
        db.close();
    }
}

// the name and size of a kept file as JSON, or "" when there is none
export async function assetInfo(key) {
    const db = await openLibrary();
    try {
        const asset = await result(db.transaction("assets").objectStore("assets").get(key));
        return asset ? JSON.stringify({ name: asset.name, size: asset.blob.size }) : "";
    } finally {
        db.close();
    }
}

// reads a kept file for sourceCopy; resolves to its length, or -1 when there is none
export async function assetLoad(key) {
    const db = await openLibrary();
    let asset;
    try {
        asset = await result(db.transaction("assets").objectStore("assets").get(key));
    } finally {
        db.close();
    }

    if (!asset) {
        return -1;
    }

    loaded = new Uint8Array(await asset.blob.arrayBuffer());
    return loaded.length;
}

export async function assetDelete(key) {
    const db = await openLibrary();
    try {
        const tx = db.transaction("assets", "readwrite");
        tx.objectStore("assets").delete(key);
        await completed(tx);
    } finally {
        db.close();
    }
}

export async function storageInfo() {
    const estimate = (await navigator.storage?.estimate?.()) ?? {};
    const persisted = (await navigator.storage?.persisted?.()) ?? false;
    return JSON.stringify({ usage: estimate.usage ?? 0, quota: estimate.quota ?? 0, persisted });
}

export async function requestPersistence() {
    return (await navigator.storage?.persist?.()) ?? false;
}

// ---- files for the user ----

export function download(name, bytes) {
    const url = URL.createObjectURL(new Blob([bytes.slice()], { type: "application/octet-stream" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = name;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 10000);
}

export function confirmAction(text) {
    return window.confirm(text);
}
