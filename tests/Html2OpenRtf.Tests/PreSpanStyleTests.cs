using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Html2OpenRtf;
using RtfPipe;

namespace Html2OpenRtf.Tests
{
    /// <summary>
    /// Fidelity tests for &lt;pre&gt; blocks with inline-styled &lt;span&gt; elements (syntax-highlighted code),
    /// inline CSS handling and conversion options.
    /// </summary>
    [TestClass]
    public class PreSpanStyleTests
    {
        static PreSpanStyleTests()
        {
            // RtfPipe needs the legacy code pages (Windows-1252) when its static state is first initialised.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        private static string Convert(string html, HtmlToRtfOptions? options = null) =>
            HtmlToRtfConverter.ConvertHtmlToRtf(html, options);

        /// <summary>Returns the font table index of the given font name, or -1.</summary>
        private static int FontIndex(string rtf, string fontName)
        {
            var m = Regex.Match(rtf, @"\{\\f(\d+)\\f\w+\\fprq\d\\fcharset\d+ " + Regex.Escape(fontName) + @"[;{]");
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        // ---- fonts ------------------------------------------------------------------------

        [TestMethod]
        public void Pre_FontFamilyList_UsesFirstFamilyWithFallbackAndMonospaceClass()
        {
            var rtf = Convert("<pre style=\"font-family: Consolas, 'Courier New', monospace\">x</pre>");
            StringAssert.Matches(rtf, new Regex(@"\{\\f\d+\\fmodern\\fprq1\\fcharset0 Consolas\{\\\*\\falt Courier New\};\}"));
            int f = FontIndex(rtf, "Consolas");
            Assert.IsTrue(f > 1, "Consolas should be registered as a new font");
            StringAssert.Contains(rtf, $"\\f{f} ");
        }

        [TestMethod]
        public void FontAvailabilityCheck_SkipsMissingFonts()
        {
            const string html = "<span style=\"font-family: 'Fancy Missing Font', Consolas, monospace\">x</span>";

            var withCheck = Convert(html, new HtmlToRtfOptions { IsFontAvailable = name => name == "Consolas" });
            Assert.IsTrue(FontIndex(withCheck, "Consolas") >= 0);
            Assert.IsFalse(withCheck.Contains("Fancy Missing Font"));

            var withoutCheck = Convert(html);
            StringAssert.Contains(withoutCheck, "Fancy Missing Font{\\*\\falt Consolas}");
            // The unknown font is classified by the generic family later in the list.
            StringAssert.Matches(withoutCheck, new Regex(@"\\fmodern\\fprq1\\fcharset0 Fancy Missing Font"));
        }

        [TestMethod]
        public void FontShorthand_SetsStyleWeightSizeAndFamily()
        {
            var rtf = Convert("<p><span style=\"font: italic bold 14pt Georgia, serif\">x</span></p>");
            StringAssert.Contains(rtf, "\\fs28 ");
            StringAssert.Contains(rtf, "\\b ");
            StringAssert.Contains(rtf, "\\i ");
            StringAssert.Matches(rtf, new Regex(@"\\froman\\fprq2\\fcharset0 Georgia"));
        }

        [TestMethod]
        public void FontTag_ColorFaceAndSize()
        {
            var rtf = Convert("<font color=\"red\" face=\"Verdana\" size=\"5\">x</font>");
            StringAssert.Contains(rtf, "\\red255\\green0\\blue0");
            Assert.IsTrue(FontIndex(rtf, "Verdana") >= 0);
            StringAssert.Contains(rtf, "\\fs36 ");
        }

        [TestMethod]
        public void FontSize_RelativeUnitsResolveAgainstParent()
        {
            var rtf = Convert("<div style=\"font-size:20px\"><span style=\"font-size:1.5em\">x</span></div>");
            StringAssert.Contains(rtf, "\\fs45 ");
        }

        [TestMethod]
        public void PreSpan_ExplicitFontSizeIsKept()
        {
            var rtf = Convert("<pre style=\"font-size:10pt\"><span style=\"font-size:16px\">x</span></pre>");
            // The last \fsN in effect before "x" must be 24 (16px = 12pt), not the pre's 20 (10pt).
            var body = rtf.Substring(rtf.IndexOf("\\pard", System.StringComparison.Ordinal));
            var textIndex = body.IndexOf("x", System.StringComparison.Ordinal);
            var sizes = Regex.Matches(rtf.Substring(0, rtf.Length - body.Length + textIndex), @"\\fs(\d+)");
            Assert.IsTrue(sizes.Count > 0);
            Assert.AreEqual("24", sizes[sizes.Count - 1].Groups[1].Value, rtf);
        }

        // ---- colors -----------------------------------------------------------------------

        [TestMethod]
        public void NestedSpanColors_RestoreParentColor()
        {
            var rtf = Convert("<pre style=\"color:#ff0000\">a<span style=\"color:#0000ff\">b</span>c</pre>");
            StringAssert.Contains(rtf, "\\cf1 a\\cf2 b\\cf1 c");
        }

        [TestMethod]
        public void BackgroundShorthand_EmitsCharacterShadingOnly()
        {
            var rtf = Convert("<span style=\"background: #ff0000 url(x.png) no-repeat\">hi</span>");
            StringAssert.Contains(rtf, "\\red255\\green0\\blue0");
            StringAssert.Contains(rtf, "\\chcbpat1 hi");
            Assert.IsFalse(rtf.Contains("\\highlight"));
        }

        [TestMethod]
        public void EmitHighlightOption_AddsHighlight()
        {
            var rtf = Convert("<span style=\"background-color:#00ff00\">hi</span>", new HtmlToRtfOptions { EmitHighlight = true });
            StringAssert.Contains(rtf, "\\chcbpat1");
            StringAssert.Contains(rtf, "\\highlight1");
        }

        [TestMethod]
        public void PreBackground_ShadesParagraphAndText()
        {
            var rtf = Convert("<pre style=\"background:#1e1e1e; color:#d4d4d4\">code</pre>");
            StringAssert.Contains(rtf, "\\cbpat1 ");
            StringAssert.Contains(rtf, "\\chcbpat1 ");
            StringAssert.Contains(rtf, "\\red212\\green212\\blue212");
        }

        [TestMethod]
        public void CssIsCaseInsensitiveAndIgnoresImportant()
        {
            var rtf = Convert("<span STYLE=\"COLOR: Red !important\">x</span>");
            StringAssert.Contains(rtf, "\\red255\\green0\\blue0");
            StringAssert.Contains(rtf, "\\cf1 x");
        }

        [TestMethod]
        public void HslAndTransparentColors()
        {
            var rtf = Convert("<span style=\"color: hsl(0, 100%, 50%); background-color: rgba(0,0,0,0)\">x</span>");
            StringAssert.Contains(rtf, "\\red255\\green0\\blue0");
            Assert.IsFalse(rtf.Contains("\\chcbpat"), "fully transparent background must be ignored");
        }

        // ---- line breaks and whitespace ---------------------------------------------------

        [TestMethod]
        public void Pre_NewlineAfterStartTagAndTrailingNewlineAreIgnored()
        {
            var rtf = Convert("<pre>\nline1\nline2\n</pre>");
            StringAssert.Contains(rtf, "line1\\line line2\\par");
            Assert.IsFalse(rtf.Contains("\\line line1"));
        }

        [TestMethod]
        public void Pre_EmptyLinesArePreserved()
        {
            var rtf = Convert("<pre>a\n\nb</pre>");
            StringAssert.Contains(rtf, "a\\line \\line b");
        }

        [TestMethod]
        public void Pre_TabsExpandToTabStopsAndSpacesAreKept()
        {
            var rtf = Convert("<pre>a\tb\n  c   d</pre>");
            StringAssert.Contains(rtf, "a       b");
            StringAssert.Contains(rtf, "\\line   c   d");
        }

        [TestMethod]
        public void LineBreak_KeepsFormattingOfTheLineItEnds()
        {
            var rtf = Convert("<pre><span style=\"font-size:20pt\">big</span>\n<span style=\"color:red\">x</span></pre>");
            // The break is written before the next span's formatting changes.
            StringAssert.Contains(rtf, "\\line \\cf1 x");
        }

        [TestMethod]
        public void WhiteSpaceNormal_CollapsesSpaces()
        {
            var rtf = Convert("<p>  a   \n  b  </p>");
            StringAssert.Contains(rtf, "a b");
        }

        [TestMethod]
        public void WhiteSpacePreCss_OnDiv()
        {
            var rtf = Convert("<div style=\"white-space: pre\">a  b\nc</div>");
            StringAssert.Contains(rtf, "a  b\\line c");
        }

        // ---- paragraph formatting ---------------------------------------------------------

        [TestMethod]
        public void LineHeight_FactorAndLength()
        {
            var factor = Convert("<pre style=\"font-size:10pt; line-height:1.5\">x</pre>");
            StringAssert.Contains(factor, "\\sl300\\slmult0 ");

            var length = Convert("<pre style=\"font-size:10pt; line-height:20px\">x</pre>");
            StringAssert.Contains(length, "\\sl300\\slmult0 ");

            // Tighter than the natural line height: exact spacing.
            var tight = Convert("<p style=\"font-size:10pt; line-height:1\">x</p>");
            StringAssert.Contains(tight, "\\sl-200\\slmult0 ");
        }

        [TestMethod]
        public void BlockMargins_CollapseIntoSpaceBefore()
        {
            var rtf = Convert("<p>A</p><p>B</p>");
            StringAssert.Contains(rtf, "A\\par");
            StringAssert.Contains(rtf, "\\sb240 B");
            Assert.IsFalse(rtf.Contains("\\sb240 A"), "no space at the top of the document");
        }

        [TestMethod]
        public void BlockMargins_ZeroMarginCss()
        {
            var rtf = Convert("<pre style=\"margin:0\">a</pre><pre style=\"margin: 0 0 0 0\">b</pre>");
            Assert.IsFalse(rtf.Contains("\\sb"));
        }

        [TestMethod]
        public void DisplayNone_HidesContent()
        {
            var rtf = Convert("<span style=\"display:none\">secret</span>visible");
            Assert.IsFalse(rtf.Contains("secret"));
            StringAssert.Contains(rtf, "visible");
        }

        [TestMethod]
        public void SpanWithDisplayBlock_StartsNewParagraph()
        {
            var rtf = Convert("a<span style=\"display:block\">b</span>c");
            StringAssert.Contains(rtf, "a\\par");
            StringAssert.Contains(rtf, "b\\par");
        }

        // ---- round trip -------------------------------------------------------------------

        [TestMethod]
        public void RoundTrip_SyntaxHighlightedPre()
        {
            const string html =
                "<pre style=\"font-family:Consolas,monospace;font-size:10pt;line-height:1.4;background:#1e1e1e;color:#d4d4d4\">\n" +
                "<span style=\"color:#569cd6\">public</span> <span style=\"color:#4ec9b0\">int</span> Add(<span style=\"color:#4ec9b0\">int</span> a)\n" +
                "{\n" +
                "    <span style=\"color:#6a9955;font-style:italic\">// comment &amp; more</span>\n" +
                "    <span style=\"color:#c586c0\">return</span> a;\n" +
                "}\n</pre>";
            var rtf = Convert(html);

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(rtf));
            var back = Rtf.ToHtml(stream);
            Assert.IsNotNull(back);
            var text = Regex.Replace(Regex.Replace(back, "<[^>]+>", " "), @"\s+", " ").Replace("&amp;", "&");
            StringAssert.Contains(text, "public");
            StringAssert.Contains(text, "Add(");
            StringAssert.Contains(text, "// comment & more");
            StringAssert.Contains(text, "return");
        }
    }
}
