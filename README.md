# Daventry

Daventry is a C# implementation of the Sierra AGI and SCI adventure game interpreters.
It is a port of the `engines/agi` and `engines/sci` code from [ScummVM](https://www.scummvm.org/),
restructured as a set of .NET libraries with a small host contract so the same engines can run
on the desktop (SDL) and, later, in the browser.

Daventry is not a ScummVM fork and does not aim to run anything other than Sierra's AGI and SCI games.

## Status

AGI games run: resources, pictures, views, sprites, the logic interpreter, text, parser and
PCjr/Tandy sound are ported, and King's Quest I through III, Space Quest I and II and the other
Sierra AGI titles play from the first room. Not ported yet: menus, inventory screen, save and
restore, MD5 game detection (so per-game workarounds do not fire), the Apple II, IIgs and CoCo3
sound formats. SCI is detected but not implemented.

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

## License

GPL-3.0-or-later, the same license as ScummVM, from which the engine code is derived. See `LICENSE`.
