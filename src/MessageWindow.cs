using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace FramePilot;

internal sealed class MessageWindow : Window
{
    private MessageWindow(string title, string message, string confirmText, bool confirmation)
    {
        Title = title;
        Width = 500;
        SizeToContent = SizeToContent.Height;
        MinHeight = 190;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#111924");
        Foreground = Brush.Parse("#EDF2F7");
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI, Arial");

        var panel = new StackPanel { Margin = new Thickness(26), Spacing = 20 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = Brush.Parse("#AAB6C6"), LineHeight = 22 });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        if (confirmation)
        {
            var cancel = new Button { Content = "Отмена", MinWidth = 100, Background = Brush.Parse("#1A2635"), Foreground = Brushes.White, CornerRadius = new CornerRadius(8) };
            cancel.Click += (_, _) => Close(false);
            buttons.Children.Add(cancel);
        }
        var confirm = new Button { Content = confirmText, MinWidth = 110, Background = Brush.Parse("#65E1B4"), Foreground = Brush.Parse("#082019"), FontWeight = FontWeight.SemiBold, CornerRadius = new CornerRadius(8) };
        confirm.Click += (_, _) => Close(true);
        buttons.Children.Add(confirm);
        panel.Children.Add(buttons);
        Content = panel;
    }

    public static async Task Show(Window owner, string title, string message)
    {
        var window = new MessageWindow(title, message, "Понятно", false);
        await window.ShowDialog<bool>(owner);
    }

    public static async Task<bool> Confirm(Window owner, string title, string message, string confirmText = "Продолжить")
    {
        var window = new MessageWindow(title, message, confirmText, true);
        return await window.ShowDialog<bool>(owner);
    }
}
