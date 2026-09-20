using ChudikChat.App.Services;
using AndroidSpecific = Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific;

namespace ChudikChat.App;

public partial class App : Application
{
	private readonly IServiceProvider _services;

	public App(IServiceProvider services)
	{
		InitializeComponent();
		_services = services;

		// Resize, а не умолчание MAUI. В режиме Pan Android сдвигает окно целиком,
		// лишь бы показать поле ввода, и всё, что выше поля, уходит за верхний край
		// экрана. В пустом чате там оказывается первое же сообщение: человек отправил,
		// ничего не увидел и решил, что не ушло. Resize вместо сдвига отдаёт странице
		// высоту за вычетом клавиатуры.
		//
		// Одного этого мало. MAUI рисует край в край на всех версиях Android, штатный
		// adjustResize там не работает вовсе, и высоту под клавиатуру считает сама по
		// оконным врезкам — но только если нижняя кромка разметки о клавиатуре знает.
		// Отсюда SafeAreaEdges на корневой сетке MainPage: без неё Resize спрячет поле
		// ввода под клавиатуру, а без Resize сама SafeAreaEdges не сработает.
		//
		// Ставится здесь, а не в [Activity]: значение свойства приложения всё равно
		// перезапишет софт-инпут окна при его создании.
		AndroidSpecific.Application.SetWindowSoftInputModeAdjust(
			this, AndroidSpecific.WindowSoftInputModeAdjust.Resize);
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var shell = _services.GetRequiredService<AppShell>();

		var window = new Window(shell)
		{
			Title = "Чудик",
		};

		// Штатный выход рассылает прощание, чтобы нас убрали из списков сразу,
		// не дожидаясь истечения таймаута.
		window.Destroying += OnWindowDestroying;

		return window;
	}

	private async void OnWindowDestroying(object? sender, EventArgs e)
	{
		try
		{
			await _services.GetRequiredService<ChatSession>().DisposeAsync();
		}
		catch (Exception)
		{
			// Закрытие приложения не должно падать из-за неудавшегося прощания.
		}
	}
}
