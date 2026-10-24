# Mt32Reference

Checks the MT-32 emulator in `Daventry.Audio/Mt32` against Munt itself, sample by sample. It builds
the copy of Munt (libmt32emu 2.7.0) in ScummVM's `audio/softsynth/mt32` into a small harness that
renders an event script through `Synth`, the way ScummVM drives it (coarse analog filter, 32 kHz,
`MIDIDelayMode_IMMEDIATE`, both output gains at 1). `Mt32ReferenceTests` renders the same scripts
through the port and compares every sample.

```
tools\Mt32Reference\build.bat                       :: needs the MSVC build tools; makes harness.exe
python tools\Mt32Reference\genscript.py 1 30 out\script1.txt
tools\Mt32Reference\harness.exe <roms>\cm32l_ctrl_1_02.rom <roms>\cm32l_pcm.rom out\script1.txt out\ref_cm32l_1.raw
echo cm32l_ctrl_1_02.rom cm32l_pcm.rom script1.txt ref_cm32l_1.raw> out\cases.txt
set MT32_REFERENCE=%CD%\out
dotnet test tests\Daventry.Sci.Tests --filter Mt32ReferenceTests
```

`genscript.py` writes a seeded script of notes on all nine parts, program changes, controllers
(modulation, volume, pan, expression, hold, RPN bend range, reset, all notes off), pitch bends,
and Roland data sets to the memory timbres, patches, patch temp areas, rhythm key map, reverb,
partial reserve, master volume and master tune, plus a display message. Each line is
`<frame> M <packed message>`, `<frame> S <sysex hex>` or `<frame> E` (end); the harness renders up
to each frame and then queues the event with `playMsg` or `playSysex`.

Munt's TVP adds `rand() & 3` to its timer; the harness is compiled with `rand` replaced by a
function returning 0, and the test gives the port a generator returning 0.

Result on 2026-10-24: four 30-second scripts through six control ROMs (CM-32L 1.00 and 1.02,
MT-32 1.04, 1.07 and 2.04, CM-32LN 1.00) give the same samples as Munt. The one difference found
on the way was the order in which `Partial::generateNextSample` evaluates the arguments of one call
(amp, pitch, cutoff): C++ leaves it unspecified, MSVC and GCC go right to left, and the pitch step
can start a new amp ramp, so the port computes them in that order.
