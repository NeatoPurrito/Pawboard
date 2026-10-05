<p align="center"><img src="docs/icon.png" width="128" alt="Pawboard icon: a blue paw on a dark board with a pink marker squiggle"></p>

# Pawboard 🐾

A whiteboard that lives on your Windows desktop. It sits behind your desktop icons as your
wallpaper, so the notes you scribble are right there every time you minimise everything.

> **🤖 Made with AI.** Every line of code in Pawboard was written by **Claude Opus 5.5**
> (Anthropic), working in Claude Code. The idea, design decisions and all the testing on a real
> desktop came from [NeatoPurrito](https://github.com/NeatoPurrito). Treat it like any hobby
> project: read the code if you're unsure, and use it at your own risk.

## What it does

- **Draw on your wallpaper** with a smooth, low-latency pen tuned for the mouse (strokes are
  smoothed with a C# port of [perfect-freehand](https://github.com/steveruizok/perfect-freehand)).
- **Real eraser**: rubs out exactly what it touches, nibbling the edge of a thick line or
  cutting through it. What you rub out is gone for good, not just hidden (see Privacy below).
  Right-drag erases with any drawing tool.
- **Text** in a handwritten font: click and type, drag to move, pull the corners to scale and the
  sides to wrap lines.
- **Your desktop keeps working.** Icons stay on top and clickable, and right-click still opens the
  desktop menu. The arrow tool steps the board aside completely.
- **Zoom in** with the mouse wheel to write small, and move around with middle-drag. 100% is the
  whole board, exactly your screen, so at 100% nothing moves and your notes stay where you put them.
- **Light or dark** mode, and a dotted, lined, squared or plain background.
- **Saves itself** constantly. Save copies of a board and open them later from the ☰ menu.
- **Undo / redo** for everything (the last 100 steps, until Pawboard closes).

## Light on resources

Nothing on the board animates by itself, so an untouched board uses **no CPU and no GPU**. It
only draws while you're drawing. While a fullscreen game or video is in front, it stops watching
the mouse entirely. Memory use is roughly 150 MB of RAM and 90 MB of video memory on a two-monitor
setup (it keeps a full-screen image of the board ready so zooming and panning stay smooth).

## Using it

The toolbar sits above the taskbar on your main monitor:

| | |
|---|---|
| ↖ Arrow | The board steps aside: icons, selection boxes and the desktop menu work as normal. |
| ✏️ Pen | Draw on empty desktop space. |
| 🧽 Eraser | Rub out lines. Text is never erased; empty a text box to delete it. |
| A Text | Click empty space to type, click a text to edit it. |
| Colours / sizes | Apply to the current tool. |
| ↶ ↷ | Undo / redo. |
| ☰ | Save a copy, open a board, back to start, background, dark mode. |

To quit, right-click the paw in the system tray and choose **Exit Pawboard**. **Clear board…** is
in that menu too. It asks first, Undo brings everything back until Pawboard closes, and a backup
copy is kept either way.

**Backups:** before you open another board, clear the board, or when an older board gets
converted, Pawboard copies your board to `board.backup-<date>.json` in `%LOCALAPPDATA%\Pawboard`.
The newest 10 are kept.

## Install / build

There is no installer yet. Grab `Pawboard-<version>-win-x64.exe` from the
[Releases](https://github.com/NeatoPurrito/pawboard/releases) page and run it: it's a single file
with everything included. (The smaller `-needs-dotnet10` exe is the same app for people who
already have the .NET 10 Desktop Runtime.)

To build it yourself you need 64-bit Windows 11 and the
[.NET 10 SDK](https://dotnet.microsoft.com/download):

```
git clone https://github.com/NeatoPurrito/pawboard.git
cd pawboard/Pawboard
dotnet build -c Release
bin\Release\net10.0-windows\Pawboard.exe
```

Run it with `--window` to get the board in an ordinary window instead of on the wallpaper.

### The exe isn't signed

Pawboard isn't code-signed, so if you run a downloaded build Windows SmartScreen will say
"Windows protected your PC" (choose **More info → Run anyway**). Because Pawboard watches mouse
input on the desktop (see below), some antivirus tools may be suspicious of it too. Building it
yourself from the source avoids both.

## Privacy and safety

- **No network access at all.** Pawboard never sends anything anywhere, never starts other
  programs and doesn't write to the registry. (Windows itself keeps its usual records of the
  programs you run, as it does for any app.)
- **Mouse:** on the desktop, Windows sends every click to the icon layer, so Pawboard uses a
  system-wide mouse hook to notice clicks meant for the board. It only takes a click on empty
  desktop space while a drawing tool is selected, and then holds on to that mouse button until
  you let go (pressing any other button ends it). Clicks on icons and in other apps are never
  touched, and a plain right-click is always passed on to the desktop menu. While a fullscreen
  game or video is in front, the mouse hook is switched off.
- **Keyboard:** Pawboard only watches the keyboard **while one of its text boxes is open**. During
  that time, what you type goes onto the board, even if another window technically still has
  focus (that's how typing on the wallpaper works). Nothing is logged or stored apart from the
  text you typed. The text box closes the moment you click elsewhere, press Esc, Alt+Tab, or
  another window comes to the front.
- **Erased means erased.** When you finish erasing, the ink you rubbed out is removed from the
  board's data itself, and a line erased completely is deleted. Boards saved by older versions
  are cleaned the same way when they're opened. So a board file you share doesn't contain what
  you erased. (Anything you *undo* is a different story: undo history lives only in memory and is
  gone once Pawboard closes; it's never saved.)
- **Board files** (`.pawboard`, and `board.json` in `%LOCALAPPDATA%\Pawboard`) are plain JSON:
  numbers, colours and text. Nothing in them is ever run. Broken entries are skipped, and files
  that are malformed, unreasonably large or full of absurd values are rejected or trimmed.
- A small `pawboard.log` next to the exe records starts, stops and errors: never anything you
  draw or type, and no file paths.

## Known limits

- Tested on one machine so far: Windows 11 (24H2 and later), two monitors. Windows 10 might work
  but hasn't been tried.
- It doesn't start with Windows yet; run it yourself after logging in.
- If Explorer restarts, Pawboard is meant to wait for the desktop to come back and reattach
  itself, but that path hasn't been tested much yet.

## License

[MIT](LICENSE) © NeatoPurrito. Third-party licenses are in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
