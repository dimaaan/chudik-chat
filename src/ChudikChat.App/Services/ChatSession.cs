using ChudikChat.Core;
using ChudikChat.Core.Model;

namespace ChudikChat.App.Services;

/// <summary>
/// Движок на всё время работы приложения. Живёт отдельно от страниц и их жизненного цикла:
/// поворот экрана или сворачивание не должны рвать сокеты.
/// </summary>
public sealed class ChatSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private bool _started;

    public ChatSession()
    {
        DeviceNames.LocalProvider = PlatformEnvironment.DeviceName;

        Engine = new ChatEngine
        {
            DownloadRoot = PlatformEnvironment.DownloadRoot(),
        };
    }

    public ChatEngine Engine { get; }

    public async Task StartAsync()
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_started)
                return;

            await Engine.StartAsync().ConfigureAwait(false);
            _started = true;
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync().ConfigureAwait(false);
        _startGate.Dispose();
    }
}
