using System.Collections.ObjectModel;
using ChudikChat.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChudikChat.App.ViewModels;

/// <summary>
/// Собеседник и вся переписка с ним. Всё — только в памяти: закрыли приложение, и ничего не осталось.
/// </summary>
public partial class PeerViewModel : ObservableObject
{
    /// <summary>Потолок истории в памяти. Старое вытесняется, на диск не попадает ничего.</summary>
    public const int MaxMessages = 500;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initials))]
    public partial string DisplayName { get; set; } = DeviceNames.Fallback;

    [ObservableProperty]
    public partial string Address { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    public partial int UnreadCount { get; set; }

    [ObservableProperty]
    public partial bool IsOnline { get; set; } = true;

    /// <summary>Картинка учётной записи собеседника. null — показываем кружок с буквой.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvatar))]
    public partial ImageSource? Avatar { get; set; }

    /// <summary>
    /// Операционная система собеседника. Unknown — иконки в строке нет.
    /// </summary>
    /// <remarks>
    /// Возни с отпечатком, как у картинки, здесь не требуется: генератор сравнивает
    /// новое значение со старым, и присвоение того же самого уведомления не поднимает.
    /// А приходит оно часто — объявления идут каждые четыре секунды.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWindows))]
    [NotifyPropertyChangedFor(nameof(IsMacOs))]
    [NotifyPropertyChangedFor(nameof(IsAndroid))]
    public partial PeerPlatform Platform { get; set; } = PeerPlatform.Unknown;

    public required PeerId Id { get; init; }

    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    public ObservableCollection<TransferViewModel> Transfers { get; } = [];

    public bool HasUnread => UnreadCount > 0;

    public bool HasAvatar => Avatar is not null;

    // Три отдельных признака, а не один преобразователь: в шаблоне строки лежат
    // три фигуры внахлёст, и каждой нужна своя привязка к видимости.
    public bool IsWindows => Platform == PeerPlatform.Windows;

    public bool IsMacOs => Platform == PeerPlatform.MacOs;

    public bool IsAndroid => Platform == PeerPlatform.Android;

    /// <summary>
    /// Отпечаток показываемой сейчас картинки. Не наблюдаемое: на экране его нет,
    /// он нужен только чтобы понять, изменилось ли что-нибудь.
    /// </summary>
    private string? _avatarTag;

    public string Initials
    {
        get
        {
            var trimmed = DisplayName.Trim();
            return trimmed.Length == 0 ? "?" : trimmed[..1].ToUpperInvariant();
        }
    }

    /// <summary>
    /// Обновляет картинку, только если сменился отпечаток.
    /// </summary>
    /// <remarks>
    /// Объявления приходят каждые четыре секунды, и новый ImageSource на каждое означал бы,
    /// что CollectionView заново загружает картинку в каждой строке — это видно глазом
    /// как мигание, и тем сильнее, чем длиннее список.
    ///
    /// Фабрика, а не готовый поток: StreamImageSource зовёт её заново при каждой загрузке —
    /// при переиспользовании строки, при смене темы, при возврате из фона. Один поток
    /// к тому моменту уже закрыт, и картинка пропала бы при первой же прокрутке.
    /// </remarks>
    public void ApplyAvatar(AvatarImage? image)
    {
        if (image?.Tag == _avatarTag)
            return;

        _avatarTag = image?.Tag;
        Avatar = image is null ? null : ImageSource.FromStream(image.OpenRead);
    }

    public void Add(MessageViewModel message)
    {
        Messages.Add(message);

        while (Messages.Count > MaxMessages)
            Messages.RemoveAt(0);
    }
}
