#if MACCATALYST
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CoreGraphics;
using Foundation;
using ImageIO;
using UIKit;
#endif

using ChudikChat.Core.Model;

namespace ChudikChat.App.Services;

/// <summary>
/// Картинка учётной записи операционной системы — та, что человек видит на экране входа.
/// Возвращает null, если её нет или добыть не удалось: тогда в списке остаётся буква.
/// </summary>
/// <remarks>
/// На Android ветки нет вовсе, и это решение, а не недоделка. Публичного API
/// «картинка учётной записи» в Android не существует; ближайшее — фото владельца через
/// ContactsContract.Profile — требует READ_CONTACTS, то есть доступа ко всей адресной
/// книге ради украшения. Для чата в домашней сети такое разрешение неуместно, а профиль
/// у большинства всё равно пуст.
///
/// На macOS приходится запускать отдельный процесс: в биндингах Mac Catalyst нет
/// OpenDirectory, а AppKit урезан до трёх десятков типов — ни CBIdentity, ни NSWorkspace
/// там нет. dscl остаётся единственным способом спросить локальную базу учётных записей.
/// Если когда-нибудь применится Platforms/MacCatalyst/Entitlements.plist (например, ради
/// Mac App Store), в песочнице Process.Start запрещён и вся ветка macOS умрёт целиком.
///
/// Ветка Windows не проверена ни на одной машине: путь в реестре взят из документации.
/// Всё в ней падает в null, поэтому худшее, что может случиться, — снова буква.
/// </remarks>
public static class AccountPicture
{
    /// <summary>
    /// Читает картинку учётной записи. Работа блокирующая: процесс, реестр, диск —
    /// вызывать только не из потока UI.
    /// </summary>
    public static byte[]? Fetch()
    {
#if MACCATALYST
        return FetchMac();
#elif WINDOWS
        return FetchWindows();
#else
        return null;
#endif
    }

#if MACCATALYST

    /// <summary>Сторона картинки в пикселях: кружку в 36 точек хватает при любом масштабе.</summary>
    private const int MaxSide = 128;

    /// <summary>
    /// Потолок на текст от dscl. На машине разработчика дамп — около 1,8 МБ,
    /// так что 16 МБ с запасом, но выделить гигабайт по чужой прихоти уже нельзя.
    /// </summary>
    private const int MaxDumpChars = 16 * 1024 * 1024;

    /// <summary>Потолок на файл по пути из атрибута Picture.</summary>
    private const int MaxPictureFileBytes = 16 * 1024 * 1024;

    private static readonly TimeSpan DsclTimeout = TimeSpan.FromSeconds(5);

    private static byte[]? FetchMac()
    {
        var user = ShortUserName();
        if (user is null)
            return null;

        // JPEGPhoto — то, что человек поставил сам. Picture — путь к стандартной картинке.
        var raw = ReadPhotoAttribute(user) ?? ReadPictureFile(user);
        if (raw is null)
            return null;

        var png = ToPng(raw, MaxSide);

        // Картинка учётной записи бывает и в несколько мегапикселей. Один повтор вдвое
        // мельче — и хватит: не влезло и так, значит что-то не то с самой картинкой.
        if (png is not null && png.Length > Avatars.MaxBytes)
            png = ToPng(raw, MaxSide / 2);

        return png;
    }

    /// <summary>
    /// Короткое имя пользователя — оно же имя записи в каталоге.
    /// </summary>
    /// <remarks>
    /// <see cref="Environment.UserName"/> на Unix — это getpwuid(getuid())->pw_name,
    /// то есть то же поле той же базы, куда смотрит «dscl .».
    ///
    /// Проверка формы имени — не перестраховка. Список аргументов закрывает подстановку
    /// в оболочку, но «/» в имени молча увёл бы путь записи /Users/имя в другое место
    /// каталога, а «-» в начале превратил бы имя в ключ командной строки.
    /// </remarks>
    private static string? ShortUserName()
    {
        var user = Environment.UserName;

        if (user.Length is 0 or > 64 || user[0] == '-')
            return null;

        foreach (var ch in user)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('.' or '_' or '-'))
                return null;
        }

        return user;
    }

    private static byte[]? ReadPhotoAttribute(string user)
    {
        const string attribute = "JPEGPhoto";

        var dump = ReadAttribute(user, attribute);
        return dump is null ? null : ParseHexDump(dump, attribute);
    }

    private static byte[]? ReadPictureFile(string user)
    {
        const string attribute = "Picture";

        var dump = ReadAttribute(user, attribute);
        if (dump is null)
            return null;

        var path = ParseSingleValue(dump, attribute);
        if (path is null || !Path.IsPathRooted(path))
            return null;

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is 0 or > MaxPictureFileBytes)
                return null;

            return File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Спрашивает у локального каталога один атрибут и отдаёт вывод dscl как есть.
    /// </summary>
    /// <remarks>
    /// Путь абсолютный: PATH внутри запущенного через launchd бандла — это то, что дал
    /// launchd, и полагаться на него нельзя.
    ///
    /// Узел «.», а не «/Search»: на машине в домене второй ходит в сеть и может подвиснуть
    /// вместе с нами.
    ///
    /// UseShellExecute = false обязателен: на Mac Catalyst значение true бросает.
    ///
    /// Поток ошибок намеренно НЕ перенаправляется. С одной трубой правило «дочитать
    /// до конца, потом ждать» доказуемо не заклинивает; с двумя пришлось бы осушать обе
    /// одновременно, иначе вторая наполнится и застопорит первую.
    /// </remarks>
    private static string? ReadAttribute(string user, string attribute)
    {
        var info = new ProcessStartInfo("/usr/bin/dscl")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };

        info.ArgumentList.Add(".");
        info.ArgumentList.Add("-read");
        info.ArgumentList.Add("/Users/" + user);
        info.ArgumentList.Add(attribute);

        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return null;

            // Читаем ДО ожидания выхода: дамп — под два мегабайта, труба — 64 КБ.
            // Дождаться сначала завершения значило бы ждать вечно.
            var text = ReadCapped(process.StandardOutput);

            if (!process.WaitForExit(DsclTimeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
                {
                    // Уже завершился сам между проверкой и выстрелом.
                }

                return null;
            }

            return process.ExitCode == 0 ? text : null;
        }
        catch (Exception e) when (e is Win32Exception
                                      or InvalidOperationException
                                      or PlatformNotSupportedException
                                      or IOException)
        {
            return null;
        }
    }

    private static string? ReadCapped(TextReader reader)
    {
        var builder = new StringBuilder();
        var chunk = new char[64 * 1024];

        while (true)
        {
            var read = reader.Read(chunk, 0, chunk.Length);
            if (read == 0)
                return builder.ToString();

            if (builder.Length + read > MaxDumpChars)
                return null;

            builder.Append(chunk, 0, read);
        }
    }

    /// <summary>
    /// Разбирает вывод вида «JPEGPhoto:\n 4d4d002a 000c0008 …» в байты.
    /// </summary>
    /// <remarks>
    /// Успех опознаётся положительно — по первой строке. Кода возврата недостаточно:
    /// при отсутствующем атрибуте dscl завершается нулём и печатает «No such key: …»
    /// (проверено). Опознавать по этому тексту тоже нельзя — он зависит от версии и языка.
    ///
    /// Весь дамп приходит ОДНОЙ строкой, а не колонками, как кажется в терминале:
    /// переносы там рисует сам терминал. Ничто здесь не должно закладываться на построчность.
    ///
    /// Разбор посимвольный, а не «вычистить пробелы и позвать Convert.FromHexString»:
    /// последнее выделило бы ещё восемьсот килобайт поверх почти двух уже имеющихся.
    /// </remarks>
    private static byte[]? ParseHexDump(string text, string attribute)
    {
        var body = Body(text, attribute);
        if (body is null)
            return null;

        var bytes = new List<byte>(body.Length / 2);
        var half = -1;

        foreach (var ch in body)
        {
            if (char.IsWhiteSpace(ch))
                continue;

            var nibble = HexValue(ch);
            if (nibble < 0)
                return null;

            if (half < 0)
            {
                half = nibble;
                continue;
            }

            bytes.Add((byte)((half << 4) | nibble));
            half = -1;
        }

        // Нечётное число полубайтов означает, что дамп оборвался на середине.
        return half < 0 && bytes.Count > 0 ? [.. bytes] : null;
    }

    /// <summary>Значение атрибута, занимающее одну строку, — например путь из Picture.</summary>
    private static string? ParseSingleValue(string text, string attribute)
    {
        var body = Body(text, attribute)?.Trim();
        return string.IsNullOrEmpty(body) ? null : body;
    }

    /// <summary>Всё после заголовка «Атрибут:», или null, если заголовок не тот.</summary>
    private static string? Body(string text, string attribute)
    {
        var breakAt = text.IndexOf('\n');
        if (breakAt < 0)
            return null;

        var header = text.AsSpan(0, breakAt).Trim();
        return header.SequenceEqual(attribute + ":") ? text[(breakAt + 1)..] : null;
    }

    private static int HexValue(char ch) => ch switch
    {
        >= '0' and <= '9' => ch - '0',
        >= 'a' and <= 'f' => ch - 'a' + 10,
        >= 'A' and <= 'F' => ch - 'A' + 10,
        _ => -1,
    };

    /// <summary>
    /// Приводит что угодно к PNG не крупнее <paramref name="maxSide"/> по длинной стороне.
    /// Возвращает null, если декодировать не удалось.
    /// </summary>
    /// <remarks>
    /// Перекодирование обязательно, а не для красоты: атрибут JPEGPhoto вопреки имени
    /// часто содержит TIFF — байты начинаются с 4d4d002a, то есть «MM\0*». Android его
    /// не декодирует, и непреобразованная картинка была бы у соседа пустым прямоугольником
    /// БЕЗ ЕДИНОЙ ОШИБКИ, что легко спутать с «ещё не сделано».
    ///
    /// CGImageSource, а не UIGraphicsImageRenderer: миниатюра строится прямо при
    /// декодировании, с прореживанием, и полноразмерный битмап в память не попадает вовсе —
    /// а исходник тут бывает под мегабайт. Заодно CreateThumbnailWithTransform применяет
    /// поворот из EXIF, иначе картинка приехала бы лежащей на боку.
    ///
    /// scale: 1 обязателен. По умолчанию взялся бы масштаб экрана, и вместо запрошенных
    /// 128 пикселей вернулось бы 256.
    ///
    /// PNG, а не JPEG: у картинок учётных записей бывает прозрачность — стандартные
    /// из /Library/User Pictures как раз такие, — а JPEG залил бы её чёрным.
    /// </remarks>
    private static byte[]? ToPng(byte[] raw, int maxSide)
    {
        try
        {
            using var data = NSData.FromArray(raw);
            using var source = CGImageSource.FromData(data);
            if (source is null)
                return null;

            var options = new CGImageThumbnailOptions
            {
                MaxPixelSize = maxSide,
                CreateThumbnailFromImageAlways = true,
                CreateThumbnailWithTransform = true,
            };

            using var thumbnail = source.CreateThumbnail(0, options);
            if (thumbnail is null)
                return null;

            using var image = UIImage.FromImage(thumbnail, scale: 1, UIImageOrientation.Up);
            using var png = image.AsPNG();
            return png?.ToArray();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

#elif WINDOWS

    private const string AccountPictureKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users";

    /// <summary>
    /// Порядок перебора размеров: сперва ближайшее к 192 пикселям сверху.
    /// </summary>
    /// <remarks>
    /// 192 с запасом хватает кружку в 36 точек при двукратном масштабе, а файл такого
    /// размера обычно весит десятки килобайт, то есть проходит проверку ядра как есть.
    /// Размер именно ВЫБИРАЕТСЯ, а не подгоняется: перекодировать на Windows нечем,
    /// своего декодера у нас нет и заводить его ради аватара не стоит.
    /// </remarks>
    private static readonly string[] Preferred =
    [
        "Image192", "Image240", "Image208", "Image96",
        "Image448", "Image1080", "Image64", "Image48", "Image40", "Image32",
    ];

    private static byte[]? FetchWindows()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value;

            if (string.IsNullOrEmpty(sid))
                return null;

            return FromRegistry(sid) ?? FromPublicFolder(sid);
        }
        catch (Exception e) when (e is System.Security.SecurityException
                                      or UnauthorizedAccessException
                                      or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Отсутствие ключа — норма, а не ошибка: человек просто не ставил картинку.
    /// </summary>
    private static byte[]? FromRegistry(string sid)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"{AccountPictureKey}\{sid}");
        if (key is null)
            return null;

        foreach (var name in Preferred)
        {
            if (key.GetValue(name) is not string path)
                continue;

            // Промах — повод взять следующий размер, а не сдаться: запись в реестре
            // может пережить сам файл.
            var bytes = ReadIfSane(path);
            if (bytes is not null)
                return bytes;
        }

        return null;
    }

    private static byte[]? FromPublicFolder(string sid)
    {
        var root = Path.Combine(
            Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public",
            "AccountPictures",
            sid);

        if (!Directory.Exists(root))
            return null;

        // Только png и jpg: *.accountpicture-ms — контейнер, а не картинка,
        // и проверку сигнатуры в ядре он всё равно не прошёл бы.
        var candidates = Directory.EnumerateFiles(root, "*.png")
            .Concat(Directory.EnumerateFiles(root, "*.jpg"))
            .Select(x => new FileInfo(x))
            .Where(x => x.Length is > 0 and <= Avatars.MaxBytes)
            .OrderByDescending(x => x.Length);

        foreach (var candidate in candidates)
        {
            var bytes = ReadIfSane(candidate.FullName);
            if (bytes is not null)
                return bytes;
        }

        return null;
    }

    private static byte[]? ReadIfSane(string path)
    {
        try
        {
            if (!Path.IsPathRooted(path) || IsGenericSilhouette(path))
                return null;

            var file = new FileInfo(path);
            if (!file.Exists || file.Length is 0 or > Avatars.MaxBytes)
                return null;

            return File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Серый силуэт из %ProgramData% отвергаем и никогда не ищем: он одинаков у всех
    /// на свете, то есть строго менее содержателен, чем буква имени.
    /// </summary>
    private static bool IsGenericSilhouette(string path)
    {
        var generic = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft",
            "User Account Pictures");

        return path.StartsWith(generic, StringComparison.OrdinalIgnoreCase);
    }

#endif
}
