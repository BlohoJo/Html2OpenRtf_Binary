using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HtmlAgilityPack;

namespace Html2OpenRtf
{
    /// <summary>
    /// Walks the HtmlAgilityPack DOM and emits RTF through <see cref="RtfDocumentBuilder"/>.
    /// <para>
    /// Formatting is modelled like CSS: every element computes an inherited format (tag defaults, presentational
    /// attributes, then the inline <c>style</c> attribute) that is pushed on a stack. Character formatting is written
    /// as minimal diffs against what is currently in effect in the RTF stream, and paragraphs are opened lazily so
    /// that nested block elements never produce stray empty paragraphs.
    /// </para>
    /// </summary>
    internal sealed class HtmlNodeVisitor
    {
        private enum WhiteSpaceMode
        {
            Normal,
            Pre,
            PreLine,
        }

        /// <summary>Inherited formatting state of an element.</summary>
        private sealed class Fmt
        {
            // Character formatting
            public int Font = RtfDocumentBuilder.DefaultFontIndex;
            public int Size = CssStyleParser.RootFontSize; // half-points
            public int Fore;                              // color index, 0 = auto
            public int Back;                              // color index, 0 = none
            public bool Bold, Italic, Underline, Strike;
            public int VAlign;                            // 0 = baseline, 1 = sub, 2 = super

            // Text handling
            public WhiteSpaceMode WhiteSpace;
            public int TabSize = 8;
            public bool MonoDefault;        // font is the browser default monospace (13px quirk applies)
            public bool SizeIsDefault = true; // size derives from browser defaults, not from an author value
            public double LineFactor;       // inherited unitless line-height
            public int LineTwips;           // inherited absolute line-height

            // Paragraph formatting
            public int LeftIndent, FirstLineIndent;
            public int Align;               // 0 = left, 1 = center, 2 = right, 3 = justify
            public int ParaLine;            // exact line height in twips, 0 = auto
            public int ParaBack;            // paragraph shading color index, 0 = none
            public ListItemState? ListItem;

            public Fmt Clone() => (Fmt)MemberwiseClone();
        }

        private sealed class ListItemState
        {
            public readonly string Marker;
            public readonly Fmt Format;
            public bool MarkerEmitted;

            public ListItemState(string marker, Fmt format)
            {
                Marker = marker;
                Format = format;
            }
        }

        private sealed class ListContext
        {
            public bool Ordered;
            public string StyleType = "disc";
            public int Next = 1;
            public int Step = 1;
        }

        private sealed class ElementInfo
        {
            public bool IsBlock;
            public bool Hidden;
            public string? ListStyle;
            public bool Border;
            public int MarginTop, MarginBottom; // vertical block margins in twips
        }

        private static readonly HashSet<string> IgnoredTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "script", "style", "head", "meta", "title", "link", "base", "noscript", "template", "svg", "canvas",
            "video", "audio", "iframe", "object", "embed", "select", "datalist", "map", "param", "source", "track",
        };

        private static readonly HashSet<string> BlockTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "html", "body", "address", "article", "aside", "blockquote", "caption", "center", "dd", "details", "dialog",
            "dir", "div", "dl", "dt", "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5",
            "h6", "header", "hgroup", "hr", "legend", "li", "listing", "main", "menu", "nav", "ol", "p", "plaintext",
            "pre", "search", "section", "summary", "table", "tbody", "td", "tfoot", "th", "thead", "tr", "ul", "xmp",
        };

        private static readonly HashSet<string> ListStyleTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "none", "disc", "circle", "square", "decimal", "decimal-leading-zero", "lower-alpha", "upper-alpha",
            "lower-latin", "upper-latin", "lower-roman", "upper-roman",
        };

        /// <summary>Half-point sizes for &lt;font size="1..7"&gt; (browser mapping 10/13/16/18/24/32/48px).</summary>
        private static readonly int[] FontTagSizes = { 15, 20, 24, 27, 36, 48, 72 };

        private const string ParCommand = "\\par ";

        private readonly RtfDocumentBuilder _rtf;
        private readonly HtmlToRtfOptions _options;
        private readonly Stack<Fmt> _stack = new Stack<Fmt>();
        private readonly Stack<ListContext> _lists = new Stack<ListContext>();

        private Fmt _emitted;              // character formatting currently in effect in the RTF stream
        private bool _paraOpen;            // a \pard has been written for the current paragraph
        private bool _hasContent;          // the current paragraph contains visible content
        private string _openParaSig = "";  // the \pard... string of the open paragraph
        private int _pendingBreaks;        // line breaks not yet written (a trailing break at block end is dropped)
        private bool _lastCharWasSpace = true;
        private int _column;               // column in the current line (tab expansion in preformatted text)
        private int _lastParPos = -1;      // position of the last \par if nothing was written after it
        private int _tableDepth;
        private int _gap;                  // collapsed vertical margin to put before the next paragraph (twips)
        private int _openParaGap;          // space-before already written for the open paragraph
        private bool _anyParagraphEnded;   // no space-before at the very top of the document
        private Fmt? _breakFmt;            // formatting of the text the pending line break belongs to

        public HtmlNodeVisitor(RtfDocumentBuilder rtf, HtmlToRtfOptions options)
        {
            _rtf = rtf ?? throw new ArgumentNullException(nameof(rtf));
            _options = options ?? new HtmlToRtfOptions();
            var root = new Fmt();
            _stack.Push(root);
            _emitted = root.Clone();
        }

        private Fmt Current => _stack.Peek();

        public void Visit(HtmlNode node)
        {
            switch (node.NodeType)
            {
                case HtmlNodeType.Document:
                    VisitChildren(node);
                    break;
                case HtmlNodeType.Element:
                    VisitElement(node);
                    break;
                case HtmlNodeType.Text:
                    VisitText(node);
                    break;
                // Comments (including <!DOCTYPE>) are ignored.
            }
        }

        /// <summary>Flushes pending state at the end of the document.</summary>
        public void Finish()
        {
            // A trailing line break at the very end does not create an extra line in browsers.
            for (int i = 1; i < _pendingBreaks; i++) _rtf.LineBreak();
            _pendingBreaks = 0;
        }

        private void VisitChildren(HtmlNode node)
        {
            foreach (var child in node.ChildNodes)
                Visit(child);
        }

        // ------------------------------------------------------------------ elements

        private void VisitElement(HtmlNode node)
        {
            var name = node.Name.ToLowerInvariant();
            if (IgnoredTags.Contains(name) || node.Attributes["hidden"] != null) return;

            switch (name)
            {
                case "br":
                    PendingBreak();
                    return;
                case "wbr":
                    return;
                case "hr":
                    if (!IsDisplayNone(node)) HorizontalRuleElement();
                    return;
                case "img":
                    if (!IsDisplayNone(node)) ImageElement(node);
                    return;
            }

            var fmt = ComputeFormat(node, name, Current, out var info);
            if (info.Hidden) return;

            if (name == "table")
            {
                VisitTable(node, fmt, info);
                return;
            }

            bool isList = name == "ul" || name == "ol" || name == "menu" || name == "dir";
            if (isList) _lists.Push(CreateListContext(node, name == "ol", info.ListStyle));
            if (name == "li" && info.IsBlock) fmt.ListItem = new ListItemState(ComputeMarker(node, info.ListStyle), fmt);

            string? href = name == "a" ? Attr(node, "href") : null;
            bool hyperlink = !string.IsNullOrWhiteSpace(href) && !ContainsBlock(node);

            _stack.Push(fmt);
            if (info.IsBlock)
            {
                BlockBoundary();
                // Adjacent vertical margins collapse (the larger one wins), as in CSS.
                _gap = Math.Max(_gap, info.MarginTop);
                OpenParagraph();
            }

            // Character formatting is written lazily right before the first content of the element,
            // so line breaks keep the formatting of the text they belong to and empty elements emit nothing.
            Fmt? linkSnapshot = null;
            if (hyperlink)
            {
                EmitContentPrefix(sync: false);
                linkSnapshot = _emitted;
                _rtf.BeginHyperlink(HtmlEntity.DeEntitize(href!).Trim());
            }

            if (name == "q") EmitInlineText("\u201C");
            VisitChildren(node);
            if (name == "q") EmitInlineText("\u201D");

            if (linkSnapshot != null)
            {
                _rtf.EndHyperlink();
                _emitted = linkSnapshot; // closing the field group restores the formatting in effect before it
            }
            if (info.IsBlock)
            {
                BlockBoundary();
                _gap = Math.Max(_gap, info.MarginBottom);
            }

            _stack.Pop();
            if (isList) _lists.Pop();
            SyncChar();
        }

        private void HorizontalRuleElement()
        {
            BlockBoundary();
            _rtf.HorizontalRule();
            if (_tableDepth > 0) _rtf.AppendRaw("\\intbl ");
            _rtf.AppendRaw(ParCommand);
            _paraOpen = false;
            _hasContent = false;
            _lastParPos = -1;
            _gap = 0;
            _anyParagraphEnded = true;
        }

        private void ImageElement(HtmlNode node)
        {
            var altAttr = node.Attributes["alt"];
            var alt = altAttr == null ? "image" : HtmlEntity.DeEntitize(altAttr.Value ?? "").Trim();
            if (alt.Length == 0) return; // alt="" marks a decorative image
            EmitInlineText($"[image: {alt}]");
        }

        // ------------------------------------------------------------------ format computation

        private Fmt ComputeFormat(HtmlNode node, string name, Fmt parent, out ElementInfo info)
        {
            info = new ElementInfo { IsBlock = BlockTags.Contains(name) };
            var f = parent.Clone();
            bool explicitSize = false;

            // 1. Browser default styles
            switch (name)
            {
                case "b":
                case "strong":
                    f.Bold = true;
                    break;
                case "i":
                case "em":
                case "cite":
                case "var":
                case "dfn":
                case "address":
                    f.Italic = true;
                    break;
                case "u":
                case "ins":
                    f.Underline = true;
                    break;
                case "s":
                case "strike":
                case "del":
                    f.Strike = true;
                    break;
                case "sub":
                    f.VAlign = 1;
                    break;
                case "sup":
                    f.VAlign = 2;
                    break;
                case "small":
                    f.Size = CssStyleParser.ParseFontSize("smaller", parent.Size);
                    break;
                case "big":
                    f.Size = CssStyleParser.ParseFontSize("larger", parent.Size);
                    break;
                case "mark":
                    f.Back = _rtf.RegisterColor(255, 255, 0);
                    break;
                case "code":
                case "kbd":
                case "samp":
                case "tt":
                    f.Font = RtfDocumentBuilder.MonospaceFontIndex;
                    f.MonoDefault = true;
                    break;
                case "pre":
                case "listing":
                case "xmp":
                case "plaintext":
                    f.Font = RtfDocumentBuilder.MonospaceFontIndex;
                    f.MonoDefault = true;
                    f.WhiteSpace = WhiteSpaceMode.Pre;
                    break;
                case "h1": Heading(f, parent, 2.0); break;
                case "h2": Heading(f, parent, 1.5); break;
                case "h3": Heading(f, parent, 1.17); break;
                case "h4": Heading(f, parent, 1.0); break;
                case "h5": Heading(f, parent, 0.83); break;
                case "h6": Heading(f, parent, 0.67); break;
                case "blockquote":
                case "dd":
                    f.LeftIndent += 720;
                    break;
                case "ul":
                case "ol":
                case "menu":
                case "dir":
                    f.LeftIndent += 360;
                    f.FirstLineIndent = 0;
                    f.ListItem = null;
                    break;
                case "li":
                    f.FirstLineIndent = -180;
                    break;
                case "center":
                case "caption":
                    f.Align = 1;
                    break;
                case "th":
                    f.Bold = true;
                    f.Align = 1;
                    break;
                case "table":
                    f.ListItem = null;
                    break;
                case "a":
                    if (!string.IsNullOrWhiteSpace(Attr(node, "href")))
                    {
                        f.Fore = _rtf.RegisterColor(0, 0, 255);
                        f.Underline = true;
                    }
                    break;
                case "font":
                    ApplyFontTag(node, f, ref explicitSize);
                    break;
            }

            // 2. Presentational attributes (lower priority than inline CSS)
            int ownBack = -1;
            int ownAlign = -1;
            int? ownIndent = null;
            string? lineHeight = null;
            string? marginTop = null, marginBottom = null;
            byte cr, cg, cb;

            if (name != "table" && name != "img")
                ownAlign = ParseAlign(Attr(node, "align"));
            var bgAttr = Attr(node, "bgcolor");
            if (bgAttr != null && CssColorParser.TryParse(bgAttr, out cr, out cg, out cb))
                ownBack = _rtf.RegisterColor(cr, cg, cb);
            if (name == "table")
            {
                var borderAttr = Attr(node, "border");
                if (borderAttr != null && (borderAttr.Trim().Length == 0 || ParseInt(borderAttr, 0) > 0))
                    info.Border = true;
            }

            // 3. Inline CSS
            var style = Attr(node, "style");
            if (!string.IsNullOrWhiteSpace(style))
            {
                foreach (var decl in CssStyleParser.ParseDeclarations(style))
                {
                    var value = decl.Value;
                    var lower = value.Trim().ToLowerInvariant();
                    switch (decl.Key)
                    {
                        case "color":
                            if (CssColorParser.TryParse(value, out cr, out cg, out cb))
                                f.Fore = _rtf.RegisterColor(cr, cg, cb);
                            break;
                        case "background-color":
                            if (CssColorParser.TryParse(value, out cr, out cg, out cb))
                                ownBack = _rtf.RegisterColor(cr, cg, cb);
                            else if (lower == "transparent")
                                ownBack = -1;
                            break;
                        case "background":
                            if (CssStyleParser.TryGetBackgroundColor(value, out cr, out cg, out cb))
                                ownBack = _rtf.RegisterColor(cr, cg, cb);
                            else if (lower == "none" || lower == "transparent")
                                ownBack = -1;
                            break;
                        case "font-family":
                            ApplyFontFamily(f, value);
                            break;
                        case "font-size":
                            {
                                int size = CssStyleParser.ParseFontSize(value, parent.Size);
                                if (size > 0)
                                {
                                    f.Size = size;
                                    explicitSize = true;
                                }
                            }
                            break;
                        case "font":
                            if (CssStyleParser.TryParseFontShorthand(value, out var shorthand))
                            {
                                f.Bold = shorthand.Bold;
                                f.Italic = shorthand.Italic;
                                int size = CssStyleParser.ParseFontSize(shorthand.Size, parent.Size);
                                if (size > 0)
                                {
                                    f.Size = size;
                                    explicitSize = true;
                                }
                                lineHeight = shorthand.LineHeight ?? "normal";
                                ApplyFontFamily(f, shorthand.Family);
                            }
                            break;
                        case "font-weight":
                            if (lower == "bold" || lower == "bolder") f.Bold = true;
                            else if (lower == "normal" || lower == "lighter") f.Bold = false;
                            else if (int.TryParse(lower, NumberStyles.Integer, CultureInfo.InvariantCulture, out int weight))
                                f.Bold = weight >= 600;
                            break;
                        case "font-style":
                            if (lower.StartsWith("italic", StringComparison.Ordinal) || lower.StartsWith("oblique", StringComparison.Ordinal))
                                f.Italic = true;
                            else if (lower == "normal")
                                f.Italic = false;
                            break;
                        case "text-decoration":
                        case "text-decoration-line":
                            ApplyTextDecoration(f, parent, lower);
                            break;
                        case "vertical-align":
                            if (lower == "sub") f.VAlign = 1;
                            else if (lower == "super") f.VAlign = 2;
                            else if (lower == "baseline") f.VAlign = 0;
                            break;
                        case "line-height":
                            lineHeight = value;
                            break;
                        case "margin":
                            {
                                var parts = CssStyleParser.SplitTokens(lower);
                                if (parts.Count > 0)
                                {
                                    marginTop = parts[0];
                                    marginBottom = parts.Count >= 3 ? parts[2] : parts[0];
                                }
                            }
                            break;
                        case "margin-top":
                        case "margin-block-start":
                            marginTop = lower;
                            break;
                        case "margin-bottom":
                        case "margin-block-end":
                            marginBottom = lower;
                            break;
                        case "white-space":
                            if (lower == "pre" || lower == "pre-wrap" || lower == "break-spaces") f.WhiteSpace = WhiteSpaceMode.Pre;
                            else if (lower == "pre-line") f.WhiteSpace = WhiteSpaceMode.PreLine;
                            else if (lower == "normal" || lower == "nowrap") f.WhiteSpace = WhiteSpaceMode.Normal;
                            break;
                        case "tab-size":
                            if (int.TryParse(lower, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tabSize) && tabSize >= 0)
                                f.TabSize = tabSize;
                            break;
                        case "text-align":
                            {
                                int align = ParseAlign(lower);
                                if (align >= 0) ownAlign = align;
                            }
                            break;
                        case "text-indent":
                            if (CssStyleParser.TryParseLength(lower, f.Size, out int indent))
                                ownIndent = indent;
                            break;
                        case "display":
                            ApplyDisplay(info, lower);
                            break;
                        case "list-style-type":
                            if (ListStyleTypes.Contains(lower)) info.ListStyle = lower;
                            break;
                        case "list-style":
                            foreach (var token in CssStyleParser.SplitTokens(lower))
                            {
                                if (ListStyleTypes.Contains(token))
                                {
                                    info.ListStyle = token;
                                    break;
                                }
                            }
                            break;
                        case "border":
                        case "border-style":
                        case "border-width":
                            if ((name == "table" || name == "td" || name == "th")
                                && lower != "none" && lower != "0" && lower != "hidden" && lower != "0px")
                                info.Border = true;
                            break;
                    }
                }
            }

            // 4. Resolve values that depend on the final font size / display type
            if (lineHeight != null
                && CssStyleParser.TryParseLineHeight(lineHeight, f.Size, out double factor, out int lineTwips))
            {
                f.LineFactor = factor;
                f.LineTwips = lineTwips;
            }

            if (ownBack >= 0)
            {
                f.Back = ownBack;
                if (info.IsBlock) f.ParaBack = ownBack;
            }

            if (info.IsBlock)
            {
                if (ownAlign >= 0) f.Align = ownAlign;
                if (ownIndent.HasValue) f.FirstLineIndent = ownIndent.Value;
                f.ParaLine = f.LineTwips > 0
                    ? f.LineTwips
                    : f.LineFactor > 0 ? ClampTwips(Round(f.LineFactor * f.Size * 10)) : 0;
            }

            // Browsers render the generic default monospace font at 13px instead of 16px
            // (unless the author specified a size), and scale back when leaving it.
            if (explicitSize)
                f.SizeIsDefault = false;
            else if (f.SizeIsDefault && f.MonoDefault != parent.MonoDefault)
                f.Size = ClampSize(Round(f.MonoDefault ? f.Size * 13.0 / 16.0 : f.Size * 16.0 / 13.0));

            // 5. Vertical margins of blocks (browser defaults, overridden by inline CSS). em is relative to
            //    the element's own font size; 1 em in twips = half-points * 10.
            if (info.IsBlock)
            {
                double em = DefaultMarginEm(name);
                int defaultMargin = Round(em * f.Size * 10);
                info.MarginTop = ResolveMargin(marginTop, f.Size, defaultMargin);
                info.MarginBottom = ResolveMargin(marginBottom, f.Size, defaultMargin);
            }

            return f;
        }

        private double DefaultMarginEm(string name)
        {
            switch (name)
            {
                case "p":
                case "pre":
                case "listing":
                case "xmp":
                case "plaintext":
                case "blockquote":
                case "dl":
                case "figure":
                    return 1.0;
                case "h1": return 0.67;
                case "h2": return 0.83;
                case "h3": return 1.0;
                case "h4": return 1.33;
                case "h5": return 1.67;
                case "h6": return 2.33;
                case "ul":
                case "ol":
                case "menu":
                case "dir":
                    return _lists.Count == 0 ? 1.0 : 0.0; // nested lists have no vertical margins
                default:
                    return 0.0;
            }
        }

        private static int ResolveMargin(string? value, int fontHalfPts, int fallback)
        {
            if (value == null) return fallback;
            if (value == "auto") return 0;
            if (!CssStyleParser.TryParseLength(value, fontHalfPts, out int twips)) return fallback;
            return Math.Max(0, Math.Min(31680, twips)); // negative margins are not representable
        }

        private static void Heading(Fmt f, Fmt parent, double scale)
        {
            f.Bold = true;
            f.Size = ClampSize(Round(parent.Size * scale));
        }

        private void ApplyFontFamily(Fmt f, string value)
        {
            var families = CssStyleParser.ParseFontFamilyList(value);
            if (families.Count == 0) return;
            if (families.Count == 1)
            {
                var keyword = families[0].ToLowerInvariant();
                if (keyword == "inherit" || keyword == "initial" || keyword == "unset" || keyword == "revert") return;
            }

            FontMapper.Resolve(families, _options.IsFontAvailable, out var primary, out var alternate, out var kind);
            f.Font = _rtf.RegisterFont(primary, kind, alternate);
            f.MonoDefault = families.Count == 1 && families[0].Equals("monospace", StringComparison.OrdinalIgnoreCase);
        }

        private void ApplyFontTag(HtmlNode node, Fmt f, ref bool explicitSize)
        {
            var color = Attr(node, "color");
            if (color != null && CssColorParser.TryParse(color, out var r, out var g, out var b))
                f.Fore = _rtf.RegisterColor(r, g, b);

            var face = Attr(node, "face");
            if (!string.IsNullOrWhiteSpace(face))
                ApplyFontFamily(f, face!);

            var sizeAttr = Attr(node, "size")?.Trim();
            if (!string.IsNullOrEmpty(sizeAttr))
            {
                bool relative = sizeAttr![0] == '+' || sizeAttr[0] == '-';
                if (int.TryParse(sizeAttr, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n))
                {
                    if (relative) n += 3;
                    n = Math.Max(1, Math.Min(7, n));
                    f.Size = FontTagSizes[n - 1];
                    explicitSize = true;
                }
            }
        }

        private static void ApplyTextDecoration(Fmt f, Fmt parent, string value)
        {
            // Decorations propagate to descendants and cannot be removed by them, so "none" only removes
            // decorations introduced by this element itself.
            bool underline = parent.Underline, strike = parent.Strike, recognized = false;
            foreach (var token in CssStyleParser.SplitTokens(value))
            {
                switch (token)
                {
                    case "underline": underline = true; recognized = true; break;
                    case "line-through": strike = true; recognized = true; break;
                    case "none":
                    case "overline":
                    case "blink":
                        recognized = true;
                        break;
                }
            }
            if (!recognized) return;
            f.Underline = underline;
            f.Strike = strike;
        }

        private static void ApplyDisplay(ElementInfo info, string value)
        {
            var tokens = CssStyleParser.SplitTokens(value);
            if (tokens.Count == 0) return;
            switch (tokens[0])
            {
                case "none":
                    info.Hidden = true;
                    break;
                case "block":
                case "list-item":
                case "table":
                case "flex":
                case "grid":
                case "flow-root":
                    info.IsBlock = true;
                    break;
                case "inline":
                case "inline-block":
                case "inline-flex":
                case "inline-grid":
                case "inline-table":
                case "contents":
                    info.IsBlock = false;
                    break;
            }
        }

        private static bool IsDisplayNone(HtmlNode node)
        {
            var style = Attr(node, "style");
            if (string.IsNullOrWhiteSpace(style)) return false;
            var display = CssStyleParser.GetLast(CssStyleParser.ParseDeclarations(style), "display");
            return display != null && display.Trim().Equals("none", StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseAlign(string? value)
        {
            if (value == null) return -1;
            switch (value.Trim().ToLowerInvariant())
            {
                case "left":
                case "start":
                case "-webkit-left":
                    return 0;
                case "center":
                case "middle":
                case "-webkit-center":
                    return 1;
                case "right":
                case "end":
                case "-webkit-right":
                    return 2;
                case "justify":
                    return 3;
                default:
                    return -1;
            }
        }

        // ------------------------------------------------------------------ lists

        private ListContext CreateListContext(HtmlNode node, bool ordered, string? ownStyle)
        {
            var ctx = new ListContext { Ordered = ordered };
            ctx.StyleType = ownStyle
                ?? MapTypeAttribute(Attr(node, "type"))
                ?? (ordered ? "decimal" : _lists.Count == 0 ? "disc" : _lists.Count == 1 ? "circle" : "square");

            if (ordered)
            {
                var startAttr = Attr(node, "start");
                bool hasStart = int.TryParse(startAttr?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int start);
                if (node.Attributes["reversed"] != null)
                {
                    ctx.Step = -1;
                    ctx.Next = hasStart ? start : CountListItems(node);
                }
                else
                {
                    ctx.Next = hasStart ? start : 1;
                }
            }
            return ctx;
        }

        private string ComputeMarker(HtmlNode li, string? ownStyle)
        {
            var ctx = _lists.Count > 0 ? _lists.Peek() : null;
            var type = ownStyle ?? MapTypeAttribute(Attr(li, "type")) ?? ctx?.StyleType ?? "disc";
            int number = 1;
            if (ctx != null)
            {
                var valueAttr = Attr(li, "value");
                if (ctx.Ordered && int.TryParse(valueAttr?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value))
                    ctx.Next = value;
                number = ctx.Next;
                ctx.Next += ctx.Step;
            }
            return FormatMarker(type, number, ctx?.Ordered ?? false);
        }

        private static string? MapTypeAttribute(string? type)
        {
            switch (type?.Trim())
            {
                case "1": return "decimal";
                case "a": return "lower-alpha";
                case "A": return "upper-alpha";
                case "i": return "lower-roman";
                case "I": return "upper-roman";
            }
            var lower = type?.Trim().ToLowerInvariant();
            return lower == "disc" || lower == "circle" || lower == "square" || lower == "none" ? lower : null;
        }

        private static int CountListItems(HtmlNode list)
        {
            int count = 0;
            foreach (var child in list.ChildNodes)
                if (child.NodeType == HtmlNodeType.Element && child.Name.Equals("li", StringComparison.OrdinalIgnoreCase))
                    count++;
            return count;
        }

        private static string FormatMarker(string type, int n, bool ordered)
        {
            switch (type)
            {
                case "none": return "";
                case "disc": return "\u2022 ";
                case "circle": return "\u25E6 ";
                case "square": return "\u25AA ";
                case "decimal": return Inv(n) + ". ";
                case "decimal-leading-zero": return (n >= 0 && n < 10 ? "0" : "") + Inv(n) + ". ";
                case "lower-alpha":
                case "lower-latin":
                    return ToAlpha(n).ToLowerInvariant() + ". ";
                case "upper-alpha":
                case "upper-latin":
                    return ToAlpha(n) + ". ";
                case "lower-roman": return ToRoman(n).ToLowerInvariant() + ". ";
                case "upper-roman": return ToRoman(n) + ". ";
                default: return ordered ? Inv(n) + ". " : "\u2022 ";
            }
        }

        private static string ToAlpha(int n)
        {
            if (n <= 0) return Inv(n);
            var sb = new StringBuilder();
            while (n > 0)
            {
                n--;
                sb.Insert(0, (char)('A' + n % 26));
                n /= 26;
            }
            return sb.ToString();
        }

        private static string ToRoman(int n)
        {
            if (n <= 0 || n >= 4000) return Inv(n);
            int[] values = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
            string[] symbols = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
            var sb = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                while (n >= values[i])
                {
                    sb.Append(symbols[i]);
                    n -= values[i];
                }
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ tables

        private void VisitTable(HtmlNode table, Fmt fmt, ElementInfo info)
        {
            _stack.Push(fmt);
            BlockBoundary();
            if (_tableDepth > 0)
            {
                // RTF nested tables are poorly supported by many readers: flatten inner tables
                // (cells separated by tabs, rows by line breaks).
                FlattenTable(table);
            }
            else
            {
                foreach (var child in table.ChildNodes)
                    if (child.NodeType == HtmlNodeType.Element && child.Name.Equals("caption", StringComparison.OrdinalIgnoreCase))
                        Visit(child);
                EmitTableRows(table, fmt, info.Border);
            }
            BlockBoundary();
            _gap = 0; // tables have no default vertical margins
            _stack.Pop();
            SyncChar();
        }

        private void EmitTableRows(HtmlNode table, Fmt tableFmt, bool border)
        {
            var prepared = new List<KeyValuePair<Fmt, List<TableCell>>>();
            int maxCols = 0;
            foreach (var row in CollectRows(table))
            {
                var rowFmt = ComputeRowFormat(row, tableFmt);
                if (rowFmt == null) continue;
                var cells = new List<TableCell>();
                int cols = 0;
                foreach (var cell in row.Key.ChildNodes)
                {
                    if (cell.NodeType != HtmlNodeType.Element) continue;
                    var cellName = cell.Name.ToLowerInvariant();
                    if (cellName != "td" && cellName != "th") continue;
                    var cellFmt = ComputeFormat(cell, cellName, rowFmt, out var cellInfo);
                    if (cellInfo.Hidden) continue;
                    cellFmt.ListItem = null;
                    if (cellInfo.Border) border = true;
                    int span = Math.Max(1, Math.Min(64, ParseInt(Attr(cell, "colspan"), 1)));
                    cells.Add(new TableCell(cell, cellFmt, span));
                    cols += span;
                }
                if (cells.Count == 0) continue;
                maxCols = Math.Max(maxCols, cols);
                prepared.Add(new KeyValuePair<Fmt, List<TableCell>>(rowFmt, cells));
            }
            if (prepared.Count == 0) return;

            int left = tableFmt.LeftIndent;
            int unit = Math.Max(1, 8640 / maxCols);

            _tableDepth++;
            foreach (var row in prepared)
            {
                var def = new StringBuilder("\\trowd\\trgaph108");
                if (left != 0) def.Append("\\trleft").Append(Inv(left));
                def.Append(' ');
                int x = left;
                foreach (var cell in row.Value)
                {
                    x += unit * cell.Span;
                    if (border)
                        def.Append("\\clbrdrt\\brdrs\\brdrw10 \\clbrdrl\\brdrs\\brdrw10 \\clbrdrb\\brdrs\\brdrw10 \\clbrdrr\\brdrs\\brdrw10 ");
                    if (cell.Format.ParaBack > 0)
                        def.Append("\\clcbpat").Append(Inv(cell.Format.ParaBack)).Append(' ');
                    def.Append("\\cellx").Append(Inv(x)).Append(' ');
                }
                _rtf.AppendRaw(def.ToString());

                _stack.Push(row.Key);
                foreach (var cell in row.Value)
                {
                    _stack.Push(cell.Format);
                    var start = ParagraphStart(cell.Format);
                    _rtf.AppendRaw(start);
                    _rtf.ResetCharFormat();
                    _emitted = new Fmt();
                    _paraOpen = true;
                    _hasContent = false;
                    _openParaSig = start;
                    _openParaGap = 0;
                    _gap = 0;
                    _pendingBreaks = 0;
                    _breakFmt = null;
                    _lastCharWasSpace = true;
                    _column = 0;
                    _lastParPos = -1;

                    VisitChildren(cell.Node);

                    for (int i = 1; i < _pendingBreaks; i++) _rtf.LineBreak();
                    _pendingBreaks = 0;
                    // \cell terminates the last paragraph itself; drop a \par written by a closing block.
                    if (!_paraOpen && _lastParPos >= 0) _rtf.Remove(_lastParPos, ParCommand.Length);
                    _rtf.AppendRaw("\\cell ");
                    _paraOpen = false;
                    _hasContent = false;
                    _lastParPos = -1;
                    _stack.Pop();
                }
                _stack.Pop();
                _rtf.AppendRaw("\\row ");
            }
            _tableDepth--;
            _paraOpen = false;
            _hasContent = false;
        }

        private void FlattenTable(HtmlNode table)
        {
            bool firstRow = true;
            foreach (var row in CollectRows(table))
            {
                var rowFmt = ComputeRowFormat(row, Current);
                if (rowFmt == null) continue;
                bool firstCell = true;
                foreach (var cell in row.Key.ChildNodes)
                {
                    if (cell.NodeType != HtmlNodeType.Element) continue;
                    var cellName = cell.Name.ToLowerInvariant();
                    if (cellName != "td" && cellName != "th") continue;
                    var cellFmt = ComputeFormat(cell, cellName, rowFmt, out var cellInfo);
                    if (cellInfo.Hidden) continue;
                    cellFmt.ListItem = null;

                    if (firstCell && !firstRow)
                    {
                        PendingBreak();
                    }
                    else if (!firstCell)
                    {
                        EmitContentPrefix();
                        _rtf.AppendRaw("\\tab ");
                        _hasContent = true;
                        _lastCharWasSpace = true;
                    }
                    firstCell = false;

                    _stack.Push(cellFmt);
                    SyncChar();
                    VisitChildren(cell);
                    _stack.Pop();
                    SyncChar();
                }
                if (!firstCell) firstRow = false;
            }
        }

        /// <summary>Computes the format of a row (including its thead/tbody/tfoot section); null if hidden.</summary>
        private Fmt? ComputeRowFormat(KeyValuePair<HtmlNode, HtmlNode?> row, Fmt tableFmt)
        {
            var parent = tableFmt;
            if (row.Value != null)
            {
                parent = ComputeFormat(row.Value, row.Value.Name.ToLowerInvariant(), tableFmt, out var sectionInfo);
                if (sectionInfo.Hidden) return null;
            }
            var rowFmt = ComputeFormat(row.Key, "tr", parent, out var rowInfo);
            return rowInfo.Hidden ? null : rowFmt;
        }

        /// <summary>Rows of a table in document order, with the section element (thead/tbody/tfoot) they belong to.</summary>
        private static List<KeyValuePair<HtmlNode, HtmlNode?>> CollectRows(HtmlNode table)
        {
            var rows = new List<KeyValuePair<HtmlNode, HtmlNode?>>();
            foreach (var child in table.ChildNodes)
            {
                if (child.NodeType != HtmlNodeType.Element) continue;
                var name = child.Name.ToLowerInvariant();
                if (name == "tr")
                {
                    rows.Add(new KeyValuePair<HtmlNode, HtmlNode?>(child, null));
                }
                else if (name == "thead" || name == "tbody" || name == "tfoot")
                {
                    foreach (var tr in child.ChildNodes)
                        if (tr.NodeType == HtmlNodeType.Element && tr.Name.Equals("tr", StringComparison.OrdinalIgnoreCase))
                            rows.Add(new KeyValuePair<HtmlNode, HtmlNode?>(tr, child));
                }
            }
            return rows;
        }

        private sealed class TableCell
        {
            public readonly HtmlNode Node;
            public readonly Fmt Format;
            public readonly int Span;

            public TableCell(HtmlNode node, Fmt format, int span)
            {
                Node = node;
                Format = format;
                Span = span;
            }
        }

        // ------------------------------------------------------------------ text

        private void VisitText(HtmlNode node)
        {
            var raw = node is HtmlTextNode textNode ? textNode.Text : node.InnerText;
            if (string.IsNullOrEmpty(raw)) return;
            var text = HtmlEntity.DeEntitize(raw);
            if (string.IsNullOrEmpty(text)) return;

            var f = Current;
            switch (f.WhiteSpace)
            {
                case WhiteSpaceMode.Pre:
                    text = NormalizeNewlines(text);
                    // A newline directly after the <pre> start tag is ignored (HTML parsing rule).
                    if (text[0] == '\n' && node.PreviousSibling == null && node.ParentNode != null && IsPreElement(node.ParentNode.Name))
                        text = text.Substring(1);
                    EmitPreformatted(text, f.TabSize);
                    break;
                case WhiteSpaceMode.PreLine:
                    var lines = NormalizeNewlines(text).Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (i > 0) PendingBreak();
                        EmitCollapsed(lines[i]);
                    }
                    break;
                default:
                    EmitCollapsed(text);
                    break;
            }
        }

        private static bool IsPreElement(string name) =>
            name.Equals("pre", StringComparison.OrdinalIgnoreCase)
            || name.Equals("listing", StringComparison.OrdinalIgnoreCase)
            || name.Equals("textarea", StringComparison.OrdinalIgnoreCase);

        private static string NormalizeNewlines(string text) =>
            text.IndexOf('\r') < 0 ? text : text.Replace("\r\n", "\n").Replace('\r', '\n');

        /// <summary>white-space: normal — collapse whitespace runs and drop spaces at line starts.</summary>
        private void EmitCollapsed(string text)
        {
            var collapsed = CollapseWhitespace(text);
            if (collapsed.Length == 0) return;
            if (!_paraOpen || !_hasContent || _lastCharWasSpace || _pendingBreaks > 0)
                collapsed = collapsed.TrimStart(' ');
            if (collapsed.Length == 0) return;

            EmitContentPrefix();
            _rtf.AppendText(collapsed);
            _hasContent = true;
            _lastCharWasSpace = collapsed[collapsed.Length - 1] == ' ';
        }

        /// <summary>white-space: pre — keep spaces, turn newlines into line breaks, expand tabs to tab stops.</summary>
        private void EmitPreformatted(string text, int tabSize)
        {
            int start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && text[i] != '\n') continue;
                if (i > start) EmitPreSegment(text.Substring(start, i - start), tabSize);
                if (i < text.Length) PendingBreak();
                start = i + 1;
            }
        }

        private void EmitPreSegment(string segment, int tabSize)
        {
            EmitContentPrefix();
            var sb = new StringBuilder(segment.Length + 8);
            foreach (char c in segment)
            {
                if (c == '\t')
                {
                    if (tabSize <= 0) continue;
                    int spaces = tabSize - (_column % tabSize);
                    sb.Append(' ', spaces);
                    _column += spaces;
                }
                else
                {
                    sb.Append(c);
                    _column++;
                }
            }
            if (sb.Length == 0) return;
            _rtf.AppendText(sb.ToString());
            _hasContent = true;
            _lastCharWasSpace = sb[sb.Length - 1] == ' ';
        }

        private void EmitInlineText(string text)
        {
            EmitContentPrefix();
            _rtf.AppendText(text);
            _hasContent = true;
            _lastCharWasSpace = text.Length > 0 && text[text.Length - 1] == ' ';
        }

        private static string CollapseWhitespace(string text)
        {
            var sb = new StringBuilder(text.Length);
            bool inSpace = false;
            foreach (char c in text)
            {
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f')
                {
                    if (!inSpace) sb.Append(' ');
                    inSpace = true;
                }
                else
                {
                    sb.Append(c);
                    inSpace = false;
                }
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ paragraph / character state

        /// <summary>
        /// A forced line break (&lt;br&gt; or a newline in preformatted text). It is written lazily so that a trailing
        /// break at the end of a block does not produce an extra empty line (as in browsers).
        /// </summary>
        private void PendingBreak()
        {
            EmitContentPrefix();
            _pendingBreaks++;
            _breakFmt = Current;
            _lastCharWasSpace = true;
        }

        /// <summary>
        /// Makes sure a paragraph is open, writes a pending list marker and pending line breaks, then
        /// (unless <paramref name="sync"/> is false) brings the character formatting up to date.
        /// </summary>
        private void EmitContentPrefix(bool sync = true)
        {
            if (!_paraOpen) OpenParagraph();

            var item = Current.ListItem;
            if (item != null && !item.MarkerEmitted)
            {
                item.MarkerEmitted = true;
                if (item.Marker.Length > 0)
                {
                    SyncCharTo(item.Format);
                    _rtf.AppendText(item.Marker);
                    SyncChar();
                }
                _hasContent = true;
                _lastCharWasSpace = true;
            }

            if (_pendingBreaks > 0)
            {
                // The break is part of the line it ends: write it with that text's formatting (font size
                // and background of a line break affect the height/shading of the line it terminates).
                if (_breakFmt != null) SyncCharTo(_breakFmt);
                for (int i = 0; i < _pendingBreaks; i++) _rtf.LineBreak();
                _pendingBreaks = 0;
                _column = 0;
            }
            _breakFmt = null;
            if (sync) SyncChar();
            _lastParPos = -1;
        }

        /// <summary>Ends the current paragraph if it has content.</summary>
        private void BlockBoundary()
        {
            if (_pendingBreaks > 0)
            {
                // The last pending break is dropped; a paragraph consisting only of a break still counts as a line.
                for (int i = 1; i < _pendingBreaks; i++) _rtf.LineBreak();
                _pendingBreaks = 0;
                _hasContent = true;
            }
            _breakFmt = null;
            if (_paraOpen && _hasContent)
            {
                _lastParPos = _rtf.Length;
                _rtf.AppendRaw(ParCommand);
                _paraOpen = false;
                _hasContent = false;
                _anyParagraphEnded = true;
            }
        }

        /// <summary>
        /// Starts a paragraph with the paragraph properties of the current format.
        /// An open paragraph without content is re-specified instead of producing an empty paragraph.
        /// </summary>
        private void OpenParagraph()
        {
            var start = ParagraphStart(Current);
            if (_paraOpen)
            {
                if (!_hasContent && _pendingBreaks == 0 && start == _openParaSig
                    && (_gap <= _openParaGap || !_anyParagraphEnded))
                {
                    _gap = 0;
                    return;
                }
                if (_hasContent || _pendingBreaks > 0) BlockBoundary();
            }

            // Collapsed block margins become space-before of the paragraph. Re-specifying an empty open
            // paragraph keeps the spacing it already had. Nothing is added at the top of the document.
            int gap = _paraOpen ? Math.Max(_gap, _openParaGap) : _gap;
            if (!_anyParagraphEnded) gap = 0;
            _gap = 0;

            _rtf.AppendRaw(gap > 0 ? start + "\\sb" + Inv(gap) + " " : start);
            _openParaGap = gap;
            _paraOpen = true;
            _hasContent = false;
            _openParaSig = start;
            _column = 0;
            _lastCharWasSpace = true;
            _lastParPos = -1;
        }

        private string ParagraphStart(Fmt f)
        {
            var sb = new StringBuilder("\\pard");
            if (_tableDepth > 0) sb.Append("\\intbl");
            sb.Append(' ');

            int firstLine = f.FirstLineIndent;
            // Continuation paragraphs of a list item align with the item text instead of hanging.
            if (f.ListItem != null && f.ListItem.MarkerEmitted) firstLine = 0;

            if (f.LeftIndent != 0) sb.Append("\\li").Append(Inv(f.LeftIndent)).Append(' ');
            if (firstLine != 0) sb.Append("\\fi").Append(Inv(firstLine)).Append(' ');
            switch (f.Align)
            {
                case 1: sb.Append("\\qc "); break;
                case 2: sb.Append("\\qr "); break;
                case 3: sb.Append("\\qj "); break;
            }
            // CSS line-height defines the line box height. When it is at least the font's natural line height
            // (~1.2 em) use "at least" spacing, so larger inline text grows the line like in a browser; tighter
            // values need exact spacing (negative \sl) because "at least" could not go below the natural height.
            if (f.ParaLine > 0)
            {
                bool exact = f.ParaLine < f.Size * 12; // 1.2 em in twips = half-points * 10 * 1.2
                sb.Append(exact ? "\\sl-" : "\\sl").Append(Inv(f.ParaLine)).Append("\\slmult0 ");
            }
            if (f.ParaBack > 0) sb.Append("\\cbpat").Append(Inv(f.ParaBack)).Append(' ');
            return sb.ToString();
        }

        private void SyncChar() => SyncCharTo(Current);

        /// <summary>Writes only the character formatting differences between the stream state and <paramref name="target"/>.</summary>
        private void SyncCharTo(Fmt target)
        {
            var e = _emitted;
            if (target.Font != e.Font) _rtf.Font(target.Font);
            if (target.Size != e.Size) _rtf.FontSize(target.Size);
            if (target.Fore != e.Fore) _rtf.ForeColor(target.Fore);
            if (target.Back != e.Back) _rtf.BackgroundColor(target.Back);
            if (target.Bold != e.Bold) _rtf.Bold(target.Bold);
            if (target.Italic != e.Italic) _rtf.Italic(target.Italic);
            if (target.Underline != e.Underline) _rtf.Underline(target.Underline);
            if (target.Strike != e.Strike) _rtf.Strikethrough(target.Strike);
            if (target.VAlign != e.VAlign)
            {
                if (target.VAlign == 1) _rtf.Subscript(true);
                else if (target.VAlign == 2) _rtf.Superscript(true);
                else _rtf.Subscript(false);
            }
            _emitted = target.Clone();
        }

        // ------------------------------------------------------------------ helpers

        private static bool ContainsBlock(HtmlNode node)
        {
            foreach (var d in node.Descendants())
                if (d.NodeType == HtmlNodeType.Element && (BlockTags.Contains(d.Name) || d.Name.Equals("hr", StringComparison.OrdinalIgnoreCase)))
                    return true;
            return false;
        }

        private static string? Attr(HtmlNode node, string name) => node.Attributes[name]?.Value;

        private static int ParseInt(string? value, int fallback) =>
            int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : fallback;

        private static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

        private static int ClampSize(int halfPoints) => Math.Max(2, Math.Min(3276, halfPoints));

        private static int ClampTwips(int twips) => Math.Max(1, Math.Min(31680, twips));

        private static string Inv(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
