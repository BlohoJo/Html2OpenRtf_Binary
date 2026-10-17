using System;
using System.Text;
using HtmlAgilityPack;

namespace Html2OpenRtf
{
    /// <summary>
    /// Converts HTML strings to RTF (Rich Text Format).
    /// Free, open-source alternative to commercial HTML-to-RTF converters.
    /// </summary>
    public static class HtmlToRtfConverter
    {
        /// <summary>
        /// Converts an HTML string to an RTF string.
        /// </summary>
        /// <param name="html">HTML content to convert.</param>
        /// <returns>RTF document string, or empty RTF document if input is null/empty.</returns>
        public static string ConvertHtmlToRtf(string? html) => ConvertHtmlToRtf(html, null);

        /// <summary>
        /// Converts an HTML string to an RTF string using the given options.
        /// </summary>
        /// <param name="html">HTML content to convert.</param>
        /// <param name="options">Conversion options; null uses the defaults.</param>
        /// <returns>RTF document string, or empty RTF document if input is null/empty.</returns>
        public static string ConvertHtmlToRtf(string? html, HtmlToRtfOptions? options)
        {
            options ??= new HtmlToRtfOptions();
            var builder = new RtfDocumentBuilder(options.EmitHighlight);

            if (string.IsNullOrWhiteSpace(html))
                return builder.Build();

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var visitor = new HtmlNodeVisitor(builder, options);
            visitor.Visit(doc.DocumentNode);
            visitor.Finish();

            return builder.Build();
        }

        /// <summary>
        /// Converts an HTML string to RTF bytes (ASCII encoded).
        /// RTF is an ASCII-based format; all non-ASCII characters are escaped as \uN? sequences.
        /// </summary>
        public static byte[] ConvertHtmlToRtfBytes(string? html) => ConvertHtmlToRtfBytes(html, null);

        /// <summary>
        /// Converts an HTML string to RTF bytes (ASCII encoded) using the given options.
        /// </summary>
        public static byte[] ConvertHtmlToRtfBytes(string? html, HtmlToRtfOptions? options)
        {
            var rtf = ConvertHtmlToRtf(html, options);
            // Safe: ConvertHtmlToRtf produces pure ASCII (non-ASCII chars are \uN? escaped).
            return Encoding.ASCII.GetBytes(rtf);
        }

        /// <summary>
        /// Converts an HTML string to a Base64-encoded RTF string.
        /// Convenient for wire formats and APIs that expect Base64 payloads.
        /// </summary>
        public static string ConvertHtmlToBase64Rtf(string? html) => ConvertHtmlToBase64Rtf(html, null);

        /// <summary>
        /// Converts an HTML string to a Base64-encoded RTF string using the given options.
        /// </summary>
        public static string ConvertHtmlToBase64Rtf(string? html, HtmlToRtfOptions? options)
        {
            var bytes = ConvertHtmlToRtfBytes(html, options);
            return Convert.ToBase64String(bytes);
        }
    }
}
