using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CodexPond.Mac;

/// <summary>Shows the source-level ASCII Cyber Buddha mark on the macOS and other Avalonia builds.</summary>
public sealed class CyberBuddhaWindow : Window
{
    public CyberBuddhaWindow()
    {
        Title = "赛博大佛 · 源码印记";
        Width = 760;
        Height = 690;
        MinWidth = 620;
        MinHeight = 540;
        Background = Brush("#111B1A");
        Foreground = Brush("#D8E7C4");

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) },
            Margin = new Thickness(18)
        };
        root.Children.Add(new TextBlock
        {
            Text = "电子佛祖以 ASCII 字符直接保存在共享源码 CyberBuddhaSeal.cs 中。",
            Margin = new Thickness(0, 0, 0, 12),
            FontSize = 13,
            Foreground = Brush("#E9D7A4"),
            TextWrapping = TextWrapping.Wrap
        });
        var stamp = new TextBox
        {
            Text = CyberBuddhaSeal.DescribeRunningBuild(),
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Menlo, Consolas, monospace"),
            FontSize = 12,
            Foreground = Brush("#CDE2B0"),
            Background = Brush("#172522"),
            Padding = new Thickness(12)
        };
        var stampScroll = new ScrollViewer
        {
            Content = stamp,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        Grid.SetRow(stampScroll, 1);
        root.Children.Add(stampScroll);
        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            Margin = new Thickness(0, 12, 0, 0)
        };
        footer.Children.Add(new TextBlock
        {
            Text = "“保佑永无 BUG”是程序员玩笑；哈希不是数字签名，也不构成质量或安全保证。",
            FontSize = 10,
            Foreground = Brush("#A6B29D"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        var close = new Button { Content = "收起印记", Padding = new Thickness(12, 6), Background = Brush("#435D4C"), Foreground = Brush("#F3D9A2") };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        footer.Children.Add(close);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
    }

    private static SolidColorBrush Brush(string color) => new(Color.Parse(color));
}
