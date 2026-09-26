using Android.App;
using Android.Content;

namespace ChudikChat.App.Platforms.Android;

/// <summary>
/// Раздаёт принятые файлы приложениям, которыми их открывают.
/// </summary>
/// <remarks>
/// Свой поставщик, а не тот, что заводит MAUI для Launcher и Share. У MAUI файл вне
/// папок приложения сначала копируется во временную, причём прямо в вызывающем потоке,
/// а принятое лежит в «Загрузках» — видео на пару гигабайт встало бы колом при нажатии.
/// Адрес и пути того поставщика вдобавок деталь MAUI, а не договорённость с нами.
///
/// Наружу поставщик закрыт: чужое приложение прочтёт только тот файл, адрес которого
/// ему выдан интентом с FLAG_GRANT_READ_URI_PERMISSION, и только его.
/// </remarks>
[ContentProvider(
    new[] { Authority },
    Exported = false,
    GrantUriPermissions = true)]
[MetaData("android.support.FILE_PROVIDER_PATHS", Resource = "@xml/received_paths")]
public sealed class ReceivedFilesProvider : AndroidX.Core.Content.FileProvider
{
    public const string Authority = "com.chudik.chat.received";
}
