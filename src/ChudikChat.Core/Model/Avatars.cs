using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace ChudikChat.Core.Model;

/// <summary>
/// Проверенная картинка учётной записи: байты и их отпечаток.
/// Неизменяема и живёт только в памяти — на диск аватары не попадают.
/// </summary>
public sealed record AvatarImage
{
    private readonly byte[] _bytes;

    internal AvatarImage(string tag, byte[] owned)
    {
        Tag = tag;
        _bytes = owned;
    }

    /// <summary>Отпечаток содержимого: <see cref="Avatars.TagLength"/> шестнадцатеричных символов.</summary>
    public string Tag { get; }

    public ReadOnlyMemory<byte> Bytes => _bytes;

    /// <summary>Массив как есть — только внутри сборки, чтобы не копировать четверть мегабайта в кадр.</summary>
    internal byte[] Buffer => _bytes;

    /// <summary>
    /// Новый поток на каждый вызов.
    /// </summary>
    /// <remarks>
    /// Голова отдаёт этот метод как фабрику в <c>ImageSource.FromStream</c>, а тот зовёт
    /// фабрику заново при каждой загрузке картинки: при переиспользовании строки списка,
    /// при смене темы, при возврате из фона. Один поток на всех закрылся бы после первой
    /// отрисовки, и картинка пропала бы при первой же прокрутке.
    /// </remarks>
    public Stream OpenRead() => new MemoryStream(_bytes, writable: false);

    /// <summary>Отпечаток и есть личность картинки: сравнение по нему, а не побайтовое.</summary>
    public bool Equals(AvatarImage? other) =>
        other is not null && string.Equals(Tag, other.Tag, StringComparison.Ordinal);

    public override int GetHashCode() => Tag.GetHashCode(StringComparison.Ordinal);
}

/// <summary>
/// Картинки учётных записей: как посчитать отпечаток и как обезвредить чужую картинку.
/// </summary>
/// <remarks>
/// Ядро картинки не добывает и не перекодирует — декодера у него нет и быть не должно.
/// Его работа здесь ровно одна: не пустить в приложение то, что приехало по сети под
/// видом картинки. Проверяются размер и сигнатура, а присланные байты дополнительно
/// сверяются с запрошенным отпечатком — иначе пир отравляет кэш или показывает не ту
/// картинку, которую объявил.
/// </remarks>
public static class Avatars
{
    /// <summary>
    /// Потолок размера. Голова обязана уложиться: картинка учётной записи бывает
    /// под мегабайт, и уменьшать её — работа платформы, у которой есть декодер.
    /// </summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>Меньше этого не бывает ни одной осмысленной картинки — только обрубок.</summary>
    public const int MinBytes = 64;

    /// <summary>Длина отпечатка в символах: 16 байт SHA-256 в шестнадцатеричном виде.</summary>
    public const int TagLength = 32;

    /// <summary>
    /// Отпечаток содержимого.
    /// </summary>
    /// <remarks>
    /// Шестнадцатеричная запись, а не base64url: проверка сводится к длине и алфавиту,
    /// и строка одинаково безопасна как значение JSON, как ключ словаря и в диагностике.
    ///
    /// Усечение до 128 бит безопасно, потому что отпечатку самому по себе никто не верит:
    /// присланные байты всё равно перехешиваются и сверяются. Выигрыш от коллизии —
    /// показать чужую картинку вместо своей, и 128 бит для этого более чем достаточно.
    /// </remarks>
    public static string ComputeTag(ReadOnlySpan<byte> raw)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(raw, digest);
        return Convert.ToHexStringLower(digest[..(TagLength / 2)]);
    }

    /// <summary>
    /// Проверяет байты и заворачивает их в <see cref="AvatarImage"/>.
    /// Возвращает false, если это не картинка или она не того размера.
    /// </summary>
    public static bool TryCreate(ReadOnlySpan<byte> raw, [NotNullWhen(true)] out AvatarImage? image)
    {
        image = null;

        // Порядок проверок — от дешёвой к дорогой: хешировать мусор незачем.
        if (raw.Length is < MinBytes or > MaxBytes)
            return false;

        if (!IsKnownFormat(raw))
            return false;

        // Копия обязательна: вызывающий мог оставить массив себе и изменить его позже,
        // а отпечаток уже уехал бы в сеть.
        var owned = raw.ToArray();
        image = new AvatarImage(ComputeTag(owned), owned);
        return true;
    }

    /// <summary>Годен ли отпечаток как ключ словаря и как значение из сети.</summary>
    public static bool IsValidTag([NotNullWhen(true)] string? tag)
    {
        if (tag is not { Length: TagLength })
            return false;

        foreach (var ch in tag)
        {
            if (ch is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Приводит отпечаток, пришедший по сети, к безопасному виду: негодный становится null.
    /// Ровно та же роль, что у <see cref="DeviceNames.Sanitize"/> для имени.
    /// </summary>
    public static string? SanitizeTag(string? tag) => IsValidTag(tag) ? tag : null;

    /// <summary>
    /// PNG или JPEG. Больше ничего пускать нельзя: декодеры платформ расходятся
    /// (Android, например, не умеет TIFF), и «картинка», которую покажет только
    /// отправитель, хуже отсутствия картинки — она молча пустая.
    /// </summary>
    private static bool IsKnownFormat(ReadOnlySpan<byte> raw)
    {
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        ReadOnlySpan<byte> jpeg = [0xFF, 0xD8, 0xFF];

        return raw.StartsWith(png) || raw.StartsWith(jpeg);
    }
}
