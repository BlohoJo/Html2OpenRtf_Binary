using System;
using System.Collections.Generic;
using System.Globalization;

namespace Html2OpenRtf
{
    /// <summary>
    /// Parses CSS color values into (R, G, B) tuples.
    /// Supports: #rgb, #rgba, #rrggbb, #rrggbbaa, rgb()/rgba() (comma, space and slash syntax, percentages),
    /// hsl()/hsla() and all 148 CSS named colors.
    /// Fully transparent values (transparent, alpha = 0) and keywords such as inherit/currentColor return false.
    /// </summary>
    internal static class CssColorParser
    {
        public static bool TryParse(string? value, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            value = value!.Trim().ToLowerInvariant();

            if (value.StartsWith("#", StringComparison.Ordinal))
                return TryParseHex(value, out r, out g, out b);

            if (value.StartsWith("rgb", StringComparison.Ordinal))
                return TryParseRgb(value, out r, out g, out b);

            if (value.StartsWith("hsl", StringComparison.Ordinal))
                return TryParseHsl(value, out r, out g, out b);

            return TryParseNamed(value, out r, out g, out b);
        }

        private static bool TryParseHex(string hex, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            hex = hex.TrimStart('#');

            if (hex.Length == 4 && hex[3] == '0')
                return false; // #rgb0 = fully transparent
            if (hex.Length == 8 && hex.Substring(6, 2) == "00")
                return false; // #rrggbb00 = fully transparent

            if (hex.Length == 3 || hex.Length == 4)
            {
                // #rgb or #rgba → expand first 3 digits, ignore alpha if present
                hex = new string(new[] { hex[0], hex[0], hex[1], hex[1], hex[2], hex[2] });
            }
            else if (hex.Length == 8)
            {
                // #rrggbbaa → #rrggbb (strip alpha)
                hex = hex.Substring(0, 6);
            }

            if (hex.Length != 6) return false;

            return byte.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
                && byte.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
                && byte.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
        }

        /// <summary>Splits the inside of a CSS color function into components; returns alpha separately (or null).</summary>
        private static bool TrySplitFunction(string value, out string[] parts, out string? alpha)
        {
            parts = Array.Empty<string>();
            alpha = null;
            int open = value.IndexOf('(');
            int close = value.LastIndexOf(')');
            if (open < 0 || close < 0 || close <= open) return false;

            var inner = value.Substring(open + 1, close - open - 1).Trim();

            // Handle slash-separated alpha: "255 128 0 / 0.5"
            int slashIdx = inner.IndexOf('/');
            if (slashIdx >= 0)
            {
                alpha = inner.Substring(slashIdx + 1).Trim();
                inner = inner.Substring(0, slashIdx).Trim();
            }

            // Try comma-separated first, then space-separated
            if (inner.IndexOf(',') >= 0)
                parts = inner.Split(',');
            else
                parts = inner.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 3) return false;
            if (parts.Length >= 4 && alpha == null)
                alpha = parts[3].Trim();
            return true;
        }

        private static bool IsZeroAlpha(string? alpha)
        {
            if (string.IsNullOrEmpty(alpha)) return false;
            var a = alpha!.Trim();
            bool pct = a.EndsWith("%", StringComparison.Ordinal);
            if (pct) a = a.Substring(0, a.Length - 1);
            return double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v <= 0;
        }

        private static bool TryParseRgb(string value, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            // rgb(255, 128, 0), rgba(255, 128, 0, 1.0), rgb(255 128 0), rgb(100%, 50%, 0%)
            if (!TrySplitFunction(value, out var parts, out var alpha)) return false;
            if (IsZeroAlpha(alpha)) return false;

            if (!TryParseColorComponent(parts[0].Trim(), out r)) return false;
            if (!TryParseColorComponent(parts[1].Trim(), out g)) return false;
            if (!TryParseColorComponent(parts[2].Trim(), out b)) return false;

            return true;
        }

        private static bool TryParseColorComponent(string val, out byte result)
        {
            result = 0;
            if (val.EndsWith("%", StringComparison.Ordinal))
            {
                if (double.TryParse(val.Substring(0, val.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
                {
                    result = ClampByte(pct / 100.0 * 255);
                    return true;
                }
                return false;
            }
            if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
            {
                result = ClampByte(num);
                return true;
            }
            return false;
        }

        private static bool TryParseHsl(string value, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            // hsl(210, 50%, 40%), hsla(210, 50%, 40%, .5), hsl(210deg 50% 40% / .5)
            if (!TrySplitFunction(value, out var parts, out var alpha)) return false;
            if (IsZeroAlpha(alpha)) return false;

            var hText = parts[0].Trim();
            double hMul = 1;
            if (hText.EndsWith("deg", StringComparison.Ordinal)) hText = hText.Substring(0, hText.Length - 3);
            else if (hText.EndsWith("turn", StringComparison.Ordinal)) { hText = hText.Substring(0, hText.Length - 4); hMul = 360; }
            else if (hText.EndsWith("rad", StringComparison.Ordinal)) { hText = hText.Substring(0, hText.Length - 3); hMul = 180 / Math.PI; }

            if (!double.TryParse(hText, NumberStyles.Float, CultureInfo.InvariantCulture, out double h)) return false;
            if (!TryParsePercent(parts[1], out double s) || !TryParsePercent(parts[2], out double l)) return false;

            h = ((h * hMul % 360) + 360) % 360 / 360.0;
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            r = ClampByte(HueToRgb(p, q, h + 1.0 / 3) * 255);
            g = ClampByte(HueToRgb(p, q, h) * 255);
            b = ClampByte(HueToRgb(p, q, h - 1.0 / 3) * 255);
            return true;
        }

        private static bool TryParsePercent(string text, out double fraction)
        {
            text = text.Trim().TrimEnd('%');
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                fraction = Math.Max(0, Math.Min(100, v)) / 100.0;
                return true;
            }
            fraction = 0;
            return false;
        }

        private static double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        private static byte ClampByte(double v) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v)));

        private static bool TryParseNamed(string name, out byte r, out byte g, out byte b)
        {
            if (NamedColors.TryGetValue(name, out int rgb))
            {
                r = (byte)(rgb >> 16);
                g = (byte)(rgb >> 8);
                b = (byte)rgb;
                return true;
            }
            r = g = b = 0;
            return false;
        }

        // All CSS Color Module Level 4 named colors.
        private static readonly Dictionary<string, int> NamedColors = BuildNamedColors(
            "aliceblue:f0f8ff antiquewhite:faebd7 aqua:00ffff aquamarine:7fffd4 azure:f0ffff beige:f5f5dc bisque:ffe4c4 " +
            "black:000000 blanchedalmond:ffebcd blue:0000ff blueviolet:8a2be2 brown:a52a2a burlywood:deb887 cadetblue:5f9ea0 " +
            "chartreuse:7fff00 chocolate:d2691e coral:ff7f50 cornflowerblue:6495ed cornsilk:fff8dc crimson:dc143c cyan:00ffff " +
            "darkblue:00008b darkcyan:008b8b darkgoldenrod:b8860b darkgray:a9a9a9 darkgreen:006400 darkgrey:a9a9a9 " +
            "darkkhaki:bdb76b darkmagenta:8b008b darkolivegreen:556b2f darkorange:ff8c00 darkorchid:9932cc darkred:8b0000 " +
            "darksalmon:e9967a darkseagreen:8fbc8f darkslateblue:483d8b darkslategray:2f4f4f darkslategrey:2f4f4f " +
            "darkturquoise:00ced1 darkviolet:9400d3 deeppink:ff1493 deepskyblue:00bfff dimgray:696969 dimgrey:696969 " +
            "dodgerblue:1e90ff firebrick:b22222 floralwhite:fffaf0 forestgreen:228b22 fuchsia:ff00ff gainsboro:dcdcdc " +
            "ghostwhite:f8f8ff gold:ffd700 goldenrod:daa520 gray:808080 green:008000 greenyellow:adff2f grey:808080 " +
            "honeydew:f0fff0 hotpink:ff69b4 indianred:cd5c5c indigo:4b0082 ivory:fffff0 khaki:f0e68c lavender:e6e6fa " +
            "lavenderblush:fff0f5 lawngreen:7cfc00 lemonchiffon:fffacd lightblue:add8e6 lightcoral:f08080 lightcyan:e0ffff " +
            "lightgoldenrodyellow:fafad2 lightgray:d3d3d3 lightgreen:90ee90 lightgrey:d3d3d3 lightpink:ffb6c1 " +
            "lightsalmon:ffa07a lightseagreen:20b2aa lightskyblue:87cefa lightslategray:778899 lightslategrey:778899 " +
            "lightsteelblue:b0c4de lightyellow:ffffe0 lime:00ff00 limegreen:32cd32 linen:faf0e6 magenta:ff00ff maroon:800000 " +
            "mediumaquamarine:66cdaa mediumblue:0000cd mediumorchid:ba55d3 mediumpurple:9370db mediumseagreen:3cb371 " +
            "mediumslateblue:7b68ee mediumspringgreen:00fa9a mediumturquoise:48d1cc mediumvioletred:c71585 " +
            "midnightblue:191970 mintcream:f5fffa mistyrose:ffe4e1 moccasin:ffe4b5 navajowhite:ffdead navy:000080 " +
            "oldlace:fdf5e6 olive:808000 olivedrab:6b8e23 orange:ffa500 orangered:ff4500 orchid:da70d6 palegoldenrod:eee8aa " +
            "palegreen:98fb98 paleturquoise:afeeee palevioletred:db7093 papayawhip:ffefd5 peachpuff:ffdab9 peru:cd853f " +
            "pink:ffc0cb plum:dda0dd powderblue:b0e0e6 purple:800080 rebeccapurple:663399 red:ff0000 rosybrown:bc8f8f " +
            "royalblue:4169e1 saddlebrown:8b4513 salmon:fa8072 sandybrown:f4a460 seagreen:2e8b57 seashell:fff5ee " +
            "sienna:a0522d silver:c0c0c0 skyblue:87ceeb slateblue:6a5acd slategray:708090 slategrey:708090 snow:fffafa " +
            "springgreen:00ff7f steelblue:4682b4 tan:d2b48c teal:008080 thistle:d8bfd8 tomato:ff6347 turquoise:40e0d0 " +
            "violet:ee82ee wheat:f5deb3 white:ffffff whitesmoke:f5f5f5 yellow:ffff00 yellowgreen:9acd32");

        private static Dictionary<string, int> BuildNamedColors(string table)
        {
            var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in table.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = entry.IndexOf(':');
                dict[entry.Substring(0, colon)] = int.Parse(entry.Substring(colon + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            return dict;
        }
    }
}
