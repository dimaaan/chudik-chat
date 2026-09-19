#if WINDOWS
using System.Runtime.InteropServices;
#endif

namespace ChudikChat.App.Services;

/// <summary>
/// Заголовок окна на Windows приходится записывать самим: иначе на панели задач
/// и в Alt+Tab вместо «Чудик» видны вопросительные знаки.
/// </summary>
/// <remarks>
/// Окно WinUI 3 создаётся классом WinUIDesktopWin32WindowClass, и это окно ANSI:
/// <c>IsWindowUnicode</c> для него возвращает false. Значит любой вызов
/// <c>SetWindowTextW</c> проходит через переход «широкая строка → ANSI», и вот
/// там-то всё и ломается — причём дважды, поэтому и лечится в два приёма.
///
/// Первое — кодовая страница. На английской Windows это 1252, кириллицы в ней
/// нет, и «Чудик» превращается в «?????» — ровно то, что видно на панели задач.
/// Лечится строкой <c>activeCodePage</c> в Platforms/Windows/app.manifest.
///
/// Второе — размер буфера в самом переходе. Он отводится по числу символов,
/// из расчёта байт на символ, а в UTF-8 кириллическая буква занимает два:
/// в буфер на пять символов их влезает три, и «Чудик» становится «Чуд».
/// Тут уже ничей не баг, который можно обойти сверху: так обрежется и заголовок
/// от WinUI, и наш собственный <c>SetWindowTextW</c>. Проверено: обе записи
/// дают «Чуд». (Из чужого процесса та же запись проходит целиком — там длину
/// считает ядро, — и на это легко купиться при отладке снаружи.)
///
/// Поэтому заголовок ставится через <c>SetWindowTextA</c> уже готовыми байтами
/// UTF-8. Окно ANSI, кодовая страница UTF-8 — преобразовывать нечего, перехода
/// не возникает, строка доходит целиком. Первое лечение при этом обязательно:
/// без <c>activeCodePage</c> эти байты прочтутся как 1252 и выйдет каша.
///
/// Видимую надпись в шапке окна всё это не затрагивает: её WinUI рисует сам
/// и из своей, правильной строки. Портился только текст HWND — тот, что читают
/// панель задач, Alt+Tab и средства чтения с экрана.
///
/// На остальных платформах метод ничего не делает.
/// </remarks>
public static class WindowTitle
{
    public static void FixEncoding()
    {
#if WINDOWS
        // Дописываем к штатному отображению, а не заменяем его: наш обработчик
        // встаёт следом и перезаписывает уже испорченный заголовок. Срабатывает
        // и при создании окна, и на каждой последующей смене Title.
        Microsoft.Maui.Handlers.WindowHandler.Mapper.AppendToMapping(
            nameof(IWindow.Title),
            (handler, window) =>
            {
                if (handler.PlatformView is not Microsoft.UI.Xaml.Window native)
                    return;

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(native);
                if (hwnd == IntPtr.Zero)
                    return;

                // Если кодовая страница процесса вдруг не UTF-8, байты ниже
                // прочлись бы как попало. Лучше оставить всё как есть.
                if (GetACP() != CpUtf8)
                    return;

                var utf8 = System.Text.Encoding.UTF8.GetBytes((window.Title ?? string.Empty) + '\0');
                SetWindowTextA(hwnd, utf8);
            });
#endif
    }

#if WINDOWS
    private const uint CpUtf8 = 65001;

    // Строка передаётся байтами, а не string: CharSet.Ansi поручил бы перевод
    // маршалеру, а нам нужно именно UTF-8, и именно без чужих преобразований.
    [DllImport("user32.dll", EntryPoint = "SetWindowTextA")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowTextA(IntPtr hWnd, byte[] text);

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
#endif
}
