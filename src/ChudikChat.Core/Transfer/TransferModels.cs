using ChudikChat.Core.Model;

namespace ChudikChat.Core.Transfer;

public enum TransferDirection
{
    Incoming,
    Outgoing,
}

public enum TransferState
{
    /// <summary>Предложено, ждём решения получателя.</summary>
    Offered,

    Running,
    Completed,
    Declined,
    Cancelled,
    Failed,
}

/// <summary>Сводка по тому, что предстоит передать.</summary>
public sealed record TransferSummary(int EntryCount, int FileCount, long TotalBytes);

/// <summary>Одна запись на стороне отправителя.</summary>
public sealed record SourceEntry
{
    public required string RelativePath { get; init; }

    public required bool IsDirectory { get; init; }

    /// <summary>Предварительный размер. Настоящий берётся из открытого потока при отправке.</summary>
    public required long Size { get; init; }

    /// <summary>Открывает содержимое. У папки равно <c>null</c>.</summary>
    public Func<CancellationToken, ValueTask<Stream>>? Open { get; init; }
}

/// <summary>
/// Что отправляем. Абстракция нужна, чтобы ядро не зависело от того,
/// откуда взялись файлы: путь в файловой системе или что-то платформенное.
/// </summary>
public interface ITransferSource
{
    /// <summary>Имя файла или верхней папки.</summary>
    string RootName { get; }

    bool IsDirectory { get; }

    /// <summary>Предварительный обход: он нужен, чтобы получатель увидел объём в запросе.</summary>
    Task<TransferSummary> ScanAsync(CancellationToken ct);

    IAsyncEnumerable<SourceEntry> EnumerateAsync(CancellationToken ct);
}

/// <summary>Входящее предложение — то, что показывается пользователю в вопросе «принять?».</summary>
public sealed record IncomingTransferOffer
{
    public required Guid TransferId { get; init; }

    public required PeerId Peer { get; init; }

    public required string PeerName { get; init; }

    public required string RootName { get; init; }

    public required bool IsDirectory { get; init; }

    public required int EntryCount { get; init; }

    public required int FileCount { get; init; }

    public required long TotalBytes { get; init; }
}

/// <summary>Ответ на предложение.</summary>
public sealed record TransferDecision
{
    public required bool Accepted { get; init; }

    /// <summary>Папка, внутри которой будет создана отдельная папка этой передачи.</summary>
    public string? DestinationRoot { get; init; }

    public string? Reason { get; init; }

    public static TransferDecision Decline(string reason) => new() { Accepted = false, Reason = reason };

    public static TransferDecision Accept(string destinationRoot) =>
        new() { Accepted = true, DestinationRoot = destinationRoot };
}

/// <summary>Состояние передачи для UI. Поднимается не чаще десяти раз в секунду.</summary>
public sealed record TransferProgress
{
    public required Guid TransferId { get; init; }

    public required PeerId Peer { get; init; }

    public required TransferDirection Direction { get; init; }

    public required string RootName { get; init; }

    public required TransferState State { get; init; }

    public long TotalBytes { get; init; }

    public long TransferredBytes { get; init; }

    public int TotalEntries { get; init; }

    public int CompletedEntries { get; init; }

    /// <summary>Куда легло принятое. Заполняется только у входящих.</summary>
    public string? DestinationPath { get; init; }

    public string? Error { get; init; }

    public double Fraction => TotalBytes <= 0
        ? (State == TransferState.Completed ? 1 : 0)
        : Math.Clamp((double)TransferredBytes / TotalBytes, 0, 1);
}
