using System.Text.RegularExpressions;

namespace ArchMind.Core.Extraction;

/// <summary>
/// Best-effort source-file trimmer used to shave input tokens before sending
/// content to the LLM extraction prompts. Strips block comments, line comments,
/// and trailing whitespace for common languages (C#, TS/JS, Python, Java/Kotlin,
/// Go).
///
/// Over-trim risk: may lose semantic context (e.g. attributes embedded in
/// comments, route definitions hidden in docstrings). Disable trimmer if
/// extraction quality regresses.
/// </summary>
public static class FileContentTrimmer
{
    // Token estimate: rough chars/4 heuristic. Anthropic models average between
    // 3.5 and 4.5 chars per token for English/code, so this is intentionally
    // approximate and only used for logging / budgeting.
    private const double CharsPerToken = 4.0;

    // Block comments: /* ... */ — works for C#, TS/JS, Java/Kotlin, Go, CSS.
    private static readonly Regex BlockCommentSlashStar = new(
        @"/\*[\s\S]*?\*/",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Single-line // comments — preserve URL-ish patterns like "http://" by
    // requiring the // not be immediately preceded by ':'.
    private static readonly Regex LineCommentDoubleSlash = new(
        @"(?<![:/])//[^\n\r]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Python: triple-quoted docstrings (greedy-min on either quote style).
    private static readonly Regex PythonTripleDouble = new(
        "\"\"\"[\\s\\S]*?\"\"\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PythonTripleSingle = new(
        @"'''[\s\S]*?'''",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Python / Ruby / shell single-line # comments. Avoid C-preprocessor
    // directives (#include / #define) and shebangs at start-of-file by NOT
    // applying at column 0 when followed by an identifier word — easier:
    // we only apply this regex when language hint == python/ruby/shell.
    private static readonly Regex HashLineComment = new(
        @"#[^\n\r]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Trailing whitespace at end of each line.
    private static readonly Regex TrailingWhitespace = new(
        @"[ \t]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    // Collapse 3+ consecutive blank lines into a single blank line.
    private static readonly Regex MultipleBlankLines = new(
        @"(\r?\n){3,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public readonly record struct TrimResult(string Content, int EstimatedTokens);

    /// <summary>
    /// Trim the content based on the file extension. Falls back to whitespace-only
    /// trimming for unknown extensions.
    /// </summary>
    public static TrimResult Trim(string filePath, string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return new TrimResult(content ?? string.Empty, 0);
        }

        var language = DetectLanguage(filePath);
        var trimmed = language switch
        {
            Language.CSharp => StripCStyleAndCollapse(content),
            Language.TypeScriptJavaScript => StripCStyleAndCollapse(content),
            Language.JavaKotlin => StripCStyleAndCollapse(content),
            Language.Go => StripCStyleAndCollapse(content),
            Language.Python => StripPythonAndCollapse(content),
            _ => CollapseWhitespace(content),
        };

        var tokens = (int)Math.Ceiling(trimmed.Length / CharsPerToken);
        return new TrimResult(trimmed, tokens);
    }

    private static string StripCStyleAndCollapse(string content)
    {
        var s = BlockCommentSlashStar.Replace(content, string.Empty);
        s = LineCommentDoubleSlash.Replace(s, string.Empty);
        return CollapseWhitespace(s);
    }

    private static string StripPythonAndCollapse(string content)
    {
        var s = PythonTripleDouble.Replace(content, string.Empty);
        s = PythonTripleSingle.Replace(s, string.Empty);
        s = HashLineComment.Replace(s, string.Empty);
        return CollapseWhitespace(s);
    }

    private static string CollapseWhitespace(string content)
    {
        var s = TrailingWhitespace.Replace(content, string.Empty);
        s = MultipleBlankLines.Replace(s, "\n\n");
        return s;
    }

    private enum Language
    {
        Unknown,
        CSharp,
        TypeScriptJavaScript,
        Python,
        JavaKotlin,
        Go,
    }

    private static Language DetectLanguage(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cs" => Language.CSharp,
            ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" => Language.TypeScriptJavaScript,
            ".py" => Language.Python,
            ".java" or ".kt" or ".kts" => Language.JavaKotlin,
            ".go" => Language.Go,
            _ => Language.Unknown,
        };
    }
}
