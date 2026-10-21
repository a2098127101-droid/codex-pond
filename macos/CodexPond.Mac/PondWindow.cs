using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace CodexPond.Mac;

/// <summary>Cross-platform standalone pond window. The Windows transparent desktop overlay remains a separate WPF host.</summary>
public sealed class PondWindow : Window
{
    private enum CastState { Idle, Waiting, Fighting }

    private readonly PondSettingsStore _settingsStore = new();
    private readonly FishBookStore _fishBook = new();
    private readonly PondEventScheduler _events = new();
    private readonly Random _random = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly Canvas _sceneCanvas = new() { Width = 500, Height = 313, ClipToBounds = true };
    private readonly Image _sceneImage = new() { Width = 500, Height = 313, Stretch = Stretch.Fill };
    private readonly Border _sceneFrame;
    private readonly TextBlock _environmentText;
    private readonly TextBlock _statusText;
    private readonly TextBlock _fishCountText;
    private readonly TextBlock _tensionText;
    private readonly ProgressBar _tensionBar;
    private readonly Button _castButton;
    private readonly ComboBox _themePicker;
    private readonly CheckBox _gentleToggle;
    private readonly PondSettings _settings;
    private AgentTaskWindow? _agentTaskWindow;
    private CastState _castState;
    private DateTimeOffset _nextBiteAt;
    private DateTimeOffset _lastChallengeUpdate;
    private FishingChallenge? _challenge;
    private bool _reelHeld;
    private string? _lastCaughtFishId;
    private DateTimeOffset _caughtFishVisibleUntil;
    private Point? _ripplePoint;
    private DateTimeOffset _rippleVisibleUntil;

    public PondWindow()
    {
        _settings = _settingsStore.Load();
        Title = "Codex Pond · 池边慢钓";
        Width = 830;
        Height = 690;
        MinWidth = 760;
        MinHeight = 630;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("#1D2927");
        Foreground = Brush("#F0E3C2");
        FontFamily = new FontFamily("Arial");
        Focusable = true;

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions
            {
                new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto)
            }
        };
        root.Margin = new Thickness(18);
        Content = root;

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        var titleStack = new StackPanel { Spacing = 3 };
        titleStack.Children.Add(new TextBlock { Text = "池边慢钓", FontSize = 24, FontWeight = FontWeight.Bold, Foreground = Brush("#F2D9A2") });
        titleStack.Children.Add(new TextBlock { Text = "跨平台独立钓鱼 · 不需要登录 Agent", FontSize = 12, Foreground = Brush("#AEBDA8") });
        header.Children.Add(titleStack);
        var headerButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        headerButtons.Children.Add(MakeButton("鱼册", (_, _) => OpenFishBook()));
        headerButtons.Children.Add(MakeButton("装备", (_, _) => OpenAppearance()));
        headerButtons.Children.Add(MakeButton("Agent 任务", (_, _) => OpenAgentTasks()));
        headerButtons.Children.Add(MakeButton("源码印记", (_, _) => OpenCyberBuddhaStamp()));
        Grid.SetColumn(headerButtons, 1);
        header.Children.Add(headerButtons);
        root.Children.Add(header);

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 12, 0, 12), VerticalAlignment = VerticalAlignment.Center };
        toolbar.Children.Add(new TextBlock { Text = "池塘外观", VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("#C5CEAF") });
        _themePicker = new ComboBox { Width = 205, ItemsSource = Enum.GetValues<PondTheme>().Select(PondThemeCatalog.Label).ToArray(), SelectedIndex = Array.IndexOf(Enum.GetValues<PondTheme>(), _settings.Theme) };
        _themePicker.SelectionChanged += (_, _) =>
        {
            if (_themePicker.SelectedIndex < 0) return;
            _settings.Theme = Enum.GetValues<PondTheme>()[_themePicker.SelectedIndex];
            SaveSettings();
            LoadTheme();
        };
        toolbar.Children.Add(_themePicker);
        _gentleToggle = new CheckBox
        {
            Content = "宽松辅助",
            IsChecked = _settings.FishingAssistMode == FishingAssistMode.Gentle,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("#E7D9B4")
        };
        _gentleToggle.IsCheckedChanged += (_, _) =>
        {
            _settings.FishingAssistMode = _gentleToggle.IsChecked == true ? FishingAssistMode.Gentle : FishingAssistMode.Standard;
            SaveSettings();
            if (_castState == CastState.Fighting) _statusText!.Text = "辅助设置已保存，将在下一竿生效。";
        };
        toolbar.Children.Add(_gentleToggle);
        toolbar.Children.Add(new TextBlock { Text = "按住收线或空格调整张力；安全区里鱼会靠近。", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("#9EAD9C") });
        Grid.SetRow(toolbar, 1);
        root.Children.Add(toolbar);

        var sceneStack = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _sceneFrame = new Border
        {
            Width = 500,
            Height = 313,
            Background = Brush("#193A35"),
            BorderBrush = Brush("#69533A"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(3),
            Child = sceneStack,
            ClipToBounds = true
        };
        sceneStack.Children.Add(_sceneImage);
        sceneStack.Children.Add(_sceneCanvas);
        _sceneCanvas.PointerPressed += SceneCanvas_PointerPressed;
        var sceneWrapper = new Border
        {
            Background = Brush("#263B34"),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10),
            Child = _sceneFrame,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var stage = new Grid { RowDefinitions = new RowDefinitions { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) } };
        _environmentText = new TextBlock
        {
            Text = "读取池塘环境…",
            FontSize = 12,
            Foreground = Brush("#E4D39F"),
            Margin = new Thickness(2, 0, 2, 8),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        stage.Children.Add(_environmentText);
        Grid.SetRow(sceneWrapper, 1);
        stage.Children.Add(sceneWrapper);
        Grid.SetRow(stage, 2);
        root.Children.Add(stage);

        var footer = new Grid
        {
            RowDefinitions = new RowDefinitions { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) },
            ColumnDefinitions = new ColumnDefinitions { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }
        };
        _statusText = new TextBlock { Text = "池塘安静。准备好时抛竿。", FontSize = 13, Foreground = Brush("#F1D9A4"), VerticalAlignment = VerticalAlignment.Center };
        footer.Children.Add(_statusText);
        _fishCountText = new TextBlock { Text = "鱼册 · 0/20", FontSize = 12, Foreground = Brush("#C5D0B6"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_fishCountText, 1);
        footer.Children.Add(_fishCountText);
        var tensionPanel = new StackPanel { Spacing = 5, Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 12, 0) };
        _tensionText = new TextBlock { Text = "咬钩后按住抛竿按钮或空格控制张力", FontSize = 11, Foreground = Brush("#CED8BB") };
        _tensionBar = new ProgressBar { Minimum = 0, Maximum = 1, Value = .52, Height = 12, IsVisible = false };
        tensionPanel.Children.Add(_tensionText);
        tensionPanel.Children.Add(_tensionBar);
        Grid.SetRow(tensionPanel, 1);
        footer.Children.Add(tensionPanel);
        _castButton = MakeButton("抛竿", CastOrReel);
        _castButton.MinWidth = 165;
        _castButton.Height = 42;
        _castButton.PointerPressed += (_, _) => { if (_castState == CastState.Fighting) _reelHeld = true; };
        _castButton.PointerReleased += (_, _) => _reelHeld = false;
        Grid.SetColumn(_castButton, 1);
        Grid.SetRow(_castButton, 1);
        footer.Children.Add(_castButton);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Space || _castState != CastState.Fighting || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
            _reelHeld = true;
            e.Handled = true;
        };
        KeyUp += (_, e) =>
        {
            if (e.Key != Key.Space || _castState != CastState.Fighting) return;
            _reelHeld = false;
            e.Handled = true;
        };

        LoadTheme();
        UpdateEnvironment(DateTimeOffset.Now);
        RefreshFishCount();
        _timer.Tick += Timer_Tick;
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            if (_agentTaskWindow is not null) _ = _agentTaskWindow.ShutdownAsync();
        };
    }

    private void LoadTheme()
    {
        var file = _settings.Theme switch
        {
            PondTheme.A => "theme-a", PondTheme.B => "theme-b", PondTheme.C => "theme-c",
            PondTheme.D => "theme-d", PondTheme.E => "theme-e", _ => "theme-fusionce"
        };
        _sceneImage.Source = LoadBitmap(System.IO.Path.Combine("Assets", "Themes", file + ".png"));
    }

    private void UpdateEnvironment(DateTimeOffset now)
    {
        var environment = PondEnvironment.At(now.LocalDateTime);
        var pondEvent = _events.Update(now, true, environment.Season);
        _environmentText.Text = $"{SeasonLabel(environment.Season)} · {PhaseLabel(environment.Phase)} · {WeatherLabel(environment.Weather)}" +
            (pondEvent is null ? " · 池水微澜" : $" · {pondEvent.Name}" + (pondEvent.Definition.LimitedFishId is null ? "" : " · 鱼册有事件鱼线索"));
        _sceneFrame.Background = Brush(environment.Phase switch
        {
            DayPhase.Dawn => "#38534C", DayPhase.Day => "#264A41", DayPhase.Dusk => "#584239", _ => "#1C2B3A"
        });
        _sceneCanvas.Children.Clear();
        DrawAccessories();
        DrawEventParticles(pondEvent, now);
        if (_ripplePoint is { } point && now < _rippleVisibleUntil)
        {
            var progress = 1 - (_rippleVisibleUntil - now).TotalSeconds / 1.1;
            var ripple = new Ellipse { Width = 18 + progress * 18, Height = 7 + progress * 5, Stroke = Brush("#B8E8D7"), StrokeThickness = 1, Opacity = Math.Clamp(1 - progress * .5, 0, 1), IsHitTestVisible = false };
            Canvas.SetLeft(ripple, point.X - ripple.Width / 2);
            Canvas.SetTop(ripple, point.Y - ripple.Height / 2);
            _sceneCanvas.Children.Add(ripple);
        }
        if (_lastCaughtFishId is not null && now < _caughtFishVisibleUntil)
        {
            AddImage(_lastCaughtFishId, 224, 195, 54, 38, 1);
            AddPixel(245, 186, 4, 4, "#F4DF9B");
            AddPixel(261, 180, 3, 3, "#F4DF9B");
        }
    }

    private void DrawEventParticles(PondEvent? pondEvent, DateTimeOffset now)
    {
        if (pondEvent is null) return;
        var phase = now.ToUnixTimeSeconds() % 8;
        for (var i = 0; i < 5; i++)
        {
            var x = 42 + i * 96 + (int)(phase * (i % 2 == 0 ? 1 : -1));
            var y = 45 + (i * 47 + (int)phase * (i % 2 == 0 ? 1 : 2)) % 190;
            var effect = pondEvent.Definition.VisualEffect;
            if (effect is "ice-glint" or "dragonflies")
            {
                AddPixel(x, y - 2, 2, 7, effect == "ice-glint" ? "#D7EFF0" : "#B8E6D3");
                AddPixel(x - 2, y, 7, 2, effect == "ice-glint" ? "#D7EFF0" : "#B8E6D3");
            }
            else if (effect is "dew" or "fireflies" or "frost")
                AddPixel(x, y, 3, 4, effect == "dew" ? "#D9F0E7" : "#E5E49A");
            else if (effect is "reed-wind")
            {
                AddPixel(x, y, 8, 1, "#D3C18B");
                AddPixel(x + 2, y + 2, 6, 1, "#9DAE83");
            }
            else
            {
                AddPixel(x, y, 4, 3, effect == "petals" ? "#E8A8AA" : "#CC8A4B");
                AddPixel(x + 2, y + 2, 2, 2, "#F0D6A4");
            }
        }
    }

    private void DrawAccessories()
    {
        if (_settings.EquippedHatId == "straw-hat")
        {
            AddPixel(101, 129, 21, 3, "#9A6735"); AddPixel(105, 122, 13, 7, "#D8AE62"); AddPixel(107, 121, 10, 2, "#E7C77C");
        }
        else if (_settings.EquippedHatId == "moonlit-hat")
        {
            AddPixel(103, 129, 20, 3, "#76869A"); AddPixel(108, 124, 12, 5, "#AFC7D0"); AddPixel(110, 122, 8, 3, "#DDE7D5");
        }
        if (_settings.LeftDecorId == "grass-block")
        {
            AddPixel(18, 255, 20, 9, "#896343"); AddPixel(18, 251, 20, 5, "#668E4E"); AddPixel(20, 249, 7, 3, "#8AAF5A");
        }
        if (_settings.RightDecorId == "four-season-mobile")
        {
            AddPixel(469, 157, 1, 16, "#A67942"); AddPixel(462, 160, 2, 2, "#E6A1A6"); AddPixel(468, 164, 2, 2, "#E4D48D");
            AddPixel(474, 160, 2, 2, "#D47C4B"); AddPixel(468, 171, 2, 2, "#C7E2E3");
        }
        else if (_settings.RightDecorId is not null)
        {
            var color = _settings.RightDecorId switch
            {
                "spring-bloom" => "#EDB1B3", "summer-shell" => "#DCCB8D", "autumn-leaf" => "#D1814C",
                "winter-star" => "#C6E2E3", _ => "#DDE78B"
            };
            AddPixel(465, 170, 8, 8, "#765339"); AddPixel(467, 161, 4, 9, "#A67942"); AddPixel(465, 158, 8, 4, color);
        }
    }

    private void SceneCanvas_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetPosition(_sceneCanvas);
        if (point.X is < 70 or > 440 || point.Y is < 85 or > 260) return;
        _ripplePoint = point;
        _rippleVisibleUntil = DateTimeOffset.Now.AddSeconds(1.1);
        _statusText.Text = "水面泛起一圈涟漪，水下有影子掠过。";
    }

    private void CastOrReel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_castState == CastState.Fighting) return;
        var now = DateTimeOffset.Now;
        if (_castState == CastState.Idle)
        {
            _castState = CastState.Waiting;
            _nextBiteAt = now.AddSeconds(_random.Next(4, 11));
            _statusText.Text = "鱼钩落进水里。坐一会儿，等浮漂轻轻动起来。";
            _castButton.Content = "收线";
        }
        else
        {
            _castState = CastState.Idle;
            _statusText.Text = "鱼还没咬钩，鱼线轻轻收回。";
            _castButton.Content = "抛竿";
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.Now;
        UpdateEnvironment(now);
        if (_castState == CastState.Waiting && now >= _nextBiteAt)
        {
            _castState = CastState.Fighting;
            _challenge = new FishingChallenge(_settings.FishingAssistMode);
            _lastChallengeUpdate = now;
            _tensionBar.Value = _challenge.Tension;
            _tensionBar.IsVisible = true;
            _tensionText.Text = "鱼咬钩了！按住收线或空格，张力进安全区时缓慢收鱼。";
            _statusText.Text = "鱼线动了！在安全区稳住张力。";
            _castButton.Content = "按住收线";
        }
        if (_castState == CastState.Fighting && _challenge is not null)
        {
            _challenge.Advance(now - _lastChallengeUpdate, _reelHeld);
            _lastChallengeUpdate = now;
            _tensionBar.Value = _challenge.Tension;
            _tensionText.Text = $"{_challenge.TensionLabel} · {(_challenge.Mode == FishingAssistMode.Gentle ? "宽松辅助" : "标准挑战")}";
            if (_challenge.IsComplete) CatchFish(now);
            else if (_challenge.IsBroken)
            {
                _castState = CastState.Idle;
                _challenge = null;
                _reelHeld = false;
                _tensionBar.IsVisible = false;
                _castButton.Content = "抛竿";
                _statusText.Text = "鱼线松开了，鱼儿游回池塘。可以休息一下再试。";
            }
        }
        RefreshFishCount();
    }

    private void CatchFish(DateTimeOffset now)
    {
        var environment = PondEnvironment.At(now.LocalDateTime);
        var pondEvent = _events.Update(now, true, environment.Season);
        try
        {
            var context = new FishCatchContext(environment, pondEvent, 0, now);
            if (_fishBook.TryCatchManual(context, out var fish) && fish is not null)
            {
                _lastCaughtFishId = fish.FishId;
                _caughtFishVisibleUntil = now.AddSeconds(4);
                var read = _fishBook.ReadResult();
                if (read.CanAppend && AccessoryUnlockRules.Apply(_settings, read.Records)) SaveSettings();
                _statusText.Text = $"钓到了「{fish.FishName}」！鱼册和收获里程碑已经更新。";
            }
            else _statusText.Text = "这条鱼已记录过了。";
        }
        catch (Exception ex)
        {
            _statusText.Text = $"鱼册保存失败，原有记录保留：{ex.Message}";
        }
        _castState = CastState.Idle;
        _challenge = null;
        _reelHeld = false;
        _tensionBar.IsVisible = false;
        _castButton.Content = "抛竿";
        DrawAccessories();
    }

    private void OpenFishBook()
    {
        var read = _fishBook.ReadResult();
        var discoveries = read.CanAppend ? FishDiscoveryCatalog.Build(read.Records) : [];
        var window = new Window
        {
            Title = "Codex Pond · 鱼册",
            Width = 620,
            Height = 650,
            MinWidth = 520,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("#DCC59A")
        };
        var root = new Grid { RowDefinitions = new RowDefinitions { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) }, Margin = new Thickness(14) };
        var header = new StackPanel { Spacing = 8 };
        header.Children.Add(new TextBlock { Text = read.CanAppend ? $"探索图鉴 · 已发现 {discoveries.Count(entry => entry.IsDiscovered)} / {FishCatalog.All.Count} 种 · 共 {read.Records.Count} 次收获" : "鱼册暂时无法读取", FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Brush("#354338") });
        if (!read.CanAppend)
            header.Children.Add(new TextBlock { Text = read.Error ?? "原文件已保留。", Foreground = Brush("#8B3328"), TextWrapping = TextWrapping.Wrap });
        var picker = new ComboBox { Width = 185, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = new[] { "全部鱼种", "尚未发现", "春季", "夏季", "秋季", "冬季", "黎明", "白天", "黄昏", "夜晚", "限定事件" }, SelectedIndex = 0 };
        header.Children.Add(picker);
        root.Children.Add(header);
        var list = new StackPanel { Spacing = 7 };
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        void PopulateRows()
        {
            list.Children.Clear();
            if (!read.CanAppend) return;
            var filter = FilterCode(picker.SelectedIndex);
            foreach (var entry in discoveries.Where(entry => FishDiscoveryCatalog.MatchesFilter(entry, filter)))
            {
                var species = entry.Species;
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions { new ColumnDefinition(new GridLength(72)), new ColumnDefinition(GridLength.Star), new ColumnDefinition(new GridLength(96)) }, Margin = new Thickness(2), MinHeight = 68 };
                var sprite = MakeFishImage(species.Id, 58, 42, entry.IsDiscovered ? 1 : .2);
                sprite.VerticalAlignment = VerticalAlignment.Top;
                row.Children.Add(sprite);
                var details = new StackPanel { Spacing = 3 };
                details.Children.Add(new TextBlock { Text = entry.IsDiscovered ? species.Name : "？？？", FontSize = 13, FontWeight = FontWeight.Bold, Foreground = Brush("#354338") });
                var seasons = string.Join("、", species.Seasons?.Select(SeasonLabel) ?? Enum.GetValues<PondSeason>().Select(SeasonLabel));
                var phases = string.Join("、", species.Phases?.Select(PhaseLabel) ?? Enum.GetValues<DayPhase>().Select(PhaseLabel));
                details.Children.Add(new TextBlock { Text = entry.IsDiscovered ? $"已钓 {entry.CatchCount} 次 · {seasons} · {phases}" : $"未发现 · {seasons} · {phases}" + (species.IsEventFish ? " · 事件限定" : ""), FontSize = 10, Foreground = Brush("#886B43") });
                details.Children.Add(new TextBlock { Text = entry.IsDiscovered ? species.Description : entry.Clue, FontSize = 11, Foreground = Brush("#5A5948"), TextWrapping = TextWrapping.Wrap });
                Grid.SetColumn(details, 1);
                row.Children.Add(details);
                var state = new TextBlock { Text = entry.LatestCatch?.CaughtAt.ToLocalTime().ToString("MM/dd HH:mm") ?? "待发现", FontSize = 10, Foreground = Brush("#65734D"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
                Grid.SetColumn(state, 2);
                row.Children.Add(state);
                list.Children.Add(new Border { Child = row, Background = Brush("#E9D7B2"), BorderBrush = Brush("#A88B5F"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(7) });
            }
            if (read.Records.Count > 0)
            {
                var historyTitle = new TextBlock { Text = "最近收获", FontSize = 16, FontWeight = FontWeight.Bold, Foreground = Brush("#354338"), Margin = new Thickness(0, 12, 0, 4) };
                list.Children.Add(historyTitle);
                foreach (var catchRecord in read.Records.OrderByDescending(record => record.CaughtAt).Take(10))
                {
                    var name = FishCatalog.All.FirstOrDefault(species => species.Id == catchRecord.FishId)?.Name ?? catchRecord.FishName;
                    list.Children.Add(new TextBlock { Text = $"{catchRecord.CaughtAt.ToLocalTime():MM/dd HH:mm}  ·  {name}  ·  {catchRecord.Season?.ToString() ?? "历史记录"}", FontSize = 10, Foreground = Brush("#5A5948") });
                }
            }
            list.Children.Add(new TextBlock { Text = "限时事件会再次出现；图鉴线索只提示季节与时段，不设错过惩罚。", FontSize = 10, Foreground = Brush("#63704F"), Margin = new Thickness(0, 10, 0, 4) });
        }
        picker.SelectionChanged += (_, _) => PopulateRows();
        window.Content = root;
        PopulateRows();
        window.ShowDialog(this);
    }

    private void OpenAgentTasks()
    {
        _agentTaskWindow ??= new AgentTaskWindow(_settings, _settingsStore, _fishBook, message =>
        {
            _statusText.Text = message;
            RefreshFishCount();
        });
        _agentTaskWindow.Show(this);
        _agentTaskWindow.Activate();
    }

    private void OpenCyberBuddhaStamp()
    {
        var window = new CyberBuddhaWindow { WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.ShowDialog(this);
    }

    private void OpenAppearance()
    {
        var window = new Window { Title = "池塘装备", Width = 380, Height = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("#DCC59A") };
        var stack = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        stack.Children.Add(new TextBlock { Text = "已解锁装饰", FontSize = 19, FontWeight = FontWeight.Bold, Foreground = Brush("#354338") });
        AddSlot(AccessorySlot.Hat, "帽子", _settings.EquippedHatId, id => _settings.EquippedHatId = id ?? "");
        AddSlot(AccessorySlot.LeftDecor, "左岸装饰", _settings.LeftDecorId, id => _settings.LeftDecorId = id);
        AddSlot(AccessorySlot.RightDecor, "右岸装饰", _settings.RightDecorId, id => _settings.RightDecorId = id);
        stack.Children.Add(new TextBlock { Text = "四季风铃会在四季各发现一条鱼后解锁；月光斗笠需要发现全部 20 种鱼。", FontSize = 11, Foreground = Brush("#5A5948"), TextWrapping = TextWrapping.Wrap });
        window.Content = stack;
        window.ShowDialog(this);

        void AddSlot(AccessorySlot slot, string label, string? current, Action<string?> setValue)
        {
            stack.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, Foreground = Brush("#46543B") });
            var options = new List<AccessoryDefinition?> { null };
            options.AddRange(AccessoryCatalog.All.Where(item => item.Slot == slot && _settings.UnlockedAccessoryIds.Contains(item.Id, StringComparer.Ordinal)));
            var selector = new ComboBox
            {
                ItemsSource = options.Select(item => item is null ? "不装备" : item.Name).ToArray(),
                SelectedIndex = Math.Max(0, options.FindIndex(item => string.Equals(item?.Id, current, StringComparison.Ordinal)))
            };
            selector.SelectionChanged += (_, _) =>
            {
                var index = selector.SelectedIndex;
                if (index < 0 || index >= options.Count) return;
                setValue(options[index]?.Id);
                DrawAccessories();
                SaveSettings();
            };
            stack.Children.Add(selector);
        }
    }

    private void SaveSettings()
    {
        try { _settingsStore.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _statusText.Text = $"设置没有保存：{ex.Message}";
        }
    }

    private void RefreshFishCount()
    {
        var read = _fishBook.ReadResult();
        var species = read.CanAppend ? read.Records.Select(record => record.FishId).Distinct(StringComparer.Ordinal).Count() : 0;
        _fishCountText.Text = read.CanAppend ? $"鱼册 · {species}/{FishCatalog.All.Count} 种 · {read.Records.Count} 条" : "鱼册 · 需检查";
    }

    private void AddPixel(double x, double y, double width, double height, string color)
    {
        var rect = new Rectangle { Width = width, Height = height, Fill = Brush(color), IsHitTestVisible = false };
        Canvas.SetLeft(rect, x); Canvas.SetTop(rect, y); _sceneCanvas.Children.Add(rect);
    }

    private void AddImage(string fishId, double x, double y, double width, double height, double opacity)
    {
        var image = MakeFishImage(fishId, width, height, opacity);
        Canvas.SetLeft(image, x); Canvas.SetTop(image, y); _sceneCanvas.Children.Add(image);
    }

    private static Image MakeFishImage(string fishId, double width, double height, double opacity)
    {
        var image = new Image { Width = width, Height = height, Stretch = Stretch.Uniform, Opacity = opacity, IsHitTestVisible = false };
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Fish", fishId + ".png");
        if (File.Exists(path)) image.Source = new Bitmap(path);
        return image;
    }

    private static Bitmap? LoadBitmap(string relativePath)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, relativePath);
        return File.Exists(path) ? new Bitmap(path) : null;
    }

    private static Button MakeButton(string content, EventHandler<Avalonia.Interactivity.RoutedEventArgs> click)
    {
        var button = new Button
        {
            Content = content,
            Padding = new Thickness(12, 7),
            Background = Brush("#435D4C"),
            Foreground = Brush("#F3D9A2"),
            BorderBrush = Brush("#A67A47"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            FontSize = 12,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        button.Click += click;
        return button;
    }

    private static SolidColorBrush Brush(string color) => new(Color.Parse(color));
    private static string FilterCode(int index) => index switch { 1 => "undiscovered", 2 => "spring", 3 => "summer", 4 => "autumn", 5 => "winter", 6 => "dawn", 7 => "day", 8 => "dusk", 9 => "night", 10 => "event", _ => "all" };
    private static string SeasonLabel(PondSeason season) => season switch { PondSeason.Spring => "春", PondSeason.Summer => "夏", PondSeason.Autumn => "秋", _ => "冬" };
    private static string PhaseLabel(DayPhase phase) => phase switch { DayPhase.Dawn => "黎明", DayPhase.Day => "白天", DayPhase.Dusk => "黄昏", _ => "夜晚" };
    private static string WeatherLabel(PondWeather weather) => weather switch { PondWeather.Clear => "晴", PondWeather.Cloudy => "多云", PondWeather.Rain => "小雨", PondWeather.Mist => "薄雾", _ => "雪" };
}
