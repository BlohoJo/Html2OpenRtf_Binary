using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Html2OpenRtf
{
    /// <summary>
    /// Small, dependency-free helpers for parsing inline CSS (the <c>style</c> attribute).
    /// Sizes are returned in RTF units: half-points for font sizes, twips (1/20 pt) for lengths.
    /// </summary>
    internal static class CssStyleParser
    {
        /// <summary>Default (root) font size in half-points: 12pt, which corresponds to the browser default of 16px.</summary>
        public const int RootFontSize = 24;

        /// <summary>
        /// Parses a style attribute into an ordered list of (lower-case property, value) pairs.
        /// Respects quotes and parentheses, so values like <c>url(data:...;base64,...)</c> or quoted font names are safe.
        /// <c>!important</c> is stripped.
        /// </summary>
        public static List<KeyValuePair<string, string>> ParseDeclarations(string? style)
        {
            var result = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrWhiteSpace(style)) return result;

            foreach (var part in SplitOutside(style!, ';'))
            {
                int colon = part.IndexOf(':');
                if (colon <= 0) continue;
                var key = part.Substring(0, colon).Trim().ToLowerInvariant();
                var val = part.Substring(colon + 1).Trim();
                int imp = val.LastIndexOf("!important", StringComparison.OrdinalIgnoreCase);
                if (imp >= 0) val = val.Substring(0, imp).Trim();
                if (key.Length > 0 && val.Length > 0)
                    result.Add(new KeyValuePair<string, string>(key, val));
            }
            return result;
        }

        /// <summary>Returns the value of the last declaration of <paramref name="property"/>, or null.</summary>
        public static string? GetLast(List<KeyValuePair<string, string>> decls, string property)
        {
            for (int i = decls.Count - 1; i >= 0; i--)
                if (decls[i].Key == property) return decls[i].Value;
            return null;
        }

        /// <summary>Splits on <paramref name="separator"/> when not inside quotes or parentheses.</summary>
        public static List<string> SplitOutside(string text, char separator)
        {
            var parts = new List<string>();
            var sb = new StringBuilder();
            int depth = 0;
            char quote = '\0';
            foreach (char c in text)
            {
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                }
                else if (c == '"' || c == '\'') quote = c;
                else if (c == '(') depth++;
                else if (c == ')' && depth > 0) depth--;
                else if (c == separator && depth == 0)
                {
                    parts.Add(sb.ToString());
                    sb.Clear();
                    continue;
                }
                sb.Append(c);
            }
            parts.Add(sb.ToString());
            return parts;
        }

        /// <summary>Splits on whitespace when not inside quotes or parentheses.</summary>
        public static List<string> SplitTokens(string text)
        {
            var tokens = new List<string>();
            var sb = new StringBuilder();
            int depth = 0;
            char quote = '\0';
            foreach (char c in text)
            {
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                }
                else if (c == '"' || c == '\'') quote = c;
                else if (c == '(') depth++;
                else if (c == ')' && depth > 0) depth--;
                else if (char.IsWhiteSpace(c) && depth == 0)
                {
                    if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0) tokens.Add(sb.ToString());
            return tokens;
        }

        private static bool TrySplitNumber(string value, out double number, out string unit)
        {
            number = 0;
            unit = "";
            value = value.Trim().ToLowerInvariant();
            int i = 0;
            while (i < value.Length && (char.IsDigit(value[i]) || value[i] == '.' || value[i] == '-' || value[i] == '+'))
                i++;
            if (i == 0) return false;
            if (!double.TryParse(value.Substring(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                return false;
            unit = value.Substring(i).Trim();
            return true;
        }

        private static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

        /// <summary>
        /// Parse CSS font-size to RTF half-points. Relative units (em, %, smaller, larger) are resolved
        /// against <paramref name="parentHalfPoints"/>. Returns 0 if the value is not understood.
        /// Supports: pt, px, em, rem, %, in, cm, mm, pc, absolute keywords (small, large, ...), smaller/larger.
        /// </summary>
        public static int ParseFontSize(string value, int parentHalfPoints)
        {
            value = value.Trim().ToLowerInvariant();

            switch (value)
            {
                case "xx-small": return 14;  // 7pt
                case "x-small": return 16;   // 8pt
                case "small": return 20;     // 10pt
                case "medium": return 24;    // 12pt
                case "large": return 28;     // 14pt
                case "x-large": return 36;   // 18pt
                case "xx-large": return 48;  // 24pt
                case "xxx-large": return 72; // 36pt
                case "smaller": return Clamp(Round(parentHalfPoints / 1.2));
                case "larger": return Clamp(Round(parentHalfPoints * 1.2));
            }

            if (!TrySplitNumber(value, out double n, out string unit) || n <= 0)
                return 0;

            double halfPts;
            switch (unit)
            {
                case "pt": halfPts = n * 2; break;
                case "px": halfPts = n * 1.5; break;          // 1px = 0.75pt
                case "em": halfPts = n * parentHalfPoints; break;
                case "rem": halfPts = n * RootFontSize; break;
                case "%": halfPts = n / 100.0 * parentHalfPoints; break;
                case "in": halfPts = n * 144; break;
                case "cm": halfPts = n * 144 / 2.54; break;
                case "mm": halfPts = n * 144 / 25.4; break;
                case "pc": halfPts = n * 24; break;
                case "": halfPts = n * 1.5; break;            // unitless: treat as px (quirks mode behavior)
                default: return 0;
            }
            return Clamp(Round(halfPts));
        }

        private static int Clamp(int halfPts) => Math.Max(2, Math.Min(3276, halfPts));

        /// <summary>
        /// Parses a CSS length into twips. em/% are resolved against <paramref name="fontHalfPoints"/>.
        /// Unitless values are rejected unless they are 0.
        /// </summary>
        public static bool TryParseLength(string value, int fontHalfPoints, out int twips)
        {
            twips = 0;
            if (!TrySplitNumber(value, out double n, out string unit)) return false;
            double t;
            switch (unit)
            {
                case "pt": t = n * 20; break;
                case "px": t = n * 15; break;
                case "em": t = n * fontHalfPoints * 10; break;
                case "rem": t = n * RootFontSize * 10; break;
                case "%": t = n / 100.0 * fontHalfPoints * 10; break;
                case "in": t = n * 1440; break;
                case "cm": t = n * 1440 / 2.54; break;
                case "mm": t = n * 1440 / 25.4; break;
                case "pc": t = n * 240; break;
                case "": if (n != 0) return false; t = 0; break;
                default: return false;
            }
            twips = Round(t);
            return true;
        }

        /// <summary>
        /// Parses CSS line-height. "normal" yields factor 0 and twips 0.
        /// A unitless number yields a factor (inherited as a factor, as in CSS);
        /// lengths and percentages yield an absolute height in twips (computed against <paramref name="fontHalfPoints"/>).
        /// </summary>
        public static bool TryParseLineHeight(string value, int fontHalfPoints, out double factor, out int twips)
        {
            factor = 0;
            twips = 0;
            value = value.Trim().ToLowerInvariant();
            if (value == "normal") return true;
            if (!TrySplitNumber(value, out double n, out string unit) || n < 0) return false;
            if (unit.Length == 0)
            {
                factor = n;
                return n > 0;
            }
            return TryParseLength(value, fontHalfPoints, out twips) && twips > 0;
        }

        /// <summary>Splits a CSS font-family list into unquoted family names.</summary>
        public static List<string> ParseFontFamilyList(string value)
        {
            var result = new List<string>();
            foreach (var raw in SplitOutside(value, ','))
            {
                var name = raw.Trim();
                if (name.Length >= 2 && (name[0] == '"' || name[0] == '\'') && name[name.Length - 1] == name[0])
                    name = name.Substring(1, name.Length - 2).Trim();
                else
                    name = string.Join(" ", name.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
                if (name.Length > 0) result.Add(name);
            }
            return result;
        }

        /// <summary>Result of parsing the CSS <c>font</c> shorthand.</summary>
        public sealed class FontShorthand
        {
            public bool Bold;
            public bool Italic;
            public string Size = "";
            public string? LineHeight;
            public string Family = "";
        }

        /// <summary>
        /// Parses the CSS <c>font</c> shorthand: <c>[style] [variant] [weight] [stretch] size[/line-height] family</c>.
        /// </summary>
        public static bool TryParseFontShorthand(string value, out FontShorthand result)
        {
            result = new FontShorthand();
            var text = value.Trim();
            int pos = 0;
            while (pos < text.Length)
            {
                while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
                int start = pos;
                while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] != '/') pos++;
                if (start == pos) return false;
                var token = text.Substring(start, pos - start);
                var lower = token.ToLowerInvariant();

                if (ParseFontSize(token, RootFontSize) > 0 && !IsWeightNumber(lower))
                {
                    result.Size = token;
                    while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
                    if (pos < text.Length && text[pos] == '/')
                    {
                        pos++;
                        while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
                        int lhStart = pos;
                        while (pos < text.Length && !char.IsWhiteSpace(text[pos])) pos++;
                        result.LineHeight = text.Substring(lhStart, pos - lhStart);
                    }
                    result.Family = text.Substring(pos).Trim();
                    return result.Family.Length > 0;
                }

                switch (lower)
                {
                    case "italic":
                    case "oblique":
                        result.Italic = true;
                        break;
                    case "bold":
                    case "bolder":
                        result.Bold = true;
                        break;
                    default:
                        if (IsWeightNumber(lower) && int.Parse(lower, CultureInfo.InvariantCulture) >= 600)
                            result.Bold = true;
                        break;
                }
            }
            return false;
        }

        private static bool IsWeightNumber(string token)
        {
            return token.Length == 3 && token[1] == '0' && token[2] == '0' && token[0] >= '1' && token[0] <= '9';
        }

        /// <summary>
        /// Extracts the color component from a <c>background</c> shorthand value (e.g. <c>#1e1e1e url(x.png) no-repeat</c>).
        /// Returns false if no (non-transparent) color is present.
        /// </summary>
        public static bool TryGetBackgroundColor(string value, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            // Only the final layer of a multi-layer background may carry a color.
            var layers = SplitOutside(value, ',');
            var last = layers[layers.Count - 1];
            foreach (var token in SplitTokens(last))
            {
                if (CssColorParser.TryParse(token, out r, out g, out b))
                    return true;
            }
            return false;
        }
    }
}
