using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Html2OpenRtf
{
    /// <summary>
    /// Builds an RTF document string incrementally.
    /// Manages font table, color table, and emits RTF control words.
    /// </summary>
    internal sealed class RtfDocumentBuilder
    {
        /// <summary>Font table index of the default proportional font (Arial).</summary>
        public const int DefaultFontIndex = 0;

        /// <summary>Font table index of the default monospace font (Courier New).</summary>
        public const int MonospaceFontIndex = 1;

        private readonly StringBuilder _body = new StringBuilder();
        private readonly List<RtfColor> _colors = new List<RtfColor>();
        private readonly List<RtfFont> _fonts = new List<RtfFont>();
        private readonly bool _emitHighlight;

        public RtfDocumentBuilder() : this(emitHighlight: false) { }

        /// <param name="emitHighlight">
        /// Also emit <c>\highlightN</c> next to <c>\chcbpatN</c> for character backgrounds
        /// (helps RichEdit-based readers such as WordPad / RichTextBox).
        /// </param>
        public RtfDocumentBuilder(bool emitHighlight)
        {
            _emitHighlight = emitHighlight;

            // Default fonts
            _fonts.Add(new RtfFont("Arial", FontKind.Sans, null));        // \f0 - default proportional
            _fonts.Add(new RtfFont("Courier New", FontKind.Mono, null));  // \f1 - monospace (for <pre>/<code>)

            // Index 0 = auto color (placeholder; rendered as empty entry ";" in color table)
            _colors.Add(new RtfColor(0, 0, 0));
        }

        /// <summary>Current length of the body buffer (used to remember positions for later removal).</summary>
        public int Length => _body.Length;

        /// <summary>Removes a previously emitted fragment from the body buffer.</summary>
        public void Remove(int start, int length) => _body.Remove(start, length);

        /// <summary>
        /// Register a color and return its 1-based index for use in \cfN / \chcbpatN.
        /// Index 0 is reserved for auto/default color and is never returned.
        /// </summary>
        public int RegisterColor(byte r, byte g, byte b)
        {
            // Skip index 0 (auto color) — explicit colors always get their own entry
            for (int i = 1; i < _colors.Count; i++)
            {
                if (_colors[i].R == r && _colors[i].G == g && _colors[i].B == b)
                    return i;
            }
            _colors.Add(new RtfColor(r, g, b));
            return _colors.Count - 1;
        }

        /// <summary>
        /// Register a font by name and return its index for use in \fN. Names are matched case-insensitively.
        /// </summary>
        public int RegisterFont(string name, FontKind kind, string? alternate)
        {
            name = SanitizeFontName(name);
            if (name.Length == 0) return DefaultFontIndex;
            alternate = alternate == null ? null : SanitizeFontName(alternate);
            if (alternate != null && (alternate.Length == 0 || alternate.Equals(name, StringComparison.OrdinalIgnoreCase)))
                alternate = null;

            for (int i = 0; i < _fonts.Count; i++)
            {
                if (_fonts[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            _fonts.Add(new RtfFont(name, kind, alternate));
            return _fonts.Count - 1;
        }

        /// <summary>True if the font at <paramref name="index"/> is a fixed-pitch (monospace) font.</summary>
        public bool IsMonospaceFont(int index) =>
            index >= 0 && index < _fonts.Count && _fonts[index].Kind == FontKind.Mono;

        private static string SanitizeFontName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                // ';' terminates a font table entry; braces/backslashes would break the RTF structure.
                if (c == ';' || c == '{' || c == '}' || c == '\\' || char.IsControl(c)) continue;
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        public void AppendText(string text) => AppendEscaped(_body, text);

        private static void AppendEscaped(StringBuilder sb, string text)
        {
            foreach (char c in text)
            {
                if (c == '\\')
                    sb.Append("\\\\");
                else if (c == '{')
                    sb.Append("\\{");
                else if (c == '}')
                    sb.Append("\\}");
                else if (c == '\n')
                    sb.Append("\\line ");
                else if (c == '\t')
                    sb.Append("\\tab ");
                else if (c < 0x20 || c == 0x7F)
                    continue; // skip CR and other control characters
                else if (c > 127)
                    AppendUnicode(sb, c);
                else
                    sb.Append(c);
            }
        }

        private static void AppendUnicode(StringBuilder sb, char c)
        {
            // RTF Unicode escape: \uN? where N is signed 16-bit and ? is fallback char
            short signed = unchecked((short)c);
            sb.Append("\\u");
            sb.Append(signed.ToString(CultureInfo.InvariantCulture));
            sb.Append('?');
        }

        public void AppendRaw(string rtf) => _body.Append(rtf);

        public void LineBreak() => _body.Append("\\line ");

        public void Bold(bool on) => _body.Append(on ? "\\b " : "\\b0 ");
        public void Italic(bool on) => _body.Append(on ? "\\i " : "\\i0 ");
        public void Underline(bool on) => _body.Append(on ? "\\ul " : "\\ulnone ");
        public void Strikethrough(bool on) => _body.Append(on ? "\\strike " : "\\strike0 ");
        public void Subscript(bool on) => _body.Append(on ? "\\sub " : "\\nosupersub ");
        public void Superscript(bool on) => _body.Append(on ? "\\super " : "\\nosupersub ");

        /// <summary>Font size in half-points (RTF convention). E.g., 24 = 12pt.</summary>
        public void FontSize(int halfPoints) => _body.Append($"\\fs{halfPoints} ");

        /// <summary>Set font by index in font table.</summary>
        public void Font(int index) => _body.Append($"\\f{index} ");

        /// <summary>Set foreground color by color table index.</summary>
        public void ForeColor(int colorIndex) => _body.Append($"\\cf{colorIndex} ");

        /// <summary>Set background color by color table index (uses \chcbpat for arbitrary colors).</summary>
        public void BackgroundColor(int colorIndex)
        {
            _body.Append($"\\chcbpat{colorIndex} ");
            if (_emitHighlight)
                _body.Append($"\\highlight{colorIndex} ");
        }

        /// <summary>Reset all character formatting to defaults.</summary>
        public void ResetCharFormat() => _body.Append("\\plain\\f0\\fs24 ");

        public void OpenGroup() => _body.Append('{');
        public void CloseGroup() => _body.Append('}');

        /// <summary>Begin an RTF hyperlink field. Call EndHyperlink() after writing link content.</summary>
        public void BeginHyperlink(string url)
        {
            // Escape quotes for the field instruction; braces/backslashes/non-ASCII via the normal RTF text escaping.
            var escaped = url.Replace("\\", "\\\\").Replace("\"", "\\\"");
            _body.Append("{\\field{\\*\\fldinst{HYPERLINK \"");
            foreach (char c in escaped)
            {
                if (c == '{') _body.Append("\\{");
                else if (c == '}') _body.Append("\\}");
                else if (c < 0x20 || c == 0x7F) continue;
                else if (c > 127) AppendUnicode(_body, c);
                else _body.Append(c);
            }
            _body.Append("\"}}{\\fldrslt{");
        }

        /// <summary>End an RTF hyperlink field.</summary>
        public void EndHyperlink()
        {
            _body.Append("}}}");
        }

        /// <summary>
        /// Horizontal rule: starts an (empty) paragraph with a bottom border.
        /// The caller is responsible for terminating it with \par.
        /// </summary>
        public void HorizontalRule()
        {
            _body.Append("\\pard\\brdrb\\brdrs\\brdrw10\\brsp20 ");
        }

        /// <summary>Left indent in twips (1 inch = 1440 twips).</summary>
        public void LeftIndent(int twips) => _body.Append($"\\li{twips} ");

        /// <summary>First-line indent in twips.</summary>
        public void FirstLineIndent(int twips) => _body.Append($"\\fi{twips} ");

        /// <summary>Builds the complete RTF document string.</summary>
        public string Build()
        {
            var sb = new StringBuilder();

            // RTF header
            sb.Append("{\\rtf1\\ansi\\ansicpg1252\\deff0\\nouicompat\\uc1");

            // Font table
            sb.Append("{\\fonttbl");
            for (int i = 0; i < _fonts.Count; i++)
            {
                var font = _fonts[i];
                sb.Append($"{{\\f{i}{FamilyControlWord(font.Kind)}{(font.Kind == FontKind.Mono ? "\\fprq1" : "\\fprq2")}\\fcharset0 ");
                AppendEscaped(sb, font.Name);
                if (font.Alternate != null)
                {
                    sb.Append("{\\*\\falt ");
                    AppendEscaped(sb, font.Alternate);
                    sb.Append('}');
                }
                sb.Append(";}");
            }
            sb.Append('}');

            // Color table
            sb.Append("{\\colortbl ;");
            for (int i = 1; i < _colors.Count; i++)
            {
                var c = _colors[i];
                sb.Append($"\\red{c.R}\\green{c.G}\\blue{c.B};");
            }
            sb.Append('}');

            // Default formatting
            sb.Append("\\f0\\fs24 ");

            // Body
            sb.Append(_body);

            // Close RTF
            sb.Append('}');

            return sb.ToString();
        }

        private static string FamilyControlWord(FontKind kind)
        {
            switch (kind)
            {
                case FontKind.Mono: return "\\fmodern";
                case FontKind.Serif: return "\\froman";
                case FontKind.Script: return "\\fscript";
                case FontKind.Decorative: return "\\fdecor";
                default: return "\\fswiss";
            }
        }

        private readonly struct RtfColor
        {
            public readonly byte R, G, B;
            public RtfColor(byte r, byte g, byte b) { R = r; G = g; B = b; }
        }

        private sealed class RtfFont
        {
            public readonly string Name;
            public readonly FontKind Kind;
            public readonly string? Alternate;
            public RtfFont(string name, FontKind kind, string? alternate) { Name = name; Kind = kind; Alternate = alternate; }
        }
    }
}
