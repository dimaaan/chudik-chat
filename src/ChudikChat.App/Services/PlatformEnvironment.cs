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
    /// </remarks>
    public static string DownloadRoot()
    {
#if ANDROID
        var external = Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
        var root = external ?? FileSystem.AppDataDirectory;
        return Path.Combine(root, "Принято");
#else
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, "Downloads", "ChudikChat");
#endif
    }

    /// <summary>Можно ли отправлять папки. На Android — нет: обход дерева там требует SAF.</summary>
    public static bool CanSendFolders =>
#if ANDROID
        false;
#else
        true;
#endif
}
