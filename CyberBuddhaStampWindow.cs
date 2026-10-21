using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexPond;

public sealed class CyberBuddhaStampWindow : Window
{
    public CyberBuddhaStampWindow()
    {
        Title = "赛博大佛 · 源码印记";
        Width = 760;
        Height = 760;
        MinWidth = 620;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("#111B1A");
        Foreground = Brush("#D8E7C4");
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = "这尊电子佛祖以 ASCII 字符直接写在 CyberBuddhaSeal.cs 源码中。",
            Margin = new Thickness(0, 0, 0, 12),
            FontSize = 13,
            Foreground = Brush("#E9D7A4")
        });

        var stampText = new TextBox
        {
            Text = CyberBuddhaSeal.DescribeRunningBuild(),
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
            FontSize = 13,
            Foreground = Brush("#CDE2B0"),
            Background = Brush("#172522"),
            BorderBrush = Brush("#7F704D"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            IsUndoEnabled = false
        };
        Grid.SetRow(stampText, 1);
        root.Children.Add(stampText);

        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(new TextBlock
        {
            Text = "“保佑永无 BUG”是程序员玩笑，不是软件质量或安全保证。",
            FontSize = 10,
            Foreground = Brush("#A6B29D"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var close = new Button
        {
            Content = "收起印记",
            Padding = new Thickness(12, 6, 12, 6),
            Background = Brush("#435D4C"),
            Foreground = Brush("#F3D9A2"),
            BorderBrush = Brush("#A67A47"),
            BorderThickness = new Thickness(1)
        };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        footer.Children.Add(close);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
