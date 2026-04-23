namespace FlightJar.Notifications.Tests;

public class TelegramEscapeTests
{
    [Theory]
    // From Telegram MarkdownV2 spec — every reserved character must be escaped.
    [InlineData("simple", "simple")]
    [InlineData("hello_world", @"hello\_world")]
    [InlineData("*bold*", @"\*bold\*")]
    [InlineData("1.0", @"1\.0")]
    [InlineData("a+b", @"a\+b")]
    [InlineData("a-b", @"a\-b")]
    [InlineData("(paren)", @"\(paren\)")]
    [InlineData("[link]", @"\[link\]")]
    [InlineData("back\\slash", @"back\\slash")]
    [InlineData("!warning!", @"\!warning\!")]
    [InlineData("#hashtag", @"\#hashtag")]
    public void Escape(string input, string expected)
    {
        Assert.Equal(expected, TelegramNotifier.EscapeMarkdownV2(input));
    }
}
