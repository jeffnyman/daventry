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

### Troll's Tale (preAGI)

- Status: identified by MD5 and refused at engine creation. The library copy is the DOS executable release, which ScummVM marks unsupported; only its disk-image release runs there, so this waits for the booter disk images above.
- Source: `preagi/troll.cpp`, `preagi/picture_troll.cpp` (about 950 lines) over the `PreAgiEngine` base that Mickey and Winnie already use.
- Size: small to medium once a disk image can be read.

### Verification debt

- Everything so far is headless smoke runs of the first minute or two of each game, plus targeted tests. Real play in the SDL window will find bugs no test has. Keep a list of anything seen and fix as reported.
- Saved games were round-tripped with ScummVM 2026.4.0 on 2026-10-22 (King's Quest III, both
  directions; `tools/ScummVmRoundTrip` has the procedure). It found that the 256 flags are packed
  into 32 bytes of bits in ScummVM's file, not one byte each, which shifted every field after them;
  fixed, and `InterpreterTests` now checks the fixed offsets ScummVM reads. ScummVM's gzip-compressed
  files are inflated by `FileSaveStore` on open. `AgiEngine.RequestRestore` carries out a host's
  restore at the end of a cycle (ScummVM's `save_slot`), and the Player's `--restore` uses it.

## preAGI: done (2026-10-22)

In `Daventry.Agi/PreAgi`, mirroring ScummVM's `engines/agi/preagi`. Mickey's Space Adventure and
Winnie the Pooh (DOS) play from their intros through their menus, messages, sounds and saved games.
`PreAgiTests` drives both headlessly through the intro, the inventory, a move to another room and
a save and load round trip, and loads Winnie's save file from the original interpreter.

- `PreAgiEngine` (`preagi.cpp`): the base over `GfxMgr`, with the text-mode text drawing (CGA and
  Hercules attribute mapping), the text area, XOR80 strings, the selection and wait loops (yes/no,
  numbers, any key, backspace; block, process events, allow interrupt), the speaker notes and the
  host plumbing: the game runs as an async method that awaits the frame scheduler wherever the
  original polled, `Tick` queues host input and presents the display, and the mixer is clocked from
  engine time when the host reads no audio (tests, screenshots). `AwaitingMenuSelection` and
  `AwaitingKey` tell a host or test what the game waits for; every string drawn is logged at debug
  level under `agi.preagi.text` (a menu redraw at the same position is logged once).
- `PictureMgrMickeyWinnie` (`picture_mickey_winnie.cpp`): the DOS/Amiga picture format (F0..F9
  with solid-circle brushes, fills that ignore the screen-off flag, the 139/159/158 clipping) and
  the Apple II/C64/CoCo opcode set, drawn at an offset with a maximum instruction count for the
  crystal animation. `PictureMgr` exposes its decoder state to subclasses for this.
- `PcSpeaker`: a square-wave `MonoAudioSource` for Mickey's notes (PIT counter and timer-tick
  lengths from `mickey.exe`).
- `WinnieEngine` (`winnie.cpp`): rooms, objects and their scripts, the parser with its option
  blocks, the menu with keyboard and mouse (hotspots at the picture edges), Tigger and the mist on
  the ten-minute timer, the wind after 150 moves, Owl's help, the game-over song, the AGIv1 sounds
  through `SoundGenPcjr` (sound 10 or 11 for the wind by sound emulation), the `WINN` save file
  (`winnie.savegame` in the host save store) and the original interpreter's 116-byte save (read
  from the low byte of each word; ScummVM reads the high byte and gets zeros).
- `MickeyEngine` (`mickey.cpp`, `MickeyData.cs`): the planets' `.dat` files and `mickey.exe` as
  the text, menu-patch and sound sources, the two-row menus with their executable patches, the
  action table (0x00 to 0xC4), the crystal hunt state, the XL30 and ship-light animations, the
  Sierra `logos.bcg` logo, the inventory, the air supply, the `MICK` saves (`mickey.sNN`).
- `AgiEngineProvider.Create` returns these engines for `AgiGameType.PreAgi` by `Gid`; the
  unsupported-variant check now runs first so Troll's Tale and the CoCo Winnie are refused with
  ScummVM's reason.

### preAGI: deferred

- Winnie's Amiga version runs but has no sound (ScummVM stubs it too); the Apple II, C64 and CoCo
  versions are refused (ScummVM starts them but their pictures are wrong).
- The mouse cursor does not change over the direction hotspots (ScummVM has the same TODO).
- Mickey's ship lights blink left to right, which ScummVM marks as not the original pattern.
- The Player's `--save` and `--restore` do nothing for these games; they save through their own
  menus.

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
- Master volume (2026-10-22): `IAudioDriver.Volume` (0 to 256, default 192 like ScummVM's mixer) is
  applied by both hosts; the Player's `--volume N` takes percent, Ctrl+Alt+Up and Down change it in the
  window, Ctrl+Alt+M mutes, and the window title shows the setting (nothing is drawn on screen, so in
  full screen the change is heard, not seen).
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

## SCI32: done (2026-10-22)

All in `Daventry.Sci`, mirroring ScummVM's SCI32 files; every SCI2/SCI2.1 game in the library
(KQ7 2.00b, SQ6 CD, PQ4 floppy, QFG4 floppy) boots, plays its intro, reaches gameplay, and saves
and restores. `Sci32Tests` boots each for 600 ticks, decodes KQ7 speech through both audio
decoders, and saves and restores QFG4 on its class screen (the title screens of KQ7 and SQ6 are
not valid save points: their replay methods read room properties and the ego that only exist in
play, as in the original).

- VM and kernel (`Engine/KernelTables32.cs`, `Kernel/K*32.cs`, `Segments32.cs`): the SCI2, SCI2.1
  and SCI3 kernel name tables with ScummVM's signatures and sub-op tables, the kernel type
  autodetection (`Sound::play`'s kDoSound number), the SCI32 opcode widths and comparison rules,
  `updateInfoFlagViewVisible`, the Array (type 11) and Bitmap (type 13) segments with their kernels
  (kArray, kString, kBitmap), kList additions (ListEachElementDo, FirstTrue, AllTrue, Sort), the
  SCI32 file, save and misc kernels (kGetConfig, kPlatform, kWinHelp...). SciArray/SciBitmap save
  and load; GC roots non-collectable bitmaps.
- Graphics (`Graphics/GfxFrameout.cs`, `Plane32.cs`, `ScreenItem32.cs`, `CelObj32.cs`,
  `GfxPalette32.cs`, `GfxRemap32.cs`, `GfxCursor32.cs`, `GfxText32.cs`, `GfxTransitions32.cs`,
  `GfxPaint32.cs`, `GfxControls32.cs`, `Sci32Common.cs`): planes and screen items with the stable
  index lists, erase and draw lists, the show list and the hardware frame; view, pic, mem and colour
  cels with the RLE readers, scalers (global scaling cadence at 320x200), mappers and the LRU cel
  cache; the 256-colour palette (hunk palettes, vary, ten cyclers, fade table, gamma tables), the
  colour remapper, the cursor with its back buffer, the text renderer (bitmaps, wrapping, `|a|`
  `|c|` `|f|` codes, the 1218-byte system font extracted from `scifont.cpp`), all show styles and
  scrolls (awaited: the blocking loops yield to the host), kAddLine, kEditText/kInputText, scroll
  windows, kMessageBox (answered with its default button, logged), hot rectangles.
- Audio (`Sound/Audio32.cs`, `Audio32Streams.cs`): the mixer with up to ten channels (SOL DPCM 8/16
  mono and stereo with the GK1 pop repair, WAVE, raw PCM), linear resampling, attenuated mixing,
  fades, pan, signal monitoring, the Robot audio stream (interleaved halves with interpolation);
  kDoAudio32, and kDoSound's sample paths route through it (play, stop, pause, fade, volume, loop).
- Video (`Graphics/RobotDecoder.cs`): the Robot player v5/v6 (LZS cels, line decimation, primers,
  cue points, frame-rate drift against the audio), hooked into the frameout; KQ7's intro plays.
- Save and restore: the array and bitmap segments, GfxPalette32, GfxRemap32, GfxCursor32, Audio32
  (SCI3 locks) and Video32 (SCI3 robots) in ScummVM's format; SCI32 skips the SCI16 port and
  screen reset; the thumbnail comes from the hardware frame.
- Harness: headless clicks now move the mouse 6 ticks before the press and release 6 ticks after
  (SCI32 scripts only accept a click on something the mouse hovered, and track the button); the
  kernel trace logs return values; `kSetShowStyle` and `Audio32` log at debug level.

### SCI32: deferred

- SCI3 (Lighthouse, LSL7, Phantasmagoria 2, RAMA): the kernel table and the version switches are in
  place, the SCI3 script format, relocation and `kDoSound` differences are not. No data in the library.
- Video players other than Robot: VMD (`video32.cpp` VMDPlayer, `video/coktel_decoder.cpp`), AVI
  (KQ7 Windows), SEQ (`kShowMovie` of SCI2), Duck (Phantasmagoria 2), QuickTime (Mac). SQ6 calls
  kPlayVMD for one cut scene, but this copy has no VMD files; KQ7 DOS uses Robots throughout.
- KQ7's video upscale option (ScummVM's `enable_video_upscale`, global 32 in `kGlobalVarKQ7UpscaleVideos`).
- kMessageBox shows no dialog; kScummVMSaveLoad only answers a restore requested by the host (see
  `GuestAdditions`), otherwise it cancels. A host dialog hook would be the way for both.
- Verification: headless runs of each intro, SQ6 up to its first street, QFG4 to the class choice;
  real play in the SDL window has not been done. SQ6's Start New Game button does not react to
  headless clicks (single or double, at 320,202 in its 640x480 coordinates) although the pointer
  reaches it; the same open question as KQ6 CD's title buttons below.
- Saved games were exchanged with ScummVM 2026.4.0 for QFG4 (class screen, both directions). A
  restore requested by the host now goes through the game's own Game::restore with SRDialog::doit
  patched to return the pending slot (`GuestAdditions`, ScummVM's launcher restore): restoring
  underneath the scripts brought QFG4 back on its title menu instead of the class screen.

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
Iceman's in `ICE`; `EngineRegistry.DetectInDirectory` looks up to two folders down when the folder itself
holds no game, and the Player and the real-game tests use it. King's Quest 6 CD's audio36 maps hold
KQ6-only lip-sync entries, so a `ResourceManager` reading them has to know the game id (the engine sets
it before `Init`); all 6155 speech entries of the library copy decode. Space Quest 4 floppy view 17 is corrupt in this copy (an LZW code past
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
- Saves are named `<game id>.NNN` (ScummVM uses its target name), so exchanging a file means renaming it
  where the target has a suffix (`kq6-cd`). Round-tripped with ScummVM 2026.4.0 on 2026-10-22 in both
  directions for KQ1 SCI, KQ5, SQ5 and QFG4 (`tools/ScummVmRoundTrip`): the format matched, but a
  restore requested by the host (`--restore`, ScummVM's `-x`) used to be carried out at the start of
  kGetEvent, which then went on with its arguments from the old state and crashed on a clone made just
  before; kGetEvent and kWait now end the call after the restore, as ScummVM's kGetEventHook does
  (`SaveLoadTests.RestoreRequestedDuringTheIntro`). ScummVM's gzip-compressed files are inflated by
  `FileSaveStore` on open.
- Debugging aids: `DAVENTRY_TRACE_KERNEL=1` logs every kernel call, `DAVENTRY_TRACE_FILE=path` writes the
  same trace to a file without slowing the interpreter (with `-- tick N` markers). Headless runs
  clock audio from engine time through `ClockedSource`, so speech-timed scenes advance without `--wav`.
- Headless clicks: SCI0/SCI01 title buttons (KQ1 SCI) select on the first click and activate on the
  second, so `--click` needs two clicks 20 ticks apart. KQ6 CD's title buttons still do not react even
  to a double click although the scripts receive the press and release and poll the four buttons;
  untested in the SDL window, worth checking there first.
- KQ1 SCI's own save and restore dialogs work headless: double-click Begin Game, F5, type the name, F7,
  Enter (`--click "500:205,162;520:205,162" --key "1200:f5,2100:f7,2400:return" --type "1500:my test"`).
- Two things only the SDL window showed (KQ6 CD's intro in full screen, 2026-10-22): a palette change
  with no pixel change (kPaletteSetIntensity fades, palette cycling, pal-vary) never reached the screen
  because nothing presented a frame, so the text and the title faded in invisibly and popped; the engine
  now presents whenever the palette version changes (`PalettePresentTests`). And `TickResult.Delay`
  counts from the start of the tick, so a host must sleep only the remainder after the tick's own work
  and the present; the SDL host used to sleep the whole delay on top, which cost each frame's draw time.
  Headless runs cannot catch either: headless never calls kDoAudio, so KQ6 CD's intro runs text-timed
  there and speech-timed in the window, and the headless host ignores the delay.

## Next stages

1. SCI0 through SCI2.1: real play testing in the SDL window, fixing what it finds.
2. NuGet packaging of `Daventry.Core`, `Daventry.Audio`, `Daventry.Agi`, `Daventry.Sci`.
3. Blazor host.
4. SCI3, once there is game data to test against.
