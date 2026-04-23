using System.Security.Cryptography;
using FlightJar.Api.Hosting;

namespace FlightJar.Api.Tests;

public class IndexHtmlRendererTests : IDisposable
{
    private readonly string _dir;

    public IndexHtmlRendererTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fj-index-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Returns_Null_When_Index_Missing()
    {
        Assert.Null(IndexHtmlRenderer.Render(_dir));
    }

    [Fact]
    public void Substitutes_Each_Placeholder_With_Hash_Of_Its_Own_File()
    {
        File.WriteAllText(Path.Combine(_dir, "app.css"), "a");
        File.WriteAllText(Path.Combine(_dir, "dialogs.css"), "b");
        File.WriteAllText(Path.Combine(_dir, "app.js"), "c");
        File.WriteAllText(Path.Combine(_dir, "index.html"),
            """
            <link rel="stylesheet" href="/static/app.css?v=__CSS_V__">
            <link rel="stylesheet" href="/static/dialogs.css?v=__CSS_V__">
            <script src="/static/app.js?v=__JS_V__"></script>
            """);

        var rendered = IndexHtmlRenderer.Render(_dir)!;

        Assert.DoesNotContain("__CSS_V__", rendered);
        Assert.DoesNotContain("__JS_V__", rendered);
        Assert.Contains($"/static/app.css?v={Short12("a")}\"", rendered);
        Assert.Contains($"/static/dialogs.css?v={Short12("b")}\"", rendered);
        Assert.Contains($"/static/app.js?v={Short12("c")}\"", rendered);
    }

    [Fact]
    public void Distinct_Files_Produce_Distinct_Hashes()
    {
        // Regression guard: an earlier design used one hash for every
        // __CSS_V__ occurrence. With three CSS files the per-file hash
        // model is what actually busts cache on individual edits.
        File.WriteAllText(Path.Combine(_dir, "app.css"), "one");
        File.WriteAllText(Path.Combine(_dir, "dialogs.css"), "two");
        File.WriteAllText(Path.Combine(_dir, "index.html"),
            """
            <link href="/static/app.css?v=__CSS_V__">
            <link href="/static/dialogs.css?v=__CSS_V__">
            """);

        var rendered = IndexHtmlRenderer.Render(_dir)!;
        var appHash = Short12("one");
        var dialogsHash = Short12("two");
        Assert.NotEqual(appHash, dialogsHash);
        Assert.Contains($"/static/app.css?v={appHash}\"", rendered);
        Assert.Contains($"/static/dialogs.css?v={dialogsHash}\"", rendered);
    }

    [Fact]
    public void Real_Index_Has_All_Placeholders_Substituted()
    {
        // Guard against someone adding a new `?v=__CSS_V__` line to
        // index.html without realising the regex needs to match it.
        var repoRoot = FindRepoRoot();
        Assert.NotNull(repoRoot);
        var staticDir = Path.Combine(repoRoot!, "app", "static");
        var rendered = IndexHtmlRenderer.Render(staticDir);
        Assert.NotNull(rendered);
        Assert.DoesNotContain("__CSS_V__", rendered);
        Assert.DoesNotContain("__JS_V__", rendered);
    }

    [Fact]
    public void Missing_Asset_Resolves_To_Dev_Sentinel()
    {
        // Dev mode sometimes has a partial static tree (e.g. generated
        // files missing before first build). A hashless "dev" sentinel is
        // better than a crash or an empty hash string.
        File.WriteAllText(Path.Combine(_dir, "index.html"),
            """<link href="/static/missing.css?v=__CSS_V__">""");
        var rendered = IndexHtmlRenderer.Render(_dir)!;
        Assert.Contains("?v=dev\"", rendered);
    }

    private static string Short12(string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash)[..12];
    }

    private static string? FindRepoRoot()
    {
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            if (Directory.Exists(Path.Combine(cursor.FullName, "app", "static")))
            {
                return cursor.FullName;
            }
            cursor = cursor.Parent;
        }
        return null;
    }
}
