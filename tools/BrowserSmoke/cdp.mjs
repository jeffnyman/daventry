// Drives headless Chrome over the DevTools protocol, for smoke tests of the browser host:
//   node cdp.mjs <url> "<step>;<step>;..."
// steps: wait:<ms>  shot:<png>  eval:<js>  key:<Key>[+code]  text:<chars>  click:<fbx>,<fby>  nav:[url]  title
//        files:<css selector>|<path>[|<path>...]  (sets a file input, a folder for a webkitdirectory one)
//        downloads:<folder>  (where the page's downloads are written)
// Confirmation dialogs (window.confirm) are accepted.
// Chrome comes from the CHROME environment variable (default: the usual Windows install), the
// profile (and so local storage) lives in CHROME_PROFILE (default: a folder in the temp dir).
import { spawn } from "node:child_process";
import { writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const chrome = process.env.CHROME ?? "C:/Program Files/Google/Chrome/Application/chrome.exe";
const profile = process.env.CHROME_PROFILE ?? join(tmpdir(), "daventry-chrome-profile");
const [url, script] = process.argv.slice(2);
const port = parseInt(process.env.CDP_PORT ?? "9333");

const proc = spawn(chrome, [
    "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions",
    "--disable-component-update", "--autoplay-policy=no-user-gesture-required", `--user-data-dir=${profile}`,
    `--remote-debugging-port=${port}`, "--window-size=1100,900", "about:blank",
], { stdio: "ignore" });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function targets() {
    for (let i = 0; i < 50; i++) {
        try {
            const res = await fetch(`http://127.0.0.1:${port}/json`);
            return await res.json();
        } catch {
            await sleep(200);
        }
    }
    throw new Error("Chrome did not open its debugging port");
}

const page = (await targets()).find((t) => t.type === "page");
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((r) => (ws.onopen = r));

let nextId = 1;
const pending = new Map();
ws.onmessage = (m) => {
    const msg = JSON.parse(m.data);
    if (msg.id && pending.has(msg.id)) {
        pending.get(msg.id)(msg);
        pending.delete(msg.id);
    } else if (msg.method === "Runtime.consoleAPICalled") {
        const text = msg.params.args.map((a) => a.value ?? a.description ?? "").join(" ");
        console.log(`[console.${msg.params.type}] ${text.slice(0, 400)}`);
    } else if (msg.method === "Page.javascriptDialogOpening") {
        console.log(`[dialog] ${msg.params.message}`);
        ws.send(JSON.stringify({ id: nextId++, method: "Page.handleJavaScriptDialog", params: { accept: true } }));
    } else if (msg.method === "Runtime.exceptionThrown") {
        console.log(`[exception] ${msg.params.exceptionDetails.text} ${msg.params.exceptionDetails.exception?.description?.slice(0, 600) ?? ""}`);
    }
};
const send = (method, params = {}) => new Promise((resolve) => {
    const id = nextId++;
    pending.set(id, resolve);
    ws.send(JSON.stringify({ id, method, params }));
});

await send("Runtime.enable");
await send("Page.enable");
await send("Page.navigate", { url });

async function canvasRect() {
    const r = await send("Runtime.evaluate", { expression: "JSON.stringify(document.querySelector('canvas').getBoundingClientRect())", returnByValue: true });
    const size = await send("Runtime.evaluate", { expression: "(() => { const c = document.querySelector('canvas'); return [c.width, c.height]; })()", returnByValue: true });
    return { rect: JSON.parse(r.result.result.value), size: size.result.result.value };
}

const keyCodes = { Enter: 13, Escape: 27, Space: 32, ArrowUp: 38, ArrowDown: 40, ArrowLeft: 37, ArrowRight: 39, Tab: 9, F1: 112, F2: 113, F3: 114, F5: 116, F7: 118 };

for (const step of script.split(";")) {
    const colon = step.indexOf(":");
    const op = colon < 0 ? step : step.slice(0, colon);
    const arg = colon < 0 ? "" : step.slice(colon + 1);
    if (op === "wait") {
        await sleep(parseInt(arg));
    } else if (op === "nav") {
        await send("Page.navigate", { url: arg || url });
    } else if (op === "shot") {
        const shot = await send("Page.captureScreenshot", { format: "png" });
        writeFileSync(arg, Buffer.from(shot.result.data, "base64"));
        console.log(`[shot] ${arg}`);
    } else if (op === "eval") {
        const r = await send("Runtime.evaluate", { expression: arg, returnByValue: true });
        console.log(`[eval] ${JSON.stringify(r.result?.result?.value ?? r.result?.exceptionDetails?.text)}`);
    } else if (op === "files") {
        const [selector, ...paths] = arg.split("|");
        const doc = await send("DOM.getDocument");
        const node = await send("DOM.querySelector", { nodeId: doc.result.root.nodeId, selector });
        const r = await send("DOM.setFileInputFiles", { nodeId: node.result.nodeId, files: paths });
        console.log(`[files] ${selector} <- ${paths.join(", ")}${r.error ? " ERROR " + r.error.message : ""}`);
    } else if (op === "downloads") {
        await send("Browser.setDownloadBehavior", { behavior: "allow", downloadPath: arg });
    } else if (op === "title") {
        const r = await send("Runtime.evaluate", { expression: "document.title", returnByValue: true });
        console.log(`[title] ${r.result.result.value}`);
    } else if (op === "key") {
        const [key, code] = arg.split("+");
        const text = key.length === 1 ? key : key === "Enter" ? "\r" : key === "Escape" ? "\u001b" : "";
        const common = { key, code: code ?? (key.length === 1 ? "Key" + key.toUpperCase() : key), windowsVirtualKeyCode: keyCodes[key] ?? (key.length === 1 ? key.toUpperCase().charCodeAt(0) : 0) };
        await send("Input.dispatchKeyEvent", { type: text ? "keyDown" : "rawKeyDown", text, ...common });
        await sleep(40);
        await send("Input.dispatchKeyEvent", { type: "keyUp", ...common });
    } else if (op === "text") {
        for (const ch of arg) {
            const common = { key: ch, code: /[a-z]/i.test(ch) ? "Key" + ch.toUpperCase() : /[0-9]/.test(ch) ? "Digit" + ch : ch === " " ? "Space" : "", windowsVirtualKeyCode: ch.toUpperCase().charCodeAt(0) };
            await send("Input.dispatchKeyEvent", { type: "keyDown", text: ch, ...common });
            await sleep(30);
            await send("Input.dispatchKeyEvent", { type: "keyUp", ...common });
            await sleep(30);
        }
    } else if (op === "click") {
        const [fx, fy] = arg.split(",").map(Number);
        const { rect, size } = await canvasRect();
        const x = rect.left + (fx + 0.5) * rect.width / size[0];
        const y = rect.top + (fy + 0.5) * rect.height / size[1];
        await send("Input.dispatchMouseEvent", { type: "mouseMoved", x, y });
        await sleep(100);
        await send("Input.dispatchMouseEvent", { type: "mousePressed", x, y, button: "left", clickCount: 1 });
        await sleep(100);
        await send("Input.dispatchMouseEvent", { type: "mouseReleased", x, y, button: "left", clickCount: 1 });
    }
}

const exited = new Promise((r) => proc.on("exit", r));
await send("Browser.close");
await Promise.race([exited, sleep(5000)]);
proc.kill();
process.exit(0);
