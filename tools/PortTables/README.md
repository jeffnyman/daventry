# PortTables

Scripts that convert ScummVM's large data tables into C# source so they are never hand-transcribed.

Planned inputs, relative to the ScummVM checkout at `../../../scummvm/engines`:

| ScummVM source | Daventry output | Notes |
|---|---|---|
| `agi/detection_tables.h` | `Daventry.Agi/Detection/AgiGameTable.g.cs` | done: `agi_detection.py`, 435 entries with MD5 of the first 5000 bytes of the directory file |
| `common/compression/dcl.cpp` | `Daventry.Sci/Resources/DclTables.g.cs` | done: `sci_dcl.py`, the three Huffman trees of the DCL implode decompressor |
| `agi/opcodes.cpp` | `Daventry.Agi/Opcodes/OpcodeTable.cs` | command and test tables, which vary by interpreter version |
| `sci/detection_tables.h`, `sci/detection.cpp`, `sci/detection_internal.cpp`, `sci/detection.h` | `Daventry.Sci/Detection/SciGameTable.g.cs`, `Daventry.Sci/SciGameId.g.cs` | done: `sci_detection.py`, 647 entries, titles, Sierra id mappings and the game id enum |
| `sci/engine/workarounds.cpp` | `Daventry.Sci/Engine/WorkaroundTables.g.cs` | done: `sci_workarounds.py`, 54 tables of script-bug workarounds with their local-call signatures |
| `sci/engine/kernel_tables.h` | `Daventry.Sci/Engine/KernelTables.cs` | ported by hand (SCI16 part): kernel functions with signature strings and workaround lists |
| `sci/engine/script_patches.cpp` | `Daventry.Sci/Patches/*.cs` | about 27,000 lines of per-game byte patches |

Run `python tools/PortTables/agi_detection.py` from the repository root after updating the ScummVM checkout; it
expands the header's macros exactly as the C preprocessor would and rewrites the generated file.
