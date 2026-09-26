#if WINDOWS && !DEBUG
using System.Runtime.InteropServices;
#endif

namespace ChudikChat.App.Services;

/// <summary>
/// Выпускная сборка под Windows запускается в одном экземпляре: второй запуск
/// показывает окно первого и выходит.
/// </summary>
/// <remarks>
/// Отладочной сборки это не касается: два экземпляра на одной машине — основной
/// стенд отладки. Второй Чудик в выпуске ничего не дал бы человеку, кроме
/// второго значка в трее и второго себя в списках у соседей.
///
/// Первый экземпляр узнаётся по именованному мьютексу. Мьютекс здесь только флаг:
/// владеть им не нужно, важно лишь, есть ли объект с таким именем, а живёт он,
/// пока открыт хотя бы один описатель — то есть пока жив первый. Пространство
/// Local — это сеанс входа: у другого пользователя на той же машине свой рабочий
/// стол, свой трей и свой Чудик.
///
/// Показать окно второй просит сообщением скрытому окну значка из
/// <see cref="TrayIcon"/>: оно уже есть, верхнеуровневое, и FindWindow его
/// находит. Показывает первый сам, тем же путём, что и щелчок по значку, — со
/// снятием свёрнутости и активацией. Выйти на передний план ему разрешает второй:
/// его только что запустил человек, передний план сейчас за ним, и право это
/// передаётся через <c>AllowSetForegroundWindow</c>. Без этого Windows не пустила
/// бы чужое окно вперёд и ограничилась бы миганием кнопки на панели задач.
///
/// Окна значка может ещё не быть — первый только запускается — или уже не быть:
/// после «Завершить» процесс ещё рассылает прощание. Поэтому второй недолго ждёт:
/// появится окно — попросит показать, исчезнет мьютекс — запустится сам.
///
/// На остальных платформах метод ничего не делает: там второй запуск и так
/// возвращает к уже открытому приложению.
/// </remarks>
public static class SingleInstance
{
    /// <summary>
    /// В выпускной сборке под Windows возвращается, только если этот экземпляр
    /// первый, иначе завершает процесс. В остальных случаях просто возвращается.
    /// </summary>
    public static void Enforce()
    {
#if WINDOWS && !DEBUG
        var deadline = Environment.TickCount64 + WaitMilliseconds;

        while (true)
        {
            _flag = new Mutex(false, MutexName, out var createdNew);
            if (createdNew)
                return;

            _flag.Dispose();
            _flag = null;

            var window = FindWindowW(TrayIcon.ClassName, null);
            if (window != IntPtr.Zero && AskToShow(window))
                Environment.Exit(0);

            if (Environment.TickCount64 > deadline)
                Environment.Exit(0);

            Thread.Sleep(RetryMilliseconds);
        }
#endif
    }

#if WINDOWS && !DEBUG
    private const string MutexName = @"Local\com.chudik.chat";
    private const int WaitMilliseconds = 10_000;
    private const int RetryMilliseconds = 100;
    private const uint ShowTimeoutMilliseconds = 5_000;
    private const uint SmtoAbortIfHung = 0x0002;

    // Описатель держится до конца процесса: закроется он — исчезнет и флаг.
    private static Mutex? _flag;

    private static bool AskToShow(IntPtr window)
    {
        if (GetWindowThreadProcessId(window, out var process) != 0)
            AllowSetForegroundWindow(process);

        // SendMessage, а не PostMessage: так видно, показал ли первый окно. Он
        // отказывает, когда уже завершается, и тогда надо дождаться его выхода.
        var sent = SendMessageTimeoutW(
            window, TrayIcon.ShowMessage, IntPtr.Zero, IntPtr.Zero,
            SmtoAbortIfHung, ShowTimeoutMilliseconds, out var shown);

        return sent != IntPtr.Zero && shown != IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);
#endif
}
