using System.Globalization;

namespace ClickUpTodo.Tui;

/// <summary>
/// Pure color math for status badges: parses a ClickUp hex color and picks a readable
/// (black or white) foreground for it using the WCAG relative-luminance / contrast model.
/// Deliberately free of any Terminal.Gui dependency so it can be unit-tested without a driver.
/// </summary>
public static class StatusBadgeColor
{
    /// <summary>
    /// Parses a ClickUp status color such as <c>#87909e</c>, <c>87909e</c>, or the 3-digit
    /// shorthand <c>#abc</c>. Returns false (and zeroed channels) for null/blank/malformed input.
    /// </summary>
    public static bool TryParseHex(string? hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex))
            return false;

        var s = hex.Trim();
        if (s.StartsWith('#'))
            s = s[1..];

        if (s.Length == 3)
            // Expand shorthand: "abc" -> "aabbcc".
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);

        if (s.Length != 6)
            return false;

        return TryHex(s, 0, out r) && TryHex(s, 2, out g) && TryHex(s, 4, out b);

        static bool TryHex(string s, int i, out int value)
            => int.TryParse(s.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>WCAG relative luminance in [0,1] for an sRGB color (channels 0–255).</summary>
    public static double RelativeLuminance(int r, int g, int b)
        => 0.2126 * Linearize(r) + 0.7152 * Linearize(g) + 0.0722 * Linearize(b);

    /// <summary>WCAG contrast ratio (1–21) between two relative luminances.</summary>
    public static double ContrastRatio(double luminanceA, double luminanceB)
    {
        var lighter = Math.Max(luminanceA, luminanceB);
        var darker = Math.Min(luminanceA, luminanceB);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// True when black text reads better on this background than white text (i.e. black has the
    /// higher WCAG contrast ratio), false when white text is preferable. Ties favor black.
    /// </summary>
    public static bool PreferDarkText(int r, int g, int b)
    {
        var bg = RelativeLuminance(r, g, b);
        var contrastWithBlack = ContrastRatio(bg, 0.0); // black text, luminance 0
        var contrastWithWhite = ContrastRatio(bg, 1.0); // white text, luminance 1
        return contrastWithBlack >= contrastWithWhite;
    }

    private static double Linearize(int channel)
    {
        var c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
