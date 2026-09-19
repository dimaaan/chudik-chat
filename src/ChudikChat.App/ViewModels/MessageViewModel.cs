using ChudikChat.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChudikChat.App.ViewModels;

public partial class MessageViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial MessageState State { get; set; }

    public required Guid Id { get; init; }

    public required string Text { get; init; }

    public required DateTimeOffset At { get; init; }

    public required bool IsMine { get; init; }

    public string TimeText => At.ToLocalTime().ToString("HH:mm");

    public string StateText => IsMine
        ? State switch
        {
            MessageState.Sending => "отправляется",
            MessageState.Delivered => "доставлено",
            MessageState.Failed => "не доставлено",
            _ => string.Empty,
        }
        : string.Empty;

    public static MessageViewModel From(ChatMessage message) => new()
    {
        Id = message.Id,
        Text = message.Text,
        At = message.At,
        IsMine = message.Direction == MessageDirection.Outgoing,
        State = message.State,
    };
}
