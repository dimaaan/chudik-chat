using System.Net;
using System.Text.Json.Serialization;
using ChudikChat.Core.Model;
using ChudikChat.Core.Wire;

namespace ChudikChat.Core.Discovery;

public enum DiscoveryKind
{
    /// <summary>«Я тут, отзовитесь» — при старте. В ответ приходят unicast-объявления.</summary>
    Hello,

    /// <summary>Периодическое объявление. Оно же признак «ещё жив».</summary>
    Announce,

    /// <summary>Прощание при выходе. Подсказка, а не основной механизм: основной — истечение по таймауту.</summary>
    Bye,
}

/// <summary>
/// Содержимое датаграммы обнаружения.
/// </summary>
/// <remarks>
/// IP-адреса здесь сознательно нет. Отправитель не знает, какой из его адресов виден
/// получателю, а самопровозглашённый адрес позволял бы перенаправлять чужие соединения.
/// Адрес берётся из заголовка датаграммы, из полезной нагрузки — только порт.
/// </remarks>
public sealed record DiscoveryDatagram
{
    public int Version { get; init; } = ProtocolConstants.Version;

    public required DiscoveryKind Kind { get; init; }

    public required PeerId PeerId { get; init; }

    public required string DisplayName { get; init; }

    public required int ListenPort { get; init; }

    /// <summary>
    /// Отпечаток картинки учётной записи — 32 символа, около полусотни байт в датаграмме.
    /// Благодаря ему картинка появляется сразу после обнаружения, а не после первого
    /// сообщения: сама картинка сюда не влезла бы, приём идёт в буфер 8 КБ.
    /// </summary>
    public string? AvatarTag { get; init; }
}

/// <summary>Кто мы для сети прямо сейчас.</summary>
public sealed record LocalBeacon(PeerId Peer, string DisplayName, int ListenPort, string? AvatarTag = null);

/// <summary>Замеченный пир. Адрес — из заголовка датаграммы.</summary>
public sealed record DiscoveryObservation(
    PeerId Peer,
    string DisplayName,
    IPAddress Address,
    int ListenPort,
    int InterfaceIndex,
    bool IsFarewell,
    string? AvatarTag = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(DiscoveryDatagram))]
internal sealed partial class DiscoveryJsonContext : JsonSerializerContext;
