# Daventry

[![Build](https://github.com/jeffnyman/daventry/actions/workflows/build.yml/badge.svg)](https://github.com/jeffnyman/daventry/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/vpre/Daventry.Player.svg)](https://www.nuget.org/packages/Daventry.Player)

Daventry is a C# implementation of Sierra On-Line's adventure game interpreters: AGI, SCI, and ADL,
the engine of the Apple II Hi-Res Adventures. It is a port of the `engines/agi`, `engines/sci` and
`engines/adl` code from [ScummVM](https://www.scummvm.org/), restructured as a set of .NET
libraries with a small host contract so the same engines run on the desktop (SDL) and in the
browser (Blazor WebAssembly).

Daventry is not a ScummVM fork and does not aim to run anything other than Sierra's own games.

## Screenshots

Taken by the headless host from the games' own data, at their native resolutions (Police Quest 4's
320×200 taken with `--aspect`, stretched to 4:3 as a monitor showed it, to sit beside its 640×480
neighbours).

<table>
<tr><th colspan="3">AGI</th></tr>
<tr>
<td align="center" valign="top"><img src="https://raw.githubusercontent.com/jeffnyman/daventry/main/assets/screenshots/agi-kq2.png" width="280" alt="King's Quest II"><br>King's Quest II</td>
<td align="center" valign="top"><img src="https://raw.githubusercontent.com/jeffnyman/daventry/main/assets/screenshots/agi-sq2.png" width="280" alt="Space Quest II"><br>Space Quest II</td>
<td align="center" valign="top"><img src="https://raw.githubusercontent.com/jeffnyman/daventry/main/assets/screenshots/agi-goldrush.png" width="280" alt="Gold Rush!"><br>Gold Rush!</td>
</tr>
<tr><th colspan="3">SCI0 to SCI1.1</th></tr>
<tr>
<td align="center" valign="top"><img src="https://raw.githubusercontent.com/jeffnyman/daventry/main/assets/screenshots/sci1-sq3.png" width="280" alt="Space Quest III"><br>Space Quest III</td>
<td align="center" valign="top"><img src="https://raw.githubusercontent.com/jeffnyman/daventry/main/assets/screenshots/sci1-kq6.png" width="280" alt="King's Quest VI"><br>King's Quest VI</td>
<td align="center" valign="top"><img src="https://raw.githubusercontent.com/jeffnyman/daventry/main/assets/screenshots/sci1-lb2.png" width="280" alt="Laura Bow 2: The Dagger of Amon Ra"><br>Laura Bow 2: The Dagger of Amon Ra</td>
</tr>
<tr><th colspan="3">SCI2 and SCI2.1</th></tr>
<tr>
<td align="center" valign="top"><img src="https://raw.githubusercontent.com/jeffnyman/daventry/main/assets/screenshots/sci2-kq7.png" width="280" alt="King's Quest VII"><br>King's Quest VII</td>
<td align="center" valign="top"><img src="https://raw.githubusercontent.com/jeffnyman/daventry/main/assets/screenshots/sci2-sq6.png" width="280" alt="Space Quest 6"><br>Space Quest 6</td>
<td align="center" valign="top"><img src="https://raw.githubusercontent.com/jeffnyman/daventry/main/assets/screenshots/sci2-pq4.png" width="280" alt="Police Quest 4"><br>Police Quest 4</td>
</tr>
<tr><th colspan="3">SCI3</th></tr>
<tr>
<td align="center" valign="top"><img src="https://raw.githubusercontent.com/jeffnyman/daventry/main/assets/screenshots/sci3-lsl7.png" width="280" alt="Leisure Suit Larry 7"><br>Leisure Suit Larry 7</td>
<td align="center" valign="top"><img src="https://raw.githubusercontent.com/jeffnyman/daventry/main/assets/screenshots/sci3-lighthouse.png" width="280" alt="Lighthouse"><br>Lighthouse</td>
<td align="center" valign="top"><img src="https://raw.githubusercontent.com/jeffnyman/daventry/main/assets/screenshots/sci3-rama.png" width="280" alt="RAMA"><br>RAMA</td>
</tr>
<tr><th colspan="3">ADL</th></tr>
<tr>
<td align="center" valign="top"><img src="https://raw.githubusercontent.com/jeffnyman/daventry/main/assets/screenshots/adl-mystery-house.png" width="280" alt="Mystery House"><br>Mystery House</td>
<td align="center" valign="top"><img src="https://raw.githubusercontent.com/jeffnyman/daventry/main/assets/screenshots/adl-wizard.png" width="280" alt="Wizard and the Princess"><br>Wizard and the Princess</td>
<td align="center" valign="top"><img src="https://raw.githubusercontent.com/jeffnyman/daventry/main/assets/screenshots/adl-dark-crystal.png" width="280" alt="The Dark Crystal"><br>The Dark Crystal</td>
</tr>
</table>

## Status

All of ScummVM's engine generations for Sierra's own games, from the Hi-Res Adventures through
AGI to SCI3, run from the games' own scripts and data. Most of the checking so far is headless:
scripted runs through each game's opening, plus targeted tests. Play testing in the window is the
next step. [ROADMAP.md](https://github.com/jeffnyman/daventry/blob/main/ROADMAP.md) has the details, what is deferred and what comes next.

| Engine | Games | State |
|---|---|---|
| AGI v2 and v3 | King's Quest I to IV, Space Quest I and II, Leisure Suit Larry, Police Quest, Manhunter, Gold Rush!, The Black Cauldron, Mixed-Up Mother Goose, fan games | Playable, with menus, inventory and saved games. |
| preAGI | Mickey's Space Adventure, Winnie the Pooh, Troll's Tale | Playable. |
| SCI0 to SCI1.1 | King's Quest I (SCI), IV (SCI), V and VI, Space Quest I (VGA) and III to V, Quest for Glory I to III, Leisure Suit Larry 1 (VGA), 2, 3, 5 and 6, Police Quest 1 (VGA), 2 and 3, Laura Bow 1 and 2, Codename: Iceman, Conquests of Camelot and of the Longbow, Jones in the Fast Lane, Mixed-Up Mother Goose (VGA) | Boot, dialogs, parser, mouse, pathfinding, music on five devices, speech, saved games. |
| SCI2 and SCI2.1 | King's Quest VII, Space Quest 6, Police Quest 4, Quest for Glory 4, Phantasmagoria, Mixed-Up Mother Goose Deluxe | Reach play and save (Mother Goose Deluxe's saves are untried): the plane renderer, transitions, digital audio, Robot and VMD video. |
| SCI3 | Leisure Suit Larry 7, Lighthouse, RAMA, Phantasmagoria 2 | Play their openings into the first scene and save; Phantasmagoria 2's Duck videos play in true colour. |
| ADL | The Hi-Res Adventures: Mission Asteroid, Mystery House, Wizard and the Princess, Cranston Manor, Ulysses and the Golden Fleece, Time Zone, The Dark Crystal | Playable from Apple II disk images, in ScummVM's NTSC colour, with ScummVM's saved games. |

| Host | What it is |
|---|---|
| Desktop | An SDL3 window (`daventry <game folder>`): resizable, full screen, volume keys, true colour for the video that needs it, saves in the user's application data or any folder. |
| Browser | A Blazor component and a page around it: canvas, Web Audio, keyboard and mouse, true colour, games kept in the browser, saves in local storage with download and import. Nothing leaves the browser. |
| Headless | No window, no clock: deterministic runs for tests, screenshots, WAV captures and scripted input. |

## What is ported

### AGI and preAGI

- **Engine:** resources, pictures, views, sprites, the logic interpreter, text, the parser and
  PCjr/Tandy sound; every Sierra AGI title plays from its first room.
- **Detection:** ScummVM's MD5 table, with a file-name and WinAGI project-file fallback for fan
  games.
- **Menus and saves:** menus, the inventory screen, saving and restoring. Saved games use
  ScummVM's file layout and have been exchanged with a real ScummVM build in both directions; its
  compressed files load too.
- **preAGI:** Mickey's Space Adventure, Winnie the Pooh (DOS) and Troll's Tale have engines of
  their own over the shared picture, text and speaker code, with their menus, sounds and saved
  games. Winnie also loads the save file of the original interpreter. Troll's Tale plays from its
  PC booter disk image, which ScummVM names `troll.img`; the DOS release's `game.dat` is the same
  image and plays too, though ScummVM refuses it under that name.
- **Not ported:** booter disk images (`.img`, `.ima`) and Apple II disk images are recognised by
  the directory inside them, as ScummVM does, and refused: the 1984-85 King's Quest uses Game
  Adaptation Language, the interpreter before AGI, which ScummVM does not run either, and the AGI
  v1 and Apple II loaders are not ported. Nor are the Apple II, IIgs and CoCo3 sound formats.

### SCI0 to SCI1.1

- **Resources:** the maps, volumes and patch files of SCI0 through SCI3, every compression (LZW,
  Huffman, LZW with view and picture unpacking, DCL, STACpack), and the interpreter generation and
  game object, detected for all 40 SCI games in the test library. Games are identified by
  ScummVM's MD5 table with its game-object fallback.
- **Graphics:** the visual, priority and control screens, EGA dithering and undithering, the
  system palette with merging and pal-vary, ports and priority bands, vector and SCI1.1 pictures,
  views with scaling, and bitmap fonts. Every picture, cel and font of every SCI16 game in the
  library decodes; `daventry <game> --picture N --view N` browses them.
- **Interpreter:** the segment manager, scripts, objects, the p-machine, kernel dispatch with
  ScummVM's signatures, lists, maths, strings, movement, events, windows, animation with
  transitions, text, controls, the menu bar and status line, the text parser (vocabulary,
  suffixes, the GNF grammar and Said matching), the SCI1.1 message resources, and the software
  mouse cursor.
- **Fixes for the original scripts:** ScummVM's workaround tables and all 911 of its script
  patches, generated from `script_patches.cpp`.
- **Pathfinding:** `kAvoidPath`, `kMergePoly` and `kIntersections`, so clicking on the ground walks
  the character around obstacles; `daventry <game> --click T:x,y` does it headless.
- **Music:** sound resources and Sierra's extended MIDI parser (signals, cues, loop points, hold
  markers, channel remapping) under the music engine's play list with fades and pausing, on five
  devices:
  - AdLib, the default: the game's own patch bank on a port of the DOSBox OPL2 emulator (dual
    OPL2 for stereo).
  - PCjr/Tandy three-voice and the PC speaker.
  - General MIDI through a SoundFont you supply (the MeltySynth synthesiser). Games with a General
    MIDI patch, such as King's Quest VI, play their GM tracks; games written for the MT-32 alone
    are mapped with ScummVM's tables (approximate for SCI1 games, as ScummVM warns). Mixed-Up
    Mother Goose Deluxe has only General MIDI music, so it always takes the SoundFont. Any General
    MIDI SoundFont works, such as the small `Roland_SC-55.sf2` in ScummVM's source
    (`dists/soundfonts`, GPL) or GeneralUser GS.
  - Roland MT-32 and CM-32L, the modules Sierra composed for from King's Quest IV to VI, emulated by
    a C# port of Munt (the emulator inside ScummVM) from ROM dumps you supply. Each game uploads its
    instruments and shows its messages on the emulated LCD. The output matches Munt sample for
    sample (`tools/Mt32Reference`). ROMs are recognised by their contents whatever the files are
    called, including chip-at-a-time dumps; the CM-32L is preferred, as in ScummVM.
- **Sound effects and speech:** a sound resource's digital sample plays in preference to the
  synthesized version, as on a Sound Blaster (`--no-digital-sfx` keeps the AdLib version).
  `kDoAudio` and `kDoSync` play and lip-sync the speech of the CD games (SOL, WAVE and raw audio,
  SCI1 audio language maps). Music and cues are clocked from the audio output, or from engine time
  when nothing reads the audio, so cue-driven scenes advance headless too.
- **Saved games:** through the games' own dialogs, in ScummVM's format version 46 (the segment
  manager, the sound play list, the palette, the windows, a thumbnail). Saves have been exchanged
  with a real ScummVM build in both directions, SCI0 through SCI2.1; `--save T:N` and
  `--restore T:N` do it headless and `tools/ScummVmRoundTrip` describes the procedure.
- **Files the games write** (`kFileIO`) go next to the saves as `<game id>-<name>`, as ScummVM
  names them: LSL3's `larry3.drv`, LSL7's preferences, the Quest for Glory character exports,
  Phantasmagoria's own save files. Files are read from there first, then from the game folder.
- **Determinism:** headless runs give the same frames on the same ticks for the same game, input
  and `--seed`, whatever the machine is doing, so a scripted run reproduces a report exactly.
- **Not ported:** CD audio tracks, the Roland D-110, the Amiga and Mac sound drivers, and the
  Windows-only touches of the SCI1.1 releases (King's Quest VI's high-resolution cels, the
  alternate General MIDI soundtracks); those releases run as their DOS versions.

### SCI2 and SCI2.1

- **VM:** the SCI32 kernel tables and the 32-bit VM differences: opcode widths, the SCI32
  comparison rules, the view-visible flag, the array and bitmap segments.
- **Graphics:** the plane and screen-item renderer (`GfxFrameout`): cels of every kind with scaling
  and remapping, the 256-colour palette with vary, cycling, fades and gamma, the colour remapper,
  the software cursor, the text renderer with its control codes, the show-style transitions (fades,
  wipes, irises, pixel dissolves, the palette morph) and picture scrolls.
- **Audio:** the digital audio mixer (`Audio32`): speech and sound effects in SOL, WAVE and raw
  formats, several channels with fades, panning and attenuated mixing.
- **Controls:** text fields, scroll windows, the message box, `kAddLine` and hot rectangles.
- **Saved games:** the SCI32 state in ScummVM's format version 46.
- **Not ported:** ScummVM's dialog for `kMessageBox` (boxes are answered with their default
  button) and the KQ7 video upscaling option.

### SCI3

- **Scripts:** the SCI3 format: no heap, 32-bit header offsets, dword-aligned locals, objects and
  strings, scripts up to 256 KB resolved through their relocation tables (VM registers carry
  18-bit offsets, saved in ScummVM's packed form).
- **Objects:** selectors described in a bank of 32-selector groups, properties found by selector,
  the `info` and `superP` opcodes, and the SCI3 redraw pass of the plane renderer.
- **Discs:** audio kept per CD, and the subfolders ScummVM searches for a game's files (`movies`,
  `robots`, `patches` and the others).
- **Checked:** Leisure Suit Larry 7 runs its logo, opening and first scene the same way as
  ScummVM 2026.4.0. Lighthouse plays its logo and intro into the first room. RAMA plays its opening
  into the hangar, and saves, restores and deletes through its own screens. Phantasmagoria 2 (the
  US release) plays its title video and opening into Curtis's bedroom, and saves and restores
  there.

### SCI32 video

- **Robot** (`*.RBT`): King's Quest VII's cut scenes and the inserts of the later games, with
  their audio channel.
- **VMD**, Coktel's format (Phantasmagoria, Lighthouse, RAMA): 8-bit frames with their palettes
  and DPCM sound. SCI3 shows them through the renderer as a screen item; SCI2.1 draws them straight
  into the game buffer.
- **Duck** (Phantasmagoria 2): AVI files with TrueMotion 1 video and Duck DK3 ADPCM sound. The
  frames are 16-bit colour, so while one plays the engine hands the host true-colour frames: the
  game's screen converted from its palette, with the video drawn over it. A click or Escape ends a
  video, as in ScummVM.
- **Not ported:** the AVI and SEQ players (King's Quest VII for Windows, some SCI2 movies),
  QuickTime (Macintosh releases), and VMDs packed with Indeo 3 or in 16 and 24 bits.

### ADL

- **Games:** the seven Hi-Res Adventures (1980 to 1982) run from their Apple II disk images: all
  four ADL engine generations, the picture drawing of each (Mystery House's line art, the pattern
  fills of the later games), Mystery House's DOS 3.3 files, Ulysses' four releases including
  Green Valley's animated title and the Atari 8-bit one, Time Zone's 41 regions on twelve disk
  sides, and The Dark Crystal's singular and plural text.
- **Disk images:** `Daventry.Core.Formats` decodes `.dsk`, `.do`, `.d13`, `.nib` and `.woz` images
  of 13- and 16-sector disks and reads Apple DOS 3.3 catalogs. Games are identified by ScummVM's
  checksums of the disks' contents, so a dump keeps whatever name it came with.
- **Display:** ScummVM's default NTSC TV emulation, the colours of the composite signal, in true
  colour, with the text sharp and white; Mystery House defaults to its monochrome picture.
  `--no-ntsc` switches to the RGB-monitor colours or a green monochrome monitor. The speaker plays
  the games' tones and bells.
- **Saved games:** ScummVM's ADL format, exchanged with a ScummVM build in both directions.
- **Not ported:** the text-to-speech option, and the debugger console and its scripted input.

### Browser host

- `Daventry.Host.Browser` is a Blazor component and `Daventry.Web` a page around it. The frames go
  to a canvas, the sound through Web Audio, the keyboard and mouse come from the canvas, saved
  games live in the browser's local storage, and the page's animation frames are the clock.
- You pick a game folder in the page and nothing leaves the browser; a site can also host games
  of its own and load them from a manifest. Subfolders are read with their paths, so a game whose
  data sits a folder or two down (King's Quest 7, Space Quest 6, Codename: Iceman) is found.
- Picked games can be kept in the browser (IndexedDB) and played again from a "Your games" list.
  Saved games can be downloaded, imported and deleted in the page, so they move between the
  browser, the desktop player and ScummVM.
- Checked with headless Chrome (`tools/BrowserSmoke`): King's Quest I in both versions plays with
  keys, typing and the mouse, saves and restores through its own dialogs, and runs at 60 ticks a
  second at well under a millisecond a tick without ahead-of-time compilation.

## Building and playing

Requires the .NET 10 SDK.

```
dotnet build
dotnet test
dotnet run --project src/Daventry.Player -- <path-to-game-folder>
```

The game folder can hold the game itself or have it a folder or two down. The player also installs
as a dotnet tool, `daventry` (`dotnet tool install -g Daventry.Player --prerelease`).

### Player options

| Option | What it does |
|---|---|
| `--scale N` | The window's starting size, N times the game's (default 3). The window can be resized freely and keeps the picture's aspect ratio. |
| `--fullscreen` | Start in full screen. |
| `--aspect` | Show 320×200 and 640×400 pictures at 4:3, the shape they had on a CRT; screenshots too. |
| `--volume N` | Master volume in percent (default 75, the level ScummVM mixes at). |
| `--sound NAME` | Sound hardware. SCI: `adlib` (default), `pcjr` (Tandy three-voice), `pc` (the speaker), `gm` or `mt32`. AGI: `pcjr` (default), `pc`, `ramp`, `amiga` or `mac`. |
| `--soundfont FILE` | SCI: play the music as General MIDI through this SoundFont (implies `--sound gm`). |
| `--mt32-roms DIR` | SCI: play the music on the MT-32 emulator with the ROMs in this folder (implies `--sound mt32`); `--mt32-model mt32` picks the MT-32 when the folder also holds CM-32L ROMs. |
| `--no-digital-sfx` | SCI: play the synthesized version of sound effects that also have a digital sample. |
| `--mono`, `--color` | ADL: monochrome or colour (colour by default, monochrome for Mystery House). |
| `--scanlines` | ADL: darken every other line, as on a CRT. |
| `--no-ntsc` | ADL: the sharper RGB-monitor colours (or a green monitor) instead of the NTSC TV emulation. |
| `--saves DIR` | Keep saved games in this folder. |

`daventry --help` lists the rest: headless screenshots and WAV captures, scripted keys, typing,
clicks, saves and restores, the random seed, and the picture and view browser.

| Key | While playing |
|---|---|
| Alt+Enter or F11 | Full screen on and off. |
| Ctrl+Alt+A | Aspect ratio correction on and off. |
| Ctrl+Alt+Up, Ctrl+Alt+Down | Volume up and down; the window title shows the setting. |
| Ctrl+Alt+M | Mute. |

### Saved games

Saves go to `Daventry/saves` in the user's application data, one folder for all the games as in
ScummVM: every file name starts with the game id, and the Quest for Glory games find the heroes the
earlier ones exported. `--saves DIR` keeps them anywhere else. The per-game folders of earlier
versions (`Daventry/saves/<game id>`) are moved into it on the next start. Daventry names SCI saves
`<game id>.NNN` where ScummVM uses its target name, so a save moved to ScummVM may need renaming.

### Games on several CDs

Daventry reads a game from one folder, so a game on several CDs is copied into one, as ScummVM asks.
These have been set up and checked; when a disc carries no `RESOURCE.AUD`, ScummVM keeps the sound
per disc, and so does Daventry.

| Game | CDs | What goes in the folder |
|---|---|---|
| Lighthouse | 2 | The game files of both discs, with their `MOVIES`, `ROBOTS` and `PATCHES` folders. |
| RAMA | 3 | Each disc's `RESMAP.00n`, `RESMDT.00n` and `RESSCI.00n`; disc 1's `RESOURCE.SFX` and `VERSION`; the `VMD`, `ROBOT`, `AUDIO` and `PATCHES` folders of all three, merged. |
| Phantasmagoria | 7 | Each disc's `RESMAP`, `RESMDT` and `RESSCI` files, and its `RESOURCE.AUD` and `RESOURCE.SFX` renamed `RESAUD.00n` and `RESSFX.00n` (each disc's sound differs); disc 1's `VERSION` and `37.MAP`; the `ROBOT`, `VMD` and `PATCHES` folders of all seven. Four videos differ between discs (`ROBOT` 90, 91 and 462, `VMD` 5460); keep the copy most discs share. |
| Phantasmagoria 2 | 5 | Each disc's `RESMAP`, `RESMDT` and `RESSCI` files; disc 1's `RESMAP.PAT`, `RESSCI.PAT`, `RESOURCE.AUD`, `RESOURCE.SFX`, `65535.MAP` and the patch files in its root (disc 5's sound files are smaller copies); the `DUK` and `PATCHES` folders of all five. |

### In the browser

```
dotnet run --project src/Daventry.Web               # a development server with the page
dotnet publish src/Daventry.Web -c Release -o out   # static files for any web server
```

- The page asks for a game folder; click the picture to give it the keyboard. The keys are the
  desktop's.
- The Sound choice offers AdLib, PCjr/Tandy, the PC speaker, General MIDI and the Roland MT-32.
  General MIDI asks for a SoundFont once and the MT-32 for its ROM files once (pick them all; the
  page finds the pair). Both are kept in the browser, and the choice is remembered.
- With "Keep in this browser" on (the default), a picked game is stored and listed under "Your
  games" for next time.
- The "Saved games" panel downloads saves (for the desktop player's save folder, or ScummVM's under
  its own target name) and imports them from either.
- `?game=<url of a manifest.json>` loads a game the server hosts instead (`tools/BrowserSmoke`
  shows the manifest).
- The engines log to the browser console, where the host also prints its ticks per second;
  `daventryAudio` in the console shows whether sound is flowing (`state`, chunks `pushed` and
  `audible`, `peak`, `underruns`).
- The engines run on the .NET interpreter unless the page is published ahead of time, which needs
  the `wasm-tools` workload: `dotnet workload install wasm-tools`, then
  `dotnet publish src/Daventry.Web -c Release -p:WasmAot=true`.

## Using the libraries

`dotnet pack -c Release -o artifacts/packages` builds a NuGet package for each library and host,
with symbol packages, and the player as the `daventry` dotnet tool. `tools/PackageSmoke/run.ps1
-Game <folder>` checks them the way a consumer would: a console app and a Blazor WebAssembly app
restore them from that folder and play a game, and the tool is installed and takes a screenshot.
Pushing a tag such as `v0.1.0-alpha` publishes them to nuget.org through GitHub Actions with
Trusted Publishing (`.github/workflows/publish.yml`, whose header lists the one-time setup).

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
`tools/PackageSmoke/BlazorSmoke` is the smallest such page. A host that implements
`ITrueColorGraphicsDriver` as well as `IGraphicsDriver` also shows the true-colour video and the
ADL NTSC colours.

## Layout

| Project | Purpose |
|---|---|
| `Daventry.Core` | Host contract (graphics, input, audio, game data, saves, logging), shared primitives, engine base class, floppy disk images. No platform dependencies. |
| `Daventry.Audio` | Mixer, sound chip emulation, the General MIDI SoundFont synthesiser (MeltySynth, MIT licence) and the MT-32 emulator (a port of Munt, LGPL 2.1 or later) shared by the engines. |
| `Daventry.Agi` | AGI interpreter, with the preAGI games. |
| `Daventry.Sci` | SCI interpreter. |
| `Daventry.Adl` | ADL interpreter, for the Apple II Hi-Res Adventures. |
| `Daventry.Host.Sdl` | SDL3 implementation of the host contract. |
| `Daventry.Host.Browser` | Browser implementation of the host contract as a Blazor component (`DaventryPlayer`): canvas, Web Audio, local storage, folder reading with subfolders and a game library in IndexedDB (`BrowserGameLibrary`). |
| `Daventry.Player` | Command line player. Packaged as a dotnet tool. |
| `Daventry.Web` | Blazor WebAssembly page that plays a game folder you pick, or a game a site hosts. Not packaged. |

Engine assemblies never touch SDL, the file system or a blocking main loop. The host calls
`Engine.Tick(elapsed)` repeatedly and the engine returns how long it would like to wait before the
next tick. Game data is read through `IGameContainer`, which can be a directory or an archive.
`tools` holds the table generators (`PortTables`), the browser and package smoke tests, the MT-32
reference harness and the ScummVM round-trip notes;
`assets/screenshots` holds the screenshots at the top of this page.

## License

GPL-3.0-or-later, the same license as ScummVM, from which the engine code is derived. See `LICENSE`.
The MT-32 emulator in `Daventry.Audio/Mt32` is a port of Munt, whose licence is the LGPL 2.1 or
later; each of its files keeps Munt's copyright notice.
