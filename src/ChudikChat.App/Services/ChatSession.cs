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

    /// <summary>
    /// Картинка учётной записи операционной системы.
    /// </summary>
    /// <remarks>
    /// Lazy, а не поле: чтение форкает процесс на macOS и ходит в реестр на Windows.
    /// Конструктор сессии исполняется при разрешении зависимостей на потоке UI —
    /// делать это там значит подвесить окно на открытии. В StartAsync это тоже не место:
    /// затвор запуска держался бы всё это время без всякой пользы.
    ///
    /// Lazy к тому же даёт идемпотентность даром: сколько бы раз ни спросили,
    /// процесс запустится один раз.
    /// </remarks>
    private readonly Lazy<Task<byte[]?>> _avatar = new(() => Task.Run(AccountPicture.Fetch));

    public ChatSession()
    {
        DeviceNames.LocalProvider = PlatformEnvironment.DeviceName;

        Engine = new ChatEngine
        {
            DownloadRoot = PlatformEnvironment.DownloadRoot(),
        };
    }

    public ChatEngine Engine { get; }

    /// <summary>Картинка учётной записи, добытая в фоне. null, если её нет.</summary>
    public Task<byte[]?> LocalAvatarAsync() => _avatar.Value;

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
