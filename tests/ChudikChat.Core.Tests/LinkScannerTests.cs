using ChudikChat.Core.Text;

namespace ChudikChat.Core.Tests;

public class LinkScannerTests
{
    [Theory]
    // Обычные адреса целиком.
    [InlineData("https://example.com")]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/path/to/page")]
    [InlineData("https://example.com/search?q=кот&lr=2")]
    [InlineData("https://example.com:8443/x")]
    [InlineData("http://localhost:5000/api")]
    [InlineData("http://192.168.1.7:9090")]
    [InlineData("https://пример.рф/страница")]
    [InlineData("HTTPS://EXAMPLE.COM")]
    // Без схемы, но с «www.».
    [InlineData("www.example.com")]
    [InlineData("www.example.com/path")]
    // Скобки внутри адреса — часть пути, а не обрамление.
    [InlineData("https://ru.wikipedia.org/wiki/Кот_(животное)")]
    public void Whole_message_is_one_link(string text)
    {
        var segment = Assert.Single(LinkScanner.Split(text));

        Assert.True(segment.IsLink);
        Assert.Equal(text, segment.Text);
        Assert.True(LinkScanner.HasLink(text));
    }

    [Theory]
    // Ничего похожего на адрес.
    [InlineData("привет")]
    [InlineData("")]
    [InlineData("   ")]
    // Голое имя ссылкой не считается — иначе ими станут имена файлов и версии.
    [InlineData("example.com")]
    [InlineData("файл.txt")]
    [InlineData("версия 1.0.101")]
    [InlineData("сохрани README.md в папку")]
    // Почтовый адрес — не ссылка, хотя и содержит «www.».
    [InlineData("пиши на mail@example.com")]
    [InlineData("пиши на mail@www.example.com")]
    // «www.» без домена второго уровня.
    [InlineData("www.")]
    [InlineData("www.com")]
    // Схемы, которыми нельзя пускать чужого человека в систему.
    [InlineData("file:///C:/Windows/System32")]
    [InlineData("ftp://example.com/pub")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("chudik://открой")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    public void Text_without_links_stays_whole(string text)
    {
        var segments = LinkScanner.Split(text);

        Assert.All(segments, segment => Assert.False(segment.IsLink));
        Assert.False(LinkScanner.HasLink(text));
        Assert.Equal(text, string.Concat(segments.Select(segment => segment.Text)));
    }

    [Theory]
    // Хвостовая пунктуация принадлежит предложению.
    [InlineData("смотри https://example.com/a.", "https://example.com/a")]
    [InlineData("смотри https://example.com/a, потом сюда", "https://example.com/a")]
    [InlineData("это https://example.com?", "https://example.com")]
    [InlineData("это https://example.com!", "https://example.com")]
    [InlineData("ну https://example.com…", "https://example.com")]
    [InlineData("вот: https://example.com/a; и всё", "https://example.com/a")]
    // Обрамляющие скобки и кавычки.
    [InlineData("(см. https://example.com)", "https://example.com")]
    [InlineData("[https://example.com]", "https://example.com")]
    [InlineData("«https://example.com»", "https://example.com")]
    [InlineData("\"https://example.com\"", "https://example.com")]
    // А вот эти скобки внутри адреса.
    [InlineData("(https://ru.wikipedia.org/wiki/Кот_(животное))", "https://ru.wikipedia.org/wiki/Кот_(животное)")]
    public void Punctuation_around_a_link_is_not_part_of_it(string text, string expected)
    {
        var segments = LinkScanner.Split(text);
        var link = Assert.Single(segments, segment => segment.IsLink);

        Assert.Equal(expected, link.Text);
        Assert.Equal(text, string.Concat(segments.Select(segment => segment.Text)));
    }

    [Fact]
    public void Several_links_are_found_in_order()
    {
        const string text = "первая https://one.example.com, вторая www.two.example.com и всё";

        var links = LinkScanner.Split(text).Where(segment => segment.IsLink).ToArray();

        Assert.Equal(
            ["https://one.example.com", "www.two.example.com"],
            links.Select(link => link.Text));
    }

    [Theory]
    // Схема дописывается, имя узла приводится к виду, понятному любому браузеру.
    [InlineData("www.example.com", "https://www.example.com/")]
    [InlineData("HTTPS://Example.COM/Path", "https://example.com/Path")]
    // Имя узла остаётся кириллическим, а вот путь экранируется.
    [InlineData("https://пример.рф", "https://пример.рф/")]
    [InlineData("https://пример.рф/страница", "https://пример.рф/%D1%81%D1%82%D1%80%D0%B0%D0%BD%D0%B8%D1%86%D0%B0")]
    public void Address_for_the_browser_is_normalized(string text, string expected)
    {
        var link = Assert.Single(LinkScanner.Split(text));

        Assert.Equal(expected, link.Url);
    }

    [Theory]
    [InlineData("привет")]
    [InlineData("смотри https://example.com/a. Там всё.")]
    [InlineData("(см. https://example.com) и www.example.org!")]
    [InlineData("https://example.com")]
    [InlineData("https://one.example.com https://two.example.com")]
    public void Segments_cover_the_original_text(string text)
    {
        var segments = LinkScanner.Split(text);

        Assert.Equal(text, string.Concat(segments.Select(segment => segment.Text)));
        Assert.DoesNotContain(segments, segment => segment.Text.Length == 0);
    }

    [Fact]
    public void Empty_text_gives_no_segments()
    {
        Assert.Empty(LinkScanner.Split(null));
        Assert.Empty(LinkScanner.Split(string.Empty));
    }
}
