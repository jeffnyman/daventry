# PortTables

Scripts that convert ScummVM's large data tables into C# source so they are never hand-transcribed.

Planned inputs, relative to the ScummVM checkout at `../../../scummvm/engines`:

| ScummVM source | Daventry output | Notes |
|---|---|---|
| `agi/detection_tables.h` | `Daventry.Agi/Detection/GameTable.cs` | 277 entries with MD5 of the first 5000 bytes of the directory file |
| `agi/opcodes.cpp` | `Daventry.Agi/Opcodes/OpcodeTable.cs` | command and test tables, which vary by interpreter version |
| `sci/detection_tables.h` | `Daventry.Sci/Detection/GameTable.cs` | about 3,150 entries |
| `sci/engine/kernel_tables.h` | `Daventry.Sci/Kernel/KernelTable.cs` | 261 kernel functions with signature strings |
| `sci/engine/script_patches.cpp` | `Daventry.Sci/Patches/*.cs` | about 27,000 lines of per-game byte patches |

Nothing here yet.
