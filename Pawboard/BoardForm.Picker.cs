using System.Numerics;
using Vortice.Direct2D1;
using Color4 = Vortice.Mathematics.Color4;
using DRect = Vortice.Mathematics.Rect;

namespace Pawboard;

// The custom colour popup above the toolbar: a saturation / brightness square, a hue bar and a
// readout. Drawn on the board like the menu, so it works the same on the wallpaper. Colours apply live.
public sealed partial class BoardForm
{
    const float PickerWidth = 236, PickerPad = 12, PickerSquareHeight = 128, PickerBarHeight = 14, PickerReadoutHeight = 44;

    bool pickerOpen;
    float pickH, pickS, pickV;   // hue 0-360, saturation and brightness 0-1; kept apart so grey keeps its hue
    RectangleF pickerRect, pickerSquareRect, pickerHueRect, pickerSwatchRect;

    bool PopupOpen => menuOpen || pickerOpen;

    void ClosePopups()
    {
        CloseMenu();
        ClosePicker();
    }

    void TogglePicker()
    {
        if (pickerOpen) { ClosePicker(); return; }
        CloseMenu();
        (pickH, pickS, pickV) = ArgbToHsv(ActiveColor);
        pickerOpen = true;
        Invalidate();
    }

    void ClosePicker()
    {
        if (!pickerOpen) return;
        pickerOpen = false;
        Invalidate();
    }

    // Handles a left click while the picker is open. Returns true if the click was used up.
    bool PickerClick(Vector2 at)
    {
        if (!pickerOpen) return false;
        if (pickerRect.Contains(at.X, at.Y))
        {
            var hueHit = RectangleF.Inflate(pickerHueRect, 8, 8);
            if (pickerSquareRect.Contains(at.X, at.Y)) BeginPick(Mode.PickSquare);
            else if (hueHit.Contains(at.X, at.Y)) BeginPick(Mode.PickHue);
            Invalidate();
            return true;
        }
        // Anywhere else closes it. A click on the toolbar still does its job; on the board it's only a close.
        ClosePicker();
        return !toolbarRect.Contains(at.X, at.Y) || pickerSwatchRect.Contains(at.X, at.Y);
    }

    void BeginPick(Mode m)
    {
        mode = m;
        modeButton = MouseButtons.Left;
        PickTo(cursor);
    }

    void PickTo(Vector2 at)
    {
        if (mode == Mode.PickSquare)
        {
            pickS = Math.Clamp((at.X - pickerSquareRect.Left) / pickerSquareRect.Width, 0, 1);
            pickV = 1 - Math.Clamp((at.Y - pickerSquareRect.Top) / pickerSquareRect.Height, 0, 1);
        }
        else pickH = Math.Clamp((at.X - pickerHueRect.Left) / pickerHueRect.Width, 0, 1) * 360;

        customColor = HsvToArgb(pickH, pickS, pickV);
        useCustomColor = true;
        if (tool is Tool.Eraser or Tool.Desktop) SetTool(Tool.Pen);   // picking a colour means you want to draw
        if (editing != null) editing.Color = customColor;
        Invalidate();
    }

    void DrawPicker(ID2D1RenderTarget r)
    {
        if (!pickerOpen) return;
        const float pad = PickerPad, gap = 14;
        float h = pad + PickerSquareHeight + gap + PickerBarHeight + gap + PickerReadoutHeight + 6;
        float minX = toolbarRect.Left, maxX = Math.Max(minX, toolbarRect.Right - PickerWidth);
        float x = Math.Clamp(pickerSwatchRect.X + pickerSwatchRect.Width / 2 - PickerWidth / 2, minX, maxX);
        float y = toolbarRect.Top - 8 - h;
        pickerRect = new RectangleF(x, y, PickerWidth, h);

        brush!.Color = Colors.PanelShadow;
        r.FillRoundedRectangle(new RoundedRectangle(new RectangleF(x, y + 2, PickerWidth, h), 12, 12), brush);
        brush.Color = Colors.Panel;
        r.FillRoundedRectangle(new RoundedRectangle(pickerRect, 12, 12), brush);
        brush.Color = Colors.PanelBorder;
        r.DrawRoundedRectangle(new RoundedRectangle(new RectangleF(x + 0.5f, y + 0.5f, PickerWidth - 1, h - 1), 12, 12), brush, 1f);

        // Saturation (left to right) and brightness (top to bottom) of the chosen hue.
        pickerSquareRect = new RectangleF(x + pad, y + pad, PickerWidth - pad * 2, PickerSquareHeight);
        var square = new RoundedRectangle(pickerSquareRect, 8, 8);
        var pure = Argb(HsvToArgb(pickH, 1, 1));
        using (var stops = r.CreateGradientStopCollection([Stop(0, new Color4(1, 1, 1, 1)), Stop(1, pure)]))
        using (var across = r.CreateLinearGradientBrush(new LinearGradientBrushProperties
        {
            StartPoint = new Vector2(pickerSquareRect.Left, 0),
            EndPoint = new Vector2(pickerSquareRect.Right, 0),
        }, stops))
            r.FillRoundedRectangle(square, across);
        using (var stops = r.CreateGradientStopCollection([Stop(0, new Color4(0, 0, 0, 0)), Stop(1, new Color4(0, 0, 0, 1))]))
        using (var down = r.CreateLinearGradientBrush(new LinearGradientBrushProperties
        {
            StartPoint = new Vector2(0, pickerSquareRect.Top),
            EndPoint = new Vector2(0, pickerSquareRect.Bottom),
        }, stops))
            r.FillRoundedRectangle(square, down);
        brush.Color = Colors.Faint(0.1f);
        r.DrawRoundedRectangle(square, brush, 1f);

        uint argb = HsvToArgb(pickH, pickS, pickV);
        var picked = Argb(argb);
        DrawPickerKnob(r, new Vector2(pickerSquareRect.Left + pickS * pickerSquareRect.Width,
            pickerSquareRect.Top + (1 - pickV) * pickerSquareRect.Height), 8, picked);

        // Hue.
        pickerHueRect = new RectangleF(x + pad, pickerSquareRect.Bottom + gap, PickerWidth - pad * 2, PickerBarHeight);
        using (var hue = HueBrush(r, new Vector2(pickerHueRect.Left, 0), new Vector2(pickerHueRect.Right, 0)))
            r.FillRoundedRectangle(new RoundedRectangle(pickerHueRect, PickerBarHeight / 2, PickerBarHeight / 2), hue);
        DrawPickerKnob(r, new Vector2(pickerHueRect.Left + pickH / 360 * pickerHueRect.Width,
            pickerHueRect.Top + PickerBarHeight / 2), 9, pure);

        // The colour as a chip, with its hex and RGB values.
        float ry = pickerHueRect.Bottom + gap;
        var chip = new Vector2(x + pad + 15, ry + PickerReadoutHeight / 2 - 2);
        brush.Color = picked;
        r.FillEllipse(new Ellipse(chip, 14, 14), brush);
        brush.Color = Colors.Faint(0.14f);
        r.DrawEllipse(new Ellipse(chip, 14, 14), brush, 1f);

        int red = (int)((argb >> 16) & 0xFF), green = (int)((argb >> 8) & 0xFF), blue = (int)(argb & 0xFF);
        float tx = x + pad + 38, tw = PickerWidth - pad * 2 - 38;
        brush.Color = Colors.Icon;
        r.DrawText($"#{argb & 0xFFFFFF:X6}", menuFont!, new DRect(tx, ry + 2, tw, 22), brush);
        brush.Color = Colors.Faint(0.5f);
        r.DrawText($"R {red}   G {green}   B {blue}", captionFont!, new DRect(tx, ry + 22, tw, 18), brush);
    }

    // A round handle: white ring, a soft shadow under it, the colour inside.
    void DrawPickerKnob(ID2D1RenderTarget r, Vector2 center, float radius, Color4 fill)
    {
        brush!.Color = new Color4(0, 0, 0, 0.3f);
        r.FillEllipse(new Ellipse(center + new Vector2(0, 1.5f), radius + 0.5f, radius + 0.5f), brush);
        brush.Color = new Color4(1, 1, 1, 1);
        r.FillEllipse(new Ellipse(center, radius, radius), brush);
        brush.Color = fill;
        r.FillEllipse(new Ellipse(center, radius - 2.5f, radius - 2.5f), brush);
    }

    // The toolbar's custom swatch: the chosen colour, or a hue sweep until one is picked.
    void DrawCustomSwatch(ID2D1RenderTarget r, Vector2 center, float alpha, bool selected)
    {
        if (selected || pickerOpen)
        {
            brush!.Color = WithAlpha(useCustomColor ? Argb(Colors.Display(customColor)) : Colors.Accent, 0.35f);
            r.DrawEllipse(new Ellipse(center, 12, 12), brush, 2f);
        }
        if (useCustomColor)
        {
            brush!.Color = WithAlpha(Argb(Colors.Display(customColor)), alpha);
            r.FillEllipse(new Ellipse(center, 8, 8), brush);
            return;
        }
        using var sweep = HueBrush(r, new Vector2(center.X - 8, center.Y), new Vector2(center.X + 8, center.Y));
        sweep.Opacity = alpha;
        r.FillEllipse(new Ellipse(center, 8, 8), sweep);
    }

    static GradientStop Stop(float position, Color4 color) => new() { Position = position, Color = color };

    static ID2D1LinearGradientBrush HueBrush(ID2D1RenderTarget r, Vector2 from, Vector2 to)
    {
        var stops = new GradientStop[7];
        for (int i = 0; i < stops.Length; i++) stops[i] = Stop(i / 6f, Argb(HsvToArgb(i * 60, 1, 1)));
        using var collection = r.CreateGradientStopCollection(stops);
        return r.CreateLinearGradientBrush(new LinearGradientBrushProperties { StartPoint = from, EndPoint = to }, collection);
    }

    static uint HsvToArgb(float h, float s, float v)
    {
        h = (h % 360 + 360) % 360;
        float c = v * s, x = c * (1 - MathF.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        static uint Byte(float f) => (uint)Math.Clamp(MathF.Round(f * 255), 0, 255);
        return 0xFF000000 | Byte(r + m) << 16 | Byte(g + m) << 8 | Byte(b + m);
    }

    static (float H, float S, float V) ArgbToHsv(uint argb)
    {
        float r = ((argb >> 16) & 0xFF) / 255f, g = ((argb >> 8) & 0xFF) / 255f, b = (argb & 0xFF) / 255f;
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b)), d = max - min;
        float h = d == 0 ? 0
            : max == r ? 60 * ((g - b) / d % 6 + 6) % 360
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        return (h, max == 0 ? 0 : d / max, max);
    }
}
