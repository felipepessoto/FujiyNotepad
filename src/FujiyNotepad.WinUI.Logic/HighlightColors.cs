using System.Globalization;

namespace FujiyNotepad.WinUI.Logic
{
    /// <summary>
    /// Resolves a highlight-rule colour token to a packed 0xAARRGGBB value. Accepts a small set of friendly
    /// colour names and hex notations (<c>#RGB</c>, <c>#RRGGBB</c>, <c>#AARRGGBB</c>). A colour given without an
    /// explicit alpha is made semi-transparent (50%) so the highlight reads as a background tint that works in
    /// both the light and dark themes (the opaque line text is always drawn on top, so it stays legible).
    /// </summary>
    public static class HighlightColors
    {
        /// <summary>Alpha applied to named colours and 3/6-digit hex (50% — a tint, not a solid block).</summary>
        public const uint DefaultAlpha = 0x80000000u;

        /// <summary>The colour used when a rule's colour token is missing or unrecognized.</summary>
        public const string DefaultColorName = "amber";

        // Friendly names -> RGB (alpha is added separately). Chosen as vivid mid shades that tint well at 50%.
        private static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
        {
            ["red"] = 0xFF5252,
            ["orange"] = 0xFF9800,
            ["amber"] = 0xFFC107,
            ["yellow"] = 0xFFEB3B,
            ["lime"] = 0xC0CA33,
            ["green"] = 0x4CAF50,
            ["teal"] = 0x009688,
            ["blue"] = 0x2196F3,
            ["cyan"] = 0x00BCD4,
            ["purple"] = 0x9C27B0,
            ["magenta"] = 0xE040FB,
            ["pink"] = 0xE91E63,
            ["gray"] = 0x9E9E9E,
            ["grey"] = 0x9E9E9E,
        };

        /// <summary>The recognized colour names, for help text and validation.</summary>
        public static IReadOnlyCollection<string> Names => Named.Keys;

        /// <summary>
        /// Parses <paramref name="token"/> (a name or hex value) into a packed 0xAARRGGBB colour. Returns false
        /// for a null/blank/unrecognized token, leaving <paramref name="argb"/> as 0.
        /// </summary>
        public static bool TryParse(string? token, out uint argb)
        {
            argb = 0;
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            token = token.Trim();

            if (Named.TryGetValue(token, out uint namedRgb))
            {
                argb = DefaultAlpha | namedRgb;
                return true;
            }

            if (token[0] == '#')
            {
                string hex = token.Substring(1);
                switch (hex.Length)
                {
                    case 3 when TryParseHex(Expand(hex), out uint rgb3):
                        argb = DefaultAlpha | rgb3;
                        return true;
                    case 6 when TryParseHex(hex, out uint rgb6):
                        argb = DefaultAlpha | rgb6;
                        return true;
                    case 8 when TryParseHex(hex, out uint argb8):
                        argb = argb8;
                        return true;
                }
            }

            return false;
        }

        // Expands a 3-digit "RGB" shorthand to the 6-digit "RRGGBB" form.
        private static string Expand(string rgb) =>
            new string(new[] { rgb[0], rgb[0], rgb[1], rgb[1], rgb[2], rgb[2] });

        private static bool TryParseHex(string hex, out uint value) =>
            uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
