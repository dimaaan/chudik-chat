using Android.App;
using Android.Content.PM;
using Android.OS;
using ChudikChat.App.Platforms.Android;

namespace ChudikChat.App;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize
        | ConfigChanges.Orientation
        | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override async void OnResume()
    {
        base.OnResume();

        // Службу запускаем, пока приложение на виду: с Android 14 запуск службы
        // переднего плана из фона ограничен.
        try
        {
            await EnsureNotificationPermissionAsync();
            ChudikForegroundService.Start(this);
        }
        catch (Exception)
        {
            // Без службы приложение всё равно работает, пока открыто.
        }
    }

    protected override void OnDestroy()
    {
        try
        {
            ChudikForegroundService.Stop(this);
        }
        catch (Exception)
        {
            // Уходим в любом случае.
        }

        base.OnDestroy();
    }

    /// <summary>
    /// С API 33 уведомление без разрешения просто не показывается: служба работает,
    /// а пользователь видит приложение как зависшее и убивает его.
    /// </summary>
    private static async Task EnsureNotificationPermissionAsync()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33))
            return;

        var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
        if (status != PermissionStatus.Granted)
            await Permissions.RequestAsync<Permissions.PostNotifications>();
    }
}
