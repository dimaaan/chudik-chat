#if ANDROID
using Android.Content;
using Android.Provider;
using ChudikChat.App.Platforms.Android;
#endif

namespace ChudikChat.App.Services;

/// <summary>
/// Показывает принятое по кнопке «Открыть».
/// </summary>
/// <remarks>
/// На Windows и macOS это папка передачи в проводнике или Finder. Сам файл там
/// не открывается намеренно: «открыть» присланный .exe или .app значит запустить его,
/// а прислать может кто угодно из той же сети.
///
/// На Android одиночный файл открывается сразу — приложением, которое выбрано в системе
/// для его типа. На телефоне «открыть» значит посмотреть, и папка с единственным файлом
/// была бы лишним шагом. Принятая папка (их присылают с компьютеров) показывается
/// в «Файлах», и туда же уходят файлы, которые напрямую не открыть, — подробности
/// в <c>ShownInFolder</c>.
///
/// <c>file://</c> на Android не годится ни для того, ни для другого: с Android 7 такой
/// адрес в интенте — это FileUriExposedException. Кнопка так и молчала: ошибка уходила
/// в строку состояния, а её в узкой раскладке с открытой перепиской не видно.
/// </remarks>
public static class ReceivedLauncher
{
    /// <param name="destination">Папка передачи — <c>TransferProgress.DestinationPath</c>.</param>
    public static Task OpenAsync(string destination)
    {
#if ANDROID
        Open(destination);
        return Task.CompletedTask;
#else
        return Launcher.Default.OpenAsync(new Uri($"file://{destination}"));
#endif
    }

#if ANDROID
    private const string ExternalStorageAuthority = "com.android.externalstorage.documents";

    /// <summary>
    /// Типы, которые не открываются напрямую, а сразу показываются в папке.
    /// </summary>
    /// <remarks>
    /// Беда у обоих одна: интент уходит, что-то открывается и тут же закрывается,
    /// и человек не видит ничего — ни файла, ни ошибки.
    ///
    /// application/octet-stream — это незнакомое расширение: так поставщик называет всё,
    /// чего нет в таблице типов. На телефонах с сервисами Google этот тип забирает импорт
    /// карт Google Кошелька.
    ///
    /// .apk ставит только установщик, а он отказывает приложениям без
    /// REQUEST_INSTALL_PACKAGES — у Чудика этого разрешения нет, и заводить его ради
    /// присланного кем угодно из сети не за что. В «Файлах» пакет виден, и поставить его
    /// оттуда человек может сам, со всеми предупреждениями системы.
    /// </remarks>
    private static readonly HashSet<string> ShownInFolder =
    [
        "application/octet-stream",
        "application/vnd.android.package-archive",
    ];

    /// <remarks>
    /// Каждая следующая попытка — на случай, когда не вышла предыдущая. Файл напрямую
    /// не открыть или нечем — показываем его папку: в «Файлах» его хотя бы видно, и там же
    /// им можно поделиться. Папку так не открыть — она вне основного хранилища или «Файлы»
    /// заменены чем-то, что таких адресов не понимает, — показываем «Загрузки» целиком,
    /// штатным интентом DownloadManager. Не вышло и это — исключение уходит наверх,
    /// в строку состояния.
    /// </remarks>
    private static void Open(string destination)
    {
        if (SingleFile(destination) is { } file && FileIntent(file) is { } view && TryStart(view))
            return;

        if (FolderIntent(destination) is { } folder && TryStart(folder))
            return;

        Start(new Intent(global::Android.App.DownloadManager.ActionViewDownloads));
    }

    /// <summary>
    /// Путь к файлу, если передача — один файл. null, если это папка.
    /// </summary>
    /// <remarks>
    /// Спрашиваем у диска: вид передачи до кнопки не доезжает, а папка передачи создаётся
    /// пустой и принадлежит ей одной, так что единственный файл в ней — это и есть
    /// присланное.
    /// </remarks>
    private static string? SingleFile(string destination)
    {
        try
        {
            var entries = Directory.GetFileSystemEntries(destination);
            return entries.Length == 1 && File.Exists(entries[0]) ? entries[0] : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Intent? FileIntent(string path)
    {
        var context = global::Android.App.Application.Context;

        global::Android.Net.Uri? uri;
        try
        {
            uri = AndroidX.Core.Content.FileProvider.GetUriForFile(
                context, ReceivedFilesProvider.Authority, new Java.IO.File(path));
        }
        catch (Java.Lang.IllegalArgumentException)
        {
            // Файл вне путей из received_paths.xml.
            return null;
        }

        if (uri is null)
            return null;

        // Тип называет сам поставщик, по расширению.
        var type = context.ContentResolver?.GetType(uri);
        if (type is null || ShownInFolder.Contains(type))
            return null;

        return new Intent(Intent.ActionView)
            .SetDataAndType(uri, type)
            .AddFlags(ActivityFlags.GrantReadUriPermission);
    }

    /// <summary>
    /// Папка как документ ExternalStorageProvider — того поставщика, через которого
    /// «Файлы» и видят общее хранилище. null, если она лежит вне основного хранилища.
    /// </summary>
    /// <remarks>
    /// Идентификатор документа — «primary:» и путь от корня хранилища, того же каталога,
    /// от которого отсчитаны «Загрузки» в <see cref="PlatformEnvironment.DownloadRoot"/>.
    /// Разрешений нам на это не нужно: читать папку будут «Файлы», у них доступ свой.
    /// </remarks>
    private static Intent? FolderIntent(string path)
    {
        var storage = global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
        if (string.IsNullOrEmpty(storage))
            return null;

        var relative = Path.GetRelativePath(storage, path);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return null;

        var uri = DocumentsContract.BuildDocumentUri(ExternalStorageAuthority, $"primary:{relative}");
        return new Intent(Intent.ActionView)
            .SetDataAndType(uri, DocumentsContract.Document.MimeTypeDir);
    }

    private static bool TryStart(Intent intent)
    {
        try
        {
            Start(intent);
            return true;
        }
        catch (ActivityNotFoundException)
        {
            return false;
        }
    }

    /// <remarks>
    /// Из текущей активности, если она есть: тогда открытое встаёт поверх Чудика,
    /// и «Назад» возвращает к переписке. Без активности запуск из контекста
    /// приложения требует нового task.
    /// </remarks>
    private static void Start(Intent intent)
    {
        if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity is { } activity)
        {
            activity.StartActivity(intent);
            return;
        }

        intent.AddFlags(ActivityFlags.NewTask);
        global::Android.App.Application.Context.StartActivity(intent);
    }
#endif
}
