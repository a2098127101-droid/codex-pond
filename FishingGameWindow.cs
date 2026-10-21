using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexPond;

/// <summary>Small standalone fishing loop; it uses the same catalog, original sprites, seasons and local fish book.</summary>
public sealed class FishingGameWindow : Window
{
    private enum CastState { Idle, Waiting, Bite }

    private readonly PondSettings _settings;
    private readonly PondSettingsStore _settingsStore;
    private readonly FishBookStore _fishBook;
    private readonly PondEventScheduler _eventScheduler = new();
    private readonly Random _random = new();
    private readonly DispatcherTimer _timer;
    private readonly Image _sceneImage;
    private readonly PondRenderer _renderer;
    private readonly TextBlock _status;
    private readonly TextBlock _environmentLabel;
    private readonly TextBlock _fishCountLabel;
    private readonly Button _castButton;
    private FishBookWindow? _fishBookWindow;
    private AppearanceWindow? _appearanceWindow;
    private CastState _castState;
    private DateTimeOffset _biteAt;
    private DateTimeOffset _nextBiteAt;
    private bool _closedForModeSwitch;

    public FishingGameWindow(PondSettings settings, PondSettingsStore settingsStore, FishBookStore fishBook, bool launchedFromSteam = false)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _fishBook = fishBook;
        LaunchedFromSteam = launchedFromSteam;

        Title = "Codex Pond · 独立钓鱼";
        Width = 690;
        Height = 460;
        MinWidth = 600;
        MinHeight = 390;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(37, 44, 39));
        FontFamily = new FontFamily("Microsoft YaHei UI");
        ResizeMode = ResizeMode.CanMinimize;
        ShowInTaskbar = true;

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(92) });
        Content = root;

        var header = new Grid { Margin = new Thickness(2, 0, 2, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock { Text = "池边慢钓", FontSize = 19, FontWeight = FontWeights.Bold, Foreground = Brush("#F1D9A4") });
        titleStack.Children.Add(new TextBlock { Text = "听水声，等一尾鱼" , FontSize = 10, Foreground = Brush("#B9C2A5") });
        header.Children.Add(titleStack);
        var headerActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        headerActions.Children.Add(MakeButton("鱼册", (_, _) => OpenFishBook()));
        headerActions.Children.Add(MakeButton("外观", (_, _) => OpenAppearance()));
        headerActions.Children.Add(MakeButton("Agent 池塘", (_, _) => RequestAgentMode()));
        Grid.SetColumn(headerActions, 1);
        header.Children.Add(headerActions);
        root.Children.Add(header);

        var scene = new Grid { ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(29, 55, 51)) };
        _sceneImage = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(_sceneImage, BitmapScalingMode.NearestNeighbor);
        _renderer = new PondRenderer { Width = 500, Height = 313, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var sceneStack = new Grid();
        sceneStack.Children.Add(_sceneImage);
        sceneStack.Children.Add(_renderer);
        scene.Children.Add(sceneStack);
        _environmentLabel = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12),
            Padding = new Thickness(8, 4, 8, 4),
            Background = Brush("#C51E3831"),
            Foreground = Brush("#FFF0C7"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        };
        scene.Children.Add(_environmentLabel);
        Grid.SetRow(scene, 1);
        root.Children.Add(scene);

        var footer = new Grid { Margin = new Thickness(4, 8, 4, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        footer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _status = new TextBlock { Text = "池水安静。准备好时抛竿。", Foreground = Brush("#F1D9A4"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        footer.Children.Add(_status);
        _fishCountLabel = new TextBlock { Text = "鱼册 · 0", Foreground = Brush("#C5D0B6"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(_fishCountLabel, 1);
        footer.Children.Add(_fishCountLabel);
        _castButton = MakeButton("抛竿", CastOrReel);
        _castButton.FontSize = 14;
        _castButton.FontWeight = FontWeights.Bold;
        _castButton.MinWidth = 150;
        _castButton.Height = 44;
        _castButton.Background = Brush("#D8C18F");
        Grid.SetRow(_castButton, 1);
        Grid.SetColumnSpan(_castButton, 2);
        footer.Children.Add(_castButton);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        ApplyTheme(_settings.Theme);
        UpdateEnvironment(DateTimeOffset.Now);
        RefreshFishCount();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += Timer_Tick;
        _timer.Start();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _timer.Start();
            else _timer.Stop();
        };
        Closed += (_, _) => _timer.Stop();
        Closing += (_, _) =>
        {
            _settings.LastMode = _closedForModeSwitch ? PondMode.AgentPond : PondMode.IndependentFishing;
            try { _settingsStore.Save(_settings); App.QueueCloudSync(_settings, _settingsStore, _fishBook); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, $"模式设置没有保存；本地鱼册仍保留。\n{ex.Message}", "设置保存失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
    }

    public bool LaunchedFromSteam { get; }
    public event Action? AgentModeRequested;

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    private static Button MakeButton(string text, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(3),
            Background = Brush("#435D4C"),
            Foreground = Brush("#F3D9A2"),
            BorderBrush = Brush("#A67A47"),
            BorderThickness = new Thickness(1),
            FontSize = 11,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        button.Click += handler;
        return button;
    }

    private void ApplyTheme(PondTheme theme)
    {
        _settings.Theme = theme;
        _sceneImage.Source = PondAssets.Theme(theme);
        _renderer.Theme = theme;
        _renderer.EquippedHatId = _settings.EquippedHatId;
        _renderer.LeftDecorId = _settings.LeftDecorId;
        _renderer.RightDecorId = _settings.RightDecorId;
        var mask = new ImageBrush(_sceneImage.Source) { Stretch = Stretch.Uniform };
        mask.Freeze();
        _renderer.OpacityMask = mask;
        _renderer.InvalidateVisual();
    }

    private void UpdateEnvironment(DateTimeOffset now)
    {
        var environment = PondEnvironment.At(now.LocalDateTime);
        var activeEvent = _eventScheduler.Update(now, IsVisible || !IsLoaded, environment.Season);
        _renderer.Environment = environment;
        _renderer.ActiveEvent = activeEvent;
        _environmentLabel.Text = $"{SeasonLabel(environment.Season)} · {PhaseLabel(environment.Phase)} · {WeatherLabel(environment.Weather)}" +
            (activeEvent is null ? "" : $" · {activeEvent.Name}");
        _renderer.Tick(now);
        _renderer.InvalidateVisual();
    }

    private static string SeasonLabel(PondSeason value) => value switch { PondSeason.Spring => "春", PondSeason.Summer => "夏", PondSeason.Autumn => "秋", _ => "冬" };
    private static string PhaseLabel(DayPhase value) => value switch { DayPhase.Dawn => "黎明", DayPhase.Day => "白天", DayPhase.Dusk => "黄昏", _ => "夜晚" };
    private static string WeatherLabel(PondWeather value) => value switch { PondWeather.Clear => "晴", PondWeather.Cloudy => "多云", PondWeather.Rain => "小雨", PondWeather.Mist => "薄雾", _ => "雪" };

    private void CastOrReel(object sender, RoutedEventArgs e)
    {
        var now = DateTimeOffset.Now;
        if (_castState == CastState.Idle)
        {
            _castState = CastState.Waiting;
            _nextBiteAt = now.AddSeconds(_random.Next(4, 11));
            _castButton.Content = "收线";
            _status.Text = "鱼钩落进水里。看一会儿水面……";
            _renderer.Stage = FishingStage.Casting;
            _renderer.InvalidateVisual();
            return;
        }

        if (_castState == CastState.Waiting)
        {
            _castState = CastState.Idle;
            _castButton.Content = "抛竿";
            _status.Text = "还没有鱼咬钩，鱼线轻轻收回。";
            _renderer.Stage = FishingStage.Retracting;
            return;
        }

        var environment = PondEnvironment.At(now.LocalDateTime);
        var activeEvent = _eventScheduler.Update(now, IsVisible, environment.Season);
        var context = new FishCatchContext(environment, activeEvent, 0, now);
        try
        {
            if (_fishBook.TryCatchManual(context, out var fish) && fish is not null)
            {
                _settings.UnlockedAccessoryIds ??= ["straw-hat"];
                if (AccessoryUnlockRules.Apply(_settings, _fishBook.ReadResult().Records)) _settingsStore.Save(_settings);
                _renderer.CatchFishId = fish.FishId;
                _renderer.Stage = FishingStage.Catching;
                _status.Text = $"釣到了「{fish.FishName}」！收进鱼册了。";
                _fishCountLabel.Text = $"鱼册 · {_fishBook.ReadAll().Count}";
                App.Platform?.OnFishCaught(_fishBook.ReadAll().Count, _fishBook.ReadAll().Select(record => record.FishId).Distinct().Count());
                App.QueueCloudSync(_settings, _settingsStore, _fishBook);
            }
        }
        catch (Exception ex)
        {
            _status.Text = "鱼册保存失败；原有记录已保留。";
            MessageBox.Show(this, ex.Message, "鱼册无法写入", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _castState = CastState.Idle;
        _castButton.Content = "抛竿";
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.Now;
        UpdateEnvironment(now);
        if (_castState == CastState.Waiting && now >= _nextBiteAt)
        {
            _castState = CastState.Bite;
            _biteAt = now;
            _status.Text = "咬钩了！快按「收线」";
            _castButton.Content = "收线！";
            _renderer.Stage = FishingStage.Fighting;
        }
        else if (_castState == CastState.Bite && now - _biteAt > TimeSpan.FromSeconds(5))
        {
            _castState = CastState.Idle;
            _castButton.Content = "抛竿";
            _status.Text = "鱼儿游开了。再等下一次咬钩吧。";
            _renderer.Stage = FishingStage.Retracting;
        }
        _renderer.Tick(now);
    }

    private void RefreshFishCount()
    {
        var read = _fishBook.ReadResult();
        _fishCountLabel.Text = read.CanAppend ? $"鱼册 · {read.Records.Count}" : "鱼册 · 需检查";
    }

    private void OpenFishBook()
    {
        if (_fishBookWindow is null)
        {
            _fishBookWindow = new FishBookWindow(this, _fishBook);
            _fishBookWindow.Closed += (_, _) => _fishBookWindow = null;
        }
        _fishBookWindow.Refresh(_fishBook);
        _fishBookWindow.Show();
        _fishBookWindow.Activate();
    }

    private void OpenAppearance()
    {
        if (_appearanceWindow is null)
        {
            _appearanceWindow = new AppearanceWindow(this, _settings);
            _appearanceWindow.ThemeSelected += theme => { ApplyTheme(theme); SaveGameSettings(); };
            _appearanceWindow.AccessorySelected += (slot, id) =>
            {
                switch (slot)
                {
                    case AccessorySlot.Hat: _settings.EquippedHatId = id ?? ""; break;
                    case AccessorySlot.LeftDecor: _settings.LeftDecorId = id; break;
                    case AccessorySlot.RightDecor: _settings.RightDecorId = id; break;
                }
                ApplyTheme(_settings.Theme);
                SaveGameSettings();
            };
            _appearanceWindow.Closed += (_, _) => _appearanceWindow = null;
        }
        _appearanceWindow.RefreshAccessories();
        _appearanceWindow.Show();
        _appearanceWindow.Activate();
    }

    private void RequestAgentMode()
    {
        _closedForModeSwitch = true;
        _settings.LastMode = PondMode.AgentPond;
        SaveGameSettings();
        AgentModeRequested?.Invoke();
    }

    private void SaveGameSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
            App.QueueCloudSync(_settings, _settingsStore, _fishBook);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = "设置保存失败；本地鱼册仍保留。";
            MessageBox.Show(this, ex.Message, "设置无法写入", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
