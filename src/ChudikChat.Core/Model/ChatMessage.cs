namespace ChudikChat.Core.Model;

public enum MessageDirection
{
    Incoming,
    Outgoing,
}

public enum MessageState
{
    Sending,
    Delivered,
    Failed,
    Received,
}

/// <summary>
/// Сообщение живёт только в памяти: на диск не попадает ничего.
/// </summary>
public sealed record ChatMessage
{
    public required Guid Id { get; init; }

    /// <summary>Собеседник — и для входящих, и для исходящих.</summary>
    public required PeerId Peer { get; init; }

    public required MessageDirection Direction { get; init; }

    public required DateTimeOffset At { get; init; }

    public required string Text { get; init; }

    public MessageState State { get; init; } = MessageState.Received;
}
