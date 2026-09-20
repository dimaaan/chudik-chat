#if MACCATALYST
using System.Runtime.InteropServices;
#endif

namespace ChudikChat.App.Services;

/// <summary>
/// Места, где платформы расходятся достаточно, чтобы это нельзя было спрятать в ядре.
/// </summary>
public static class PlatformEnvironment
{
    /// <summary>
    /// Имя устройства. На Android <see cref="Environment.MachineName"/> возвращает
    /// «localhost», поэтому имя берётся из системных настроек.
    /// </summary>
    public static string DeviceName()
    {
#if ANDROID
        var resolver = Android.App.Application.Context.ContentResolver;

        var name = resolver is null
            ? null
            : Android.Provider.Settings.Global.GetString(resolver, "device_name")
              ?? Android.Provider.Settings.Secure.GetString(resolver, "bluetooth_name");

        return string.IsNullOrWhiteSpace(name) ? Android.OS.Build.Model ?? "Android" : name;
#else
        return Environment.MachineName;
#endif
    }

    /// <summary>
    /// Куда складывать принятое.
    /// </summary>
    /// <remarks>
    /// На Android это папка приложения во внешнем хранилище: обычные пути System.IO,
    /// никаких разрешений, целые деревья — и всё стирается при удалении приложения,
    /// что ровно соответствует обещанию ничего не хранить.
    ///
    /// На Mac Catalyst домашнюю папку приходится добывать отдельно: подробности
    /// в <see cref="HomeOutsideContainer"/>.
    /// </remarks>
    public static string DownloadRoot()
    {
#if ANDROID
        var external = Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
        var root = external ?? FileSystem.AppDataDirectory;
        return Path.Combine(root, "Принято");
#elif MACCATALYST
        var profile = HomeOutsideContainer() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, "Downloads", "ChudikChat");
#else
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, "Downloads", "ChudikChat");
#endif
    }

#if MACCATALYST
    /// <summary>
    /// Настоящая домашняя папка пользователя, /Users/имя — в обход контейнера.
    /// Возвращает null, если добыть её не удалось.
    /// </summary>
    /// <remarks>
    /// <see cref="Environment.SpecialFolder.UserProfile"/> под Mac Catalyst — это
    /// NSHomeDirectory(), то есть контейнер приложения:
    /// ~/Library/Containers/com.chudik.chat/Data. Контейнер Catalyst выдаёт всем
    /// приложениям, а не только песочным, поэтому отсутствие
    /// com.apple.security.app-sandbox ничего не меняет — принятые файлы уезжали бы
    /// туда, где пользователь их в Finder не найдёт.
    ///
    /// getpwuid читает запись в базе учётных записей и отдаёт pw_dir: путь мимо
    /// контейнера. Это штатный способ, а не обход защиты — прав он не добавляет.
    /// Если macOS попросит разрешение на «Загрузки», это ожидаемо и правильно.
    /// </remarks>
    private static string? HomeOutsideContainer()
    {
        try
        {
            var entry = getpwuid(getuid());
            if (entry == IntPtr.Zero)
                return null;

            var record = Marshal.PtrToStructure<PasswordEntry>(entry);
            var home = Marshal.PtrToStringUTF8(record.Directory);

            // Пустой или относительный путь означал бы, что раскладка struct passwd
            // разъехалась с нашей. Лучше вернуться к контейнеру, чем писать наугад.
            return home is not null && Path.IsPathRooted(home) && Directory.Exists(home)
                ? home
                : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Раскладка struct passwd из pwd.h, 64-битная Darwin. Поля после
    /// <see cref="Directory"/> не нужны, но описаны: так видно, что смещение
    /// взято из заголовка, а не подобрано.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PasswordEntry
    {
        public IntPtr Name;
        public IntPtr Password;
        public uint UserId;
        public uint GroupId;
        public nint PasswordChange;
        public IntPtr Class;
        public IntPtr Gecos;
        public IntPtr Directory;
        public IntPtr Shell;
        public nint Expire;
        public uint Fields;
    }

    // DllImport, а не LibraryImport: генератор последнего требует AllowUnsafeBlocks
    // на весь проект, а включать небезопасный код ради двух вызовов в libc — перебор.
    [DllImport("libc")]
    private static extern IntPtr getpwuid(uint uid);

    [DllImport("libc")]
    private static extern uint getuid();
#endif

    /// <summary>Можно ли отправлять папки. На Android — нет: обход дерева там требует SAF.</summary>
    public static bool CanSendFolders =>
#if ANDROID
        false;
#else
        true;
#endif
}
