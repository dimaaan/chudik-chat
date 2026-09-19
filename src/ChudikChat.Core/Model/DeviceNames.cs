using System.Globalization;
using System.Text;

namespace ChudikChat.Core.Model;

/// <summary>
/// Имена, показываемые в списке пиров: как получить своё и как обезвредить чужое.
/// </summary>
public static class DeviceNames
{
    public const int MaxLength = 64;
    public const string Fallback = "Без имени";

    /// <summary>
    /// Приводит имя, пришедшее по сети, к безопасному для показа виду.
    /// Без этого пир вставляет в имя перевод строки или bidi-override
    /// и подделывает чужую строку в списке участников.
    /// </summary>
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Fallback;

        var normalized = raw.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(Math.Min(normalized.Length, MaxLength));

        foreach (var ch in normalized)
        {
            if (builder.Length == MaxLength)
                break;

            if (IsForbidden(ch))
                continue;

            builder.Append(ch);
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? Fallback : result;
    }

    private static bool IsForbidden(char ch)
    {
        // Управляющие символы: перевод строки разрывает строку списка.
        if (ch < 0x20 || ch == 0x7F)
            return true;

        // Bidi-override и изоляты: ими переворачивают видимый порядок символов.
        if (ch is >= '‪' and <= '‮')
            return true;

        if (ch is >= '⁦' and <= '⁩')
            return true;

        // Прочие невидимые форматирующие символы.
        return CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format;
    }

    /// <summary>
    /// Имя этого устройства. На Android <see cref="Environment.MachineName"/> возвращает
    /// "localhost", поэтому голова приложения подменяет провайдер платформенным.
    /// </summary>
    public static Func<string> LocalProvider { get; set; } = static () => Environment.MachineName;

    public static string Local() => Sanitize(LocalProvider());
}
