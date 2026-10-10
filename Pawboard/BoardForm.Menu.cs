using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Color4 = Vortice.Mathematics.Color4;
using DRect = Vortice.Mathematics.Rect;

namespace Pawboard;

// The ☰ menu at the end of the toolbar: save / open, the board's background pattern, and
// light / dark mode. Drawn on the board like the toolbar, so it works the same on the wallpaper.
public sealed partial class BoardForm
{
    enum Backdrop { Dots, Lines, Squares, Plain }

    const string MenuIcon = "\uE700";
    const float MenuWidth = 236;

    Backdrop backdrop = Backdrop.Dots;
    float patternStrength = 0.5f;   // 0 faint, 0.5 the normal look, 1 strong
    enum Slider { Pattern, Veil }   // pattern strength, and how much the board colour covers the wallpaper
    Slider activeSlider;
    RectangleF patternTrack, veilTrack;   // where each slider's track is, for dragging
    bool menuOpen;
    RectangleF menuRect;
    RectangleF menuButtonRect;
    readonly List<(RectangleF Rect, Action Click)> menuButtons = new();

    IDWriteTextFormat? menuFont, captionFont, chipFont;

    void CreateMenuFonts()
    {
        menuFont = dwrite.CreateTextFormat("Segoe UI", FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, 13.5f);
        menuFont.ParagraphAlignment = ParagraphAlignment.Center;
        captionFont = dwrite.CreateTextFormat("Segoe UI", FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, 11.5f);
        captionFont.ParagraphAlignment = ParagraphAlignment.Center;
        chipFont = dwrite.CreateTextFormat("Segoe UI", FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, 11f);
        chipFont.TextAlignment = TextAlignment.Center;
        chipFont.ParagraphAlignment = ParagraphAlignment.Center;
    }

    void DisposeMenuFonts()
    {
        menuFont?.Dispose();
        captionFont?.Dispose();
        chipFont?.Dispose();
    }

    void SaveSettings()
    {
        try
        {
            BoardStore.SaveSettings(new BoardStore.Settings
            {
                Dark = dark,
                Background = backdrop.ToString(),
                PatternStrength = patternStrength,
                ToolbarHidden = toolbarHidden,
                ZoomLocked = zoomLocked,
                ShowWallpaper = showDesktopImage,
                WallpaperVeil = imageVeil,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // just won't be remembered
    }

    void SetBackdrop(Backdrop b)
    {
        backdrop = b;
        contentVersion++;
        cacheDirty = true;
        SaveSettings();
        Invalidate();
    }

    // The pattern's colour at the current strength. baseAlpha is how strong the pattern is drawn
    // normally. Below the middle it fades out; above, it darkens (light) or brightens (dark).
    Color4 PatternColor(float baseAlpha)
    {
        float t = patternStrength;
        if (t <= 0.5f) return WithAlpha(Colors.Dots, baseAlpha * (0.12f + 0.88f * t / 0.5f));
        float u = (t - 0.5f) / 0.5f;
        Color4 a = Colors.Dots, b = Colors.DotsStrong;
        return new Color4(a.R + (b.R - a.R) * u, a.G + (b.G - a.G) * u, a.B + (b.B - a.B) * u, baseAlpha + (1 - baseAlpha) * u);
    }

    void BeginSlide(Slider which)
    {
        activeSlider = which;
        mode = Mode.Slide;
        modeButton = MouseButtons.Left;
        SlideTo(cursor.X);
    }

    void SlideTo(float x)
    {
        var track = activeSlider == Slider.Pattern ? patternTrack : veilTrack;
        float t = Math.Clamp((x - track.Left) / track.Width, 0, 1);
        if (activeSlider == Slider.Pattern)
        {
            if (MathF.Abs(t - 0.5f) < 0.03f) t = 0.5f;   // easy to land back on the normal look
            if (t == patternStrength) return;
            patternStrength = t;
            contentVersion++;
        }
        else
        {
            // The veil isn't part of the zoom picture, so that one can stay as it is.
            if (t == imageVeil) return;
            imageVeil = t;
        }
        cacheDirty = true;
        Invalidate();
    }

    void CloseMenu()
    {
        if (!menuOpen) return;
        menuOpen = false;
        Invalidate();
    }

    // Runs a menu action after closing the menu, so dialogs open over a clean board.
    void FromMenu(Action action)
    {
        CloseMenu();
        BeginInvoke(action);
    }

    // Handles a left click while the menu is open. Returns true if the click was used up.
    bool MenuClick(Vector2 at)
    {
        if (!menuOpen) return false;
        if (menuRect.Contains(at.X, at.Y))
        {
            foreach (var (rect, click) in menuButtons)
                if (rect.Contains(at.X, at.Y)) { click(); break; }
            Invalidate();
            return true;
        }
        // Anywhere else closes it. A click on the toolbar still does its job; on the board it's only a close.
        CloseMenu();
        return !toolbarRect.Contains(at.X, at.Y) || menuButtonRect.Contains(at.X, at.Y);
    }

    void DrawMenu(ID2D1RenderTarget r)
    {
        menuButtons.Clear();
        if (!menuOpen) return;
        const float pad = 6, row = 36, caption = 24, chipH = 58, sliderH = 32, divider = 9;
        float h = pad + row * 4 + divider + caption + chipH + sliderH + row + (showDesktopImage ? sliderH : 0) + divider + row * 3 + pad;
        float x = toolbarRect.Right - MenuWidth, y = toolbarRect.Top - 8 - h;
        menuRect = new RectangleF(x, y, MenuWidth, h);

        brush!.Color = Colors.PanelShadow;
        r.FillRoundedRectangle(new RoundedRectangle(new RectangleF(x, y + 2, MenuWidth, h), 12, 12), brush);
        brush.Color = Colors.Panel;
        r.FillRoundedRectangle(new RoundedRectangle(menuRect, 12, 12), brush);
        brush.Color = Colors.PanelBorder;
        r.DrawRoundedRectangle(new RoundedRectangle(new RectangleF(x + 0.5f, y + 0.5f, MenuWidth - 1, h - 1), 12, 12), brush, 1f);

        float cy = y + pad;
        MenuRow(r, x, ref cy, row, "\uE74E", "Save a copy…", () => FromMenu(SaveBoardAs));
        MenuRow(r, x, ref cy, row, "\uE8E5", "Open a board…", () => FromMenu(OpenBoard));
        MenuRow(r, x, ref cy, row, "\uEE71", "Save as picture…", () => FromMenu(SaveAsPicture));
        // Locked, you're always at the start already.
        MenuRow(r, x, ref cy, row, "\uE80F", "Back to start", zoomLocked ? null : () => { CloseMenu(); GoHome(); });
        MenuDivider(r, x, ref cy, divider);

        brush.Color = Colors.Faint(0.5f);
        r.DrawText("BACKGROUND", captionFont!, new DRect(x + 16, cy, MenuWidth - 32, caption), brush);
        cy += caption;

        // Four chips with a little preview of each pattern.
        float chipW = (MenuWidth - pad * 2 - 6 * 3) / 4;
        float cx = x + pad;
        foreach (var b in new[] { Backdrop.Dots, Backdrop.Lines, Backdrop.Squares, Backdrop.Plain })
        {
            var chip = new RectangleF(cx, cy + 2, chipW, chipH - 6);
            bool selected = backdrop == b;
            brush.Color = selected ? WithAlpha(Colors.Accent, dark ? 0.16f : 0.12f) : Colors.Faint(0.04f);
            r.FillRoundedRectangle(new RoundedRectangle(chip, 8, 8), brush);
            if (selected)
            {
                brush.Color = Colors.Accent;
                r.DrawRoundedRectangle(new RoundedRectangle(RectangleF.Inflate(chip, -0.75f, -0.75f), 8, 8), brush, 1.5f);
            }
            DrawPatternPreview(r, b, new RectangleF(chip.X + chip.Width / 2 - 13, chip.Y + 8, 26, 20));
            brush.Color = selected ? Colors.Accent : Colors.Icon;
            r.DrawText(b.ToString(), chipFont!, new DRect(chip.X, chip.Bottom - 20, chip.Width, 16), brush);
            var choice = b;
            menuButtons.Add((chip, () => SetBackdrop(choice)));
            cx += chipW + 6;
        }
        cy += chipH;
        DrawSlider(r, new RectangleF(x + pad, cy, MenuWidth - pad * 2, sliderH), Slider.Pattern, patternStrength, backdrop != Backdrop.Plain);
        cy += sliderH;
        SwitchRow(r, x, ref cy, row, "\uE91B", "Show my wallpaper", showDesktopImage, ToggleDesktopImage);
        if (showDesktopImage)
        {
            DrawSlider(r, new RectangleF(x + pad, cy, MenuWidth - pad * 2, sliderH), Slider.Veil, imageVeil, ShowingDesktopImage);
            cy += sliderH;
        }
        MenuDivider(r, x, ref cy, divider);

        // Switches.
        SwitchRow(r, x, ref cy, row, dark ? SunIcon : MoonIcon, "Dark mode", dark, ToggleDark);
        SwitchRow(r, x, ref cy, row, "\uE72E", "Lock zoom", zoomLocked, ToggleZoomLock);
        SwitchRow(r, x, ref cy, row, "\uE7E8", "Start with Windows", autostartOn, ToggleAutostart);
    }

    // A slider from a small faint dot on the left to a bigger strong one on the right: how strong
    // the pattern is (with a tick in the middle for the normal look), or how much the board colour
    // covers the wallpaper. Greyed out when there's nothing for it to change.
    void DrawSlider(ID2D1RenderTarget r, RectangleF slot, Slider which, float value, bool enabled)
    {
        float cy = slot.Y + slot.Height / 2 - 2;
        float dim = enabled ? 1 : 0.4f;
        brush!.Color = Colors.Faint(0.25f * dim);
        r.FillEllipse(new Ellipse(new Vector2(slot.X + 14, cy), 2.5f, 2.5f), brush);
        brush.Color = Colors.Faint(0.6f * dim);
        r.FillEllipse(new Ellipse(new Vector2(slot.Right - 14, cy), 4.5f, 4.5f), brush);

        var track = new RectangleF(slot.X + 32, cy - 2, slot.Width - 64, 4);
        if (which == Slider.Pattern) patternTrack = track; else veilTrack = track;
        float knobX = track.Left + track.Width * value;
        brush.Color = Colors.Faint(dark ? 0.16f : 0.12f);
        r.FillRoundedRectangle(new RoundedRectangle(track, 2, 2), brush);
        if (which == Slider.Pattern)
        {
            brush.Color = Colors.Faint(dark ? 0.35f : 0.3f);
            float mid = MathF.Round(track.Left + track.Width / 2) + 0.5f;
            r.DrawLine(new Vector2(mid, cy - 6), new Vector2(mid, cy + 6), brush, 1f);
        }
        if (enabled)
        {
            brush.Color = Colors.Accent;
            r.FillRoundedRectangle(new RoundedRectangle(new RectangleF(track.X, track.Y, knobX - track.X, 4), 2, 2), brush);
        }

        var knob = new Ellipse(new Vector2(knobX, cy), 8, 8);
        brush.Color = Colors.PanelShadow;
        r.FillEllipse(new Ellipse(new Vector2(knobX, cy + 1.5f), 8, 8), brush);
        brush.Color = enabled ? new Color4(1, 1, 1, 1) : Colors.Panel;
        r.FillEllipse(knob, brush);
        brush.Color = enabled ? Colors.Accent : Colors.PanelBorder;
        r.DrawEllipse(knob, brush, enabled ? 1.5f : 1f);

        if (enabled) menuButtons.Add((slot, () => BeginSlide(which)));
    }

    void SwitchRow(ID2D1RenderTarget r, float x, ref float cy, float row, string icon, string label, bool on, Action toggle)
    {
        const float pad = 6;
        var rect = new RectangleF(x + pad, cy, MenuWidth - pad * 2, row);
        brush!.Color = Colors.Icon;
        r.DrawText(icon, iconFont, new DRect(rect.X + 4, cy, 28, row), brush);
        r.DrawText(label, menuFont!, new DRect(rect.X + 40, cy, rect.Width - 96, row), brush);
        var track = new RectangleF(rect.Right - 46, cy + row / 2 - 10, 38, 20);
        brush.Color = on ? Colors.Accent : Colors.Faint(0.22f);
        r.FillRoundedRectangle(new RoundedRectangle(track, 10, 10), brush);
        brush.Color = new Color4(1, 1, 1, 1);
        float knobX = on ? track.Right - 10 : track.Left + 10;
        r.FillEllipse(new Ellipse(new Vector2(knobX, track.Y + 10), 7, 7), brush);
        menuButtons.Add((rect, toggle));
        cy += row;
    }

    // Whether the Startup shortcut exists; read when the menu opens, not on every frame.
    bool autostartOn;

    void ToggleAutostart()
    {
        try
        {
            Autostart.Set(!autostartOn);
            autostartOn = Autostart.IsOn();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            ShowNotice($"Couldn't change Start with Windows: {ex.Message}", seconds: 10);
        }
        Invalidate();
    }

    void ToggleMenu()
    {
        menuOpen = !menuOpen;
        if (menuOpen) autostartOn = Autostart.IsOn();
        Invalidate();
    }

    // A null click draws the row greyed out.
    void MenuRow(ID2D1RenderTarget r, float x, ref float cy, float row, string icon, string label, Action? click)
    {
        var rect = new RectangleF(x + 6, cy, MenuWidth - 12, row);
        brush!.Color = click != null ? Colors.Icon : WithAlpha(Colors.Icon, Colors.Icon.A * 0.35f);
        r.DrawText(icon, iconFont, new DRect(rect.X + 4, cy, 28, row), brush);
        r.DrawText(label, menuFont!, new DRect(rect.X + 40, cy, rect.Width - 44, row), brush);
        if (click != null) menuButtons.Add((rect, click));
        cy += row;
    }

    void MenuDivider(ID2D1RenderTarget r, float x, ref float cy, float height)
    {
        brush!.Color = Colors.Faint(dark ? 0.12f : 0.08f);
        float ly = MathF.Round(cy + height / 2) + 0.5f;
        r.DrawLine(new Vector2(x + 12, ly), new Vector2(x + MenuWidth - 12, ly), brush, 1f);
        cy += height;
    }

    void DrawPatternPreview(ID2D1RenderTarget r, Backdrop b, RectangleF area)
    {
        brush!.Color = Colors.Faint(0.45f);
        switch (b)
        {
            case Backdrop.Dots:
                for (int i = 0; i < 4; i++)
                    for (int j = 0; j < 3; j++)
                        r.FillEllipse(new Ellipse(new Vector2(area.X + 2 + i * 7.3f, area.Y + 3 + j * 7), 1.1f, 1.1f), brush);
                break;
            case Backdrop.Lines:
                for (int j = 0; j < 3; j++)
                    r.DrawLine(new Vector2(area.X, area.Y + 3 + j * 7), new Vector2(area.Right, area.Y + 3 + j * 7), brush, 1f);
                break;
            case Backdrop.Squares:
                for (int j = 0; j < 4; j++)
                    r.DrawLine(new Vector2(area.X, area.Y + j * 6.6f), new Vector2(area.Right, area.Y + j * 6.6f), brush, 1f);
                for (int i = 0; i < 5; i++)
                    r.DrawLine(new Vector2(area.X + i * 6.5f, area.Y), new Vector2(area.X + i * 6.5f, area.Bottom), brush, 1f);
                break;
            case Backdrop.Plain:
                r.DrawRoundedRectangle(new RoundedRectangle(area, 3, 3), brush, 1f);
                break;
        }
    }

    // ---------- the board background itself ----------

    void DrawBackdrop(ID2D1RenderTarget c)
    {
        switch (backdrop)
        {
            case Backdrop.Dots: DrawDots(c); break;
            case Backdrop.Lines: DrawRules(c, vertical: false); break;
            case Backdrop.Squares: DrawRules(c, vertical: true); break;
        }
    }

    // Notebook lines (horizontal only) or squared paper (both). Like the dots, the spacing stays
    // between 32 and 64 DIPs at any zoom, and in-between lines fade in so nothing pops.
    void DrawRules(ID2D1RenderTarget c, bool vertical)
    {
        float s = 32 * zoom;
        while (s < 32) s *= 2;
        while (s >= 64) s /= 2;
        float fade = Math.Clamp((s - 32) / 32, 0, 1);
        fade = fade * fade * (3 - 2 * fade);

        var size = ClientDips;
        float px = 1 / DpiScale;   // one device pixel: crisp hairlines
        float Snap(float v) => (MathF.Round(v * DpiScale) + 0.5f) / DpiScale;
        float strength = dark ? 0.75f : 0.6f;

        for (int pass = 0; pass < 2; pass++)
        {
            bool coarsePass = pass == 0;
            if (!coarsePass && fade <= 0.01f) break;
            var color = PatternColor(strength);
            brush!.Color = WithAlpha(color, color.A * (coarsePass ? 1 : fade));

            int j0 = (int)MathF.Floor(-offset.Y / s), j1 = (int)MathF.Ceiling((size.Y - offset.Y) / s);
            for (int j = j0; j <= j1; j++)
            {
                if (((j & 1) == 0) != coarsePass) continue;
                float y = Snap(offset.Y + j * s);
                c.DrawLine(new Vector2(0, y), new Vector2(size.X, y), brush, px);
            }
            if (!vertical) continue;
            int i0 = (int)MathF.Floor(-offset.X / s), i1 = (int)MathF.Ceiling((size.X - offset.X) / s);
            for (int i = i0; i <= i1; i++)
            {
                if (((i & 1) == 0) != coarsePass) continue;
                float x = Snap(offset.X + i * s);
                c.DrawLine(new Vector2(x, 0), new Vector2(x, size.Y), brush, px);
            }
        }
    }
}
