using System;
using System.Collections.Generic;

namespace Html2OpenRtf
{
    /// <summary>RTF font family classes (\fswiss, \froman, \fmodern, ...).</summary>
    internal enum FontKind
    {
        Sans,
        Serif,
        Mono,
        Script,
        Decorative,
    }

    /// <summary>
    /// Resolves a CSS <c>font-family</c> list into a concrete font name (plus an alternate for \falt)
    /// and an RTF font family class, so RTF readers can substitute sensibly when a font is missing.
    /// </summary>
    internal static class FontMapper
    {
        private static readonly HashSet<string> MonoFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Courier New", "Courier", "Consolas", "Lucida Console", "Lucida Sans Typewriter", "Cascadia Code",
            "Cascadia Mono", "Menlo", "Monaco", "SF Mono", "SFMono-Regular", "DejaVu Sans Mono", "Liberation Mono",
            "Bitstream Vera Sans Mono", "Ubuntu Mono", "Source Code Pro", "Fira Code", "Fira Mono", "JetBrains Mono",
            "Roboto Mono", "Inconsolata", "Hack", "Andale Mono", "Noto Mono", "Noto Sans Mono", "IBM Plex Mono",
            "MS Gothic", "NSimSun", "SimSun-ExtB", "Fixedsys", "Terminal", "OCR A Extended", "PT Mono", "Droid Sans Mono",
        };

        private static readonly HashSet<string> SerifFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Times New Roman", "Times", "Georgia", "Cambria", "Garamond", "Palatino", "Palatino Linotype",
            "Book Antiqua", "Constantia", "Baskerville", "Bookman Old Style", "Century Schoolbook", "Liberation Serif",
            "DejaVu Serif", "Noto Serif", "Sylfaen", "MS Mincho", "SimSun", "Perpetua", "Rockwell", "Didot",
        };

        /// <summary>Maps CSS generic family keywords to concrete Windows fonts.</summary>
        public static bool TryMapGeneric(string name, out string font, out FontKind kind)
        {
            switch (name.ToLowerInvariant())
            {
                case "monospace":
                case "ui-monospace":
                    font = "Courier New"; kind = FontKind.Mono; return true;
                case "serif":
                case "ui-serif":
                    font = "Times New Roman"; kind = FontKind.Serif; return true;
                case "sans-serif":
                case "ui-sans-serif":
                    font = "Arial"; kind = FontKind.Sans; return true;
                case "system-ui":
                case "-apple-system":
                case "blinkmacsystemfont":
                case "ui-rounded":
                    font = "Segoe UI"; kind = FontKind.Sans; return true;
                case "cursive":
                    font = "Comic Sans MS"; kind = FontKind.Script; return true;
                case "fantasy":
                    font = "Impact"; kind = FontKind.Decorative; return true;
                case "math":
                    font = "Cambria Math"; kind = FontKind.Serif; return true;
                case "emoji":
                    font = "Segoe UI Emoji"; kind = FontKind.Sans; return true;
                case "fangsong":
                    font = "FangSong"; kind = FontKind.Serif; return true;
                default:
                    font = name; kind = FontKind.Sans; return false;
            }
        }

        private static readonly HashSet<string> SansFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Arial", "Helvetica", "Helvetica Neue", "Verdana", "Tahoma", "Segoe UI", "Calibri", "Trebuchet MS",
            "Candara", "Corbel", "Lucida Sans Unicode", "Lucida Grande", "Gill Sans", "Franklin Gothic Medium",
            "Liberation Sans", "DejaVu Sans", "Noto Sans", "Roboto", "Open Sans", "Ubuntu", "Aptos", "Microsoft Sans Serif",
        };

        /// <summary>Classifies well-known fonts; returns false for fonts we know nothing about.</summary>
        private static bool TryClassifyKnown(string name, out FontKind kind)
        {
            if (TryMapGeneric(name, out _, out kind)) return true;
            if (MonoFonts.Contains(name)) { kind = FontKind.Mono; return true; }
            if (SerifFonts.Contains(name)) { kind = FontKind.Serif; return true; }
            if (SansFonts.Contains(name)) { kind = FontKind.Sans; return true; }
            if (name.IndexOf(" Mono", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Courier", StringComparison.OrdinalIgnoreCase) >= 0)
            { kind = FontKind.Mono; return true; }
            kind = FontKind.Sans;
            return false;
        }

        /// <summary>Best-effort classification of a concrete font name (unknown fonts are treated as sans-serif).</summary>
        public static FontKind Classify(string name)
        {
            TryClassifyKnown(name, out var kind);
            return kind;
        }

        /// <summary>
        /// Picks the font to use from a CSS family list, mimicking browser fallback:
        /// the first family that is a generic keyword or (when <paramref name="isAvailable"/> is given) is installed.
        /// Without an availability check, the first family is used. The next usable family becomes the \falt alternate.
        /// The font class is derived from the chosen font, or from any generic keyword in the list.
        /// </summary>
        public static void Resolve(List<string> families, Func<string, bool>? isAvailable,
            out string primary, out string? alternate, out FontKind kind)
        {
            primary = "";
            alternate = null;
            int chosenAt = -1;

            for (int i = 0; i < families.Count && chosenAt < 0; i++)
            {
                var name = families[i];
                if (TryMapGeneric(name, out var mapped, out _))
                {
                    primary = mapped;
                    chosenAt = i;
                }
                else if (isAvailable == null || SafeIsAvailable(isAvailable, name))
                {
                    primary = name;
                    chosenAt = i;
                }
            }

            if (chosenAt < 0)
            {
                // Nothing installed and no generic keyword: keep the author's first choice.
                primary = families.Count > 0 ? families[0] : "Arial";
                chosenAt = 0;
            }

            for (int i = chosenAt + 1; i < families.Count && alternate == null; i++)
            {
                var candidate = TryMapGeneric(families[i], out var mapped, out _) ? mapped : families[i];
                if (!candidate.Equals(primary, StringComparison.OrdinalIgnoreCase))
                    alternate = candidate;
            }

            if (!TryClassifyKnown(primary, out kind))
            {
                // Unknown font: the first recognizable family later in the list (e.g. a generic keyword)
                // tells us which class the author expects as fallback.
                for (int i = chosenAt + 1; i < families.Count; i++)
                    if (TryClassifyKnown(families[i], out kind)) break;
            }
        }

        private static bool SafeIsAvailable(Func<string, bool> isAvailable, string name)
        {
            try { return isAvailable(name); }
            catch { return true; }
        }
    }
}
