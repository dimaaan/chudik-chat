using ChudikChat.App.Services;

namespace ChudikChat.App;

public partial class App : Application
{
	private readonly IServiceProvider _services;

	public App(IServiceProvider services)
	{
		InitializeComponent();
		_services = services;
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
