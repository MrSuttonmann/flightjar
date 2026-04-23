using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace FlightJar.Api.Hosting;

/// <summary>
/// Renders <c>index.html</c> with per-asset cache-busting hashes substituted
/// for the <c>__CSS_V__</c> / <c>__JS_V__</c> placeholders. Each occurrence
/// resolves to the SHA-256 prefix of the <em>specific</em> file it sits on:
/// <c>&lt;link href="/static/dialogs.css?v=__CSS_V__"&gt;</c> becomes a hash
/// of <c>dialogs.css</c>, not of some shared CSS bundle. That way changing
/// one file invalidates only its own cached URL.
/// </summary>
public static class IndexHtmlRenderer
{
    // Match any href= or src= that references a /static/&lt;file&gt; with a
    // __CSS_V__ or __JS_V__ cache-bust placeholder. The file name is
    // capture group 1 and determines which asset we hash.
    private static readonly Regex PlaceholderPattern = new(
        @"(?<attr>href|src)=""/static/(?<file>[^""?]+)\?v=__(?<kind>CSS|JS)_V__""",
        RegexOptions.Compiled);

    /// <summary>
    /// Read <c>index.html</c> from <paramref name="staticRoot"/>, substitute
    /// each placeholder with the hash of its owning asset, and return the
    /// rendered document. Returns <c>null</c> if the file is missing so the
    /// caller can 404.
    /// </summary>
    public static string? Render(string staticRoot)
    {
        var indexPath = Path.Combine(staticRoot, "index.html");
        if (!File.Exists(indexPath))
        {
            return null;
        }
        var template = File.ReadAllText(indexPath);
        return PlaceholderPattern.Replace(template, match =>
        {
            var file = match.Groups["file"].Value;
            var hash = ShortHash(Path.Combine(staticRoot, file));
            return match.Value
                .Replace("__CSS_V__", hash)
                .Replace("__JS_V__", hash);
        });
    }

    /// <summary>Short SHA-256 prefix of a file's contents, or <c>"dev"</c>
    /// if the file is missing — matching the Python original's fallback so
    /// a partial static tree during dev doesn't poison the rendered HTML
    /// with an empty hash.</summary>
    private static string ShortHash(string path)
    {
        if (!File.Exists(path))
        {
            return "dev";
        }
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexStringLower(hash)[..12];
    }
}
