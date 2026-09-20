namespace ChudikChat.Core.Model;

/// <summary>Операционная система устройства. Всё, что не из этого списка, — <see cref="Unknown"/>.</summary>
public enum PeerPlatform
{
    /// <summary>Собеседник старой сборки, Linux или что-то ещё. Иконки в списке не будет.</summary>
    Unknown,

    Windows,

    MacOs,

    Android,
}

/// <summary>
/// Операционная система в списке пиров: как узнать свою и как понять чужую.
/// </summary>
/// <remarks>
/// Та же роль, что у <see cref="DeviceNames"/> для имени и у <see cref="Avatars"/> для
/// картинки: своё значение добыть, чужое обезвредить. Обезвреживание здесь дешёвое —
/// значение никогда не показывается как текст, оно лишь выбирает одну из трёх картинок,
/// поэтому всё незнакомое просто становится <see cref="PeerPlatform.Unknown"/>.
/// </remarks>
public static class PeerPlatforms
{
    private const string WindowsWire = "windows";
    private const string MacOsWire = "macos";
    private const string AndroidWire = "android";

    /// <summary>
    /// Платформа этой машины. Считается один раз: за время работы она не меняется.
    /// </summary>
    /// <remarks>
    /// В ядре, а не в голове приложения, в отличие от имени устройства и картинки
    /// учётной записи. Тем двум нужны системные настройки и декодер, а здесь хватает
    /// <see cref="OperatingSystem"/> — и консольный стенд получает верное значение даром.
    /// </remarks>
    public static PeerPlatform Local { get; } = Detect();

    /// <summary>Значение для провода. <see cref="PeerPlatform.Unknown"/> — это отсутствие поля.</summary>
    public static string? Wire(PeerPlatform platform) => platform switch
    {
        PeerPlatform.Windows => WindowsWire,
        PeerPlatform.MacOs => MacOsWire,
        PeerPlatform.Android => AndroidWire,
        _ => null,
    };

    /// <summary>
    /// Разбирает значение, пришедшее по сети. Незнакомое — <see cref="PeerPlatform.Unknown"/>,
    /// и это не ошибка: так выглядит и старая сборка, и платформа из будущей.
    /// </summary>
    public static PeerPlatform Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return PeerPlatform.Unknown;

        if (string.Equals(raw, WindowsWire, StringComparison.OrdinalIgnoreCase))
            return PeerPlatform.Windows;

        if (string.Equals(raw, MacOsWire, StringComparison.OrdinalIgnoreCase))
            return PeerPlatform.MacOs;

        if (string.Equals(raw, AndroidWire, StringComparison.OrdinalIgnoreCase))
            return PeerPlatform.Android;

        return PeerPlatform.Unknown;
    }

    /// <summary>
    /// Порядок проверок здесь имеет значение, и менять его нельзя.
    /// </summary>
    /// <remarks>
    /// Под Mac Catalyst <c>IsMacOS()</c> возвращает false, а <c>IsIOS()</c> — true:
    /// Catalyst для среды исполнения отдельная платформа, а не macOS. Проверяй мы одну
    /// <c>IsMacOS()</c> — собранное приложение объявляло бы себя «неизвестным», хотя
    /// консольный стенд на той же машине отвечал бы правильно. <c>IsIOS()</c> не
    /// проверяем вовсе: на Catalyst он истинен и увёл бы в ветку, которой у нас нет.
    ///
    /// Android идёт первым по той же причине: это отдельная платформа, и проверять её
    /// после чего бы то ни было — приглашение к ошибке.
    /// </remarks>
    private static PeerPlatform Detect()
    {
        if (OperatingSystem.IsAndroid())
            return PeerPlatform.Android;

        if (OperatingSystem.IsWindows())
            return PeerPlatform.Windows;

        if (OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS())
            return PeerPlatform.MacOs;

        return PeerPlatform.Unknown;
    }
}
