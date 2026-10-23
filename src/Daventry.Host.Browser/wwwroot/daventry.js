// The browser side of Daventry.Host.Browser: one canvas, one audio context, the input
// listeners and the animation frame loop. The .NET host calls these through [JSImport] and
// hands over three callbacks in start(); nothing here keeps state the host does not know about.

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
