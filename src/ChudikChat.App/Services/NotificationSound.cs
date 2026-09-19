#if WINDOWS
using System.Runtime.InteropServices;
#endif

namespace ChudikChat.App.Services;

/// <summary>
/// Звук при получении сообщения. Системный, а не свой.
/// </summary>
/// <remarks>
/// Своего звукового файла в ресурсах намеренно нет. Системный звук ничего
/// не весит, не тянет за собой проигрыватель и — главное — подчиняется тому,
/// что человек уже настроил: громкости, схеме звуков, режиму «не беспокоить».
/// Цена в том, что на разных ОС он разный, и это осознанный размен.
///
/// На Android звук идёт мимо уведомлений, и не по лени. Канал
/// <c>chudik-presence</c> из <c>ChudikForegroundService</c> создан с важностью
/// <c>Low</c>, то есть беззвучен по определению, а важность канала Android
/// после создания уже не меняет — на телефонах, где приложение стоит давно,
/// починить это нечем. Заводить второй канал значит завести и вторую запись
/// в шторке, а нужен был звук.
///
/// На платформах, которых тут нет, метод молчит.
/// </remarks>
public static class NotificationSound
{
    /// <summary>
    /// Пачка сообщений подряд не должна превращаться в очередь звонков.
    /// На Windows перезапуск звука поверх играющего даёт дёрганый обрубок,
    /// на Android — наложение. Одного звука на пачку достаточно: он сообщает,
    /// что пришло новое, а сколько именно — видно на экране.
    /// </summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);

    private static DateTime _lastPlayed = DateTime.MinValue;

    public static void Play()
    {
        var now = DateTime.UtcNow;
        if (now - _lastPlayed < Quiet)
            return;

        _lastPlayed = now;

        try
        {
#if WINDOWS
            // Псевдоним из схемы звуков, а не файл: это штатный звук мгновенного
            // сообщения, и человек может сменить или выключить его в настройках.
            //
            // SND_NODEFAULT обязателен. Без него выключенный звук события
            // Windows подменяет звуком по умолчанию — то есть мы перебили бы
            // прямо высказанное желание молчать.
            //
            // Возвращаемое значение не проверяется, и это не небрежность:
            // PlaySound отдаёт TRUE даже для заведомо несуществующего
            // псевдонима, так что отличить «сыграло» от «нет» по нему нельзя.
            PlaySound("Notification.IM", IntPtr.Zero, SndAsync | SndNoDefault | SndAlias);
#elif ANDROID
            var context = global::Android.App.Application.Context;

            var uri = global::Android.Media.RingtoneManager.GetActualDefaultRingtoneUri(
                context,
                global::Android.Media.RingtoneType.Notification);

            // Звук уведомления может быть выключен совсем — тогда тишина.
            if (uri is null)
                return;

            global::Android.Media.RingtoneManager.GetRingtone(context, uri)?.Play();
#elif MACCATALYST
            // Экземпляр живёт до конца работы приложения. Освобождать его сразу
            // после запуска нельзя: воспроизведение асинхронное, и звук оборвётся.
            _macSound ??= global::AudioToolbox.SystemSound.FromFile(
                global::Foundation.NSUrl.FromFilename("/System/Library/Sounds/Ping.aiff"));

            _macSound?.PlaySystemSound();
#endif
        }
        catch (Exception)
        {
            // Звук — украшение, и ронять из-за него переписку не за что.
            //
            // Перехват здесь обязателен, а не на всякий случай: движок глотает
            // исключения подписчиков сам, но этот код выполняется уже после
            // перехода в поток UI, и то укрытие сюда не достаёт.
        }
    }

#if WINDOWS
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndAlias = 0x0001_0000;

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string sound, IntPtr module, uint flags);
#endif

#if MACCATALYST
    private static global::AudioToolbox.SystemSound? _macSound;
#endif
}
