using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Html2OpenRtf;
using Microsoft.Win32;

namespace Html2OpenRtf.Cli;

/// <summary>
/// Command-line front end: <c>Html2OpenRtf.exe &lt;input.html&gt; [output.rtf]</c>.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 1;
    private const int ExitInputError = 2;
    private const int ExitConversionError = 3;
    private const int ExitWriteError = 4;

    private static string ExeName =>
        OperatingSystem.IsWindows() ? "Html2OpenRtf.exe" : "Html2OpenRtf";

    private static int Main(string[] args)
    {
        int code;
        try
        {
            code = Run(args);
        }
        catch (Exception ex)
        {
            // Last-resort guard: never show a raw .NET crash dialog/stack trace to the user.
            Console.Error.WriteLine($"Error: unexpected failure: {ex.Message}");
            code = ExitConversionError;
        }

        PauseIfLaunchedFromExplorer();
        return code;
    }

    private static int Run(string[] args)
    {
        if (!CliOptions.TryParse(args, out var opts, out var parseError))
        {
            Console.Error.WriteLine($"Error: {parseError}");
            Console.Error.WriteLine();
            WriteUsage(Console.Error);
            return ExitUsage;
        }

        if (opts.ShowHelp)
        {
            WriteUsage(Console.Out);
            return ExitOk;
        }

        if (opts.ShowVersion)
        {
            Console.Out.WriteLine($"Html2OpenRtf {GetVersion()}");
            return ExitOk;
        }

        // ---- Resolve paths -------------------------------------------------------------
        string inputPath;
        try
        {
            inputPath = Path.GetFullPath(opts.Input!);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Console.Error.WriteLine($"Error: invalid input path '{opts.Input}': {ex.Message}");
            return ExitInputError;
        }

        if (Directory.Exists(inputPath))
        {
            Console.Error.WriteLine($"Error: input '{inputPath}' is a directory, not an HTML file.");
            return ExitInputError;
        }

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Error: input file not found: '{inputPath}'");
            return ExitInputError;
        }

        string outputPath;
        try
        {
            outputPath = ResolveOutputPath(inputPath, opts.Output);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Console.Error.WriteLine($"Error: invalid output path '{opts.Output}': {ex.Message}");
            return ExitUsage;
        }

        if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Error: the output file would overwrite the input file. Please specify a different output path.");
            return ExitUsage;
        }

        // ---- Read input ----------------------------------------------------------------
        Encoding? forcedEncoding = null;
        if (opts.EncodingName != null)
        {
            forcedEncoding = TryGetEncoding(opts.EncodingName);
            if (forcedEncoding == null)
            {
                Console.Error.WriteLine($"Error: unknown encoding '{opts.EncodingName}'. Examples: utf-8, windows-1252, iso-8859-1, utf-16.");
                return ExitUsage;
            }
        }

        string html;
        Encoding usedEncoding;
        try
        {
            var bytes = File.ReadAllBytes(inputPath);
            html = HtmlFileDecoder.Decode(bytes, forcedEncoding, out usedEncoding);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"Error: cannot read input file '{inputPath}': {ex.Message}");
            return ExitInputError;
        }
        catch (DecoderFallbackException ex)
        {
            Console.Error.WriteLine($"Error: input file is not valid {forcedEncoding?.WebName ?? "text"}: {ex.Message}");
            return ExitInputError;
        }

        if (!opts.Quiet && string.IsNullOrWhiteSpace(html))
            Console.Error.WriteLine("Warning: input file is empty; writing an empty RTF document.");

        // ---- Convert -------------------------------------------------------------------
        byte[] rtf;
        try
        {
            var options = new HtmlToRtfOptions
            {
                EmitHighlight = opts.Highlight,
                IsFontAvailable = opts.FontCheck ? InstalledFonts.CreateChecker() : null,
            };
            rtf = HtmlToRtfConverter.ConvertHtmlToRtfBytes(html, options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: conversion failed: {ex.Message}");
            return ExitConversionError;
        }

        // ---- Write output --------------------------------------------------------------
        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // Write to a temporary file first so a failure never leaves a truncated RTF behind.
            var tempPath = outputPath + ".tmp-" + Environment.ProcessId;
            try
            {
                File.WriteAllBytes(tempPath, rtf);
                File.Move(tempPath, outputPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"Error: cannot write output file '{outputPath}': {ex.Message}");
            return ExitWriteError;
        }

        if (!opts.Quiet)
        {
            Console.Out.WriteLine($"Converted '{inputPath}'");
            Console.Out.WriteLine($"       -> '{outputPath}' ({rtf.Length:N0} bytes, input read as {usedEncoding.WebName})");
        }

        return ExitOk;
    }

    /// <summary>
    /// Default output is the input path with an .rtf extension. If the given output is an existing
    /// directory (or ends with a path separator), the file is placed in that directory.
    /// </summary>
    private static string ResolveOutputPath(string inputPath, string? output)
    {
        var defaultName = Path.GetFileNameWithoutExtension(inputPath) + ".rtf";

        if (string.IsNullOrWhiteSpace(output))
            return Path.Combine(Path.GetDirectoryName(inputPath) ?? "", defaultName);

        bool looksLikeDir = output.EndsWith(Path.DirectorySeparatorChar) || output.EndsWith(Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(output);
        if (looksLikeDir || Directory.Exists(full))
            return Path.Combine(full, defaultName);

        return full;
    }

    private static Encoding? TryGetEncoding(string name)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var enc = int.TryParse(name, out var codePage) ? Encoding.GetEncoding(codePage) : Encoding.GetEncoding(name);
            // Use strict decoding for an explicitly requested encoding so bad input is reported, not garbled.
            return Encoding.GetEncoding(enc.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string GetVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "1.0.0";
    }

    private static void WriteUsage(TextWriter w)
    {
        w.WriteLine($"Html2OpenRtf {GetVersion()} - convert an HTML file to RTF");
        w.WriteLine();
        w.WriteLine("Usage:");
        w.WriteLine($"  {ExeName} <input.html> [output.rtf] [options]");
        w.WriteLine();
        w.WriteLine("Arguments:");
        w.WriteLine("  input.html          HTML file to convert.");
        w.WriteLine("  output.rtf          RTF file to write (overwritten if it exists). If omitted, the");
        w.WriteLine("                      input name is used with an .rtf extension (page.html -> page.rtf).");
        w.WriteLine("                      If it is an existing folder, the .rtf is written into that folder.");
        w.WriteLine();
        w.WriteLine("Options:");
        w.WriteLine("  --highlight         Also write background colors as \\highlight (for WordPad /");
        w.WriteLine("                      RichTextBox, which ignore character shading). Word then shows");
        w.WriteLine("                      the nearest of its 16 highlight colors instead of the exact color.");
        w.WriteLine("  --encoding <name>   Input encoding (e.g. utf-8, windows-1252). Default: auto-detect");
        w.WriteLine("                      (BOM, <meta charset>, UTF-8, then Windows-1252).");
        w.WriteLine("  --no-font-check     Do not check installed Windows fonts when resolving CSS");
        w.WriteLine("                      font-family lists; always use the first family listed.");
        w.WriteLine("  -q, --quiet         Only print errors.");
        w.WriteLine("  -v, --version       Show version information.");
        w.WriteLine("  -h, --help, /?      Show this help.");
        w.WriteLine();
        w.WriteLine("Preserved formatting:");
        w.WriteLine("  fonts, font sizes, text colors, background colors, bold/italic/underline/strike,");
        w.WriteLine("  line breaks and whitespace in <pre>, line-height, alignment, lists, tables, links.");
        w.WriteLine("  Inline CSS: color, background, background-color, font-family, font-size, font,");
        w.WriteLine("  font-weight, font-style, line-height, text-decoration, text-align, white-space, ...");
        w.WriteLine();
        w.WriteLine("Examples:");
        w.WriteLine($"  {ExeName} code.html");
        w.WriteLine($"  {ExeName} code.html C:\\Docs\\code.rtf");
        w.WriteLine($"  {ExeName} \"My Page.html\" out\\ --highlight");
        w.WriteLine();
        w.WriteLine("Exit codes: 0 success, 1 bad arguments, 2 input error, 3 conversion error, 4 write error.");
    }

    // ---- Double-click / drag-and-drop support --------------------------------------------

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    /// <summary>
    /// When the exe is started from Explorer (double-click or drag-and-drop of an HTML file onto it),
    /// Windows creates a console that closes as soon as the program exits. In that case wait for a key
    /// so the user can read the result.
    /// </summary>
    private static void PauseIfLaunchedFromExplorer()
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected)
            return;
        try
        {
            var buffer = new uint[4];
            if (GetConsoleProcessList(buffer, (uint)buffer.Length) == 1)
            {
                Console.WriteLine();
                Console.Write("Press any key to close this window...");
                Console.ReadKey(intercept: true);
            }
        }
        catch
        {
            // Not critical: just exit.
        }
    }
}

/// <summary>Parsed command-line options.</summary>
internal sealed class CliOptions
{
    public string? Input { get; private set; }
    public string? Output { get; private set; }
    public bool ShowHelp { get; private set; }
    public bool ShowVersion { get; private set; }
    public bool Highlight { get; private set; }
    public bool FontCheck { get; private set; } = true;
    public bool Quiet { get; private set; }
    public string? EncodingName { get; private set; }

    public static bool TryParse(string[] args, out CliOptions options, out string? error)
    {
        options = new CliOptions();
        error = null;

        if (args.Length == 0)
        {
            options.ShowHelp = true;
            return true;
        }

        var positional = new List<string>();
        bool onlyPositional = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (onlyPositional || a.Length < 2 || !(a[0] == '-' || a == "/?"))
            {
                positional.Add(a);
                continue;
            }

            switch (a.ToLowerInvariant())
            {
                case "--":
                    onlyPositional = true;
                    break;
                case "-h":
                case "--help":
                case "-?":
                case "/?":
                    options.ShowHelp = true;
                    return true;
                case "-v":
                case "--version":
                    options.ShowVersion = true;
                    return true;
                case "--highlight":
                    options.Highlight = true;
                    break;
                case "--no-font-check":
                    options.FontCheck = false;
                    break;
                case "-q":
                case "--quiet":
                    options.Quiet = true;
                    break;
                case "--encoding":
                case "-e":
                    if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        error = $"option '{a}' requires an encoding name (e.g. utf-8).";
                        return false;
                    }
                    options.EncodingName = args[++i].Trim();
                    break;
                default:
                    if (a.StartsWith("--encoding=", StringComparison.OrdinalIgnoreCase))
                    {
                        options.EncodingName = a.Substring("--encoding=".Length).Trim();
                        if (options.EncodingName.Length == 0)
                        {
                            error = "option '--encoding' requires an encoding name (e.g. utf-8).";
                            return false;
                        }
                        break;
                    }
                    error = $"unknown option '{a}'.";
                    return false;
            }
        }

        if (positional.Count == 0)
        {
            error = "missing input file.";
            return false;
        }

        if (positional.Count > 2)
        {
            error = $"too many arguments ('{positional[2]}'). Paths containing spaces must be quoted.";
            return false;
        }

        options.Input = positional[0];
        options.Output = positional.Count > 1 ? positional[1] : null;
        return true;
    }
}

/// <summary>Decodes HTML bytes the way browsers do (simplified): BOM, then meta charset, then UTF-8, then Windows-1252.</summary>
internal static class HtmlFileDecoder
{
    private static readonly Regex MetaCharset = new(
        @"<meta[^>]*?charset\s*=\s*[""']?\s*([A-Za-z0-9_\-:.]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Decode(byte[] bytes, Encoding? forced, out Encoding used)
    {
        // 1. Byte order mark always wins.
        if (TryDetectBom(bytes, out var bomEncoding, out var bomLength))
        {
            used = bomEncoding;
            return bomEncoding.GetString(bytes, bomLength, bytes.Length - bomLength);
        }

        // 2. Explicit --encoding.
        if (forced != null)
        {
            used = forced;
            return forced.GetString(bytes);
        }

        // 3. <meta charset="..."> / <meta http-equiv="Content-Type" content="text/html; charset=...">.
        var head = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        var m = MetaCharset.Match(head);
        if (m.Success)
        {
            var declared = m.Groups[1].Value;
            // Per the HTML spec, a declared UTF-16 in a file without BOM is treated as UTF-8.
            if (declared.StartsWith("utf-16", StringComparison.OrdinalIgnoreCase))
                declared = "utf-8";
            var enc = TryGet(declared);
            if (enc != null && enc.CodePage != Encoding.UTF8.CodePage)
            {
                used = enc;
                return enc.GetString(bytes);
            }
        }

        // 4. Valid UTF-8 (also covers plain ASCII).
        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            var text = strictUtf8.GetString(bytes);
            used = Encoding.UTF8;
            return text;
        }
        catch (DecoderFallbackException)
        {
            // fall through
        }

        // 5. Legacy fallback used by browsers for Western content.
        used = TryGet("windows-1252") ?? Encoding.Latin1;
        return used.GetString(bytes);
    }

    private static Encoding? TryGet(string name)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(name);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool TryDetectBom(byte[] b, out Encoding encoding, out int length)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
        {
            encoding = Encoding.UTF8; length = 3; return true;
        }
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0 && b[3] == 0)
        {
            encoding = Encoding.UTF32; length = 4; return true;
        }
        if (b.Length >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0xFE && b[3] == 0xFF)
        {
            encoding = new UTF32Encoding(bigEndian: true, byteOrderMark: true); length = 4; return true;
        }
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
        {
            encoding = Encoding.Unicode; length = 2; return true;
        }
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
        {
            encoding = Encoding.BigEndianUnicode; length = 2; return true;
        }
        encoding = Encoding.UTF8;
        length = 0;
        return false;
    }
}

/// <summary>
/// Installed-font lookup used to resolve CSS font-family lists like a browser on the same machine would.
/// Only implemented on Windows (reads the Fonts registry keys); elsewhere no check is done.
/// </summary>
internal static class InstalledFonts
{
    private static readonly string[] StyleWords =
    {
        "Regular", "Normal", "Bold", "Italic", "Oblique", "Light", "SemiLight", "Semilight", "Semibold",
        "SemiBold", "DemiBold", "Demibold", "Medium", "Black", "Heavy", "Thin", "ExtraLight", "UltraLight",
        "ExtraBold", "UltraBold", "Book",
    };

    private static readonly Regex TrailingParen = new(@"\s*\([^)]*\)\s*$", RegexOptions.CultureInvariant);

    public static Func<string, bool>? CreateChecker()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var fonts = LoadWindowsFonts();
        // If the registry could not be read, do not pretend that nothing is installed.
        if (fonts.Count == 0)
            return null;

        return name => fonts.Contains(name.Trim());
    }

    [SupportedOSPlatform("windows")]
    private static HashSet<string> LoadWindowsFonts()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                if (key == null) continue;
                foreach (var valueName in key.GetValueNames())
                    AddRegistryName(set, valueName);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Ignore unreadable hive.
            }
        }
        return set;
    }

    /// <summary>
    /// Registry value names look like "Consolas Bold (TrueType)" or "Cambria &amp; Cambria Math (TrueType)".
    /// Adds each full face name plus its family name with style words removed.
    /// </summary>
    internal static void AddRegistryName(HashSet<string> set, string valueName)
    {
        var name = TrailingParen.Replace(valueName, "");
        foreach (var part in name.Split(new[] { " & " }, StringSplitOptions.RemoveEmptyEntries))
        {
            var face = part.Trim();
            if (face.Length == 0) continue;
            set.Add(face);

            var words = face.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            while (words.Count > 1 && StyleWords.Contains(words[^1], StringComparer.OrdinalIgnoreCase))
                words.RemoveAt(words.Count - 1);
            set.Add(string.Join(' ', words));
        }
    }
}
