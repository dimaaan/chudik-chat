namespace ChudikChat.Core;

/// <summary>
/// Пир прислал то, чего в протоколе быть не может. Соединение после этого закрывается:
/// разбираться с полусломанным собеседником дороже, чем начать заново.
/// </summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message)
    {
    }

    public ProtocolException(string message, Exception inner) : base(message, inner)
    {
    }
}
