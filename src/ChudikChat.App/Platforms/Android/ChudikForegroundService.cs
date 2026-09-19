using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net.Wifi;
using Android.OS;
using AndroidX.Core.App;

namespace ChudikChat.App.Platforms.Android;

/// <summary>
/// Служба переднего плана: держит уведомление и системные блокировки, пока приложение работает.
/// </summary>
/// <remarks>
/// Тип службы — <c>connectedDevice</c>, а не <c>dataSync</c>: у второго с API 35
/// лимит шесть часов в сутки, после чего система вызывает <c>onTimeout()</c>,
/// что для постоянно включённого чата неприемлемо.
/// Сокеты живут не здесь, а в общем коде: служба владеет только уведомлением и замками,
/// иначе соединения умирали бы вместе с ней при любой пересборке активити.
/// </remarks>
[Service(
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeConnectedDevice)]
public sealed class ChudikForegroundService : Service
{
    private const string ChannelId = "chudik-presence";
    private const int NotificationId = 7714;

    private WifiManager.MulticastLock? _multicastLock;
    private WifiManager.WifiLock? _wifiLock;
    private PowerManager.WakeLock? _wakeLock;

    public static void Start(Context context) =>
        context.StartForegroundService(new Intent(context, typeof(ChudikForegroundService)));

    public static void Stop(Context context) =>
        context.StopService(new Intent(context, typeof(ChudikForegroundService)));

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        CreateChannel();

        var builder = new NotificationCompat.Builder(this, ChannelId);
        builder.SetContentTitle("Чудик");
        builder.SetContentText("Виден в локальной сети");
        builder.SetSmallIcon(global::Android.Resource.Drawable.StatSysUploadDone);
        builder.SetOngoing(true);
        builder.SetPriority((int)NotificationPriority.Low);

        if (builder.Build() is { } notification)
        {
            // С API 34 тип обязан передаваться и в манифесте, и здесь.
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
                StartForeground(NotificationId, notification, ForegroundService.TypeConnectedDevice);
            else
                StartForeground(NotificationId, notification);
        }

        AcquireLocks();

        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        ReleaseLocks();
        base.OnDestroy();
    }

    private void AcquireLocks()
    {
        if (GetSystemService(WifiService) is WifiManager wifi)
        {
            // Главный замок: без него многоадресные кадры до приложения не доходят,
            // и телефон молча никого не видит, хотя на настольной машине всё работает.
            _multicastLock = wifi.CreateMulticastLock("chudik-discovery");
            _multicastLock?.SetReferenceCounted(true);
            _multicastLock?.Acquire();

            // Энергосбережение Wi-Fi добавляет десятки миллисекунд на пакет
            // и способно наглухо застопорить передачу файла.
            _wifiLock = wifi.CreateWifiLock(global::Android.Net.WifiMode.FullHighPerf, "chudik-transfer");
            _wifiLock?.SetReferenceCounted(true);
            _wifiLock?.Acquire();
        }

        if (GetSystemService(PowerService) is PowerManager power)
        {
            // При погасшем экране процессор засыпает, и передача зависает на середине.
            _wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "chudik:transfer");
            _wakeLock?.SetReferenceCounted(false);
            _wakeLock?.Acquire();
        }
    }

    private void ReleaseLocks()
    {
        TryRelease(() => _wakeLock?.Release());
        TryRelease(() => _wifiLock?.Release());
        TryRelease(() => _multicastLock?.Release());

        _wakeLock = null;
        _wifiLock = null;
        _multicastLock = null;
    }

    private static void TryRelease(Action release)
    {
        try
        {
            release();
        }
        catch (Java.Lang.RuntimeException)
        {
            // Замок мог быть уже отпущен системой.
        }
    }

    private void CreateChannel()
    {
        if (GetSystemService(NotificationService) is not NotificationManager manager)
            return;

        if (manager.GetNotificationChannel(ChannelId) is not null)
            return;

        var channel = new NotificationChannel(ChannelId, "Присутствие в сети", NotificationImportance.Low)
        {
            Description = "Пока приложение открыто, оно видно другим устройствам в сети",
        };

        manager.CreateNotificationChannel(channel);
    }
}
