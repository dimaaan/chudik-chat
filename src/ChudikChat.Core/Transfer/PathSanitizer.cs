using System.Buffers;
using System.Text;

namespace ChudikChat.Core.Transfer;

/// <summary>
/// Превращает относительный путь, пришедший от недоверенного отправителя, в путь
/// внутри папки назначения — или отказывается это делать.
/// </summary>
/// <remarks>
/// Чистая функция без обращений к файловой системе: именно поэтому её можно
/// прогнать по таблице враждебных случаев в тестах.
/// Нарушение означает отказ от всей передачи целиком: отправитель, присылающий
/// «../../../.ssh/authorized_keys», не тот, с кем стоит доводить разговор до конца.
/// </remarks>
public static class PathSanitizer
{
    public const int MaxRelativeLengthBytes = 4096;
    public const int MaxSegmentLength = 255;
    public const int MaxFullPathLength = 32_000;

    /// <summary>Порог, после которого Windows нужен префикс расширенных путей.</summary>
    private const int WindowsShortPathLimit = 255;

    /// <summary>
    /// На Windows и macOS файловая система регистронезависима, поэтому и сравнивать
    /// надо так же — иначе «A/b» уедет мимо проверки принадлежности корню.
    /// </summary>
    public static StringComparison PathComparison { get; } =
        OperatingSystem.IsLinux() || OperatingSystem.IsAndroid()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

    private static readonly SearchValues<char> ForbiddenInSegment = SearchValues.Create("<>:\"|?*");

    private static readonly HashSet<string> ReservedNames = BuildReservedNames();

    /// <summary>
    /// Проверяет путь и возвращает его сегменты. Бросает <see cref="ProtocolException"/>,
    /// если путь нельзя принять.
    /// </summary>
    public static string[] Validate(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            throw Reject("пустой путь", relativePath);

        if (Encoding.UTF8.GetByteCount(relativePath) > MaxRelativeLengthBytes)
            throw Reject($"путь длиннее {MaxRelativeLengthBytes} байт", relativePath);

        if (relativePath[0] is '/' or '\\')
            throw Reject("абсолютный путь или UNC", relativePath);

        var normalized = relativePath.Normalize(NormalizationForm.FormC);

        foreach (var ch in normalized)
        {
            if (ch < 0x20 || ch == 0x7F)
                throw Reject("управляющий символ в пути", relativePath);

            if (ch is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069'))
                throw Reject("bidi-подмена в пути", relativePath);
        }

        var segments = normalized.Replace('\\', '/').Split('/');
        foreach (var segment in segments)
            ValidateSegment(segment, relativePath);

        return segments;
    }

    /// <summary>
    /// Собирает абсолютный путь внутри <paramref name="destinationRoot"/> и убеждается,
    /// что результат действительно внутри. Это и есть решающая проверка — всё
    /// остальное существует, чтобы отказывать раньше и с понятной причиной.
    /// </summary>
    public static string Resolve(string destinationRoot, string relativePath)
    {
        var segments = Validate(relativePath);

        var root = Path.GetFullPath(destinationRoot);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;

        var combined = Path.Combine(root, string.Join(Path.DirectorySeparatorChar, segments));
        var full = Path.GetFullPath(combined);

        if (full.Length > MaxFullPathLength)
            throw Reject($"итоговый путь длиннее {MaxFullPathLength} символов", relativePath);

        if (!full.StartsWith(root, PathComparison))
            throw Reject("путь выходит за пределы папки назначения", relativePath);

        return full;
    }

    /// <summary>
    /// Форма пути для файловых API. На Windows длинный путь получает префикс
    /// расширенной формы: системный флаг LongPathsEnabled включён не у всех,
    /// а внутри MSIX он к тому же вёл себя непредсказуемо.
    /// </summary>
    public static string ForFileSystem(string fullPath)
    {
        if (!OperatingSystem.IsWindows())
            return fullPath;

        if (fullPath.Length <= WindowsShortPathLimit || fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            return fullPath;

        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? string.Concat(@"\\?\UNC\", fullPath.AsSpan(2))
            : @"\\?\" + fullPath;
    }

    /// <summary>
    /// Делает из произвольной строки — например, имени пира — безопасное имя папки.
    /// В отличие от <see cref="Validate"/> здесь не отказ, а замена: имя пира
    /// выбирали не мы, и отказываться принимать файлы из-за двоеточия в нём глупо.
    /// </summary>
    public static string ToSafeFolderName(string? raw, string fallback = "пир")
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        var builder = new StringBuilder(Math.Min(raw.Length, 48));

        foreach (var ch in raw.Normalize(NormalizationForm.FormC))
        {
            if (builder.Length == 48)
                break;

            var forbidden = ch < 0x20
                || ch == 0x7F
                || ch is '/' or '\\'
                || ForbiddenInSegment.Contains(ch)
                || (ch is (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩'));

            builder.Append(forbidden ? '_' : ch);
        }

        var name = builder.ToString().Trim().TrimEnd('.');

        if (name.Length == 0 || ReservedNames.Contains(name))
            return fallback;

        return name;
    }

    /// <summary>
    /// Проверяет манифест целиком, включая совпадения. Две записи, сводящиеся к одному
    /// пути, означают попытку перезаписи — такой манифест не принимаем.
    /// </summary>
    public static void ValidateManifest(IEnumerable<string> relativePaths)
    {
        var seen = new HashSet<string>(
            PathComparison == StringComparison.Ordinal
                ? StringComparer.Ordinal
                : StringComparer.OrdinalIgnoreCase);

        foreach (var relativePath in relativePaths)
        {
            var joined = string.Join('/', Validate(relativePath));
            if (!seen.Add(joined))
                throw Reject("путь встречается в манифесте дважды", relativePath);
        }
    }

    private static void ValidateSegment(string segment, string full)
    {
        // Пустой сегмент ловит ведущий, хвостовой и сдвоенный разделитель разом.
        if (segment.Length == 0)
            throw Reject("пустой сегмент пути", full);

        if (segment.Length > MaxSegmentLength)
            throw Reject($"сегмент длиннее {MaxSegmentLength} символов", full);

        if (segment is "." or "..")
            throw Reject("переход вверх по дереву", full);

        // Windows молча срезает хвостовые точки и пробелы, и «foo..» превращается в «foo».
        if (segment[^1] is '.' or ' ')
            throw Reject("сегмент оканчивается точкой или пробелом", full);

        if (segment.AsSpan().ContainsAny(ForbiddenInSegment))
            throw Reject("недопустимый символ в имени", full);

        var dot = segment.IndexOf('.');
        var stem = dot < 0 ? segment : segment[..dot];
        if (ReservedNames.Contains(stem))
            throw Reject($"зарезервированное имя устройства «{stem}»", full);
    }

    private static HashSet<string> BuildReservedNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL" };

        for (var i = 0; i <= 9; i++)
        {
            names.Add($"COM{i}");
            names.Add($"LPT{i}");
        }

        // Windows считает зарезервированными и варианты с надстрочными цифрами.
        foreach (var superscript in new[] { '\u00B9', '\u00B2', '\u00B3' })
        {
            names.Add($"COM{superscript}");
            names.Add($"LPT{superscript}");
        }

        return names;
    }

    private static ProtocolException Reject(string reason, string? path)
    {
        if (path is null)
            return new ProtocolException($"{reason}: <пусто>");

        var shown = path.Length <= 120 ? path : path[..120] + "…";
        return new ProtocolException($"{reason}: «{shown}»");
    }
}
