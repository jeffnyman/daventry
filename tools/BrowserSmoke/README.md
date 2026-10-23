# Browser host smoke test

How the browser host was checked on 2026-10-23 without a person at the keyboard: publish the web
app, serve it next to a game, and drive headless Chrome over the DevTools protocol. Needs Node 22
or later (for its built-in WebSocket) and Chrome.

```
dotnet publish src/Daventry.Web -c Release -o out
mkdir out\wwwroot\games\kq1
copy "<King's Quest 1 folder>\*" out\wwwroot\games\kq1\
```

Write `out\wwwroot\games\kq1\manifest.json` with the game's name and file names:

```json
{ "name": "kq1", "files": ["KQ1.DIR", "LOGDIR", "OBJECT", "PICDIR", "SNDDIR", "VIEWDIR", "VOL.0", "VOL.1", "VOL.2", "WORDS.TOK"] }
```

Then serve and drive it (Python's http.server cuts off the runtime's larger files, which is why
there is a server here):

```
node tools\BrowserSmoke\server.mjs out\wwwroot 8124
node tools\BrowserSmoke\cdp.mjs "http://127.0.0.1:8124/?game=games/kq1/manifest.json" ^
    "wait:25000;shot:title.png;key:Enter;wait:3000;key:Enter;wait:8000;shot:room1.png"
```

`?game=<manifest url>` makes the page load the files from the server and start the game by itself,
which is also how a site can host games of its own. The steps: `wait:<ms>`, `shot:<png>` of the
page, `key:<Key>` (`Enter`, `Escape`, `F5`, `ArrowRight`, a letter), `text:<chars>` typed one by
one, `click:<x>,<y>` in framebuffer pixels, `nav:` to reload the page, `eval:<js>` to print a
value (`eval:document.querySelector('.status')?.textContent`, `eval:Object.keys(localStorage)`), and
`title`. The browser console, where the engines log and where the host prints its ticks per second
and milliseconds per tick every ten seconds, is echoed to the terminal.

What the first run checked: KQ1 AGI and KQ1 SCI play (keys, typing, mouse clicks on the SCI
title buttons), the save dialog of KQ1 SCI writes `daventry:save:kq1sci.001` to local storage,
the restore list shows it after a reload and restores it, and QFG1 VGA's title runs. The host
reported 60 ticks a second at 0.3 to 0.6 ms per tick for these games without ahead-of-time
compilation. Headless Chrome only writes local storage to the profile when it exits cleanly,
which the driver waits for; a killed browser loses the saves of that session. Headless Chrome
also plays the games' sound through the machine's speakers, so a run is audible; the page's
`daventryAudio` object (`eval:JSON.stringify(daventryAudio)`) counts the chunks scheduled, the
audible ones and the peak level without listening.
