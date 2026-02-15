using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace EncryptedDiary;

public static partial class MarkdownRenderer
{
    private static readonly Regex HeadingRegex = HeadingPattern();
    private static readonly Regex BoldRegex = BoldPattern();
    private static readonly Regex ItalicRegex = ItalicPattern();
    private static readonly Regex CodeRegex = CodePattern();
    private static readonly Regex LinkRegex = LinkPattern();

    public static string ToHtml(string markdown)
    {
        StringBuilder html = new();
        html.Append("""
            <!doctype html>
            <html>
            <head>
                <meta charset="utf-8">
                <style>
                    body {
                        margin: 0;
                        padding: 18px;
                        color: #223247;
                        background: #fcfff9;
                        font-family: "Segoe UI", "Helvetica Neue", sans-serif;
                        line-height: 1.45;
                    }
                    h1,h2,h3,h4,h5,h6 {
                        margin: 0.6em 0 0.35em;
                        color: #1b2a39;
                    }
                    p {
                        margin: 0.5em 0;
                    }
                    ul {
                        margin: 0.5em 0;
                        padding-left: 1.25em;
                    }
                    code {
                        background: #edf2f7;
                        padding: 1px 5px;
                        border-radius: 4px;
                        font-family: "Cascadia Code", Consolas, monospace;
                    }
                    a {
                        color: #0c5d93;
                    }
                </style>
            </head>
            <body>
            """);

        string normalized = (markdown ?? string.Empty).Replace("\r\n", "\n");
        string[] lines = normalized.Split('\n');
        bool inList = false;

        foreach (string lineRaw in lines)
        {
            string line = lineRaw.TrimEnd();

            if (string.IsNullOrWhiteSpace(line))
            {
                if (inList)
                {
                    html.Append("</ul>");
                    inList = false;
                }

                html.Append("<div style=\"height:10px\"></div>");
                continue;
            }

            if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                if (!inList)
                {
                    html.Append("<ul>");
                    inList = true;
                }

                html.Append("<li>")
                    .Append(RenderInline(line[2..].Trim()))
                    .Append("</li>");
                continue;
            }

            if (inList)
            {
                html.Append("</ul>");
                inList = false;
            }

            Match headingMatch = HeadingRegex.Match(line);
            if (headingMatch.Success)
            {
                int level = Math.Min(6, headingMatch.Groups[1].Value.Length);
                string headingText = headingMatch.Groups[2].Value.Trim();
                html.Append($"<h{level}>")
                    .Append(RenderInline(headingText))
                    .Append($"</h{level}>");
                continue;
            }

            html.Append("<p>")
                .Append(RenderInline(line.Trim()))
                .Append("</p>");
        }

        if (inList)
        {
            html.Append("</ul>");
        }

        html.Append("</body></html>");
        return html.ToString();
    }

    private static string RenderInline(string text)
    {
        string result = WebUtility.HtmlEncode(text);
        result = CodeRegex.Replace(result, "<code>$1</code>");
        result = BoldRegex.Replace(result, "<strong>$1</strong>");
        result = ItalicRegex.Replace(result, "<em>$1</em>");

        result = LinkRegex.Replace(result, static match =>
        {
            string label = match.Groups[1].Value;
            string urlRaw = WebUtility.HtmlDecode(match.Groups[2].Value);

            if (!Uri.TryCreate(urlRaw, UriKind.Absolute, out Uri? parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                return label;
            }

            string safeUrl = WebUtility.HtmlEncode(parsed.AbsoluteUri);
            return $"<a href=\"{safeUrl}\">{label}</a>";
        });

        return result;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"\*\*(.+?)\*\*", RegexOptions.Compiled)]
    private static partial Regex BoldPattern();

    [GeneratedRegex(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", RegexOptions.Compiled)]
    private static partial Regex ItalicPattern();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.Compiled)]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"\[(.*?)\]\((.*?)\)", RegexOptions.Compiled)]
    private static partial Regex LinkPattern();
}
