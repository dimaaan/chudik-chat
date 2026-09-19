namespace ChudikChat.App.Services;

/// <summary>
/// Перетаскивание файлов из системного проводника в окно.
/// </summary>
/// <remarks>
/// Сделано на нативном обработчике, а не на <c>DropGestureRecognizer</c>: штатный
/// распознаватель MAUI до сих пор падает с NullReferenceException на файлах,
/// перетаскиваемых из проводника, и не отдаёт пути.
/// На платформах, где обработчик не реализован, метод ничего не делает — кнопки
/// выбора файла и папки остаются рабочим путём везде.
/// </remarks>
public static class FileDrop
{
    public static void Attach(VisualElement element, Action<IReadOnlyList<string>> onDropped)
    {
#if WINDOWS
        if (element.Handler?.PlatformView is not Microsoft.UI.Xaml.UIElement native)
            return;

        native.AllowDrop = true;

        native.DragOver += (_, e) =>
        {
            e.AcceptedOperation = e.DataView.Contains(global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)
                ? global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
                : global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;

            e.DragUIOverride.Caption = "Отправить";
            e.Handled = true;
        };

        native.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                return;

            var deferral = e.GetDeferral();
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();

                var paths = items
                    .Select(item => item.Path)
                    .Where(path => !string.IsNullOrEmpty(path))
                    .ToArray();

                if (paths.Length > 0)
                    onDropped(paths);
            }
            catch (Exception)
            {
                // Проводник мог отдать элемент без пути — например, файл из архива.
            }
            finally
            {
                deferral.Complete();
            }
        };
#else
        _ = element;
        _ = onDropped;
#endif
    }
}
