# Daventry roadmap

What is done, what is deferred and what comes next. Source references are to the ScummVM checkout
(`scummvm/engines/agi`, `scummvm/engines/sci`, `scummvm/audio`).

## AGI: done

Resource layer (v2, v3, LZW), pictures, views, sprites, motion, logic interpreter with both
opcode tables, timers, keyboard and mouse, text and parser, system dialogs, PCjr and Sarien
sound, MD5 detection with WinAGI fallback, pull-down menus, inventory screen, save and
restore in ScummVM's file layout, shake.screen (the display jumps 8 pixels right and 4 down with a
black edge, four times per repeat; the engine presents a shifted copy, so the host needs nothing).
Every DOS AGI game in the library runs.

## AGI: deferred

Ordered by how likely it is to matter for the games we actually have.

### Hercules font and render modes

- Status: Hercules green and amber palettes exist; the hi-res font and 640x400 upscaling path are only partly wired.
- Source: `font.cpp` (`fontData_Hercules`), `graphics.cpp` upscaling.
- Size: small to medium. Low value unless someone wants the Hercules look.

### Amiga and Atari ST picture transitions

- Status: `ShowPictureWithTransition` falls back to the plain PC behaviour.
- Source: `graphics.cpp` `transition_Amiga`, `transition_AtariSt`.
- Size: small. Only matters for Amiga and Atari ST data, which the library does not have.

### Apple IIgs sound

- Status: resources of type sample (1) and MIDI (2) are loaded but finish immediately with a warning.
- Source: `sound_2gs.cpp`, `sound_2gs.h` (about 1,000 lines: wavetable synthesiser plus a MIDI sequencer and sample playback), `sound_midi.cpp` for the generic MIDI path.
- Size: large. Only matters for Apple IIgs data.

### Apple II and CoCo3 sound

- Status: same as above, the raw formats are recognised and skipped.
- Source: `sound_a2.cpp`, `sound_coco3.cpp`.
- Size: medium. Only matters for Apple II and CoCo3 data.

### Apple IIgs speed menu

- Status: not ported. ScummVM adds a Speed menu to the menu bar for IIgs games when the option is on.
- Source: `menu.cpp` `submit`, `cycle.cpp` the `appleIIgsSpeedControllerSlot` handling.
- Size: small.

### Right-to-left text (Hebrew)

- Status: the menu, inventory and saved-game dialogs skip the RTL branches.
- Source: `isLanguageRTL()` branches in `menu.cpp`, `inv.cpp`, `systemui.cpp`, `text.cpp`.
- Size: small to medium. Only matters for the Hebrew fan translations.

### Booter disk images (AGI v1)

- Status: table entries with file name `*` are skipped by the detector; the v1 loader does not exist.
- Source: `disk_image.cpp`, `loader_v1.cpp`, `detection.cpp` `detectPcDiskImageGame` and `detectA2DiskImageGame`, `loader_gal.cpp` for the earliest format.
- Needs: reading 360k PC and Apple II disk images, the v1 directory layout, the v1 opcode table (already present in `Opcodes.cs`), PCjr v1 sound (already present).
- Size: medium to large. Needed for the 1984 King's Quest and the Apple II releases, and for Troll's Tale, whose only supported release is a disk image.

### preAGI games

- Status: Mickey's Space Adventure, Winnie the Pooh and Troll's Tale are identified by MD5 and refused at engine creation. Mickey and Winnie data are in the library; the Troll's Tale copy is the DOS executable release, which ScummVM itself does not run.
- Source: `preagi/preagi.cpp`, `preagi/mickey.cpp`, `preagi/winnie.cpp`, `preagi/troll.cpp` (about 4,500 lines), sharing only the picture decoder and `GfxMgr` with AGI.
- Needs: a `PreAgiEngine` base using the existing picture and text code, then one class per game.
- Size: medium. Self-contained, could be done at any point.

### Codename Iceman style subfolders

- Status: `DirectoryGameContainer` now indexes two levels deep so Mickey's `obj/` file is found, but a game whose whole data set lives in a subfolder (Iceman's `ICE`) is not detected from the parent folder.
- Needs: the registry or the player to try subfolders as containers when the parent yields nothing. Mostly an SCI concern.
- Size: small.

### Verification debt

- Everything so far is headless smoke runs of the first minute or two of each game, plus targeted tests. Real play in the SDL window will find bugs no test has. Keep a list of anything seen and fix as reported.
- Save files are written in ScummVM's layout but have not yet been round-tripped through a real ScummVM build. Worth doing once: save in Daventry, load in ScummVM, and the reverse.

## SCI: done

- Resource layer (`Daventry.Sci/Resources`): maps, volumes and patches of SCI0 through SCI3, every
  compression, the SCI1 and SCI1.1 audio maps, SCI1 audio language maps (`AUDIO%03d.MAP`), version and
  game-object detection, MD5 detection generated from ScummVM's table by `tools/PortTables/sci_detection.py`.
- Graphics (`Daventry.Sci/Graphics`): screen, palette with pal-vary, ports and windows, pictures, views,
  fonts, cache, paint, animate, transitions, text, controls, compare, the menu bar and pull-down menus,
  the software mouse cursor. `--picture` and `--view` still work as a resource viewer.
- VM (`Daventry.Sci/Engine`): segment manager, scripts, objects, the p-machine, kernel dispatch with
  ScummVM's signatures and the workaround tables generated by `tools/PortTables/sci_workarounds.py`,
  lists, maths, strings, movement, events, the parser (vocabulary, suffixes, GNF grammar, Said matching),
  message resources, game features detection. Games boot from their own scripts; SCI0 games answer typed
  commands.
- Sound (`Daventry.Sci/Sound`): the DOSBox OPL2 emulator, Sierra's AdLib driver with the game's patch
  bank, sound resources, the extended MIDI parser, the music engine with its play list, fades and channel
  remapping, kDoSound, the digital samples of sound resources, kDoAudio speech (SOL, WAVE, raw) and kDoSync
  lip sync. Music is clocked from the audio output, or from engine time when the host reads none, so
  cue-driven scenes advance headless.
- Pathfinding (`Daventry.Sci/Engine/Pathfinding.cs`, `Kernel/KPathing.cs`): kAvoidPath with the
  visibility graph and A* over the polygon vertices, the start and end fixups, keyboard mode, the
  LB2/LSL5/QFG1VGA workarounds, kMergePoly and kIntersections. The Player's `--click T:x,y` drives
  it headless; KQ5 CD's Graham walks across Crispin's clearing through it.
- Script patches (`Daventry.Sci/Engine/ScriptPatcher.cs`, `ScriptPatchTables.g.cs` generated by
  `tools/PortTables/sci_script_patches.py` from `script_patches.cpp`): all 911 of ScummVM's patches for
  54 games, the magic-DWORD matcher with selector resolution, and the per-game enabling (KQ6 CD and
  LB2 CD audio + text, KQ4's missing waterfall view, LB2's control panel, the SQ4 CD timepod room and
  so on). A test loads every script of every library game through the patcher and lists what applied;
  KQ6 CD gets 52 patches, LB2 30, QFG3 28, SQ1 VGA 25, QFG1 VGA and Longbow 24. Not enabled: the KQ5
  Windows GM signal checks (the alternate GM soundtrack is not ported). KQ5 CD's falling sound patch
  follows the digital sound effects preference, as in ScummVM.
- Save and restore (`Daventry.Sci/Engine/Savegame.cs`, `SaveSerializer.cs`, the `*.SaveLoad.cs` partial
  classes, `Kernel/KSave.cs`): ScummVM's saved game format version 46, byte for byte (metadata and
  thumbnail, the picture port, every segment but the hunks with the script string heaps, the class
  table, the sound play list, the palette with pal-vary, the script windows, the synonyms), the
  restore path through the game object's replay method, the delayed restore at kGameIsRestarting and
  kGetEvent for the host, and the per-game fix-ups after restoring. Verified by round trips in KQ1 SCI,
  KQ5 CD and QFG1 VGA, and by an object-by-object comparison before and after a restore. The Player's
  `--save T:N`, `--restore T:N` and `--saves DIR` drive it headless.
- Tests: every resource of every library game decompresses, every picture, cel and font decodes, every
  sound parses and mixes, every SCI16 game boots for 600 ticks, KQ1 renders music through the engine,
  KQ5 CD and KQ6 CD speech decodes.

## SCI: deferred

Ordered by what blocks playing a game through.

### Sound drivers other than AdLib

- Status: everything plays through the AdLib driver (track type 0x00 for SCI1, device masks 0x04/0x09
  for SCI0). Sounds with no AdLib data are stopped at once.
- Source: `sound/drivers/midi.cpp` (MT-32 and General MIDI, needs an MT-32 emulator or a host MIDI
  output), `pcjr.cpp` and `pcspeaker.cpp` (small, the `Daventry.Audio` PCjr synth could be reused),
  `amigamac0.cpp` and `amigamac1.cpp` (Amiga and Mac sample-based drivers), `cms.cpp`, `fmtowns.cpp`.
- Size: PCjr and PC speaker small; MT-32 large; the rest only matter for data the library lacks.

### CD audio tracks

- Status: `kDoAudio` CD sub-op warns; Jones CD and Mother Goose 256 CD red-book audio is not played.
- Source: `engine/ksound.cpp` `kDoCdAudio`, `sound/audio.cpp` `audioCdPlay`.
- Size: small, but needs a host CD audio facility (or track files) that does not exist yet.

### Windows versions and alternate soundtracks

- Status: the Windows SCI1.1 releases are detected but run as DOS: no hires KQ6 cels, no alternate GM
  soundtrack (`useAltWinGMSound`), no SQ4 CD Windows samples.
- Source: `engine/features.cpp` `useAltWinGMSound`, `sound/soundcmd.cpp` `getSoundResourceId`,
  `graphics/view.cpp` hires handling.
- Size: medium.

### Resource layer leftovers

- Macintosh resource forks, SCI2.1 chunk resources, the built-in resource patcher
  (`resource_patcher.cpp`), GM and D110 patch injection, the LRU cache (resources are never unloaded).

### Graphics leftovers

- Macintosh 480x300 and 640x400 upscaled modes, SCI16 colour remapping (`remap.cpp`, QFG4 demo and
  Catdate), the custom palette mods in `scifx.cpp`. The script patches that add the "BOTH" speech+text
  mode to KQ6 CD and Laura Bow 2 are applied; whether the mode works end to end is untested.
- LSL6 picture 100 renders the Sierra logo in magenta from its embedded palette; check against the
  scripts now that they run (they may load palette 100 over it).

### Library quirks (not bugs)

King's Quest 7's data sits in a nested `King's Quest 7` folder, Space Quest 6's in `CDROM`, Codename
Iceman's in `ICE`; the player needs to look one folder down. The King's Quest 6 CD copy has a truncated
RESOURCE.AUD that the maps overrun, so most speech entries past the first few hundred do not load
(ScummVM would report the same). Space Quest 4 floppy view 17 is corrupt in this copy (an LZW code past
the table), which ScummVM rejects the same way; its views 992 and 993 are stored with the plain LZW1
method but hold reordered view data, so they decode to nonsense in ScummVM as well. Leisure Suit Larry 6
view 111 is larger than 64 KB and the SCI1.1 volume header stores sizes in 16 bits, so it arrives
truncated; the cel unpacker leaves the missing pixels transparent.

### Verification debt

- As with AGI: headless smoke runs of the first minute of each game plus targeted tests. Playing in the
  SDL window will find the rest. The speed benchmark rooms (KQ5, KQ6) busy-wait on `kGetTime` and log
  "script loops without waiting" while they run; that is expected.
- A save made on a title screen whose room has no picture (KQ6 CD's room 100) cannot be replayed: the
  game object's replay method draws picture 0 and the engine stops, exactly as ScummVM does. Saves made
  in play are fine. ScummVM's own save dialog (a patched Game::save calling kSaveGame with a null game
  id) is not provided; the games' dialogs are used.
- Saves are named `<game id>.NNN` (ScummVM uses its target name), so exchanging a file means renaming it.
  Round-tripping a file through a real ScummVM build has not been done yet; worth doing once per direction.
- Debugging aids: `DAVENTRY_TRACE_KERNEL=1` logs every kernel call, `DAVENTRY_TRACE_FILE=path` writes the
  same trace to a file without slowing the interpreter (with `-- tick N` markers). Headless runs
  clock audio from engine time through `ClockedSource`, so speech-timed scenes advance without `--wav`.
- Headless clicks: SCI0/SCI01 title buttons (KQ1 SCI) select on the first click and activate on the
  second, so `--click` needs two clicks 20 ticks apart. KQ6 CD's title buttons still do not react even
  to a double click although the scripts receive the press and release and poll the four buttons;
  untested in the SDL window, worth checking there first.
- KQ1 SCI's own save and restore dialogs work headless: double-click Begin Game, F5, type the name, F7,
  Enter (`--click "500:205,162;520:205,162" --key "1200:f5,2100:f7,2400:return" --type "1500:my test"`).

## Next stages

1. SCI0 through SCI1.1: real play testing in the SDL window, fixing what it finds.
2. SCI32.
3. NuGet packaging of `Daventry.Core`, `Daventry.Audio`, `Daventry.Agi`, `Daventry.Sci`.
4. Blazor host.
