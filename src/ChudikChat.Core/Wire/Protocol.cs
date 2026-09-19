using System.Text.Json.Serialization;
using ChudikChat.Core.Model;

namespace ChudikChat.Core.Wire;

public static class ProtocolConstants
{
    /// <summary>Версия формата. Едет в каждом кадре и в каждой датаграмме.</summary>
    public const int Version = 1;

    /// <summary>
    /// Потолок длины кадра. Без него пир присылает заголовок 0x7FFFFFFF
    /// и получатель пытается выделить два гигабайта.
    /// </summary>
    public const int MaxFrameBytes = 1024 * 1024;

    public const int DiscoveryPort = 45678;

    public const string MulticastGroup = "239.255.42.99";

    /// <summary>Сколько входящих соединений обслуживаем одновременно.</summary>
    public const int MaxConcurrentInbound = 32;
}

/// <summary>
/// Кадр управляющего обмена. Дискриминатор — поле "t".
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(IdentifyFrame), "id")]
[JsonDerivedType(typeof(TextFrame), "txt")]
[JsonDerivedType(typeof(AckFrame), "ack")]
[JsonDerivedType(typeof(ErrorFrame), "err")]
[JsonDerivedType(typeof(TransferOfferFrame), "offer")]
[JsonDerivedType(typeof(TransferDecisionFrame), "decision")]
[JsonDerivedType(typeof(TransferEntryFrame), "entry")]
[JsonDerivedType(typeof(TransferEndFrame), "end")]
public abstract record WireFrame
{
    public int Version { get; init; } = ProtocolConstants.Version;
}

/// <summary>
/// Первый кадр в любом соединении: кто звонит. До него никакие данные не принимаются.
/// </summary>
public sealed record IdentifyFrame : WireFrame
{
    public required PeerId PeerId { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Порт, на котором звонящий сам принимает входящие.</summary>
    public required int ListenPort { get; init; }
}

public sealed record TextFrame : WireFrame
{
    public required Guid MessageId { get; init; }

    public required DateTimeOffset SentAt { get; init; }

    public required string Text { get; init; }
}

/// <summary>
/// Подтверждение на конкретное сообщение. Честнее, чем «сокет ещё открыт».
/// </summary>
public sealed record AckFrame : WireFrame
{
    public required Guid MessageId { get; init; }
}

public sealed record ErrorFrame : WireFrame
{
    public required string Reason { get; init; }
}

/// <summary>
/// Предложение передачи. Несёт только сводку, а не полный список записей:
/// сводки хватает и на осмысленный вопрос пользователю, и на общий прогресс,
/// и на проверку свободного места, а дерево из ста тысяч файлов в один кадр всё равно не влезет.
/// Сами записи идут потоком следом.
/// </summary>
public sealed record TransferOfferFrame : WireFrame
{
    public required Guid TransferId { get; init; }

    /// <summary>Имя файла или верхней папки — то, что видит получатель в запросе.</summary>
    public required string RootName { get; init; }

    public required bool IsDirectory { get; init; }

    public required int EntryCount { get; init; }

    public required int FileCount { get; init; }

    public required long TotalBytes { get; init; }
}

public sealed record TransferDecisionFrame : WireFrame
{
    public required Guid TransferId { get; init; }

    public required bool Accepted { get; init; }

    public string? Reason { get; init; }
}

/// <summary>
/// Заголовок одной записи. Для папки <see cref="Size"/> равен нулю и байтов следом нет —
/// так переживают пустые папки.
/// </summary>
public sealed record TransferEntryFrame : WireFrame
{
    public required int Index { get; init; }

    public required string RelativePath { get; init; }

    public required long Size { get; init; }

    public required bool IsDirectory { get; init; }
}

public sealed record TransferEndFrame : WireFrame
{
    public required Guid TransferId { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WireFrame))]
internal sealed partial class WireJsonContext : JsonSerializerContext;
