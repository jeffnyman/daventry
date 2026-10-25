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

### Booter disk images (AGI v1, Apple II, GAL)

- Status: detected since 2026-10-24 and refused. `AgiDetector` ports `detectPcDiskImageGame` and
  `detectA2DiskImageGame` (360k `.img`/`.ima` files, and Apple II `.do`/`.dsk`/`.img`/`.nib`/`.woz`
  through `Core.Formats.DiskImage`): it hashes LOGDIR through INITDIR or the known KQ2 and Black
  Cauldron positions, or GAL's directory, and matches the `*` entries of the table, after the
  file-based table and before the fallback, as ScummVM's `detectGame` does. `AgiDiskImage` holds
  `disk_image.h`'s layout and the open helpers; `AgiDetectedGame.DataFile` names the matched image.
  The provider refuses v1 ("PC booter disks (AGI v1) are not supported yet") and Apple II, and GAL
  carries ScummVM's own "Early King's Quest releases are not currently supported."
- The library's `Kings Quest I - Booter` (a KryoFlux dump: two sector images plus the flux files)
  is the Tandy 1000 release 01.01.00 of 1985, found by its GAL directory hash at 0x1400. ScummVM
  2026.4.0 detects it too, then stops: GAL ("Game Adaptation Language not implemented yet") has
  only a resource loader there (`loader_gal.cpp`, `loader_gal_a2.cpp`), no interpreter. Running it
  would mean writing one from the original game with no ScummVM source to follow.
- Not ported: `loader_v1.cpp` (KQ2, Black Cauldron and Donald Duck's Playground booters) and
  `loader_a2.cpp` (the Apple II AGI releases). No such images in the library to test against.
- Size: medium each, once there is data. The v1 opcode table and PCjr v1 sound are already present.

### Troll's Tale (preAGI): done (2026-10-24)

- `PreAgi/TrollEngine.cs` and `PreAgi/PictureMgrTroll.cs` port `troll.cpp` and
  `picture_troll.cpp`: the data read out of the booter image between its gaps (plus the one
  sector that is elsewhere), the intro with the alternating copyright line, the tutorial, the
  credits, the room menus built in a C string as the original does (the option texts are 35-byte
  fields without terminators), the treasures, the Troll that appears one time in three and blocks
  taking, the flashlight, the tunes, Ctrl-S, the treasure list on F or T, the game over. The
  picture decoder has the F3 no-op/terminator and the Troll mode fills (white first, then the
  colour; white fills stop on colour 11, vertical ones on white too).
- The library copy is the DOS release: `troll.com` and `game.dat`. `game.dat` has the checksum and
  size of ScummVM's supported `troll.img` booter image, and ScummVM 2.7.0 and 2026.4.0 both detect
  it as supported once it is copied to that name; ScummVM's table marks `game.dat` itself
  unsupported. Daventry plays `game.dat` as the booter release (Extra "booter image in game.dat").
- Verified: ScummVM 2026.4.0 on the copied `troll.img` renders the first room and the flashlight
  field identically to Daventry. `PreAgiTests` covers the intro, the tutorial, the flashlight and
  the treasure list, and the Troll guarding the money until the picture changes; a 300-move random
  walk took treasures with no warnings.
- The Apple II disk in the library (`Trolls_Tale_AppleII`) is a different program, an Applesoft
  BASIC game from 1983 that ScummVM does not know; it is not detected.
- ScummVM's `_treasuresLeft` is unset until the game starts, and the tutorial's menu can open the
  treasure list before that; Daventry starts it at 16, so the list says NONE.

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
  unsupported-variant check now runs first so the CoCo Winnie is refused with ScummVM's reason.
  Troll's Tale followed on 2026-10-24 (see the AGI deferred list).

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

- SCI3: see "SCI3: done" below.
- Video players other than Robot and VMD: AVI (KQ7 Windows), SEQ (`kShowMovie` of SCI2), Duck
  (Phantasmagoria 2), QuickTime (Mac). SQ6 calls kPlayVMD for one cut scene, but this copy has no
  VMD files; KQ7 DOS uses Robots throughout.
- KQ7's video upscale option (ScummVM's `enable_video_upscale`, global 32 in `kGlobalVarKQ7UpscaleVideos`).
- kMessageBox shows no dialog; kScummVMSaveLoad only answers a restore requested by the host (see
  `GuestAdditions`), otherwise it cancels. A host dialog hook would be the way for both.
- Verification: headless runs of each intro, SQ6 up to its first street, QFG4 to the class choice;
  real play in the SDL window has not been done beyond the title buttons of KQ6 CD and SQ6, which
  work with a real mouse (2026-10-22). SQ6's Start New Game button also works headless
  (`--click 2500:320,202`, screen coordinates; the scripts see (160,85) in their 320x200 space and
  the intro starts in the same room 500); `TitleButtonTests` keeps both title buttons covered.
- Mixed-Up Mother Goose Deluxe (English/Spanish Windows CD, 2026-10-25): runs headless from the
  title through the character board and the name entry into the first scene, with speech and
  General MIDI music. `GameFeatures.GeneralMidiOnly` (ScummVM's `generalMidiOnly`) makes it take
  the SoundFont whatever device is chosen. The game needs 26 files of the disc (`RESOURCE.*` and
  the `*.MAP` audio maps, 123 MB); the rest is the Playtoons and Dr. Brain demos. Not ported: the
  other language directories of `kSetLanguage` (`changeAudioDirectory`); this disc has none.
- Saved games were exchanged with ScummVM 2026.4.0 for QFG4 (class screen, both directions). A
  restore requested by the host now goes through the game's own Game::restore with SRDialog::doit
  patched to return the pending slot (`GuestAdditions`, ScummVM's launcher restore): restoring
  underneath the scripts brought QFG4 back on its title menu instead of the class screen.

## SCI3: done (2026-10-25)

Tested with Leisure Suit Larry 7 (English, one CD: `RESMAP.000`, `RESSCI.000`, `RESOURCE.AUD`,
`RESOURCE.SFX`; the disc image was reduced to these). Three of its scripts are larger than 64 KB.

- `RegT`: offsets are 18 bits wide. `Make` truncates to 16 bits like make_reg, `Make32` keeps 18
  like make_reg32 (used where ScummVM uses it); `WithOffset`/`IncOffset` wrap at 18 bits, which
  differs from ScummVM before SCI3 only for an offset already outside any script. Saves store
  ScummVM's packed form (offset bits 16 and 17 in the top of the segment word).
- `Script`: the SCI3 header (code, string and relocation offsets, locals and export counts),
  dword-aligned locals, objects and strings, `relocateOffsetSci3`, `relocateSci3`, exports
  relative to the code block or relocated (`validateExportFunc`'s relocSci3), classes registered
  before the objects are initialised.
- `SciObject`: `initSelectorsSci3` (the 256-byte selector bank, 64-byte groups with their type
  masks), species, superclass and -info- kept outside the properties, the view-visible flag per
  selector group, relocation by property position, the name from the first property.
- VM: properties addressed by selector (`locateVarSelector`), lofsa/lofss through the relocation
  table (lofs type SCI3), the `info` and `superP` opcodes, `super` leaving the superclass in the
  accumulator, and the unsigned dividend of `%` from SCI2.1 Late on (LSL7's ocean motion; this
  also changes Torin, LSL7 and the other late SCI2.1 games).
- Graphics: the SCI3 erase pass of `Plane::calcLists` (items sorted in place; picture cels after
  the first only redrawn once a view was drawn over them).
- Saves: the SCI3 object fields and string heap, the robot closed before a restore, the version
  from LSL7's and Phantasmagoria 2's Str object, the save and restore patches of the guest
  additions (export 2 of script 64866 for LSL7 and Torin, `srGetGame` for Phantasmagoria 2, RAMA's
  Save and Restore), each game's kScummVMSaveLoad answer, Phantasmagoria 2's bookmark auto-save
  and Lighthouse's "rst" new-game save.
- Lighthouse's sounds that are not restarted while they play (ScummVM's
  `isUninterruptableSoundPlaying`).
- Verified against ScummVM 2026.4.0: the logo waits for Larry's voice, room 110 opens on black
  and fades into the burning bedroom, where play starts; the screens match at 6, 18, 30, 60, 120
  and 210 seconds. ScummVM has to run with `SDL_AUDIODRIVER=dummy` here: without an audio device
  its audio clock stands still and the logo never ends. `Sci3Tests` checks the register packing,
  the opening (logo in room 100, bedroom in room 110 at tick 3600) and a save and restore in the
  bedroom through LSL7's own restore.
- Not ported: the Polish LSL7's patches in an InstallShield cabinet, the Duck player
  (Phantasmagoria 2 is mostly video), and ScummVM's save dialog (kScummVMSaveLoad answers only a
  restore the host asked for). RAMA and Phantasmagoria 2 have not been run.

### Lighthouse and the VMD player: done (2026-10-25)

Tested with Lighthouse (English DOS, two CDs; the folder holds the game files of both discs with
their `MOVIES`, `ROBOTS` and `PATCHES` folders, 947 MB).

- `Resources/SciSearchPath`: the subfolders SciEngine adds to ScummVM's search path (`robots`,
  `movies`, `patches`, `patch`, `aud`, `vmd`, the Mac folders and the rest), whose files are
  found and listed by their bare names, so patch files in a `patches` folder are read as well.
  Folders two levels down (`music/22s16`, `german/msg`) are beyond what the containers scan.
- Multi-disc audio (`addAppropriateSources`, `readResourceMapSCI1`): games with RESSCI.001 and
  no RESOURCE.AUD read each disc's audio maps as they come, against that disc's RESAUD.00n or
  RESSFX.00n (RESAUD.001 and RESSCI.PAT for the patch volume, RAMA's RESSFX fallbacks).
  Lighthouse keeps all its audio maps in RESSCI.PAT: 132 speech resources that were not indexed
  before. `RealGameTests` now finds games whose maps start at RESMAP.001.
- `Graphics/VmdDecoder`: the VMD format of the Sierra games (the 814-byte header with its
  palette, 8-bit frames in Coktel's whole, sparse, RLE, 4X and 2Y blocks, LZ77 packing, palette
  changes, 8-bit raw and 16-bit DPCM sound including old-style stereo), port of ScummVM's
  VMDDecoder and CoktelDecoder. Frames fall due on the engine clock (ScummVM counts on the audio
  track's position, which the mixer advances in step); the sound goes to the mixer through a
  queue read on the audio thread. Not ported: Indeo 3, 16 and 24-bit frames, ADPCM sound, the
  Addy 5 header, embedded files, subtitles.
- `Graphics/VmdPlayer` and kPlayVMD: open (also from a VMD resource), init with its flags, the
  composited path of SCI3 (a bitmap with a hunk palette as a screen item in a plane of its own)
  and the overlay path of SCI2.1 (frames into the game buffer, palette submitted), yields to the
  scripts, the blackout plane, censorship blobs, palette restriction, the brightness boost
  (inactive, as ScummVM's black-lined video option is off), kPlayVMDPlayUntilEvent ending at once
  while the host restores (`kPlayDuckPlayVMDHook`), and `VMDOpenStopsAudio`.
- Fixed on the way: SCI3 export calls and local calls keep 18-bit addresses (Lighthouse's script
  9 is a 150 KB patch), and so does `findObjectByName`.
- Verified: the 88 VMDs of Lighthouse decode to their last frame, 85 with sound that never
  outlasts the frames (`EveryLighthouseVideoDecodesWithItsSound`); the logo VMD and the menu look
  as in ScummVM 2026.4.0; New Game plays the 34-second intro (its sound measured in the mix) into
  room 20, where a save restores (`LighthousePlaysItsVideosIntoTheFirstRoomAndSaves`).
- Not verified: the timing of the logo against ScummVM (a window screenshot could not settle it);
  the SCI2.1 overlay path has no game with VMD files in the library.

### kFileIO: done (2026-10-25)

- `Engine/SciFile` and `Engine/Kernel/KFile`, port of `file.cpp` and the file half of
  `kfile.cpp`: a game's files go to the host's save store as `<game id>-<name>` (ScummVM's
  wrapped names, lowered, English part of a multilingual name), and reads fall back to the game's
  own files. Open (all three modes, and the rewrite-in-memory files of Phantasmagoria and
  PQ:SWAT), close, raw and line reads (`readLine` with CR and CR/LF), writes, seek, unlink,
  exists, rename, copy, FindFirst/FindNext, and the SCI32 byte and word calls. Handles are closed
  after a restart or a restore, as in `runGame`.
- The game-specific cases: SQ4's `sq4sg.*` and `/\` names, ECO2's printer, the VERSION file
  (LSL6 hires' `VERSION.`), Shivers' `.SG` and Mother Goose Deluxe's `.DTA` files made up from the
  saves (the save header now carries their scores and avatar), KQ7's `temp.tmp`, PQ:SWAT's
  profile folders, Phantasmagoria 2's RESDUK.PAT, RAMA's saves written through kFileIO with
  ScummVM's header and whole registers in their words, LSL7's `autosvsg.000`, KQ5 FM-Towns' fixed
  save paths, GK1's `buster`, LSL5's default `memory.drv`, Pepper's CDAUDIO, the SCI32 save
  catalogues (KQ7's and RAMA's made from the saves, the others answered by the saves' existence),
  and the virtual listing of the QFG import rooms.
- kDeviceInfo deletes a save through its throw-away name, kCheckFreeSpace answers its three
  sub-ops, and `SavePattern` ports `matchString` (ScummVM's `*`, `?` and `#` wildcards) for the
  store listings and kDeviceInfo's path comparison.
- Verified: LSL7 writes `larry7.prf` on its first start and reads it on the next
  (`Larry7KeepsItsPreferencesBetweenRuns`); LSL3 keeps `larry3.drv`; SCI1.1 and SCI2 games read
  their VERSION files; files round-trip through the kernel calls, kDeviceInfo deletes the save it
  names and the catalogues have KQ7's and RAMA's layouts (`FileIOTests`).
- The desktop player now keeps all the games' saves in one folder (`Daventry/saves`), as ScummVM
  does, so the QFG import rooms find the earlier games' exports; the per-game folders of earlier
  versions are moved into it at start-up (`FileSaveStore.MergeSubfolders`, a name already taken
  stays put with a warning; `SaveFolderTests`). The browser's store was already shared.
- Not done: RAMA's play time is not taken from its saves (the tick count is the
  host's clock); the fan-made sciAudio commands are ignored.

### RAMA: run (2026-10-25)

- The English DOS/Windows CDs (ScummVM's entry from bug #4583) merged into one folder: the
  resource files of the three discs, disc 1's RESOURCE.SFX and the VMD, ROBOT, AUDIO and PATCHES
  folders. Detected as SCI3 with 3330 resources; every resource decompresses (`RealGameTests`).
- kResCheck was missing ScummVM's SCI32 ending: a robot, duck or VMD that is not in the volumes is
  looked for as `N.rbt`, `N.duk` or `N.vmd` among the game's files. RAMA checks each video that
  way before playing it, so without it the opening showed a black window
  (`RamaFindsItsVideosWithResCheck`). LSL6 hires' wave-as-audio check in the same call is not
  ported, as there is no wave resource type here.
- Verified headless: the title, NEW GAME into the opening VMDs (100, 39999, 38111) and the hangar;
  the game menu; saving through RAMA's own screen (`rama.001`, with ScummVM's header and
  thumbnail; the catalogue is not created, as in ScummVM); LOAD GAME on the title listing it and
  restoring into the hangar; DELETE with its confirmation removing the file through kFileIO's
  RAMA unlink. The game saves through kSave, so the `ramasg.N` kFileIO path is still not used.

### Phantasmagoria 2: run (2026-10-25)

- The US release (ScummVM's uncensored entry from jvprat), five CDs merged: the resource files of
  each, disc 1's patch volume, RESOURCE.AUD, RESOURCE.SFX and 65535.MAP (disc 5's copies of the
  last two are smaller), the root patch files, and the DUK and PATCHES folders.
- Verified headless: it boots with ScummVM's script patches, shows the main menu (its buttons are
  the puzzle-piece icons), starts a new game in Curtis's bedroom with his idle animation, and a
  headless save there restores, through the game's own "Please click mouse to continue" prompt.
- The Duck videos came in the next stage (below). The game's own save screens have not been
  tried.

### Duck video and true colour: done (2026-10-25)

- Core: `Surface32` (XRGB8888) and `ITrueColorGraphicsDriver`, an optional interface beside
  `IGraphicsDriver` (which is unchanged, so outside hosts keep working): `Present(Surface32)` shows
  a true-colour frame until the next indexed one. The SDL host keeps an XRGB8888 surface next to
  its indexed one, the browser converts to RGBA as it does for palettes, and the headless host
  keeps the last true-colour frame (`ShowsTrueColor`, `LastTrueColorFrame`) and writes whichever
  kind it shows as a PNG. `PngWriter` writes RGB PNGs.
- `Video/TrueMotion1Decoder` (port of ScummVM's, from FFmpeg; its tables generated from
  `truemotion1data.h`), `Video/DuckDecoder` (the part of ScummVM's AVIDecoder the Duck files use:
  the header, the movie list chunk by chunk, the first frame decoded for the size, the sound
  queued three chunks ahead, frames timed by the engine clock), `Dk3Adpcm` (ScummVM's
  DK3_ADPCMStream, one chunk at a time as its packetized stream) and `Graphics/DuckPlayer` (ScummVM's
  DuckPlayer with the VideoPlayer loop; the event checks are VmdPlayer's, now shared).
- kPlayDuck's Play, SetFrameOut, Close and SetVolume. ScummVM switches the screen to the video's
  16-bit format and copies frames to it; here the engine, while a video plays, converts the game's
  screen through the hardware palette and draws the frame over it (`PresentSci32`). A host
  without true colour gets a warning and the video is skipped, as ScummVM shows a dialog on an
  8-bit screen.
- Verified: the title video (4.duk) and the opening (1.duk, 2717 frames) play pixel-doubled at
  (0, 71) as ScummVM places them; a click ends the opening into Curtis's bedroom, back on the
  indexed screen with the cursor shown again; the title's sound reaches the mixer (a WAV capture);
  the largest video (8215.duk, 2770 frames) decodes with natural colour (`DuckVideoTests`,
  `PngWriterTests`, `ConvertsTrueColorFrames`).
- Not done: ScummVM's HQ video mode (smooth scaling of doubled videos, an option there) and its
  black-lined video option, as for VMDs; the sound's last moments after the final frame (ScummVM
  lets the audio track finish before the video ends); the windowed player has not been watched
  playing one.

### Phantasmagoria: run (2026-10-25)

- The English DOS/Windows release (ScummVM's entry from csnover), seven CDs merged: the resource
  files of each, each disc's RESOURCE.AUD and RESOURCE.SFX as RESAUD.00n and RESSFX.00n (the discs'
  sound effects differ, so they are kept per disc, which ScummVM does when there is no
  RESOURCE.AUD), disc 1's VERSION and 37.MAP, and the ROBOT, VMD and PATCHES folders. The loose
  65535.MAP of each CD is ignored, as in ScummVM. Four videos differ between discs (ROBOT 90, 91
  and 462, VMD 5460); the copy most discs share was kept.
- Verified headless, with nothing to fix: the title and main menu at 630x450; Watch Intro plays
  10.vmd; Start New Game opens the "Name your game" keyboard and the chapter menu; chapter one
  plays 30.vmd in the game's interface into the kitchen, where the player has control. The game
  keeps its games in files of its own through kFileIO's rewrite-in-memory path (phantsg.dir, the
  list of names, and phantsg.0), writes pref.dat, and the next start offers "Continue old game",
  which lists ADRIENNE and resumes in the kitchen (`PhantasmagoriaKeepsItsGamesInItsOwnFiles`).
- Not tried: play past the kitchen, the censorship mosaics (the game's own option), the chase
  sequences (chase.dat) and the other chapters.

## SCI: deferred

Ordered by what blocks playing a game through.

### Sound drivers other than AdLib

- Status: AdLib is the default (track type 0x00 for SCI1, device masks 0x04/0x09 for SCI0). The
  PCjr/Tandy three-voice and PC speaker drivers are ported (2026-10-23, `Sound/Drivers/Pcjr.cs`
  from `pcjr.cpp`: the drivers' own square waves and envelopes, SCI1 instruments from patch 101,
  track types 0x02/0x10/0x13 and 0x04/0x20/0x12); `--sound pcjr` or `--sound pc` in the Player,
  the Sound choice in the page, `SciEngineProvider.SoundDevice`. A game without patch 101 falls
  back to AdLib with a warning. `PcjrSoundTests` checks the square wave, the 60 Hz timer and the
  release of a note, then KQ1 SCI, KQ5 and SQ5 through the engine. Two port bugs worth
  remembering: `sampleRate << 16` overflows an int (ScummVM uses uint32), and the SCI0 header is
  read with `++readPos` (the flags byte follows the voice count).
- General MIDI and the MT-32: see "General MIDI: done" and "MT-32: done" below. Not ported: the
  D-110 variants of `midi.cpp` (`readD110DrvData`, `readD110SysEx`; only KQ4's `DKQ4.DRV` is
  known), and the SysEx delays a real module needs.
- Not ported: `amigamac0.cpp` and `amigamac1.cpp` (Amiga and Mac sample-based drivers),
  `cms.cpp`, `fmtowns.cpp`, `pc9801.cpp`, `casio.cpp`, `fb01.cpp`: only matter for data the
  library lacks.
- Openings without music: QFG1 VGA and PQ3 play nothing in their first 1500 ticks on any device
  until Enter moves them on, which is the games, not the drivers.

### General MIDI: done (2026-10-24)

- `Sound/Drivers/Midi.cs` ports `MidiPlayer_Midi` for a General MIDI device: the GM patch
  (patch 4: instrument map, key shifts, volume adjustments, percussion map, velocity curves and
  its MIDI data), the choice between GM and MT-32 tracks (patch 1's format, then
  `ResourceManager.IsGmTrackIncluded` from `isGMTrackIncluded`), ScummVM's MT-32 to GM mapping of
  patch 1 or `MT32.DRV` for MT-32-only games (tables generated by
  `tools/PortTables/sci_mt32_to_gm.py` from `map-mt32-to-gm.h`), the built-in SCI32 patch, the
  GS drum kit fallback map, channels 1 to 8, the 250 Hz timer of `MidiDriver_Emulated` and the
  pitch bend range RPN of `MidiDriver::setPitchBendRange`. ScummVM's `Mt32dynamicMappings` (a
  list its debugger fills) is left out.
- The device is `Daventry.Audio.GeneralMidiSynth`, MeltySynth 2.4.1 (MIT, pure C#, so it runs in
  the browser) behind `IMidiDevice`; `SoundFontBank` loads a SoundFont once for every game. The
  gain is MeltySynth's default 0.5: at 1.0 KQ6 and LSL3 clipped, at 0.5 the music sits between
  the AdLib levels of the same games. `GeneralMidiOptions.Light` (no reverb and chorus, half the
  rate with interpolation; MeltySynth goes no lower than 16 kHz) is there for slow devices;
  full quality kept up in headless Chrome on the interpreted runtime, the same tick rate as AdLib.
- Hosts: `--sound gm` and `--soundfont FILE` in the Player (a SoundFont alone implies GM; GM
  without one is an error), a General MIDI choice in the page with a SoundFont picker that keeps
  the file in IndexedDB (an `assets` store, library version 2) and remembers the sound choice in
  local storage. `SciEngineProvider.SoundFont` and `.GeneralMidiOptions` for other hosts; GM
  without a SoundFont falls back to AdLib with a warning.
- Verified: ScummVM 2026.4.0 with FluidSynth and `--debugflags=Sound` logs its MT-32 to GM
  mapping of LSL3's 96 patches, and Daventry's map matches it entry for entry. `GeneralMidiTests`
  records what the driver sends a device for a GM patch (instrument, key shift, volume and
  velocity maps, rhythm patches), an MT-32 mapping (bend range RPN, louder low velocities,
  rhythm timbres), SCI32's built-in patch, early SCI0's channel filter and the 250 Hz timer,
  without game data; with the SC-55 SoundFont (`DAVENTRY_SOUNDFONT`, or a ScummVM checkout next
  to the project) KQ6 CD, LSL3 and SQ6 play their music and the light settings keep the pitch.
  In headless Chrome, LSL3 picked with General MIDI and the SoundFont plays from the page.
- Not verified by ear: nobody has listened yet. ScummVM itself calls its mapping approximate
  for SCI1 games (KQ5, for one, has no GM patch and goes through it).

### MT-32: done (2026-10-24)

- `Daventry.Audio/Mt32` ports Munt (libmt32emu 2.7.0, as bundled in ScummVM's
  `audio/softsynth/mt32`, LGPL 2.1 or later): the LA32 wave generator and ramps, TVA, TVF, TVP,
  partials, polys, parts and the rhythm part, the partial manager, the BOSS reverb models of the
  MT-32 and the CM-32L, the coarse analog output filter, the LCD, the ROM identification tables
  and the synth with its memory regions and MIDI event queue. Only the path ScummVM uses is
  ported: the 16-bit integer renderer, the coarse analog mode at the native 32 kHz, the LAPC-I
  free-partials logic. The packed parameter structs are views over one byte array.
- `Mt32Emulator` is the `IMidiDevice` and drives the synth as ScummVM's `MidiDriver_MT32` does:
  messages played at once, Roland data sets written straight to memory, both output gains at 1,
  the 32 kHz output converted to the host rate by linear interpolation. `Mt32RomSet` finds the
  ROMs by SHA-1 (`Core.Storage.Sha1`, managed so it runs in the browser), whatever the files are
  called, joins chip-at-a-time dumps, and prefers CM-32L 1.02, then 1.00, then the MT-32s from
  1.07 down, the second generation, and the CM-32LN last; `Mt32Model` asks for one kind.
- `Sound/Drivers/Midi.cs` gains the emulated MT-32 device of `MidiPlayer_Midi`: the reset,
  `readMt32Patch` (with the before and after texts swapped for KQ4 and LSL2), a GM-format patch 1,
  `readMt32DrvData` for early SCI0 (`MT32.DRV` of 1747, 1759, 1773 or 2771 bytes; ScummVM's
  errors become a fall back to AdLib), the reverb configurations and the SCI0 default reverb,
  play mask 0x0C (0x01 for SCI0), and no patch changes on the rhythm channel. The goodbye message
  is left out, since the engine never closes the driver.
- Hosts: `--sound mt32`, `--mt32-roms DIR` and `--mt32-model` in the Player (a ROM folder alone
  implies the MT-32), the Roland MT-32 choice in the page with a picker that takes any number of
  files and keeps the chosen pair in IndexedDB (`mt32-control`, `mt32-pcm`).
  `SciEngineProvider.Mt32Roms` for other hosts; without ROMs the MT-32 falls back to AdLib.
- Verified: `tools/Mt32Reference` builds ScummVM's Munt with MSVC into a harness that renders
  seeded event scripts (notes on all parts, program changes, controllers, bend, RPNs, timbre,
  patch, rhythm, reverb, reserve and display data sets); four 30-second scripts through six
  control ROMs (CM-32L 1.00 and 1.02, MT-32 1.04, 1.07 and 2.04, CM-32LN 1.00) give the same
  samples as Munt (`Mt32ReferenceTests`). The one difference found on the way was C++'s argument
  evaluation order in `Partial::generateNextSample` (MSVC and GCC go right to left). `Mt32Tests`
  checks the ROM identification and a note with the LCD banner; `Mt32DriverTests` records the
  upload order, the KQ4 swap, the reverb, KQ4's early `MT32.DRV`, and plays LSL3, KQ5 and SQ6
  through the emulator with their own LCD messages. Sixteen games from KQ1 SCI to SQ6 put their
  messages on the LCD.
- Levels: the MT-32 plays louder than AdLib (LSL3's title about four times), as in ScummVM, which
  also runs its emulator at full gain; nothing clips.
- Speed: in headless Chrome on the interpreted runtime, LSL3 keeps 60 ticks a second at about
  5 ms a tick with the MT-32, against 1.3 ms with AdLib. A slow device may need the AOT build.
- Not ported: the D-110 (`kMt32TypeD110`), Munt's float renderer and its accurate and
  oversampled analog modes, the MT-32 rev00 quirks (no ROM of it here). Not compared with
  ScummVM's audio output directly, as ScummVM writes no WAV; the emulator matches Munt, and the
  driver is checked by what it sends.

### CD audio tracks

- Status: shelved (2026-10-25). `kDoAudio`'s CD sub-op warns, and `cdaudio.map` is ignored, so a
  game plays as ScummVM does with "Use CD audio" off.
- Jones in the Fast Lane CD (DOS) needs nothing more: its 533 voiced lines are both CD tracks
  (`cdaudio.map`, positions in frames) and one digital volume (`audio001.map`, `audio001.002`),
  and the latter plays; checked through the demo, with the music on MIDI. The tracks would only
  bring the same recordings at CD quality.
- Mixed-Up Mother Goose 256 CD (DOS) is the one that needs it: its English rhymes exist only as
  CD tracks (the other four languages are `AUDIO###` files). That release is very rare and there
  is no copy to test against.
- To do when there is one: `features.cpp` `usesCdTrack`, `ksound.cpp` `kDoCdAudio` and the
  Mother Goose language routing in `kDoAudio`, `sound/audio.cpp` `audioCdPlay`, and a source of
  tracks in the hosts (`trackNN` files beside the game, or a cue sheet and image).

### Windows versions and alternate soundtracks

- Status: the Windows SCI1.1 releases are detected but run as DOS: no hires KQ6 cels, no alternate GM
  soundtrack (`useAltWinGMSound`), no SQ4 CD Windows samples. Releases that exist only for Windows
  run as Windows (2026-10-25: `kPlatform`, the INI profile and the kernel signatures are ported;
  detection still puts a DOS match first when a CD carries both).
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
- Headless runs are deterministic (2026-10-22): a host reports `IHost.DeterministicTime` and
  `IHost.RandomSeed` (the headless host: on, seed 1; the SDL host keeps the real clock). The clock
  the scripts read (`SciEngine.Millis`) then advances 1/16 ms per read inside a tick instead of the
  real time the tick took (busy-wait loops still end; the speed benchmarks measure a fixed, slow
  machine), the calendar kGetTime answers starts at 2000-01-01 12:00 (KQ5 and QFG1 read the time of
  day every cycle, and saved games made headless carry that date), the SCI16 palette timestamps come
  from the engine clock (offset by a second so they never equal the fresh-palette marks 0 and 1), and
  kRandom, the AGI wander and follow motions and the preAGI dice follow the seed (`--seed N` in the
  Player). `DeterminismTests` runs KQ5 CD, QFG1 VGA and QFG4 twice and compares every presented
  frame; `InterpreterTests` does the same for KQ1 AGI; two Player processes give identical kernel
  traces and screenshots. Before this, the QFG4 save test failed only when the suite ran in parallel,
  because its class-screen snapshot landed mid-animation under CPU load.
- First report from browser play testing (2026-10-23): Laura Bow 1's title and copy-protection
  screen were rows of white blocks. kDisplay's pen and background colours arrive as 0xffff for
  "none" and ScummVM keeps them in an int16, so they are -1; read unsigned, the background filled
  the text box white and the white text vanished in it. Same on every host; fixed in
  `GfxPaint16.KernelDisplay` (`TitleScreenTests`).
- Debugging aids: `DAVENTRY_TRACE_KERNEL=1` logs every kernel call, `DAVENTRY_TRACE_FILE=path` writes the
  same trace to a file without slowing the interpreter (with `-- tick N` markers). Headless runs
  clock audio from engine time through `ClockedSource`, so speech-timed scenes advance without `--wav`.
- Headless clicks: SCI0/SCI01 title buttons (KQ1 SCI) select on the first click and activate on the
  second, so `--click` needs two clicks 20 ticks apart. KQ6 CD's title buttons work since `--click`
  moves the mouse before the press (Escape at 700 and 1300 reaches the title by 2000, Play is
  `--click 2200:245,190` and enters the beach, room 200; `TitleButtonTests`), and with a real mouse
  in the SDL window.
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

## Browser host: done (2026-10-23)

`src/Daventry.Host.Browser` is a Razor class library with the host contract on top of a page:
`CanvasGraphicsDriver` expands the indexed frame through its palette into RGBA (`FrameConverter`)
and blits it into a canvas kept at the framebuffer's pixel size and scaled by CSS (4:3 with aspect
correction); `WebAudioDriver` schedules the engine's stereo chunks as AudioBuffers ahead of the
output clock (120 ms target, pumped every frame, resumed on the first click or key as browsers
require); `BrowserInputDriver` turns the canvas's key and mouse events into engine events
(`KeyTranslator`: the typed character from the browser's `key`, the key code from `code` the way
the SDL host derives it, Alt+Enter and F11 and the Ctrl+Alt volume keys handled by the host);
`LocalStorageSaveStore` keeps saves as base64 under `daventry:save:<name>` (gzip inflated on
read like the file store); `BrowserHost` ticks the engine from `requestAnimationFrame` through
`TickScheduler`, which counts the next tick from when the last was due (so 144 Hz displays do not
run the game fast and 60 Hz ones do not halve it) and starts over after a stall. The JavaScript
side is one ES module (`wwwroot/daventry.js`) reached through `[JSImport]`, with three delegates
handed over for the frame, key and mouse callbacks; nothing uses `IJSRuntime`. `DaventryPlayer`
is the component; `src/Daventry.Web` is a standalone WebAssembly page with a folder picker
(`InputFile` with `webkitdirectory`), a `?game=<manifest>` loader for games a site hosts, and
the usual controls. Detection needed a managed MD5 (`Core.Storage.Md5`), the runtime's being
unavailable in the browser. `tools/BrowserSmoke` drives headless Chrome over the DevTools
protocol: KQ1 AGI and KQ1 SCI play with keys, typing and clicks, KQ1 SCI saves and restores
through local storage across a reload, QFG1 VGA runs; 60 ticks/s at 0.3 to 0.6 ms a tick on the
interpreted runtime, so ahead-of-time compilation is optional (`-p:WasmAot=true` with the
`wasm-tools` workload, not installed here).

### Browser polish: done (2026-10-24)

- Subfolders: the page reads a picked folder through the host's JavaScript module instead of
  `InputFile`, which has no relative paths. `pickedFiles` lists the input's files with their
  `webkitRelativePath` (less the picked folder's name) and keeps the Blobs; .NET reads them one at
  a time (`sourceLoad` resolves to the length, `sourceCopy` fills a buffer of it) into a
  `MemoryGameContainer` with slash paths. `Core.Storage.SubfolderGameContainer` shows one folder of
  such a tree with the same depth rule as `DirectoryGameContainer`, and
  `EngineRegistry.DetectInContainer` looks two folders down like `DetectInDirectory`. The manifest
  loader takes paths in file names too.
- Kept games: `BrowserGameLibrary` stores a picked game's Blobs in IndexedDB (`daventry-library`:
  a `games` store of `LibraryGame` records keyed by engine, game id and folder; a `files` store
  keyed `id\npath`), lists them most recently played first, reads one back, deletes one, reports
  `navigator.storage.estimate()` and asks for `persist()` when the first game is kept. The page's
  "Keep in this browser" box (on by default) and "Your games" list use it; the record remembers
  which release of a multi-detection was played.
- Saved games: a panel lists the local-storage saves with Download (a Blob link), Delete (after
  the browser's confirmation) and Import (`InputFile`, asking before replacing).
- Verified in headless Chrome (`tools/BrowserSmoke`, new `files:` step for
  `DOM.setFileInputFiles` on the folder input, `downloads:` for `Browser.setDownloadBehavior`,
  and confirmation dialogs accepted): Codename: Iceman picked as its outer folder is found in
  `ICE`, kept, played again from the list after a browser restart; King's Quest 7 (517 MB, data
  in `King's Quest 7/King's Quest 7`) is found, kept, and plays its intro Robot from `AVI` when
  started from the list; a save imported, downloaded to disk and deleted. `SubfolderContainerTests`
  and two `BrowserHostTests` cover the tree view, the detection and the library records.

### Browser host: deferred

- Saves stay in local storage (a few MB, synchronous as `ISaveStore` is); SCI saves of 50 to
  100 KB fit dozens. Moving them to IndexedDB would need an asynchronous save store or a cache
  written behind.
- A kept game is read whole into memory to play, as a picked one is; KQ7 takes about 520 MB of
  the WebAssembly heap. Reading files on demand would need an asynchronous game container.
- Downloaded saves keep Daventry's names (`<game id>.NNN` for SCI); ScummVM wants its target name,
  as on the desktop (see `tools/ScummVmRoundTrip`).
- Audio goes through scheduled AudioBuffers; an AudioWorklet with a ring buffer would cut the
  120 ms latency, at the cost of cross-origin isolation for a SharedArrayBuffer.
- No touch or gamepad input; a phone has no keyboard for the parsers.
- Sound: the "silent AGI game" report turned out to be the second detection of the KQ1 folder,
  the Color Computer 3 release, which shares the DOS directory hash and whose sound is not
  ported. The page now shows the best match first and the others as "also detected as" with
  their platform, and `DetectedGame.Variant` names the platform in `--list` too. The page keeps
  `window.daventryAudio` (context state, chunks pushed, audible chunks, peak, underruns) for the
  next sound report; a `state` of `suspended` means the browser is waiting for a click or key.
- Blazor Server is not a target: the host needs the engines in the page (`[JSImport]`), and the
  engines in a server process would need a different transport for frames and input.

## NuGet packaging: done (2026-10-23)

- `dotnet pack -c Release -o artifacts/packages` builds seven packages: `Daventry.Core`,
  `Daventry.Audio`, `Daventry.Agi`, `Daventry.Sci`, `Daventry.Host.Sdl` and `Daventry.Host.Browser`
  with `.snupkg` symbol packages, and `Daventry.Player` as the `daventry` dotnet tool. The ADL
  stage added an eighth, `Daventry.Adl`, which reaches nuget.org with the next release tag. The shared
  metadata (version prefix and suffix, icon, tags, release notes, license, README) is in
  `Directory.Build.props`; the settings that depend on `IsPackable` (Source Link, symbols, package
  validation) are in `Directory.Build.targets`, because a project sets `IsPackable` after the props
  are read.
- `tools/PackageSmoke/run.ps1 -Game <folder> [-BrowserGame <folder>]` is the consumer check: it
  packs into `artifacts/packages`, restores a console app from that feed (its own `NUGET_PACKAGES`
  cache, so a re-packed prerelease is never served stale from the global one) and plays a game
  headless, installs the tool from the feed and takes a screenshot, publishes a Blazor WebAssembly
  app that references `Daventry.Host.Browser` and checks the host's module landed under
  `_content/`, and with Node and Chrome drives the published page with `tools/BrowserSmoke`.
  First run: KQ1 SCI headless (22307 of 64000 pixels lit after 600 ticks, audio peak 9838), the
  tool's screenshot, and the page playing KQ1 AGI at 60 ticks/s (audio peak 11628). The smoke
  projects have an empty `Directory.Build.props` and `Directory.Packages.props` of their own, so
  they consume the packages like a stranger would.
- Worth remembering: `GenerateDocumentationFile` has to be in `Directory.Build.props`, the SDK
  derives the documentation file name before `Directory.Build.targets` is read, so the XML went
  missing from the first packages. A tool package publishes every SDL3 native library, 33 MB,
  which the Player trims to 14 MB by dropping the Android and iOS runtimes from
  `ResolvedFileToPublish`. `dotnet build tests/...` does not rebuild the Player. `node` can be a
  version manager's shim whose child outlives it, so the smoke stops its file server by port.
- The repository and project URLs point at `github.com/jeffnyman/daventry` (set 2026-10-23, once
  the remote existed), so Source Link records the commit URLs in the symbol packages.
- Publishing: `.github/workflows/publish.yml` runs on a `v*` tag (or by hand with a version),
  tests, packs with the tag as the version (`-p:Version` overrides the prefix and suffix in the
  props), and pushes with nuget.org Trusted Publishing: `NuGet/login` trades the workflow's OIDC
  token for a short-lived key, so no API key is stored. Needs a Trusted Publishing policy on
  nuget.org for the repository and `publish.yml`, and a `NUGET_USER` repository secret with the
  nuget.org username. Runs on Windows, where the suite has always passed; the game-library tests
  skip there. The first run (tag `v0.1.0-alpha`) passed the tests, the pack and the login, then
  failed the push: on Windows `dotnet nuget push` expands a wildcard only when it is written with
  backslashes, so the step now pushes each `.nupkg` by its full path. A manual run with version
  0.1.0-alpha then published all seven packages and six symbol packages (2026-10-23), the first
  release on nuget.org.
- `.github/workflows/build.yml` (2026-10-24) builds with warnings as errors, tests and packs on
  every push to main and every pull request, on Windows like the publish workflow; a newer push
  cancels the run it replaces. The README shows its badge and the NuGet version.
- Not done: no Linux or macOS leg in the workflows (the suite has only ever run on Windows, though
  the tool ships SDL for all three), and the public surface is as the port left it: 43, 4,
  101, 297, 3 and 13 public types in Core, Audio, Agi, Sci, Host.Sdl and Host.Browser, about 8,900
  public members without XML comments (CS1591 is off). A pass that makes the engines' internals
  `internal` (with `InternalsVisibleTo` for the tests, as `Daventry.Agi` already has) belongs before
  a 1.0.

## ADL: done (2026-10-24)

- `src/Daventry.Adl` mirrors ScummVM's `engines/adl`: `AdlEngine` (adl.cpp) and the v2 to v5
  layers, `Display`/`DisplayA2`, `GraphicsMan` (v1 line art, v2 pattern fills, v3 fills), `Sound`
  (the Apple II speaker, ScummVM's edge-accurate square wave at its stream volume of 25/255),
  `HiRes1` to `HiRes6` and `AdlEngineProvider` (metaengine). The game logic is async, as in the
  preAGI engines: delays, key waits, the "press return" page break and the speaker are awaited on
  the frame scheduler; action opcodes are `ValueTask<int>`, conditions stay synchronous.
- `src/Daventry.Core/Formats`: `DiskImage` (port of `common/formats/disk_image`): `.do`/`.dsk`,
  `.d13`, `.nib` and `.woz` (WOZ1 and WOZ2) with 5-and-3 and 6-and-2 decoding, `.xfd` and `.img`
  geometry, sector-limited reads, ScummVM's detection checksum, `AppleDosFiles` (DOS 3.3 catalogs)
  and `PlainFiles`; `SeekableReadStream` gives ported loops ScummVM's end-of-stream semantics.
  Decoding is always eager (ScummVM can decode lazily).
- Detection (`AdlDetector`) uses ScummVM's two tables, generated from `detection.cpp`, but matches
  disk images by contents instead of by ScummVM's fixed names, so `Wizard (1980).do` is found as
  well as `wizard.dsk`; files with ScummVM's names and unknown checksums are reported as
  "unverified dump", ScummVM's unknown variant. Mystery House's plain files use the advanced
  detector's 5000-byte MD5, disk images the first 16 KB of the decoded sectors.
- The library (`sierra-games/hires0-apple2` to `hires6-apple2`) matches ScummVM's table for every
  game: Mission Asteroid (13-sector .nib), Mystery House (Anthology), Wizard and the Princess
  (Green Valley [B]), Cranston Manor (13-sector WOZ2), Ulysses (Green Valley [A], version 0.0 with
  the animated shape-table title), Time Zone (Anthology 1.1, twelve .nib sides), The Dark Crystal
  (SierraVenture [A]).
- Display: ScummVM's renderers that fit an indexed palette: the RGB-monitor colours (16 colours by
  window and phase) and the green monochrome monitor, text in white (ScummVM's "monotext"
  default), each with dimmed scanlines; 560x384 frames over a 64-entry palette. Rendering happens
  when the game calls `RenderText`/`RenderGraphics`, as ScummVM's does, so a picture appears when
  the game shows it. ScummVM's Wizard and the Princess with NTSC off looks the same.
- Tests: `Daventry.Adl.Tests` (41): detection of every release, content matching under another
  name, unverified dumps, the 13- and 16-sector decoders against ScummVM's checksums, rendering
  and picture drawing, the speaker, each game reaching its first room with its picture and
  answering a command, menu saves and restores for v1, v2, v4 and v5, the typed SAVE/RESTORE
  commands, the text window's Return wait, determinism. `Daventry.Core.Tests.DiskImageTests`
  builds 16-sector .nib and WOZ2 images with a DOS 3.3 encoder and checks they decode back, plus
  sector-limited reads and a DOS 3.3 catalog, without game data, so they run on GitHub.
- ScummVM 2.7.0 (bundled with the user's "Collection Chamber" Hi-Res Adventures) lists Daventry's
  saves for Wizard and the Princess and The Dark Crystal and loads Wizard's at start; a save it
  made in that game (gzip-compressed, after DROP KNIFE) restores in Daventry with the knife on the
  ground. `tools/ScummVmRoundTrip` has the ADL notes.
- Worth remembering: v4 and v5 games jump back to the game loop after a menu restore, and Time
  Zone's saves keep no move counter (it restarts at 1), both as in ScummVM. The menu save follows
  ScummVM's rule (accepted only when the game would take "SAVE GAME" then), so in the opening rooms
  of Time Zone and The Dark Crystal, where a catch-all command answers the first input, it waits
  for one move. A bell inside printed text (Ctrl-G) starts the tone without waiting for it.
- The Player's `--saves DIR` now applies to windowed play too (it was headless only), so saves can
  live anywhere; without it they go to `Daventry/saves/<game id>` in the user's application data.

### ADL: deferred

- ScummVM's default look, NTSC composite colour (`ntsc=true`), blends neighbouring pixels into
  thousands of colours; it needs a true-colour path through the host contract (the SDL, browser
  and headless hosts present 8-bit frames). The RGB-monitor colours are an authentic mode, but not
  ScummVM's default, and Mystery House's monochrome differs slightly from ScummVM's NTSC mono.
- Not ported: text to speech, the debugger console (`console.cpp`) and its scripted input files,
  the PC data blocks (`disk.cpp`, no PC release in the table), lazy disk decoding.
- The Atari 8-bit Ulysses is ported but untested (no copy in the library), as are the French
  Mystery House and the plain-file Mystery House releases.
- The page has no ADL display options yet (colour, scanlines); it uses each game's default.

## Next stages

1. SCI0 through SCI2.1, ADL and Troll's Tale: real play testing in the SDL window and the
   browser, fixing what it finds.
2. Touch input for the browser page, so it plays on a tablet.
3. AGI v1 and Apple II booter loaders, once there are disk images to test against.
4. ADL's NTSC colour, now that the hosts can show true colour (the Duck stage added the path).
