# Daventry

[![Build](https://github.com/jeffnyman/daventry/actions/workflows/build.yml/badge.svg)](https://github.com/jeffnyman/daventry/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/vpre/Daventry.Player.svg)](https://www.nuget.org/packages/Daventry.Player)

Daventry is a C# implementation of Sierra On-Line's adventure game interpreters: AGI, SCI, and ADL,
the engine of the Apple II Hi-Res Adventures. It is a port of the `engines/agi`, `engines/sci` and
`engines/adl` code from [ScummVM](https://www.scummvm.org/), restructured as a set of .NET
libraries with a small host contract so the same engines run on the desktop (SDL) and in the
browser (Blazor WebAssembly).

Daventry is not a ScummVM fork and does not aim to run anything other than Sierra's own games.

## Status

All of ScummVM's engine generations for Sierra's own games, from the Hi-Res Adventures through
AGI to SCI2.1, run from the games' own scripts and data; SCI3 waits for game data. The sections below say what each engine
covers and what is still missing.

| Engine | Games | State |
|---|---|---|
| AGI v2 and v3 | King's Quest I to IV, Space Quest I and II, Leisure Suit Larry, Police Quest, Manhunter, Gold Rush!, The Black Cauldron, Mixed-Up Mother Goose, fan games | Playable, with menus, inventory and saved games. |
| preAGI | Mickey's Space Adventure, Winnie the Pooh, Troll's Tale | Playable. |
| SCI0 to SCI1.1 | King's Quest I (SCI), IV (SCI), V and VI, Space Quest I (VGA) and III to V, Quest for Glory I to III, Leisure Suit Larry 1 (VGA), 2, 3, 5 and 6, Police Quest 1 (VGA), 2 and 3, Laura Bow 1 and 2, Codename: Iceman, Conquests of Camelot and of the Longbow, Jones in the Fast Lane, Mixed-Up Mother Goose (VGA) | Playable in principle: boot, dialogs, parser, mouse, pathfinding, sound, speech, saved games. Real play testing is the next step. |
| SCI2 and SCI2.1 | King's Quest VII, Space Quest 6, Police Quest 4, Quest for Glory 4 and their generation | Runs: the plane renderer, transitions, digital audio, Robot video, saved games. Other video players are missing. |
| SCI3 | Lighthouse, RAMA, Phantasmagoria 2 | Resources are indexed; the interpreter waits for game data. |
| ADL | The Hi-Res Adventures: Mission Asteroid, Mystery House, Wizard and the Princess, Cranston Manor, Ulysses and the Golden Fleece, Time Zone, The Dark Crystal | Playable from Apple II disk images (`.dsk`, `.do`, `.d13`, `.nib`, `.woz`), with ScummVM's saved games. |

| Host | What it is |
|---|---|
| Desktop | An SDL3 window (`daventry <game folder>`): resizable, full screen, volume keys, saves in the user's application data or any folder (`--saves`). |
| Browser | A Blazor component and a page around it: canvas, Web Audio, keyboard and mouse, saves in local storage. Nothing leaves the browser. |
| Headless | No window, no clock: deterministic runs for tests, screenshots, WAV captures and scripted input. |

### AGI and preAGI

- Resources, pictures, views, sprites, the logic interpreter, text, the parser and PCjr/Tandy
  sound are ported, and the Sierra AGI titles play from the first room.
- Games are identified by ScummVM's MD5 table, with a file-name and WinAGI project-file fallback
  for fan games.
- Menus, the inventory screen and saving and restoring work. Saved games use ScummVM's file
  layout and have been exchanged with a real ScummVM build in both directions; its compressed
  files load too.
- The preAGI games, Mickey's Space Adventure, Winnie the Pooh (DOS) and Troll's Tale, have
  engines of their own over the shared picture, text and speaker code, with their menus, sounds
  and saved games. Winnie also loads the save file of the original interpreter. Troll's Tale plays
  from its PC booter disk image, which ScummVM names `troll.img`; the DOS release's `game.dat` is
  the same image and plays too, though ScummVM refuses it under that name.
- Booter disk images (`.img`, `.ima`) and Apple II disk images are recognised by the directory
  inside them, as ScummVM does, and refused: the 1984-85 King's Quest uses Game Adaptation
  Language, the interpreter before AGI, which ScummVM does not run either, and the AGI v1 and
  Apple II loaders are not ported. Not ported either: the Apple II, IIgs and CoCo3 sound formats.

### SCI0 to SCI1.1

- Resources: the maps, volumes and patch files of SCI0 through SCI3 are indexed, every
  compression (LZW, Huffman, LZW with view and picture unpacking, DCL, STACpack) is ported, and the
  interpreter generation and game object are detected for all 28 SCI games in the test library.
  Games are identified by ScummVM's MD5 table with its game-object fallback.
- Graphics: the visual, priority and control screens, EGA dithering and undithering, the system
  palette with merging and pal-vary, ports and priority bands, vector and SCI1.1 pictures, views
  with scaling, and bitmap fonts. Every picture, cel and font of every SCI16 game in the library
  decodes; `daventry <game> --picture N --view N` browses them.
- Interpreter: the segment manager, scripts, objects, the p-machine, kernel dispatch with
  ScummVM's signatures, lists, maths, strings, movement, events, windows, animation with
  transitions, text, controls, the menu bar with its pull-down menus and status line, the text
  parser (vocabulary, suffixes, the GNF grammar and Said matching), the SCI1.1 message resources,
  and the mouse cursor (cursor resources and view cels, drawn in software). SCI0 games answer typed
  commands; in KQ1, "look" describes the castle.
- ScummVM's script workaround tables and all of its script patches, the load-time fixes for bugs
  in the original scripts, are ported (generated from `script_patches.cpp`).
- Sound: sound resources are parsed into tracks and channels, and Sierra's extended MIDI parser
  (signals, cues, loop points, hold markers, channel remapping) drives the AdLib driver with the
  game's own patch bank on a port of the DOSBox OPL2 emulator (dual OPL2 for stereo), or the
  PCjr/Tandy and PC speaker drivers on request. The music engine keeps the priority-ordered play
  list with fades, pausing and channel remapping. The one digital sample of a sound resource plays
  in preference to the synthesized version, as it did on a Sound Blaster (`--no-digital-sfx` keeps
  the AdLib version).
- Speech: kDoAudio and kDoSync play and lip-sync the speech of the CD games (SOL, WAVE and raw
  audio, SCI1 audio language maps).
- Music and cues are clocked from the audio output, or from engine time when the host reads no
  audio, so cue-driven scenes advance headless too: KQ6 CD reaches its title menu and KQ5 CD plays
  its spoken intro.
- Polygon pathfinding (`kAvoidPath`, `kMergePoly`, `kIntersections`), so clicking on the ground
  walks the character around obstacles; `daventry <game> --click T:x,y` does it headless.
- Saving and restoring work through the games' own dialogs (`kSaveGame`, `kRestoreGame`,
  `kGetSaveFiles`, `kCheckSaveGame`) and write ScummVM's saved game format, version 46: the
  segment manager, the sound play list, the palette and pal-vary, the windows, the synonyms and a
  thumbnail. Saves have been exchanged with a real ScummVM build in both directions, SCI0 through
  SCI2.1; `--save T:N` and `--restore T:N` do it headless and `tools/ScummVmRoundTrip` describes
  the procedure.
- Headless runs are deterministic: the same game, inputs and `--seed` give the same frames on the
  same ticks whatever the machine is doing, so a scripted run reproduces a report exactly.
- Not ported yet: CD audio tracks and the MT-32, General MIDI and Amiga/Mac sound drivers.

### SCI2 and SCI2.1

- The SCI32 kernel tables and the 32-bit VM differences: opcode widths, the SCI32 comparison
  rules, the view-visible flag, the array and bitmap segments.
- The plane and screen-item renderer (`GfxFrameout`): cels of every kind with scaling and
  remapping, the 256-colour palette with vary, cycling, fades and gamma, the colour remapper, the
  software cursor, and the text renderer with its control codes.
- The show-style transitions (fades, wipes, irises, pixel dissolves, the palette morph) and
  picture scrolls.
- The digital audio mixer (`Audio32`): speech and sound effects in SOL, WAVE and raw formats,
  several channels with fades, panning and attenuated mixing, and the Robot audio channel.
- The Robot video player (KQ7's cut scenes, `*.RBT`), text fields, scroll windows, the message
  box, kAddLine and hot rectangles.
- Saving and restoring: the SCI32 state is written in ScummVM's format version 46 alongside the
  rest.
- Not ported yet: SCI3 (no game in the library), the VMD, AVI, SEQ and Duck video players, the
  KQ7 video upscaling option, and ScummVM's dialog for kMessageBox (boxes are answered with their
  default button).

### ADL

- The seven Hi-Res Adventures (1980 to 1982) run from their Apple II disk images: all four ADL
  engine generations, the picture drawing of each (Mystery House's line art, the pattern fills of
  the later games), Mystery House's DOS 3.3 files, Ulysses' four releases including Green Valley's
  animated title and the Atari 8-bit one, Time Zone's 41 regions on twelve disk sides, and The Dark
  Crystal's singular and plural text.
- The disk-image reader (`Daventry.Core.Formats`) decodes nibble and WOZ images of 13- and
  16-sector disks and reads Apple DOS 3.3 catalogs. Games are identified by ScummVM's checksums of
  the disks' contents, so a dump keeps whatever name it came with.
- The Apple II display is drawn with the RGB-monitor colours or a green monochrome monitor
  (Mystery House's default), optionally with scanlines (`--mono`, `--color`, `--scanlines`), and
  the speaker plays the games' tones and bells.
- Saved games use ScummVM's ADL format and have been exchanged with a ScummVM build in both
  directions.
- Not ported yet: ScummVM's default NTSC composite colour, which needs a true-colour host, the
  text-to-speech option, and its debugger console and scripted input.

### Browser host

- `Daventry.Host.Browser` is a Blazor component and `Daventry.Web` a page around it. The frames go
  to a canvas, the sound through Web Audio, the keyboard and mouse come from the canvas, saved
  games live in the browser's local storage, and the page's animation frames are the clock.
- You pick a game folder in the page and nothing leaves the browser; a site can also host games of
  its own and load them from a manifest.
- Checked with headless Chrome (`tools/BrowserSmoke`): King's Quest I in both versions plays with
  keys, typing and the mouse, saves and restores through its own dialogs, and runs at 60 ticks a
  second at well under a millisecond a tick even without ahead-of-time compilation.

## Layout

| Project | Purpose |
|---|---|
| `Daventry.Core` | Host contract (graphics, input, audio, game data, saves, logging), shared primitives, engine base class, floppy disk images. No platform dependencies. |
| `Daventry.Audio` | Mixer and sound chip emulation shared by the engines. |
| `Daventry.Agi` | AGI interpreter. |
| `Daventry.Sci` | SCI interpreter. |
| `Daventry.Adl` | ADL interpreter, for the Apple II Hi-Res Adventures. |
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
that folder and play a game, and the tool is installed and takes a screenshot. Pushing a tag such
as `v0.1.0-alpha` publishes them to nuget.org through GitHub Actions with Trusted Publishing
(`.github/workflows/publish.yml`, whose header lists the one-time setup).

An app references the engines it wants (`Daventry.Agi`, `Daventry.Sci`, `Daventry.Adl`) and brings
a host. Without a window:

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
For the Hi-Res Adventures, `--mono` and `--color` choose the monitor (colour by default, green
monochrome for Mystery House) and `--scanlines` darkens every other line.
Saves go to `Daventry/saves/<game id>` in the user's application data; `--saves DIR` keeps them
in any other folder instead.

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
