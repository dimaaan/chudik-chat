using System.Net;
using ChudikChat.Core.Model;
using ChudikChat.Core.Wire;

namespace ChudikChat.Core.Transport;

/// <summary>
/// Куда входящий обмен отдаёт разобранное. Реализация обязана быть неблокирующей:
/// методы вызываются из потока, обслуживающего сокет, и должны лишь класть событие в очередь.
/// </summary>
public interface IExchangeSink
{
    /// <summary>Собеседник представился. Адрес берётся из сокета, а не из кадра.</summary>
    void OnIdentified(PeerId peer, string displayName, IPEndPoint remote, int listenPort);

    void OnText(PeerId peer, Guid messageId, DateTimeOffset sentAt, string text);

    /// <summary>
    /// Предложена передача. С этого момента соединением распоряжается реализация:
    /// она спрашивает пользователя, отвечает решением и, если согласились, вычитывает поток.
    /// В отличие от остальных методов, этот блокирующий и длительный — таков и есть обмен.
    /// </summary>
    Task HandleTransferAsync(
        PeerId peer,
        string displayName,
        TransferOfferFrame offer,
        Stream stream,
        CancellationToken ct);
}
