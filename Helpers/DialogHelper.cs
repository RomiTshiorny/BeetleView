using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BeetleView.Helpers;

/// <summary>
/// Small helpers around <see cref="ContentDialog"/> so call sites can stay
/// one-liners. Long messages are wrapped in a scrollable, selectable
/// <see cref="TextBlock"/> — handy when the body is a stack trace.
/// </summary>
internal static class DialogHelper
{
    public static Task ShowErrorAsync(XamlRoot xamlRoot, string title, string message)
        => ShowScrollableAsync(xamlRoot, title, message);

    public static Task ShowInfoAsync(XamlRoot xamlRoot, string title, string message)
        => ShowScrollableAsync(xamlRoot, title, message);

    private static Task ShowScrollableAsync(XamlRoot xamlRoot, string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 400,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
            },
            CloseButtonText = "OK",
            XamlRoot = xamlRoot,
        };
        return dialog.ShowAsync().AsTask();
    }
}
