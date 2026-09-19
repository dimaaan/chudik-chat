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

    public required PeerId Id { get; init; }

    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    public ObservableCollection<TransferViewModel> Transfers { get; } = [];

    public bool HasUnread => UnreadCount > 0;

    public string Initials
    {
        get
        {
            var trimmed = DisplayName.Trim();
            return trimmed.Length == 0 ? "?" : trimmed[..1].ToUpperInvariant();
        }
    }

    public void Add(MessageViewModel message)
    {
        Messages.Add(message);

        while (Messages.Count > MaxMessages)
            Messages.RemoveAt(0);
    }
}
