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
  cutting through it. Right-drag erases with any drawing tool.
- **Text** in a handwritten font: click and type, drag to move, pull the corners to scale and the
  sides to wrap lines.
- **Your desktop keeps working.** Icons stay on top and clickable, and right-click still opens the
  desktop menu. The arrow tool steps the board aside completely.
- **Zoom and pan** (mouse wheel, middle-drag) on a board with edges, so you can't get lost.
- **Light or dark** mode, and a dotted, lined, squared or plain background.
- **Saves itself** constantly. Save copies of a board and open them later from the ☰ menu.
- **Undo / redo** for everything.

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

To quit, right-click the little whiteboard in the system tray and choose **Exit Pawboard**.
**Clear board…** is in that menu too (it asks first, and Undo can bring everything back).

## Install / build

There is no installer yet. You need Windows 10 or 11 and the
[.NET 10 SDK](https://dotnet.microsoft.com/download).

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
  programs and doesn't touch the registry.
- **Mouse:** on the desktop, Windows sends every click to the icon layer, so Pawboard uses a
  system-wide mouse hook to notice clicks meant for the board. It only claims clicks on empty
  desktop space while a drawing tool is selected; clicks on icons and on other apps are never
  touched.
- **Keyboard:** Pawboard only watches the keyboard **while one of its text boxes is open**. Keys
  go onto the board and nowhere else; nothing is logged or stored apart from the text you typed.
  The text box closes the moment you click elsewhere, press Alt+Tab or another window comes
  to the front.
- **Board files** (`.pawboard`, and `board.json` in `%LOCALAPPDATA%\Pawboard`) are plain JSON:
  numbers, colours and text. Nothing in them is ever run. Unreasonably large or malformed files
  are rejected or trimmed.
- A small `pawboard.log` next to the exe records starts, stops and errors (never anything you
  draw or type).

## Known limits

- Tested on one machine so far: Windows 11 (24H2 and later), two monitors.
- It doesn't start with Windows yet; run it yourself after logging in.
- If Explorer restarts, Pawboard is meant to wait for the desktop to come back and reattach
  itself, but that path hasn't been tested much yet.

## License

[MIT](LICENSE) © NeatoPurrito. Third-party licenses are in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
