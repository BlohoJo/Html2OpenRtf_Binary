# Html2OpenRtf

Free, open-source .NET library for converting HTML to RTF (Rich Text Format).

[![Build](https://github.com/ifmelate/Html2OpenRtf/actions/workflows/ci.yml/badge.svg)](https://github.com/ifmelate/Html2OpenRtf/actions)
[![NuGet](https://img.shields.io/nuget/v/Html2OpenRtf.svg)](https://www.nuget.org/packages/Html2OpenRtf/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)



## Command-line tool (`Html2OpenRtf.exe`)

A standalone Windows x64 console program is included in `src/Html2OpenRtf.Cli`. It is a self-contained
single-file executable (the .NET 8 runtime is embedded): no .NET installation is needed on the target machine
(64-bit Windows 10 / 11 or Windows Server).

```text
Html2OpenRtf.exe <input.html> [output.rtf] [options]
```

```bat
Html2OpenRtf.exe code.html                      :: writes code.rtf next to code.html
Html2OpenRtf.exe code.html C:\Docs\code.rtf     :: explicit output file (overwritten if it exists)
Html2OpenRtf.exe "My Page.html" out\            :: existing folder (or trailing \) -> out\My Page.rtf
```

You can also drag an `.html` file onto `Html2OpenRtf.exe` in Explorer; the window stays open until a key is pressed.

| Option | Description |
|--------|-------------|
| `--highlight` | Also write background colors as `\highlight` (WordPad / RichTextBox ignore character shading; Word then shows the nearest of its 16 highlight colors). |
| `--encoding <name>` | Input encoding (`utf-8`, `windows-1252`, ...). Default: auto-detect (BOM, `<meta charset>`, valid UTF-8, else Windows-1252). |
| `--no-font-check` | Don't consult installed Windows fonts when resolving `font-family` lists; always use the first family. |
| `-q`, `--quiet` | Only print errors. |
| `-v`, `--version` | Print the version. |
| `-h`, `--help`, `/?` | Print usage help (also shown when started without arguments). |

Exit codes: `0` success, `1` bad arguments, `2` input error (missing/unreadable file), `3` conversion error, `4` write error.
Output is written to a temporary file first and then moved into place, so a failed run never leaves a half-written RTF.

### Formatting fidelity

Designed for syntax-highlighted code, e.g. `<pre>` blocks containing `<span style="color:...; background:...">`:

- **Fonts**: CSS `font-family` lists (`'Fira Code', Consolas, monospace`) are resolved like a browser does - the first
  family installed on the machine wins, generic families map to Windows fonts (`monospace` -> Courier New,
  `sans-serif` -> Arial, `serif` -> Times New Roman), and the fallback is stored as the RTF alternate font (`\falt`).
- **Colors**: `color`, `background`, `background-color` (hex, `rgb()/rgba()`, `hsl()/hsla()`, named colors, `transparent`),
  on spans (character shading) and on blocks such as `<pre>`/`<p>`/`<div>` (paragraph shading).
- **Sizes**: `font-size` in `px`, `pt`, `em`, `rem`, `%`, keywords (`small`, `x-large`, ...), plus `<font size>`;
  `line-height` (number, `%`, length, `normal`).
- **Line breaks & whitespace**: newlines inside `<pre>` (or `white-space: pre|pre-wrap|pre-line|break-spaces`) become RTF
  line breaks, runs of spaces and indentation are kept, tabs are expanded to the next tab stop, `<br>` is honoured.
- Also: `font` shorthand, `font-weight`, `font-style`, `text-decoration`, `text-align`, `text-indent`, `margin(-top/-bottom)`,
  `vertical-align: sub|super`, `display: block|inline|none`, nested spans restoring the parent's formatting.

### Building the exe

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) or newer (can be built on Windows, Linux or macOS):

```bash
dotnet publish -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -c Release
```

The executable is written to `publish/win-x64/Html2OpenRtf.exe` (use `-o <dir>` to choose another folder).
Use `-r linux-x64` or `-r osx-arm64` to build the same tool for other platforms.

### Building the exe with GitHub Actions

- **Build and Test** (`ci.yml`, runs on every push / PR to `main`, or manually): builds and tests the library on Linux and
  Windows, then publishes and smoke-tests `Html2OpenRtf.exe`; download it from the run's **Artifacts** section.
- **Release Windows exe** (`publish.yml`): when a GitHub Release is published, the exe is built from the release tag
  (its version is taken from the tag, e.g. `v1.2.0` -> `1.2.0`), tested, and `Html2OpenRtf.exe` plus
  `Html2OpenRtf_win-x64_<tag>.zip` (exe, README, LICENSE, sample) are attached to the release. It can also be started
  manually from the Actions tab; enter an existing tag to (re)attach the files to that release.

No secrets are needed. (The upstream project's workflow pushed the library to nuget.org with a `NUGET_API_KEY`; this
fork builds the exe instead.)

## Installation

```bash
dotnet add package Html2OpenRtf
```

## Usage

```csharp
using Html2OpenRtf;

// HTML string to RTF string
string rtf = HtmlToRtfConverter.ConvertHtmlToRtf("<p>Hello <b>World</b></p>");

// HTML to RTF bytes (ASCII)
byte[] rtfBytes = HtmlToRtfConverter.ConvertHtmlToRtfBytes(html);

// HTML to Base64-encoded RTF (useful for APIs)
string base64Rtf = HtmlToRtfConverter.ConvertHtmlToBase64Rtf(html);
```

## Supported HTML

| Category | Tags |
|----------|------|
| **Block** | `p`, `div`, `br`, `h1`-`h6`, `blockquote`, `hr`, `pre` |
| **Inline** | `b`/`strong`, `i`/`em`, `u`, `s`/`strike`/`del`, `sub`, `sup` |
| **Lists** | `ul`, `ol`, `li` (including nested) |
| **Tables** | `table`, `tr`, `td`, `th` (equal-width cells) |
| **Links** | `a` (clickable RTF hyperlinks) |
| **Images** | `img` (placeholder `[image: alt]`) |
| **Code** | `code`, `kbd`, `samp` (monospace font) |
| **Highlight** | `mark` (yellow background) |
| **Styles** | `color`, `background`/`background-color`, `font-family`, `font-size`, `font`, `line-height`, `text-align`, `font-weight`, `font-style`, `text-decoration`, `white-space`, `margin` via `style` attribute (see [Formatting fidelity](#formatting-fidelity)) |
| **Unicode** | Full support (Cyrillic, CJK, etc. via `\uN?` escapes) |
| **Entities** | Full HTML entity decoding |

## Target

- **.NET Standard 2.0** (compatible with .NET Framework 4.6.1+, .NET Core 2.0+, .NET 5-9)
- Single dependency: [HtmlAgilityPack](https://html-agility-pack.net/) (MIT)
- Cross-platform: Windows, Linux, macOS

## Contributing

PRs welcome. Run tests with:

```bash
dotnet test
```

## License

MIT
