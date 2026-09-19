using System.Net;
using ChudikChat.Core;
using ChudikChat.Core.Model;
using ChudikChat.Core.Transfer;

// Консольный стенд движка. Нужен не для красоты: упакованное MSIX-приложение
// не может говорить само с собой через loopback, а две такие консоли — могут,
// поэтому протокол отлаживается здесь, а не на двух устройствах.

await using var engine = new ChatEngine();

if (args.Length > 0)
    engine.LocalDisplayName = args[0];

var names = new Dictionary<PeerId, string>();
PeerId? lastPeer = null;

engine.PeerAppeared += peer =>
{
    names[peer.Id] = peer.DisplayName;
    lastPeer ??= peer.Id;
    Write(ConsoleColor.Green, $"+ {peer.DisplayName} [{peer.Id.Short}] {peer.PrimaryEndpoint}");
};

engine.PeerUpdated += peer => names[peer.Id] = peer.DisplayName;

engine.PeerGone += id =>
{
    Write(ConsoleColor.DarkGray, $"- {Name(id)} [{id.Short}] ушёл");
    names.Remove(id);
};

engine.MessageReceived += message =>
{
    lastPeer = message.Peer;
    Write(ConsoleColor.Cyan, $"[{message.At:HH:mm:ss}] {Name(message.Peer)}: {message.Text}");
};

engine.Diagnostic += text => Write(ConsoleColor.DarkYellow, $"  ! {text}");

// Стенд принимает всё подряд: спрашивать в консоли посреди потока событий неудобно,
// а в приложении за это отвечает диалог подтверждения.
engine.TransferDecider = (offer, _) =>
{
    Write(ConsoleColor.Yellow,
        $"  ⇩ {offer.PeerName} шлёт «{offer.RootName}» ({offer.FileCount} файл(ов), {Size(offer.TotalBytes)}) — принимаю");

    return Task.FromResult(TransferDecision.Accept(engine.DownloadRoot));
};

engine.TransferChanged += progress =>
{
    switch (progress.State)
    {
        case TransferState.Completed when progress.Direction == TransferDirection.Incoming:
            Write(ConsoleColor.Green, $"  ⇩ «{progress.RootName}» принято в {progress.DestinationPath}");
            break;

        case TransferState.Completed:
            Write(ConsoleColor.Green, $"  ⇧ «{progress.RootName}» отправлено");
            break;

        case TransferState.Failed:
        case TransferState.Declined:
            Write(ConsoleColor.Red, $"  ✗ «{progress.RootName}»: {progress.Error}");
            break;

        case TransferState.Cancelled:
            Write(ConsoleColor.DarkGray, $"  ✗ «{progress.RootName}»: отменено");
            break;
    }
};

await engine.StartAsync();

Console.WriteLine($"Я: {engine.LocalDisplayName} [{engine.LocalId.Short}]");
Console.WriteLine($"Слушаю порт {engine.ListenPort}");
Console.WriteLine($"Приём в {engine.DownloadRoot}");
Console.WriteLine();
Console.WriteLine("  /to <ip:порт> <текст>   отправить по адресу");
Console.WriteLine("  /peers                  известные пиры");
Console.WriteLine("  /send <путь>            отправить файл или папку последнему собеседнику");
Console.WriteLine("  /name <имя>             сменить имя");
Console.WriteLine("  /quit                   выход");
Console.WriteLine("  <текст>                 отправить последнему собеседнику");
Console.WriteLine();

while (true)
{
    var line = Console.ReadLine();
    if (line is null || line.Equals("/quit", StringComparison.OrdinalIgnoreCase))
        break;

    if (line.Length == 0)
        continue;

    try
    {
        if (line.StartsWith("/to ", StringComparison.OrdinalIgnoreCase))
            await SendToAddressAsync(line[4..]);
        else if (line.Equals("/peers", StringComparison.OrdinalIgnoreCase))
            ListPeers();
        else if (line.StartsWith("/send ", StringComparison.OrdinalIgnoreCase))
            await SendPathAsync(line[6..].Trim().Trim('"'));
        else if (line.StartsWith("/name ", StringComparison.OrdinalIgnoreCase))
            Rename(line[6..].Trim());
        else
            await SendToLastAsync(line);
    }
    catch (Exception e)
    {
        Write(ConsoleColor.Red, $"  ! {e.Message}");
    }
}

async Task SendToAddressAsync(string rest)
{
    var split = rest.IndexOf(' ');
    if (split <= 0)
    {
        Write(ConsoleColor.Red, "  ! нужно: /to <ip:порт> <текст>");
        return;
    }

    if (!IPEndPoint.TryParse(rest[..split], out var target))
    {
        Write(ConsoleColor.Red, $"  ! не разбирается как адрес: {rest[..split]}");
        return;
    }

    var text = rest[(split + 1)..];
    var sent = await engine.SendTextToAsync(target, text);
    Report(sent, target.ToString());

    if (sent.State == MessageState.Delivered)
        lastPeer = sent.Peer;
}

async Task SendToLastAsync(string text)
{
    if (lastPeer is not { } peer)
    {
        Write(ConsoleColor.Red, "  ! пока некому писать, начните с /to");
        return;
    }

    Report(await engine.SendTextAsync(peer, text), Name(peer));
}

async Task SendPathAsync(string path)
{
    if (lastPeer is not { } peer)
    {
        Write(ConsoleColor.Red, "  ! пока некому отправлять, начните с /to");
        return;
    }

    if (Directory.Exists(path))
    {
        await engine.SendFolderAsync(peer, path);
        return;
    }

    if (File.Exists(path))
    {
        await engine.SendFileAsync(peer, path);
        return;
    }

    Write(ConsoleColor.Red, $"  ! не найдено: {path}");
}

void ListPeers()
{
    var peers = engine.KnownPeers;
    if (peers.Count == 0)
    {
        Console.WriteLine("  (пусто)");
        return;
    }

    foreach (var id in peers)
        Console.WriteLine($"  {Name(id)} [{id.Short}]");
}

void Rename(string name)
{
    engine.LocalDisplayName = name;
    Console.WriteLine($"  теперь я {engine.LocalDisplayName}");
}

void Report(ChatMessage message, string target)
{
    if (message.State == MessageState.Delivered)
        Write(ConsoleColor.DarkGreen, $"  → {target}: доставлено");
    else
        Write(ConsoleColor.Red, $"  → {target}: не доставлено");
}

string Name(PeerId id) => names.TryGetValue(id, out var name) ? name : id.Short;

static string Size(long value) => value switch
{
    >= 1L << 30 => $"{value / (double)(1L << 30):0.#} ГБ",
    >= 1L << 20 => $"{value / (double)(1L << 20):0.#} МБ",
    >= 1L << 10 => $"{value / (double)(1L << 10):0.#} КБ",
    _ => $"{value} Б",
};

static void Write(ConsoleColor color, string text)
{
    var previous = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(text);
    Console.ForegroundColor = previous;
}
