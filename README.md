# Daventry

Daventry is a C# implementation of the Sierra AGI and SCI adventure game interpreters.
It is a port of the `engines/agi` and `engines/sci` code from [ScummVM](https://www.scummvm.org/),
restructured as a set of .NET libraries with a small host contract so the same engines can run
on the desktop (SDL) and, later, in the browser.

Daventry is not a ScummVM fork and does not aim to run anything other than Sierra's AGI and SCI games.

## Status

AGI games run: resources, pictures, views, sprites, the logic interpreter, text, parser and
PCjr/Tandy sound are ported, and King's Quest I through III, Space Quest I and II and the other
Sierra AGI titles play from the first room. Games are identified by ScummVM's MD5 table, with a
file-name and WinAGI project-file fallback for fan games. Menus, the inventory screen and saving
and restoring work; saved games use ScummVM's file layout, so they can be exchanged with it. Not
ported yet: booter disk images, preAGI games, the Apple II, IIgs and CoCo3 sound formats.
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
digital sample of a sound resource plays, and kDoAudio and kDoSync play and lip-sync the speech of
the CD games (SOL, WAVE and raw audio, SCI1 audio language maps). Music and cues are clocked
from the audio output, or from engine time when the host reads no audio, so cue-driven scenes
advance headless too (KQ6 CD reaches its title menu, KQ5 CD plays its spoken intro). Polygon
pathfinding (`kAvoidPath`, `kMergePoly`, `kIntersections`) is ported, so clicking on the ground
walks the character around obstacles (`daventry <game> --click T:x,y` does it headless). Saving
and restoring work through the games' own dialogs (`kSaveGame`, `kRestoreGame`, `kGetSaveFiles`,
`kCheckSaveGame`) and write ScummVM's saved game format (version 46: the segment manager, the
sound play list, the palette and pal-vary, the windows, the synonyms, a thumbnail), so saves can be
exchanged with it; `--save T:N` and `--restore T:N` do it headless. Not ported yet: CD audio tracks
and the MT-32, General MIDI, PC speaker, PCjr/Tandy and Amiga/Mac sound drivers (everything plays
through AdLib). The SCI0 to SCI1.1 games are now playable in principle; real play testing is the
next step.

Staging plan:

1. AGI (`Daventry.Agi`): port of ScummVM's AGI engine, proves the host contract and the tick-driven loop.
2. SCI0 through SCI1.1 (`Daventry.Sci`).
3. SCI32 (SCI2 through SCI3) as a later phase.

## Layout

| Project | Purpose |
|---|---|
| `Daventry.Core` | Host contract (graphics, input, audio, game data, saves, logging), shared primitives, engine base class. No platform dependencies. |
| `Daventry.Audio` | Mixer and sound chip emulation shared by both engines. |
| `Daventry.Agi` | AGI interpreter. |
| `Daventry.Sci` | SCI interpreter. |
| `Daventry.Host.Sdl` | SDL3 implementation of the host contract. |
| `Daventry.Player` | Command line player. Packaged as a dotnet tool. |

Engine assemblies never touch SDL, the file system or a blocking main loop. The host calls
`Engine.Tick(elapsed)` repeatedly and the engine returns how long it would like to wait before the next
tick. Game data is read through `IGameContainer`, which can be a directory or an archive.

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

## License

GPL-3.0-or-later, the same license as ScummVM, from which the engine code is derived. See `LICENSE`.
