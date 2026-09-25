#if WINDOWS
using System.Runtime.InteropServices;
using Microsoft.Maui.LifecycleEvents;
using Microsoft.UI.Windowing;
#endif

namespace ChudikChat.App.Services;

/// <summary>
/// Значок в области уведомлений Windows. Крестик прячет окно туда, а не завершает
/// Чудика: человек остаётся в сети и в списках у соседей.
/// </summary>
/// <remarks>
/// Штатного значка в трее у Windows App SDK нет, поэтому всё на Win32 — как и
/// заголовок окна в <see cref="WindowTitle"/>. Пакет ради одного значка и двух
/// пунктов меню принёс бы свои проекции WinRT, а поломки там видны не при сборке.
///
/// Сообщения значка приходят в собственное скрытое окно, а не в окно WinUI.
/// Окно WinUI — ANSI-окно, и на этом держится лечение заголовка. Подмена его
/// оконной процедуры через <c>SetWindowLongPtrW</c> переключает окно на Unicode,
/// и лечение пришлось бы выверять заново — ради значка рисковать им незачем.
/// <c>SetWindowSubclass</c> обошёл бы это, но живёт в comctl32 v6, а в манифесте
/// exe такой зависимости нет. Своё окно — обычное верхнеуровневое, а не окно
/// только для сообщений (<c>HWND_MESSAGE</c>): таким не приходят широковещательные
/// сообщения, а без <c>TaskbarCreated</c> значок не пережил бы перезапуск проводника.
///
/// Значок различается по номеру, а не по GUID. GUID Windows привязывает к пути exe:
/// отладочная сборка и установленная спорили бы за него, а второй экземпляр на той
/// же машине — основной стенд отладки — не получил бы значка вовсе.
///
/// Выход — только пункт «Завершить». Он закрывает окно по-настоящему, тем же путём,
/// каким раньше закрывал крестик: <c>Closed</c> у окна WinUI, за ним <c>Destroying</c>
/// в <see cref="App"/> и прощание. <c>Application.Quit()</c> ведёт мимо этого пути —
/// через <c>Application.Exit()</c>, который <c>Closed</c> у окон не обещает, а без
/// него не будет и прощания.
///
/// На остальных платформах метод ничего не делает.
/// </remarks>
public static class TrayIcon
{
    public static void Install(MauiAppBuilder builder)
    {
#if WINDOWS
        builder.ConfigureLifecycleEvents(events =>
            events.AddWindows(windows => windows.OnWindowCreated(Attach)));
#else
        _ = builder;
#endif
    }

#if WINDOWS
    private const string Tooltip = "Чудик";
    private const string ClassName = "ChudikTrayWindow";
    private const uint IconId = 1;
    private const uint CallbackMessage = WmApp + 1;
    private const int CommandOpen = 1;
    private const int CommandQuit = 2;

    // Указатель на делегат держит Windows, и сборщик мусора о нём не знает.
    // Без статического поля оконная процедура однажды исчезла бы из-под окна.
    private static readonly WndProc Procedure = WindowProcedure;

    private static Microsoft.UI.Xaml.Window? _window;
    private static IntPtr _trayWindow;
    private static IntPtr _icon;
    private static uint _taskbarCreated;
    private static bool _iconShown;
    private static bool _quitting;

    private static void Attach(Microsoft.UI.Xaml.Window window)
    {
        // Окно у Чудика одно, и значок ему нужен один.
        if (_window is not null)
            return;

        _trayWindow = CreateTrayWindow();
        if (_trayWindow == IntPtr.Zero)
            return;

        _window = window;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        ShowIcon();

        window.AppWindow.Closing += OnClosing;
        window.Closed += OnClosed;
    }

    private static void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // Без значка спрятанное окно было бы уже не вернуть: процесс жив, а открыть
        // нечем. Тогда пусть крестик завершает, как раньше.
        if (_quitting || !_iconShown)
            return;

        args.Cancel = true;
        sender.Hide();
    }

    private static void OnClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        // Процесс вот-вот выйдет, но значок сам не исчезнет: без NIM_DELETE он
        // провисит в трее, пока над ним не проведут мышью.
        HideIcon();

        DestroyWindow(_trayWindow);
        _trayWindow = IntPtr.Zero;

        if (_icon != IntPtr.Zero)
            DestroyIcon(_icon);
        _icon = IntPtr.Zero;
    }

    private static void Open()
    {
        if (_window is null)
            return;

        var appWindow = _window.AppWindow;
        appWindow.Show();

        // Закрыть могли и свёрнутое окно — с панели задач. Show вернёт его
        // таким же свёрнутым, и по щелчку в трее ничего не появится.
        if (appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();

        _window.Activate();
    }

    private static void Quit()
    {
        if (_window is null)
            return;

        _quitting = true;
        HideIcon();
        _window.Close();
    }

    private static IntPtr CreateTrayWindow()
    {
        var instance = GetModuleHandleW(null);

        var windowClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Procedure),
            hInstance = instance,
            lpszClassName = ClassName,
        };

        if (RegisterClassExW(ref windowClass) == 0)
            return IntPtr.Zero;

        return CreateWindowExW(
            WsExToolWindow, ClassName, Tooltip, WsPopup,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
    }

    private static IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (message == CallbackMessage)
            {
                // NOTIFYICON_VERSION_4: событие в младшем слове lParam, точка
                // для меню — в wParam, уже в экранных координатах.
                switch ((uint)(lParam.ToInt64() & 0xFFFF))
                {
                    case NinSelect:
                    case NinKeySelect:
                        Open();
                        break;

                    case WmContextMenu:
                        var point = wParam.ToInt64();
                        ShowMenu(hwnd, (short)(point & 0xFFFF), (short)((point >> 16) & 0xFFFF));
                        break;
                }

                return IntPtr.Zero;
            }

            // Проводник перезапустился: панель задач создана заново, и прежних
            // значков на ней нет.
            if (message == _taskbarCreated && _taskbarCreated != 0)
            {
                ShowIcon();
                return IntPtr.Zero;
            }
        }
        catch (Exception)
        {
            // Исключение, вылетевшее из оконной процедуры обратно в Windows,
            // роняет процесс целиком. Значок того не стоит.
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static void ShowMenu(IntPtr hwnd, int x, int y)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        try
        {
            AppendMenuW(menu, MfString, CommandOpen, "Открыть");
            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString, CommandQuit, "Завершить");

            // Жирным — то же, что делает щелчок по значку.
            SetMenuDefaultItem(menu, CommandOpen, 0);

            var flags = TpmReturnCmd | TpmNoNotify | TpmRightButton
                | (GetSystemMetrics(SmMenuDropAlignment) != 0 ? TpmRightAlign : TpmLeftAlign);

            // Классический рецепт, и обе его половины обязательны. Без
            // SetForegroundWindow меню не закрывается щелчком мимо, без WM_NULL
            // следом второй показ мелькает и тут же пропадает.
            SetForegroundWindow(hwnd);
            var command = TrackPopupMenuEx(menu, flags, x, y, hwnd, IntPtr.Zero);
            PostMessageW(hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case CommandOpen:
                    Open();
                    break;

                case CommandQuit:
                    Quit();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static void ShowIcon()
    {
        var previous = _icon;
        _icon = LoadIcon();

        var data = NewIconData();
        data.uFlags = NifMessage | NifIcon | NifTip | NifShowTip;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.szTip = Tooltip;

        // TaskbarCreated волен разослать кто угодно, и значок тогда цел:
        // добавить его второй раз не выйдет, но обновить можно.
        _iconShown = Shell_NotifyIconW(NimAdd, ref data) || Shell_NotifyIconW(NimModify, ref data);

        if (_iconShown)
        {
            data.uVersion = NotifyIconVersion4;
            Shell_NotifyIconW(NimSetVersion, ref data);
        }

        // Проводник держит свою копию иконки, так что прежнюю можно отпустить.
        if (previous != IntPtr.Zero)
            DestroyIcon(previous);
    }

    private static void HideIcon()
    {
        if (!_iconShown)
            return;

        var data = NewIconData();
        Shell_NotifyIconW(NimDelete, ref data);
        _iconShown = false;
    }

    private static NOTIFYICONDATAW NewIconData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _trayWindow,
        uID = IconId,
    };

    /// <summary>
    /// Иконка приложения размером под трей при текущем масштабе.
    /// </summary>
    /// <remarks>
    /// Файл кладёт рядом с exe сборка MAUI — тот же, из которого сделана иконка
    /// приложения, — а публикация и MSI забирают его вместе с остальным. Внутри
    /// одна картинка 64×64, так что размер приходится просить явно: иначе
    /// LoadImage отдал бы её как есть. Запасной путь — иконка из ресурсов exe:
    /// сборка вшивает туда тот же файл, но размер там берётся по масштабу
    /// на момент входа в систему, а не по текущему.
    /// </remarks>
    private static IntPtr LoadIcon()
    {
        var dpi = GetDpiForWindow(_trayWindow);
        var size = GetSystemMetricsForDpi(SmCxSmIcon, dpi == 0 ? 96 : dpi);

        var path = Path.Combine(AppContext.BaseDirectory, "appicon.ico");
        var icon = LoadImageW(IntPtr.Zero, path, ImageIcon, size, size, LrLoadFromFile);
        if (icon != IntPtr.Zero)
            return icon;

        if (Environment.ProcessPath is { } exe
            && ExtractIconExW(exe, 0, IntPtr.Zero, out var small, 1) > 0)
            return small;

        return IntPtr.Zero;
    }

    private const uint WmNull = 0x0000;
    private const uint WmContextMenu = 0x007B;
    private const uint WmApp = 0x8000;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;

    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 0x01;
    private const uint NifIcon = 0x02;
    private const uint NifTip = 0x04;
    private const uint NifShowTip = 0x80;
    private const uint NotifyIconVersion4 = 4;

    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;
    private const uint TpmLeftAlign = 0x0000;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmRightAlign = 0x0008;
    private const uint TpmNoNotify = 0x0080;
    private const uint TpmReturnCmd = 0x0100;

    private const int SmMenuDropAlignment = 40;
    private const int SmCxSmIcon = 49;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;
    private const uint WsPopup = 0x8000_0000;
    private const uint WsExToolWindow = 0x0000_0080;

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, nuint id, string? text);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int index, IntPtr large, out IntPtr small, uint count);
#endif
}
