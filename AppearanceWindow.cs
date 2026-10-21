using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexPond;

public sealed class AppearanceWindow : Window
{
    private readonly Dictionary<PondTheme, RadioButton> _themeButtons = new();
    private readonly Dictionary<AccessorySlot, ComboBox> _accessorySelectors = new();
    private readonly PondSettings _settings;

    public AppearanceWindow(Window owner, PondSettings settings)
    {
        _settings = settings;
        Title = "池塘外观与装备";
        if (owner.IsVisible) Owner = owner;
        Width = 330;
        Height = 490;
        MinWidth = 310;
        MinHeight = 450;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var outer = new Border
        {
            Margin = new Thickness(5),
            Padding = new Thickness(3),
            Background = new SolidColorBrush(Color.FromRgb(33, 26, 24)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(128, 83, 49)),
            BorderThickness = new Thickness(2)
        };
        var surface = new Grid { Background = new SolidColorBrush(Color.FromRgb(221, 193, 145)) };
        surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
        surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.Child = surface;

        var header = new Grid { Background = new SolidColorBrush(Color.FromRgb(59, 41, 34)) };
        header.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) try { DragMove(); } catch (InvalidOperationException) { } };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleStack = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock { Text = "池塘外观与装备", FontSize = 15, FontWeight = FontWeights.Bold, Foreground = BrushColor("#F3D99E") });
        titleStack.Children.Add(new TextBlock { Text = "切换立即预览；主题与装备会自动保存", Margin = new Thickness(0, 1, 0, 0), FontSize = 9, Foreground = BrushColor("#C6A779") });
        header.Children.Add(titleStack);
        var close = MakeButton("完成", 9);
        close.Margin = new Thickness(0, 12, 9, 12);
        close.Click += (_, _) => Hide();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        surface.Children.Add(header);

        var content = new StackPanel { Margin = new Thickness(12, 10, 12, 8) };
        var contentScroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(contentScroll, 1);
        surface.Children.Add(contentScroll);
        content.Children.Add(SectionLabel("六套场景构图"));
        foreach (var theme in Enum.GetValues<PondTheme>())
        {
            var button = new RadioButton
            {
                Content = PondThemeCatalog.Label(theme),
                GroupName = "PondTheme",
                Tag = theme,
                IsChecked = settings.Theme == theme,
                Margin = new Thickness(2, 3, 2, 3),
                Padding = new Thickness(8, 6, 8, 6),
                Background = new SolidColorBrush(Color.FromRgb(205, 178, 130)),
                Foreground = BrushColor("#3E3B2F"),
                BorderBrush = BrushColor("#9B7548"),
                BorderThickness = new Thickness(1),
                FontSize = 10,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            button.Checked += (_, _) => ThemeSelected?.Invoke(theme);
            _themeButtons[theme] = button;
            content.Children.Add(button);
        }

        content.Children.Add(SectionLabel("固定装备槽"));
        AddAccessorySelector(content, "帽子", AccessorySlot.Hat, settings.EquippedHatId);
        AddAccessorySelector(content, "左岸装饰", AccessorySlot.LeftDecor, settings.LeftDecorId);
        AddAccessorySelector(content, "右岸装饰", AccessorySlot.RightDecor, settings.RightDecorId);
        var inputTracking = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "任务运行时统计键盘按下和鼠标按钮的匿名活动脉冲，只用于本轮鱼获稀有度。不会读取按键内容、鼠标位置或目标应用；关闭后不加成。",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 264
            },
            IsChecked = settings.TrackInputActivity,
            Margin = new Thickness(1, 7, 1, 3),
            FontSize = 9,
            Foreground = BrushColor("#51432F")
        };
        System.Windows.Automation.AutomationProperties.SetName(inputTracking, "本轮匿名键盘与鼠标活动计数；只统计按下事件，不读取或保存输入内容与位置");
        inputTracking.Checked += (_, _) => InputTrackingChanged?.Invoke(true);
        inputTracking.Unchecked += (_, _) => InputTrackingChanged?.Invoke(false);
        content.Children.Add(inputTracking);

        var footer = new TextBlock
        {
            Text = "原创像素装饰会随鱼获逐步解锁。匿名活动计数默认开启，可在上方关闭。",
            Margin = new Thickness(12, 3, 12, 8),
            FontSize = 9,
            Foreground = BrushColor("#61563F")
        };
        Grid.SetRow(footer, 2);
        surface.Children.Add(footer);
        Content = outer;
    }

    public event Action<PondTheme>? ThemeSelected;
    public event Action<AccessorySlot, string?>? AccessorySelected;
    public event Action<bool>? InputTrackingChanged;

    public void RefreshAccessories()
    {
        foreach (var (slot, selector) in _accessorySelectors)
        {
            var selectedId = slot switch
            {
                AccessorySlot.Hat => _settings.EquippedHatId,
                AccessorySlot.LeftDecor => _settings.LeftDecorId,
                _ => _settings.RightDecorId
            } ?? "";
            PopulateAccessorySelector(selector, slot, selectedId);
        }
    }

    private void AddAccessorySelector(Panel parent, string label, AccessorySlot slot, string? selectedId)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 9, Foreground = BrushColor("#51432F") });
        var selector = new ComboBox
        {
            Height = 26,
            FontSize = 9,
            Background = BrushColor("#F1DEB8"),
            Foreground = BrushColor("#35271E"),
            BorderBrush = BrushColor("#987449"),
            Padding = new Thickness(4, 2, 4, 2)
        };
        PopulateAccessorySelector(selector, slot, selectedId ?? "");
        selector.SelectionChanged += (_, _) =>
        {
            if (selector.SelectedItem is ComboBoxItem item)
                AccessorySelected?.Invoke(slot, string.IsNullOrEmpty(item.Tag as string) ? null : item.Tag as string);
        };
        Grid.SetColumn(selector, 1);
        row.Children.Add(selector);
        parent.Children.Add(row);
        _accessorySelectors[slot] = selector;
    }

    private void PopulateAccessorySelector(ComboBox selector, AccessorySlot slot, string selectedId)
    {
        selector.Items.Clear();
        selector.Items.Add(new ComboBoxItem { Content = "不装备", Tag = "" });
        var unlocked = new HashSet<string>(_settings.UnlockedAccessoryIds ?? ["straw-hat"], StringComparer.Ordinal);
        foreach (var item in AccessoryCatalog.All.Where(item => item.Slot == slot && unlocked.Contains(item.Id)))
            selector.Items.Add(new ComboBoxItem { Content = item.Name, Tag = item.Id });
        selector.SelectedItem = selector.Items.Cast<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Tag as string, selectedId, StringComparison.Ordinal)) ?? selector.Items[0] as ComboBoxItem;
    }

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        Margin = new Thickness(0, 3, 0, 3),
        FontSize = 10,
        FontWeight = FontWeights.Bold,
        Foreground = BrushColor("#3E513C")
    };

    private static Button MakeButton(string text, double fontSize) => new()
    {
        Content = text,
        Padding = new Thickness(8, 4, 8, 4),
        Background = BrushColor("#314B3D"),
        Foreground = BrushColor("#F4DFAF"),
        BorderBrush = BrushColor("#AE7A42"),
        BorderThickness = new Thickness(1),
        FontSize = fontSize,
        Cursor = System.Windows.Input.Cursors.Hand
    };

    private static SolidColorBrush BrushColor(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
