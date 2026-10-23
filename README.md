# Daventry

Daventry is a C# implementation of the Sierra AGI and SCI adventure game interpreters.
It is a port of the `engines/agi` and `engines/sci` code from [ScummVM](https://www.scummvm.org/),
restructured as a set of .NET libraries with a small host contract so the same engines run
on the desktop (SDL) and in the browser (Blazor WebAssembly).

Daventry is not a ScummVM fork and does not aim to run anything other than Sierra's AGI and SCI games.

## Status

AGI games run: resources, pictures, views, sprites, the logic interpreter, text, parser and
PCjr/Tandy sound are ported, and King's Quest I through III, Space Quest I and II and the other
Sierra AGI titles play from the first room. Games are identified by ScummVM's MD5 table, with a
file-name and WinAGI project-file fallback for fan games. Menus, the inventory screen and saving
and restoring work; saved games use ScummVM's file layout and have been exchanged with a real
ScummVM build in both directions (its compressed files load too). The preAGI games run too: Mickey's Space Adventure and Winnie the Pooh (DOS) have engines of their
own over the shared picture, text and speaker code, with their menus, sounds and saved games
(Winnie also loads the save file of the original interpreter). Not ported yet: booter disk
images, Troll's Tale (ScummVM plays only its disk-image release either), the Apple II, IIgs and
CoCo3 sound formats.
SCI has its resource layer: maps, volumes and patch files of SCI0 through SCI3 are indexed, every
compression (LZW, Huffman, LZW with view and picture unpacking, DCL, STACpack) is ported, and the
interpreter generation and game object are detected for all 28 SCI games in the test library, and
games are identified by ScummVM's MD5 table with its game-object fallback. The SCI0 to SCI1.1
graphics core is ported too: the visual, priority and control screens, EGA dithering and
undithering, the system palette with merging and pal-vary, ports and priority bands, vector and
SCI1.1 pictures, views with scaling, and bitmap fonts. Every picture, cel and font of every SCI16
game in the library decodes; `daventry <game> --picture N --view N` browses them. The SCI0 to
SCI1.1 interpreter runs: the segment manager, scripts, objects, the p-machine, kernel dispatch
with ScummVM's signatures, lists, maths, strings, movement, events, windows, animation with
transitions, text, controls, the menu bar with its pull-down menus and status line, the text
parser (vocabulary, suffixes, the GNF grammar and Said matching), the SCI1.1 message resources,
the mouse cursor (cursor resources and view cels, drawn in software), ScummVM's script
workaround tables and all of its script patches (the load-time fixes for bugs in the original
scripts, generated from `script_patches.cpp`) are ported, so games boot from their own scripts,
show their title screens and dialogs, and SCI0 games answer typed commands (KQ1: "look" describes
the castle). Sound is
ported: sound resources are parsed into tracks and channels, Sierra's extended MIDI parser
(signals, cues, loop points, hold markers, channel remapping) drives the AdLib driver with the
game's own patch bank on a port of the DOSBox OPL2 emulator (dual OPL2 for stereo), the music
engine keeps the priority ordered play list with fades, pausing and channel remapping, the one
digital sample of a sound resource plays in preference to the synthesized version, as it did on a
Sound Blaster (`--no-digital-sfx` keeps the AdLib version), and kDoAudio and kDoSync play and lip-sync the speech of
the CD games (SOL, WAVE and raw audio, SCI1 audio language maps). Music and cues are clocked
from the audio output, or from engine time when the host reads no audio, so cue-driven scenes
advance headless too (KQ6 CD reaches its title menu, KQ5 CD plays its spoken intro). Polygon
pathfinding (`kAvoidPath`, `kMergePoly`, `kIntersections`) is ported, so clicking on the ground
walks the character around obstacles (`daventry <game> --click T:x,y` does it headless). Saving
and restoring work through the games' own dialogs (`kSaveGame`, `kRestoreGame`, `kGetSaveFiles`,
`kCheckSaveGame`) and write ScummVM's saved game format (version 46: the segment manager, the
sound play list, the palette and pal-vary, the windows, the synonyms, a thumbnail), and saves have
been exchanged with a real ScummVM build in both directions, SCI0 through SCI2.1; `--save T:N` and
`--restore T:N` do it headless (`tools/ScummVmRoundTrip` describes the procedure). Headless runs
are deterministic: the same game, inputs and `--seed` give the same frames on the same ticks,
whatever the machine is doing, so a scripted run reproduces a report exactly. Not ported yet: CD audio tracks
and the MT-32, General MIDI and Amiga/Mac sound drivers (music plays through AdLib, or through the
PCjr/Tandy and PC speaker drivers on request). The SCI0 to SCI1.1 games are now playable in principle; real play testing is the
next step.

SCI32 (SCI2 and SCI2.1: King's Quest VII, Space Quest 6, Police Quest 4, Quest for Glory 4 and
their generation) runs as well: the SCI32 kernel tables and the 32-bit VM differences (opcode
widths, the SCI32 comparison rules, the view-visible flag), the array and bitmap segments, the
plane and screen-item renderer (`GfxFrameout`: cels of every kind with scaling and remapping, the
256-colour palette with vary, cycling, fades and gamma, the colour remapper, the software cursor,
the text renderer with its control codes), the show-style transitions (fades, wipes, irises, pixel
dissolves, the palette morph) and picture scrolls, the digital audio mixer (`Audio32`: speech and
sound effects in SOL, WAVE and raw formats, several channels with fades, panning and attenuated
mixing, the Robot audio channel), the Robot video player (KQ7's cut scenes, `*.RBT`), the text
fields, scroll windows and message box, kAddLine, hot rectangles, and saving and restoring (the
SCI32 state is written in ScummVM's format version 46 alongside the rest). Not ported yet: SCI3
(no game in the library), the VMD, AVI, SEQ and Duck video players, the KQ7 video upscaling
option, and ScummVM's dialog for kMessageBox (boxes are answered with their default button).

The browser host (`Daventry.Host.Browser`, a Blazor component, and `Daventry.Web`, a page around
it) runs the same engines in WebAssembly: the frames go to a canvas, the sound through Web Audio,
the keyboard and mouse come from the canvas, saved games live in the browser's local storage, and
the page's animation frames are the clock. You pick a game folder in the page and nothing leaves
the browser; a site can also host games of its own and load them from a manifest. Checked with
headless Chrome (`tools/BrowserSmoke`): King's Quest I in both versions plays with keys, typing and
the mouse, saves and restores through its own dialogs, and runs at 60 ticks a second at well under
a millisecond a tick even without ahead-of-time compilation.

Staging plan:

1. AGI (`Daventry.Agi`): port of ScummVM's AGI engine, proves the host contract and the tick-driven loop.
2. SCI0 through SCI1.1 (`Daventry.Sci`).
3. SCI32 (SCI2 and SCI2.1) in the same assembly; SCI3 waits for game data.
4. The browser host.

## Layout

| Project | Purpose |
|---|---|
| `Daventry.Core` | Host contract (graphics, input, audio, game data, saves, logging), shared primitives, engine base class. No platform dependencies. |
| `Daventry.Audio` | Mixer and sound chip emulation shared by both engines. |
| `Daventry.Agi` | AGI interpreter. |
| `Daventry.Sci` | SCI interpreter. |
| `Daventry.Host.Sdl` | SDL3 implementation of the host contract. |
| `Daventry.Host.Browser` | Browser implementation of the host contract: canvas, Web Audio, local storage, as a Blazor component (`DaventryPlayer`). |
| `Daventry.Player` | Command line player. Packaged as a dotnet tool. |
| `Daventry.Web` | Blazor WebAssembly page that plays a game folder you pick, or a game a site hosts. |

Engine assemblies never touch SDL, the file system or a blocking main loop. The host calls
`Engine.Tick(elapsed)` repeatedly and the engine returns how long it would like to wait before the next
tick. Game data is read through `IGameContainer`, which can be a directory or an archive.

## Packages

`dotnet pack -c Release -o artifacts/packages` builds a NuGet package per project in the table above:
the libraries and hosts with symbol packages, and the player as the `daventry` dotnet tool
(`dotnet tool install -g Daventry.Player --prerelease`). `tools/PackageSmoke/run.ps1 -Game <folder>`
checks them the way a consumer would: a console app and a Blazor WebAssembly app restore them from
that folder and play a game, and the tool is installed and takes a screenshot. Nothing is on
nuget.org yet; pushing is a manual step.

An app references `Daventry.Agi` and `Daventry.Sci` and brings a host. Without a window:

```csharp
using Daventry.Agi;
using Daventry.Core.Hosting;
using Daventry.Sci;

var registry = new EngineRegistry().Add(new AgiEngineProvider()).Add(new SciEngineProvider());
if (registry.DetectInDirectory(path) is not var (container, games))
    return;

var host = new HeadlessHost();
using var engine = registry.Create(host, container, games[0]);
host.Run(engine, 600);
File.WriteAllBytes("title.png", host.Graphics.EncodePng());
```

`Daventry.Host.Sdl` runs the same engine in a window (`SdlHost`), and `Daventry.Host.Browser` in a
Blazor page: `<DaventryPlayer @ref="player" />`, then `player.PlayAsync(registry, container, game)`;
`tools/PackageSmoke/BlazorSmoke` is the smallest such page.

## Building

Requires the .NET 10 SDK.

```
dotnet build
dotnet test
dotnet run --project src/Daventry.Player -- <path-to-game-folder>
```

The window can be resized freely; the picture keeps its aspect ratio. Alt+Enter or F11 switches
full screen on and off, and `--fullscreen` starts that way. `--scale N` sets the initial window size.
`--aspect` shows the picture at 4:3, the shape it had on a CRT; Ctrl+Alt+A switches that while playing.
`--volume N` sets the master volume in percent (default 75, the level ScummVM mixes at); Ctrl+Alt+Up and
Ctrl+Alt+Down change it while playing, Ctrl+Alt+M mutes, and the window title shows the setting.
`--sound NAME` picks the sound hardware: for SCI games `adlib` (default), `pcjr` (Tandy three-voice) or
`pc` (the speaker); for AGI games `pcjr` (default), `pc`, `ramp`, `amiga` or `mac`.

In the browser:

```
dotnet run --project src/Daventry.Web               # a development server with the page
dotnet publish src/Daventry.Web -c Release -o out   # static files for any web server
```

The page asks for a game folder; click the picture to give it the keyboard. Alt+Enter or F11 for
full screen, Ctrl+Alt+Up, Down and M for the volume, as on the desktop, and a Sound choice for
AdLib, PCjr/Tandy or the PC speaker. Saved games stay in the browser's local storage. `?game=<url of a manifest.json>` loads a game the server hosts instead
(`tools/BrowserSmoke` shows the manifest). The engines log to the browser console, the host prints
its ticks per second there, and `daventryAudio` in the console shows whether sound is flowing
(`state`, chunks `pushed` and `audible`, `peak`, `underruns`). The interpreters run on the .NET interpreter in the
browser unless the app is published ahead of time, which needs the `wasm-tools` workload:
`dotnet workload install wasm-tools`, then `dotnet publish src/Daventry.Web -c Release -p:WasmAot=true`.

## License

GPL-3.0-or-later, the same license as ScummVM, from which the engine code is derived. See `LICENSE`.
