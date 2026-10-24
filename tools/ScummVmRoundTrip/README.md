# Saved game round trip with ScummVM

How the saved games were exchanged with a real ScummVM build (2026.4.0, the same source as the
reference tree) on 2026-10-22, so it can be repeated after a change to either save format.

## Setup

Unpack the portable Windows build of ScummVM into a scratch folder and give it a configuration
of its own, so nothing touches a real installation:

```
scummvm --config=rt\scummvm.ini --add --recursive -p "<game folder>"      # once per game
```

Then put these lines into the `[scummvm]` section of `rt\scummvm.ini`:

```
originalsaveload=true          # F5 opens the games' own dialogs, which keys can drive
savepath=<absolute path>\rt\saves

[keymapper]
keymap_global_DEBUGGER=F11     # the debugger console on a plain key (modifier chords do not
                               # reach the window from synthesized input)
```

ScummVM's target names (`kq3`, `kq1sci`, `kq5`, `kq6-cd`, `sq5`, `sq6`, `qfg4`) are the file
prefixes it expects; Daventry names its files after the game id, which differs only where ScummVM
adds a suffix (`kq6-cd`). ScummVM writes its files through a gzip stream; Daventry's
`FileSaveStore` inflates those on open, and ScummVM reads Daventry's plain files as they are.

## Daventry to ScummVM

```
daventry "<game folder>" --screenshot out.png --frames 1250 --save 1200:1 --saves rt\dv-saves
copy rt\dv-saves\kq1sci.001 rt\saves\kq1sci.001
scummvm --config=rt\scummvm.ini --list-saves --game=kq1sci     # the header must parse
scummvm --config=rt\scummvm.ini -x 1 kq1sci                    # loads the slot at start
```

AGI games take `--key 100:return,200:return` to get past the title; KQ1 SCI needs its Begin
Game button double-clicked (`--click "500:205,162;520:205,162"`); SCI32 games need a save made in
play, not on their title screens (QFG4: `--click 700:165,58` for Create Character, save at 1490).

## ScummVM to Daventry

`drive.ps1` starts a program, brings its window to the front, and runs a script of steps separated
by semicolons: `wait:<ms>`, `keys:<text>` with `{ENTER}`, `{ESC}`, `{F5}`, `{F11}`, arrows and
`{CTRL+ALT+d}` chords, `click:<x>,<y>` in 320x200 game coordinates, and `shot:<png>` of the window.
Keys go in as hardware scan codes, which SDL needs (SendKeys events have none and are dropped).

```
powershell -ExecutionPolicy Bypass -File drive.ps1 -Exe scummvm.exe `
    -ExeArgs @("--config=rt\scummvm.ini","kq3") `
    -Script "wait:4000;keys:{ENTER};wait:1500;keys:{ENTER};wait:4000;keys:{F5};wait:2000;keys:{ENTER};wait:1500;keys:scummvm kq3;wait:500;keys:{ENTER};wait:1500;keys:{ENTER};wait:3000"
```

ADL (2026-10-24, with the ScummVM 2.7.0 bundled with a Hi-Res Adventures collection): the targets
are named after the game id (`hires2`), and `--save-slot=1` loads a slot at start. The ADL save
format has not changed since. Daventry saves through `--save T:N` once the game waits for a command
(after a long text, press Return first: the text window waits for it). In ScummVM the games' own
commands save: `SAVE GAME{ENTER}`, a letter (`C` is slot 2), `{ENTER}`. Run `drive.ps1` from a
PowerShell 7 session (`& .\drive.ps1 -ExeArgs @(...)`): started from Windows PowerShell 5 with
absolute paths in the arguments, ScummVM ignored everything after `--config` and opened its
launcher.

That is King's Quest III's own dialog: F5, Enter for slot 1, the description, Enter, Enter to
confirm. SCI16 games save with F5, the description and Enter. Screens without a save key (QFG4's
class screen) use the debugger console instead: `keys:{F11};wait:2500;keys:save_game qfg4.001{ENTER}`.

Then restore in Daventry, which also checks the gzip path:

```
copy rt\saves\kq3.001 rt\sv-saves\kq3.001
daventry "<game folder>" --screenshot out.png --frames 200 --restore 60:1 --saves rt\sv-saves
```

## What the first round found

- AGI saves stored one byte per flag; ScummVM packs the 256 flags into 32 bytes of bits, so every
  field after them was read shifted ("invalid render start offset"). Fixed in `AgiEngine.SaveLoad`.
- A restore requested by the host (`--restore`, ScummVM's `-x`) was carried out at the start of
  kGetEvent and the call then went on with its arguments from the old state, which crashed SCI1
  games on a freshly cloned object. kGetEvent and kWait now end the call after the restore, as
  ScummVM does.
- SCI32 games restored underneath their scripts came back on the wrong screen (QFG4's title menu
  instead of the class screen). They now restore through their own Game::restore with the save
  dialog script patched to return the pending slot, which is ScummVM's launcher restore.
