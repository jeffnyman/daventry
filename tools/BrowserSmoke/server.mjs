// Static file server for the published site: node server.mjs <dir> <port>
import { createServer } from "node:http";
import { createReadStream, statSync } from "node:fs";
import { join, extname, normalize } from "node:path";

const [dir, port] = process.argv.slice(2);
const types = {
    ".html": "text/html; charset=utf-8", ".js": "text/javascript", ".mjs": "text/javascript", ".css": "text/css",
    ".json": "application/json", ".wasm": "application/wasm", ".dat": "application/octet-stream", ".png": "image/png",
};

createServer((req, res) => {
    let path = decodeURIComponent(new URL(req.url, "http://x").pathname);
    if (path.endsWith("/")) {
        path += "index.html";
    }

    const file = join(dir, normalize(path));
    let stat;
    try {
        stat = statSync(file);
    } catch {
        res.writeHead(404);
        res.end();
        return;
    }

    res.writeHead(200, { "Content-Type": types[extname(file).toLowerCase()] ?? "application/octet-stream", "Content-Length": stat.size, "Cache-Control": "no-cache" });
    createReadStream(file).pipe(res);
}).listen(parseInt(port), "127.0.0.1", () => console.log(`serving ${dir} on ${port}`));
