using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace ChudikChat.Core.Text;

/// <summary>Кусок текста сообщения: либо обычный, либо ссылка.</summary>
/// <param name="Text">То, что видно на экране, — ровно как в сообщении.</param>
/// <param name="Url">
/// Для ссылки — проверенный абсолютный адрес, которым можно открывать браузер.
/// Он может отличаться от <paramref name="Text"/>: у «www.example.com» дописана
/// схема, схема и имя узла приведены к нижнему регистру, кириллица в пути
/// экранирована процентами. Кириллица в имени узла остаётся как есть —
/// в punycode её переводит уже браузер.
/// </param>
public readonly record struct TextSegment(string Text, string? Url)
{
    [MemberNotNullWhen(true, nameof(Url))]
    public bool IsLink => Url is not null;
}

/// <summary>
/// Находит в тексте сообщения ссылки, по которым можно открыть браузер.
/// </summary>
/// <remarks>
/// Живёт в Core, а не в приложении, по той же причине, что и
/// <see cref="Transfer.PathSanitizer"/>: это чистая функция, и разбирать её
/// пограничные случаи — хвостовая точка в конце предложения, скобки внутри
/// адреса, адрес в кавычках — дело таблицы в тестах, а не проверок руками.
///
/// Распознаются только http и https, и это не упрощение, а намеренное сужение.
/// Сообщение присылает кто угодно из локальной сети, а нажатие на ссылку —
/// это <c>Launcher.OpenAsync</c>, то есть «открой этим тем, чем принято».
/// Позволить туда произвольную схему — file:, ms-settings:, чью-нибудь
/// собственную — значит отдать соседу по сети кнопку «запусти что-нибудь
/// на моей машине». Голые имена вроде «example.com» тоже не ссылки: иначе
/// в них превращались бы «файл.txt» и «версия 1.0.101».
/// </remarks>
public static partial class LinkScanner
{
    /// <summary>
    /// Кандидаты в ссылки. Слева — проверка, что мы не внутри слова, адреса
    /// почты или имени файла: «mail@www.example.com» ссылкой быть не должен.
    /// Справа адрес обрывается на пробеле и на кавычках, включая русские.
    /// </summary>
    [GeneratedRegex(
        """(?<![\w@.])(?:https?://|www\.)[^\s<>"'«»]+""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Candidates();

    /// <summary>
    /// Знаки, которые почти наверняка принадлежат предложению, а не адресу.
    /// Кавычки сюда не нужны: до них дело не дойдёт, их обрывает сам поиск.
    /// </summary>
    private static readonly SearchValues<char> TrailingPunctuation = SearchValues.Create(".,;:!?…");

    /// <summary>
    /// Разбирает текст на куски по порядку. Куски покрывают исходную строку
    /// целиком и без перекрытий, так что склейка их <see cref="TextSegment.Text"/>
    /// даёт ровно то, что было. Если ссылок нет, вернётся один кусок.
    /// </summary>
    public static IReadOnlyList<TextSegment> Split(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        List<TextSegment>? segments = null;
        var plainFrom = 0;

        foreach (var match in Candidates().EnumerateMatches(text))
        {
            // Пропускаем совпадение, начавшееся внутри уже разобранной ссылки:
            // такого быть не должно, но порядок кусков важнее экономии.
            if (match.Index < plainFrom)
                continue;

            var candidate = TrimTail(text.AsSpan(match.Index, match.Length));
            if (!TryNormalize(candidate, out var url))
                continue;

            segments ??= [];

            if (match.Index > plainFrom)
                segments.Add(new TextSegment(text[plainFrom..match.Index], null));

            segments.Add(new TextSegment(new string(candidate), url));
            plainFrom = match.Index + candidate.Length;
        }

        if (segments is null)
            return [new TextSegment(text, null)];

        if (plainFrom < text.Length)
            segments.Add(new TextSegment(text[plainFrom..], null));

        return segments;
    }

    /// <summary>Есть ли в тексте хоть одна ссылка.</summary>
    public static bool HasLink(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        foreach (var match in Candidates().EnumerateMatches(text))
        {
            if (TryNormalize(TrimTail(text.AsSpan(match.Index, match.Length)), out _))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Отрезает от хвоста то, что принадлежит не адресу, а предложению вокруг.
    /// Обрезка только с конца — начало задано схемой и трогать его нечего.
    /// </summary>
    private static ReadOnlySpan<char> TrimTail(ReadOnlySpan<char> candidate)
    {
        while (candidate.Length > 0)
        {
            var last = candidate[^1];

            if (last is ')' or ']' or '}')
            {
                // Скобка остаётся, если ей есть пара внутри самого адреса:
                // в ссылке на «Кот_(животное)» она часть пути. Лишняя же
                // закрывающая пришла снаружи — из «(см. https://…)».
                var open = last switch { ')' => '(', ']' => '[', _ => '{' };
                if (candidate.Count(last) <= candidate.Count(open))
                    break;
            }
            else if (!TrailingPunctuation.Contains(last))
            {
                break;
            }

            candidate = candidate[..^1];
        }

        return candidate;
    }

    private static bool TryNormalize(ReadOnlySpan<char> candidate, [NotNullWhen(true)] out string? url)
    {
        url = null;

        if (candidate.Length == 0)
            return false;

        var bare = candidate.StartsWith("www.", StringComparison.OrdinalIgnoreCase);

        // У «www.example.com» точек две. Одной мало: её съедает само «www.»,
        // то есть остаётся «www.com» или вовсе «www.» — это не адрес.
        if (bare && candidate.Count('.') < 2)
            return false;

        var absolute = bare ? string.Concat("https://", candidate) : new string(candidate);

        if (!Uri.TryCreate(absolute, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        if (uri.Host.Length == 0)
            return false;

        url = uri.AbsoluteUri;
        return true;
    }
}
