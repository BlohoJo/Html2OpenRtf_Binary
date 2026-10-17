using System;

namespace Html2OpenRtf
{
    /// <summary>
    /// Optional settings for <see cref="HtmlToRtfConverter"/>.
    /// </summary>
    public sealed class HtmlToRtfOptions
    {
        /// <summary>
        /// When true, character background colors are written as <c>\highlightN</c> in addition to <c>\chcbpatN</c>.
        /// Useful for RichEdit-based readers (WordPad, WinForms RichTextBox) that ignore <c>\chcbpat</c>.
        /// Note that Microsoft Word only supports 16 highlight colors and may show an approximate color.
        /// Default: false.
        /// </summary>
        public bool EmitHighlight { get; set; }

        /// <summary>
        /// Optional font availability check used to resolve CSS <c>font-family</c> lists the way a browser does
        /// (first installed family wins). When null, the first family in the list is used and the next one is
        /// written as the RTF alternate font (<c>\falt</c>).
        /// </summary>
        public Func<string, bool>? IsFontAvailable { get; set; }
    }
}
