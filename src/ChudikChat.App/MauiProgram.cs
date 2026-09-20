using ChudikChat.App.Services;
using ChudikChat.App.ViewModels;
using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;

namespace ChudikChat.App;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseMauiCommunityToolkit()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// Отображения правятся до Build(): к этому моменту ни одного окна ещё нет,
		// и обработчик успеет встать раньше, чем заголовок поставят в первый раз.
		WindowTitle.FixEncoding();

		// Движок один на всё приложение и не зависит от жизненного цикла страниц.
		builder.Services.AddSingleton<ChatSession>();
		builder.Services.AddSingleton<MainViewModel>();
		builder.Services.AddSingleton<MainPage>();
		builder.Services.AddTransient<AppShell>();

		// Transient, в отличие от главной страницы: состояния у «О программе» нет,
		// и держать её живой между показами незачем.
		builder.Services.AddTransient<AboutPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
