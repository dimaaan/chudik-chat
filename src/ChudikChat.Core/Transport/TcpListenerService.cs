using System.Net;
using System.Net.Sockets;
using ChudikChat.Core.Wire;

namespace ChudikChat.Core.Transport;

/// <summary>
/// Приём входящих соединений. Один слушатель покрывает все интерфейсы.
/// </summary>
public sealed class TcpListenerService : IDisposable
{
    private readonly Socket _socket;
    private readonly SemaphoreSlim _slots = new(ProtocolConstants.MaxConcurrentInbound);

    public TcpListenerService()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        // Порт эфемерный: фиксированный не дал бы запустить два экземпляра на одной машине,
        // а это основной стенд для отладки. Фактический порт едет в ANNOUNCE.
        // ReuseAddress на TCP сознательно не ставим — на Windows он позволяет чужому
        // процессу перехватить порт.
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        _socket.Listen(128);

        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
    }

    public int Port { get; }

    public async Task RunAsync(Func<Socket, CancellationToken, Task> handle, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Ждём слот до Accept, а не после: иначе очередь принятых сокетов растёт без предела.
            await _slots.WaitAsync(ct).ConfigureAwait(false);

            Socket client;
            try
            {
                client = await _socket.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                _slots.Release();
                break;
            }
            catch
            {
                _slots.Release();
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await handle(client, ct).ConfigureAwait(false);
                }
                catch
                {
                    // Сбой одного собеседника не должен ронять приём остальных.
                }
                finally
                {
                    client.Dispose();
                    _slots.Release();
                }
            }, CancellationToken.None);
        }
    }

    public void Dispose()
    {
        _socket.Dispose();
        _slots.Dispose();
    }
}
