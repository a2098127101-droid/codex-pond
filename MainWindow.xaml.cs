using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace CodexPond;

public partial class MainWindow : Window
{
    private readonly PondSettingsStore _settingsStore = new();
    private readonly FishBookStore _fishBook = new();
    private readonly FishingStateMachine _fishing = new();
    private readonly FishingActivityTracker _activity = new();
    private readonly PondEventScheduler _eventScheduler = new();
    private readonly GlobalInputPulseMonitor _inputMonitor;
    private readonly DispatcherTimer _animationTimer;
    private readonly DispatcherTimer _positionSaveTimer;
    private readonly DispatcherTimer _environmentTimer;
    private readonly Queue<CodexApprovalRequest> _pendingRequests = new();
    private readonly Dictionary<string, IAgentTaskLauncher> _agentLaunchers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _terminalTurnIds = new(StringComparer.Ordinal);
    private PondSettings _settings;
    private CodexAppServerAdapter? _adapter;
    private TaskPanelWindow? _taskPanel;
    private FishBookWindow? _fishBookWindow;
    private AppearanceWindow? _appearanceWindow;
    private FishingGameWindow? _gameWindow;
    private Forms.NotifyIcon? _trayIcon;
    private Point _pointerStart;
    private bool _pondPressed;
    private bool _pondDragStarted;
    private bool _exitRequested;
    private bool _turnBusy;
    private long _turnLaunchSequence;
    private bool _interruptRequested;
    private bool _stopRequestSent;
    private string? _lastSettingsError;
    private string? _lastFishBookError;
    private IAgentTaskLauncher? _agentLauncher;
    private string? _activeThreadId;
    private string? _currentTurnId;
    private DateTime _stageDeadline = DateTime.MinValue;
    private AgentApprovalPrompt? _pendingAgentApproval;

    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
        ApplyTheme(_settings.Theme, persist: false);
        ApplyAccessories();
        _activity.Enabled = _settings.TrackInputActivity;
        _inputMonitor = new GlobalInputPulseMonitor(_activity);
        _inputMonitor.PulseAccepted += () =>
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try { Dispatcher.BeginInvoke(() => PondArt.RegisterActivity(_activity.Score)); }
            catch (InvalidOperationException) { }
        };
        RestorePosition();

        _animationTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _animationTimer.Tick += AnimationTimer_Tick;

        _positionSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _positionSaveTimer.Tick += (_, _) =>
        {
            _positionSaveTimer.Stop();
            SavePosition();
        };

        _environmentTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _environmentTimer.Tick += (_, _) => UpdateEnvironmentAndEvent(DateTimeOffset.Now);

        IsVisibleChanged += MainWindow_IsVisibleChanged;
        LocationChanged += (_, _) =>
        {
            if (IsLoaded) _positionSaveTimer.Start();
        };
        Closing += MainWindow_Closing;
        Loaded += (_, _) =>
        {
            InitializeTrayIcon();
            ShowActivityTrackingDisclosureIfNeeded();
            UpdateFishCount();
            ApplyTheme(_settings.Theme, persist: false);
            UpdateEnvironmentAndEvent(DateTimeOffset.Now);
            _environmentTimer.Start();
            _animationTimer.Start();
        };
    }

    private void ShowActivityTrackingDisclosureIfNeeded()
    {
        if (_settings.ActivityTrackingNoticeSeen) return;
        var choice = MessageBox.Show(
            this,
            "Codex Pond 可在 Codex turn 运行期间统计全局键盘按下与鼠标按钮活动，每 200 毫秒最多计一次，只用于本轮鱼获稀有度。程序不会读取按键内容、鼠标坐标或目标应用，也不会保存逐次输入；计数只保留在当前 turn 内存中。\n\n选择“是”开启（默认），选择“否”关闭。之后可在“外观 → 匿名活动计数”中更改。",
            "匿名活动计数说明",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information,
            MessageBoxResult.Yes);
        _settings.TrackInputActivity = choice == MessageBoxResult.Yes;
        _settings.ActivityTrackingNoticeSeen = true;
        _activity.Enabled = _settings.TrackInputActivity;
        SaveSettings();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WindowProc);
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmNcHitTest) return IntPtr.Zero;

        var packed = lParam.ToInt64();
        var screenPoint = new Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff)));
        Point localPoint;
        try { localPoint = PointFromScreen(screenPoint); }
        catch (InvalidOperationException) { return IntPtr.Zero; }

        var hit = InputHitTest(localPoint) as DependencyObject;
        if (IsPondInteractiveElement(hit)) return IntPtr.Zero;

        handled = true;
        return new IntPtr(HtTransparent);
    }

    private bool IsPondInteractiveElement(DependencyObject? element)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, PondHitTarget) ||
                ReferenceEquals(element, DragHandle) ||
                ReferenceEquals(element, PondStatusButton) ||
                ReferenceEquals(element, FishCountButton) ||
                 ReferenceEquals(element, FishCountText) ||
                 ReferenceEquals(element, PondStatusText) ||
                ReferenceEquals(element, HidePondButton))
                return true;

            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    private string CurrentWorkspace => _settings.HasChosenWorkspace && Directory.Exists(_settings.LastWorkspace)
        ? Path.GetFullPath(_settings.LastWorkspace)
        : "";

    private void RestorePosition()
    {
        var work = SystemParameters.WorkArea;
        Left = _settings.PondLeft ?? Math.Max(work.Left, work.Right - Width - 26);
        Top = _settings.PondTop ?? Math.Max(work.Top, work.Bottom - Height - 30);
        var minLeft = SystemParameters.VirtualScreenLeft - Width + 90;
        var maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 90;
        var minTop = SystemParameters.VirtualScreenTop;
        var maxTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 90;
        Left = Math.Clamp(Left, minLeft, Math.Max(minLeft, maxLeft));
        Top = Math.Clamp(Top, minTop, Math.Max(minTop, maxTop));
    }

    private void SavePosition()
    {
        _settings.PondLeft = Left;
        _settings.PondTop = Top;
        SaveSettings();
    }

    private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            _animationTimer.Start();
            _environmentTimer.Start();
            UpdateEnvironmentAndEvent(DateTimeOffset.Now);
        }
        else
        {
            _animationTimer.Stop();
            _environmentTimer.Stop();
            var environment = PondEnvironment.At(DateTime.Now);
            _eventScheduler.Update(DateTimeOffset.Now, false, environment.Season);
        }
    }

    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        PondArt.Tick(DateTimeOffset.Now);
        _animationTimer.Interval = _fishing.Stage == FishingStage.Idle ? TimeSpan.FromMilliseconds(1500) : TimeSpan.FromMilliseconds(180);
        if (_stageDeadline == DateTime.MinValue || DateTime.Now < _stageDeadline) return;

        _stageDeadline = DateTime.MinValue;
        if (_fishing.Stage is FishingStage.Catching or FishingStage.Retracting or FishingStage.ConnectionLost)
        {
            _fishing.Reset();
            SetFishingStage(FishingStage.Idle);
        }
    }

    private void InitializeTrayIcon()
    {
        if (_trayIcon is not null) return;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示池塘", null, (_, _) => Dispatcher.BeginInvoke(new Action(ShowPond)));
        menu.Items.Add("打开任务面板", null, (_, _) => Dispatcher.BeginInvoke(new Action(OpenTaskPanelFromTray)));
        menu.Items.Add("打开独立钓鱼", null, (_, _) => Dispatcher.BeginInvoke(new Action(OpenIndependentFishing)));
        menu.Items.Add("打开鱼册", null, (_, _) => Dispatcher.BeginInvoke(new Action(OpenFishBookFromTray)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("隐藏池塘", null, (_, _) => Dispatcher.BeginInvoke(new Action(HidePond)));
        menu.Items.Add("退出 Codex Pond", null, (_, _) => Dispatcher.BeginInvoke(new Action(ExitApplication)));

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Codex Pond · 池塘",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.MouseDoubleClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                Dispatcher.BeginInvoke(new Action(OpenTaskPanelFromTray));
        };
    }

    private void OpenTaskPanelFromTray()
    {
        ShowPond();
        OpenTaskPanel();
    }

    private void OpenFishBookFromTray()
    {
        ShowPond();
        OpenFishBook();
    }

    private void OpenTaskPanel()
    {
        if (_taskPanel is null)
        {
            _taskPanel = new TaskPanelWindow(this, CurrentWorkspace);
            PlaceWindowNearPond(_taskPanel);
            _taskPanel.WorkspaceChanged += ChangeWorkspace;
            _taskPanel.PromptSubmitted += SubmitPrompt;
            _taskPanel.CancelRequested += InterruptTurn;
            _taskPanel.NewThreadRequested += StartNewThread;
            _taskPanel.FishBookRequested += OpenFishBook;
            _taskPanel.AppearanceRequested += OpenAppearance;
            _taskPanel.IndependentModeRequested += OpenIndependentFishing;
            _taskPanel.WorkBuddyStatusRequested += OpenWorkBuddyStatusPage;
            _taskPanel.ProviderSelected += SelectAgentProvider;
            _taskPanel.RequestDecision += HandleApprovalDecision;
            _taskPanel.UserInputSubmitted += HandleUserInput;
            _taskPanel.AgentApprovalDecision += HandleAgentApprovalDecision;
        }

        _taskPanel.SetProviderSelection(_settings.AgentProviderId);
        if (!_taskPanel.IsVisible) _taskPanel.Show();
        _taskPanel.Activate();
        if (!_settings.HasChosenWorkspace || !Directory.Exists(_settings.LastWorkspace))
            Dispatcher.BeginInvoke(_taskPanel.RequestWorkspaceSelection);
    }

    private void OpenIndependentFishing()
    {
        if (_gameWindow is null)
        {
            _settings.LastMode = PondMode.IndependentFishing;
            SaveSettings();
            _gameWindow = new FishingGameWindow(_settings, _settingsStore, _fishBook);
            _gameWindow.AgentModeRequested += () =>
            {
                _gameWindow?.Close();
                _gameWindow = null;
                _settings.LastMode = PondMode.AgentPond;
                SaveSettings();
                ShowPond();
            };
            _gameWindow.Closed += (_, _) =>
            {
                _gameWindow = null;
                if (!_exitRequested && !IsVisible) ShowPond();
            };
        }
        Hide();
        if (!_gameWindow.IsVisible) _gameWindow.Show();
        _gameWindow.Activate();
    }

    private void OpenWorkBuddyStatusPage()
    {
        var dialog = new Window
        {
            Title = "Work Buddy 状态页",
            Owner = _taskPanel,
            Width = 430,
            Height = 178,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(221, 193, 145)),
            FontFamily = new FontFamily("Microsoft YaHei UI")
        };
        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = "填入你本机 Work Buddy Dashboard 的 HTTP 或 HTTPS 地址。池塘不会读取 Work Buddy 数据库或改写其配置。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 9),
            Foreground = new SolidColorBrush(Color.FromRgb(54, 43, 33))
        });
        var urlBox = new TextBox { Text = _settings.WorkBuddyDashboardUrl, Height = 29, Padding = new Thickness(7, 4, 7, 4) };
        Grid.SetRow(urlBox, 1);
        root.Children.Add(urlBox);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 11, 0, 0) };
        var openButton = new Button { Content = "保存并打开", MinWidth = 94, Margin = new Thickness(0, 0, 7, 0) };
        openButton.Click += (_, _) =>
        {
            if (!Uri.TryCreate(urlBox.Text.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                MessageBox.Show(dialog, "请输入有效的 HTTP 或 HTTPS 地址。", "地址格式不正确", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _settings.WorkBuddyDashboardUrl = uri.AbsoluteUri;
            SaveSettings();
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "无法打开 Work Buddy 状态页", MessageBoxButton.OK, MessageBoxImage.Warning); }
            dialog.Close();
        };
        var cancelButton = new Button { Content = "关闭", MinWidth = 64 };
        cancelButton.Click += (_, _) => dialog.Close();
        footer.Children.Add(openButton);
        footer.Children.Add(cancelButton);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        dialog.Content = root;
        dialog.ShowDialog();
    }

    private void OpenAppearance()
    {
        if (_appearanceWindow is null)
        {
            _appearanceWindow = new AppearanceWindow(this, _settings);
            PlaceWindowNearPond(_appearanceWindow);
            _appearanceWindow.ThemeSelected += theme => ApplyTheme(theme);
            _appearanceWindow.AccessorySelected += SetAccessory;
            _appearanceWindow.InputTrackingChanged += SetInputTracking;
            _appearanceWindow.Closed += (_, _) => _appearanceWindow = null;
        }
        _appearanceWindow.RefreshAccessories();
        if (!_appearanceWindow.IsVisible) _appearanceWindow.Show();
        _appearanceWindow.Activate();
    }

    private void ApplyTheme(PondTheme theme, bool persist = true)
    {
        _settings.Theme = theme;
        ThemeArt.Source = PondAssets.Theme(theme);
        PondArt.Theme = theme;
        var sceneMask = new ImageBrush(ThemeArt.Source) { Stretch = Stretch.Fill };
        sceneMask.Freeze();
        PondArt.OpacityMask = sceneMask;

        const double statusX = 14, statusY = 13, statusWidth = 206, fishX = 384, fishY = 13;

        StatusPanelBorder.Width = statusWidth;
        StatusPanelBorder.Height = 26;
        Canvas.SetLeft(StatusPanelBorder, statusX);
        Canvas.SetTop(StatusPanelBorder, statusY);
        PondStatusText.Width = statusWidth - 12;
        PondStatusButton.Width = statusWidth - 8;
        PondStatusButton.Height = 22;
        Canvas.SetLeft(PondStatusButton, statusX + 4);
        Canvas.SetTop(PondStatusButton, statusY + 2);

        FishCountPanelBorder.Width = 92;
        FishCountPanelBorder.Height = 25;
        Canvas.SetLeft(FishCountPanelBorder, fishX);
        Canvas.SetTop(FishCountPanelBorder, fishY);
        FishCountText.Width = FishCountPanelBorder.Width - 8;
        FishCountButton.Width = FishCountPanelBorder.Width - 8;
        FishCountButton.Height = 21;
        Canvas.SetLeft(FishCountButton, fishX + 4);
        Canvas.SetTop(FishCountButton, fishY + 2);

        Canvas.SetLeft(EnvironmentText, 262);
        Canvas.SetTop(EnvironmentText, 14);
        Canvas.SetLeft(PondEventText, 15);
        Canvas.SetTop(PondEventText, 42);
        Canvas.SetLeft(HidePondButton, 476);
        Canvas.SetTop(HidePondButton, 5);
        Canvas.SetLeft(DragHandle, 226);
        Canvas.SetTop(DragHandle, 5);
        DragHandle.Width = 52;

        var scenic = BrushColor(theme == PondTheme.C ? "#D92F554A" : "#DD4D3928");
        StatusPanelBorder.Background = scenic;
        FishCountPanelBorder.Background = BrushColor("#E523453F");
        if (persist) SaveSettings();
        PondArt.InvalidateVisual();
    }

    private void SetAccessory(AccessorySlot slot, string? id)
    {
        switch (slot)
        {
            case AccessorySlot.Hat: _settings.EquippedHatId = id ?? ""; break;
            case AccessorySlot.LeftDecor: _settings.LeftDecorId = id; break;
            case AccessorySlot.RightDecor: _settings.RightDecorId = id; break;
        }
        ApplyAccessories();
        SaveSettings();
    }

    private void ApplyAccessories()
    {
        PondArt.EquippedHatId = _settings.EquippedHatId;
        PondArt.LeftDecorId = _settings.LeftDecorId;
        PondArt.RightDecorId = _settings.RightDecorId;
        PondArt.InvalidateVisual();
    }

    private void SetInputTracking(bool enabled)
    {
        _settings.TrackInputActivity = enabled;
        _activity.Enabled = enabled;
        if (!enabled) _inputMonitor.Stop();
        else if (_activity.IsTurnActive)
        {
            try { _inputMonitor.Start(); }
            catch (System.ComponentModel.Win32Exception ex) { _taskPanel?.AddNotice($"无法启用匿名键鼠计数：{ex.Message}"); }
        }
        SaveSettings();
    }

    private void UpdateEnvironmentAndEvent(DateTimeOffset now)
    {
        var environment = PondEnvironment.At(now.LocalDateTime);
        var pondEvent = _eventScheduler.Update(now, IsVisible, environment.Season);
        PondArt.Environment = environment;
        PondArt.ActiveEvent = pondEvent;
        ApplyAccessories();
        EnvironmentText.Text = $"{environment.SeasonLabel} · {environment.PhaseLabel} · {WeatherLabel(environment.Weather)}";
        PondEventText.Text = pondEvent?.Name ?? "";
        PondEventText.Visibility = pondEvent is null ? Visibility.Collapsed : Visibility.Visible;
        PondArt.InvalidateVisual();
    }

    private static string WeatherLabel(PondWeather weather) => weather switch
    {
        PondWeather.Clear => "晴",
        PondWeather.Cloudy => "多云",
        PondWeather.Rain => "小雨",
        PondWeather.Mist => "薄雾",
        _ => "雪"
    };

    private void ChangeWorkspace(string path)
    {
        if (!Directory.Exists(path))
        {
            _taskPanel?.SetStatus("找不到这个工作目录，请重新选择");
            return;
        }
        var fullPath = Path.GetFullPath(path);
        var changed = !string.Equals(CurrentWorkspace, fullPath, StringComparison.OrdinalIgnoreCase);
        _settings.LastWorkspace = fullPath;
        _settings.HasChosenWorkspace = true;
        if (changed)
        {
            _activeThreadId = null;
            _settings.LastThreadId = null;
            _settings.LastThreadWorkspace = null;
            _settings.ProviderSessions.Remove(_settings.AgentProviderId);
            _settings.ProviderSessionWorkspaces.Remove(_settings.AgentProviderId);
        }
        SaveSettings();
        _taskPanel?.SetWorkspace(fullPath);
        _taskPanel?.SetStatus(changed ? "工作目录已更新；下一轮会开启对应会话" : "工作目录已记住");
    }

    private void StartNewThread()
    {
        _activeThreadId = null;
        _settings.LastThreadId = null;
        _settings.LastThreadWorkspace = null;
        _settings.ProviderSessions.Remove(_settings.AgentProviderId);
        _settings.ProviderSessionWorkspaces.Remove(_settings.AgentProviderId);
        SaveSettings();
        _taskPanel?.SetStatus($"下一次抛竿会开启新的 {AgentProviderCatalog.Label(_settings.AgentProviderId)} 会话");
        _taskPanel?.AddNotice("已准备新会话。之前的鱼获仍保留在鱼册中。");
    }

    private void SelectAgentProvider(string providerId)
    {
        if (_turnBusy || !AgentProviderCatalog.Available.Any(provider => provider.Id == providerId)) return;
        if (string.Equals(_settings.AgentProviderId, providerId, StringComparison.Ordinal)) return;
        _settings.AgentProviderId = providerId;
        _activeThreadId = null;
        _currentTurnId = null;
        SaveSettings();
        _taskPanel?.SetStatus($"已切换到 {AgentProviderCatalog.Label(providerId)}；该提供方的权限与停止边界不同");
        _taskPanel?.AddNotice(AgentProviderCatalog.Available.First(provider => provider.Id == providerId).Guidance);
    }

    private async void SubmitPrompt(string prompt)
    {
        if (_turnBusy || string.IsNullOrWhiteSpace(prompt)) return;
        var launchSequence = ++_turnLaunchSequence;
        if (string.IsNullOrWhiteSpace(CurrentWorkspace))
        {
            _taskPanel?.RequestWorkspaceSelection();
            if (string.IsNullOrWhiteSpace(CurrentWorkspace)) return;
        }

        _turnBusy = true;
        _interruptRequested = false;
        _stopRequestSent = false;
        _currentTurnId = null;
        _activity.EndTurn();
        _activity.Reset();
        _taskPanel?.AddUserMessage(prompt);
        _taskPanel?.SetBusy(true, false);
        var providerId = _settings.AgentProviderId;
        var providerLabel = AgentProviderCatalog.Label(providerId);
        _taskPanel?.SetStatus($"正在连接 {providerLabel}…");
        PondStatusText.Text = "鱼线准备中…";

        try
        {
            IAgentTaskLauncher launcher = GetLauncher(providerId);
            _agentLauncher = launcher;
            if (string.IsNullOrWhiteSpace(_activeThreadId))
            {
                var resumeId = providerId == AgentProviderIds.Codex
                    ? string.Equals(_settings.LastThreadWorkspace, CurrentWorkspace, StringComparison.OrdinalIgnoreCase) ? _settings.LastThreadId : null
                    : _settings.ProviderSessionWorkspaces.TryGetValue(providerId, out var priorWorkspace) && string.Equals(priorWorkspace, CurrentWorkspace, StringComparison.OrdinalIgnoreCase) &&
                      _settings.ProviderSessions.TryGetValue(providerId, out var priorSession) ? priorSession : null;
                _activeThreadId = await launcher.StartThreadAsync(GetProviderExecutable(providerId), CurrentWorkspace, resumeId);
                if (providerId == AgentProviderIds.Codex)
                {
                    _settings.LastThreadId = _activeThreadId;
                    _settings.LastThreadWorkspace = CurrentWorkspace;
                }
                else
                {
                    _settings.ProviderSessions[providerId] = _activeThreadId;
                    _settings.ProviderSessionWorkspaces[providerId] = CurrentWorkspace;
                }
                SaveSettings();
            }

            if (launchSequence != _turnLaunchSequence || !_turnBusy) return;
            var startedTurnId = await launcher.StartTurnAsync(_activeThreadId, prompt, CurrentWorkspace);
            if (launchSequence != _turnLaunchSequence || _terminalTurnIds.Contains(startedTurnId))
            {
                if (_currentTurnId == startedTurnId) _currentTurnId = null;
                return;
            }
            if (_currentTurnId is not null && !string.Equals(_currentTurnId, startedTurnId, StringComparison.Ordinal))
                throw new InvalidOperationException("turn/start 回包与已观察到的 turn/started ID 不一致。");
            _currentTurnId = startedTurnId;
            _taskPanel?.SetStatus($"{providerLabel} 已接手；离散工作事件会带动鱼线");
            if (_currentTurnId is not null) _taskPanel?.SetBusy(true, _agentLauncher?.Capabilities.CanCancel == true);
            if (_interruptRequested && _currentTurnId is not null)
            {
                _interruptRequested = false;
                InterruptTurn();
            }
        }
        catch (Exception ex)
        {
            if (launchSequence != _turnLaunchSequence || !_turnBusy) return;
            _turnBusy = false;
            _interruptRequested = false;
            _stopRequestSent = false;
            _activity.EndTurn();
            _inputMonitor.Stop();
            _currentTurnId = null;
            _taskPanel?.SetBusy(false, false);
            _taskPanel?.FinishAssistantMessage("这次没有启动成功。");
            _taskPanel?.SetStatus("没有抛竿成功");
            _taskPanel?.AddNotice($"无法启动 {providerLabel}：{ex.Message}");
            _taskPanel?.AddNotice(GetProviderSetupGuidance(providerId));
            SetFishingStage(FishingStage.Retracting);
            _stageDeadline = DateTime.Now.AddSeconds(1.8);
        }
    }

    private string GetProviderExecutable(string providerId) => providerId switch
    {
        AgentProviderIds.DeepSeekHarness => _settings.DeepSeekExecutable,
        AgentProviderIds.WorkBuddy => _settings.ClaudeExecutable,
        _ => _settings.CodexExecutable
    };

    private static string GetProviderSetupGuidance(string providerId) => providerId switch
    {
        AgentProviderIds.DeepSeekHarness => "检查 DeepSeek Harness headless CLI 是否可从 PATH 运行（命令名 dsh），并确认 headless profile 与登录已配置。应用不会自动安装 CLI 或读取凭据。",
        AgentProviderIds.WorkBuddy => "检查 Claude Code CLI（命令名 claude）和 Work Buddy MCP 是否已配置；在 Claude Code 中可用 /mcp 重新连接。Work Buddy 的原生授权仍需在它自己的界面处理。",
        _ => "检查本机 Codex CLI、登录状态和 app-server 启动条件。"
    };

    private IAgentTaskLauncher GetLauncher(string providerId)
    {
        if (_agentLaunchers.TryGetValue(providerId, out var existing)) return existing;
        IAgentTaskLauncher launcher = providerId switch
        {
            AgentProviderIds.DeepSeekHarness => new DeepSeekHarnessAdapter(GetProviderExecutable(providerId)),
            AgentProviderIds.WorkBuddy => new ClaudeCodeAgentAdapter(GetProviderExecutable(providerId)),
            _ => new CodexAppServerAdapter()
        };
        _agentLaunchers[providerId] = launcher;
        launcher.AgentEventReceived += OnAgentEvent;
        if (launcher is CodexAppServerAdapter codex)
        {
            _adapter = codex;
            codex.RequestReceived += OnServerRequest;
        }
        if (launcher is IAgentApprovalResponder approvalResponder)
            approvalResponder.ApprovalPromptReceived += OnAgentApprovalPrompt;
        if (launcher is IAgentSessionState sessionState)
            sessionState.ResumableSessionResolved += OnResumableSessionResolved;
        return launcher;
    }

    private void OnResumableSessionResolved(string threadKey, string sessionId)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (string.IsNullOrWhiteSpace(_agentLauncher?.ProviderId)) return;
                _settings.ProviderSessions[_agentLauncher.ProviderId] = sessionId;
                _settings.ProviderSessionWorkspaces[_agentLauncher.ProviderId] = CurrentWorkspace;
                SaveSettings();
            });
        }
        catch (InvalidOperationException) { }
    }

    private void OnAgentEvent(AgentEvent agentEvent)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try { Dispatcher.BeginInvoke(() => HandleAgentEvent(agentEvent)); }
        catch (InvalidOperationException) { }
    }

    private void HandleAgentEvent(AgentEvent agentEvent)
    {
        if (agentEvent.Kind == AgentEventKind.Disconnected)
        {
            HandleAgentDisconnected(agentEvent.Text ?? "Agent 连接中断。");
            return;
        }

        if (!string.IsNullOrWhiteSpace(agentEvent.SessionId) &&
            !string.Equals(agentEvent.SessionId, _activeThreadId, StringComparison.Ordinal)) return;

        if (agentEvent.Kind == AgentEventKind.Started)
        {
            if (string.IsNullOrWhiteSpace(agentEvent.TurnId)) return;
            if (!string.IsNullOrWhiteSpace(_currentTurnId) && !string.IsNullOrWhiteSpace(agentEvent.TurnId) &&
                !string.Equals(_currentTurnId, agentEvent.TurnId, StringComparison.Ordinal)) return;
        }
        else if (!string.IsNullOrWhiteSpace(_currentTurnId) &&
                 (string.IsNullOrWhiteSpace(agentEvent.TurnId) || !string.Equals(_currentTurnId, agentEvent.TurnId, StringComparison.Ordinal))) return;

        if (agentEvent.Kind == AgentEventKind.Completed && !string.Equals(agentEvent.Status, "completed", StringComparison.Ordinal))
            agentEvent = agentEvent with { Kind = AgentEventKind.Failed };

        if (agentEvent.Kind is AgentEventKind.Completed or AgentEventKind.Failed or AgentEventKind.Cancelled)
        {
            if (!string.IsNullOrWhiteSpace(agentEvent.TurnId) && _terminalTurnIds.Contains(agentEvent.TurnId)) return;
            if (!string.IsNullOrWhiteSpace(_currentTurnId) && !string.IsNullOrWhiteSpace(agentEvent.TurnId) &&
                !string.Equals(_currentTurnId, agentEvent.TurnId, StringComparison.Ordinal)) return;
        }

        if (agentEvent.Kind == AgentEventKind.Started)
        {
            _currentTurnId = agentEvent.TurnId ?? _currentTurnId;
            _activity.BeginTurn();
            try { if (_settings.TrackInputActivity) _inputMonitor.Start(); }
            catch (System.ComponentModel.Win32Exception ex) { _taskPanel?.AddNotice($"无法启用匿名键鼠计数：{ex.Message}"); }
            _fishing.Apply(CodexSignal.TurnStarted);
            SetFishingStage(_fishing.Stage);
            _taskPanel?.SetBusy(true, _agentLauncher?.Capabilities.CanCancel == true && !string.IsNullOrWhiteSpace(_currentTurnId));
            _taskPanel?.SetStatus($"{AgentProviderCatalog.Label(agentEvent.ProviderId)} turn 已开始，鱼钩落水");
            if (_interruptRequested && _currentTurnId is not null) InterruptTurn();
            return;
        }

        if (agentEvent.Kind == AgentEventKind.StepStarted)
        {
            if (_pendingRequests.Count == 0)
            {
                _fishing.Apply(CodexSignal.ItemStarted);
                SetFishingStage(_fishing.Stage);
            }
            _taskPanel?.SetStatus($"水面有动静，{AgentProviderCatalog.Label(agentEvent.ProviderId)} 正在处理工作项");
            return;
        }

        if (agentEvent.Kind == AgentEventKind.StepCompleted)
        {
            if (_pendingRequests.Count == 0)
            {
                _fishing.Apply(CodexSignal.ItemCompleted);
                SetFishingStage(_fishing.Stage);
            }
            _taskPanel?.SetStatus($"鱼影游过水面，{AgentProviderCatalog.Label(agentEvent.ProviderId)} 完成了一个工作项");
            return;
        }

        if (agentEvent.Kind == AgentEventKind.OutputDelta)
        {
            if (!string.IsNullOrEmpty(agentEvent.Text))
                _taskPanel?.AppendAssistantDelta(agentEvent.Text);
            return;
        }

        if (agentEvent.Kind == AgentEventKind.ApprovalRequired) return;
        if (agentEvent.Kind is not (AgentEventKind.Completed or AgentEventKind.Failed or AgentEventKind.Cancelled)) return;
        _currentTurnId = agentEvent.TurnId ?? _currentTurnId;
        if (!string.IsNullOrWhiteSpace(_currentTurnId)) _terminalTurnIds.Add(_currentTurnId);
        _turnBusy = false;
        _interruptRequested = false;
        _stopRequestSent = false;
        _inputMonitor.Stop();
        _activity.EndTurn();
        _taskPanel?.SetBusy(false, false);
        _taskPanel?.FinishAssistantMessage(agentEvent.Text);
        _taskPanel?.ClearRequest();
        _pendingRequests.Clear();

        if (agentEvent.Kind == AgentEventKind.Completed)
        {
            if (string.IsNullOrWhiteSpace(_currentTurnId))
            {
                _fishing.Apply(CodexSignal.TurnFailed);
                SetFishingStage(_fishing.Stage);
                _taskPanel?.AddNotice($"{AgentProviderCatalog.Label(agentEvent.ProviderId)} 报告 turn 已完成，但没有返回可用于去重的 turn ID，因此没有记入鱼册。");
                _taskPanel?.SetStatus("完成事件缺少 turn ID，未记鱼");
                _stageDeadline = DateTime.Now.AddSeconds(2);
                _currentTurnId = null;
            }
            else
            {
                if (agentEvent.ProviderId == AgentProviderIds.WorkBuddy &&
                    agentEvent.ProviderPayload is WorkBuddyRunEvidence workBuddyEvidence &&
                    (!workBuddyEvidence.InitializationSucceeded ||
                     (workBuddyEvidence.DurableWorkflowObserved && !workBuddyEvidence.VerifiedWorkflowCompletion)))
                {
                    var unverifiedTurnId = _currentTurnId;
                    if (!string.IsNullOrWhiteSpace(unverifiedTurnId)) _terminalTurnIds.Add(unverifiedTurnId);
                    _turnBusy = false;
                    _interruptRequested = false;
                    _stopRequestSent = false;
                    _inputMonitor.Stop();
                    _activity.EndTurn();
                    _taskPanel?.SetBusy(false, false);
                    _taskPanel?.FinishAssistantMessage(agentEvent.Text);
                    _taskPanel?.ClearRequest();
                    _pendingAgentApproval = null;
                    _fishing.Apply(CodexSignal.TurnFailed);
                    SetFishingStage(FishingStage.Retracting);
                    var missingInitialization = !workBuddyEvidence.InitializationSucceeded;
                    var workflowEndedUnsuccessfully = workBuddyEvidence.VerifiedWorkflowUnsuccessfulTerminal;
                    _taskPanel?.SetStatus(missingInitialization ? "Work Buddy 会话未完成 wb_init；没有收鱼" :
                        workflowEndedUnsuccessfully ? "Work Buddy 工作流已失败或取消；没有收鱼" : "Work Buddy 工作流终态未核实；没有收鱼");
                    _taskPanel?.AddNotice(missingInitialization
                        ? "未观察到 Work Buddy 必需的 wb_init 成功结果。为避免把未接入 Work Buddy 的 Claude 任务算作已完成，本轮没有收鱼。请检查 Claude Code 的 Work Buddy MCP 连接后重试。"
                        : workflowEndedUnsuccessfully
                            ? "wb_status 对本轮已启动的工作流返回了明确的失败或取消终态。该 turn 没有收鱼；可以在 Work Buddy 状态界面检查后台运行情况。"
                            : "检测到 Work Buddy 工作流调用，但 wb_status 没有为同一个 workflow_run_id 返回明确的成功终态，因此没有记鱼。请在 Work Buddy 状态界面确认终态后再继续。");
                    PondStatusText.Text = missingInitialization ? "等待 Work Buddy 初始化" :
                        workflowEndedUnsuccessfully ? "Work Buddy 工作流未成功" : "等待确认 Work Buddy 工作流";
                    _stageDeadline = DateTime.Now.AddSeconds(3);
                    _currentTurnId = null;
                    return;
                }
                var completedTurnId = _currentTurnId;
                try
                {
                    var environment = PondEnvironment.At(DateTime.Now);
                    var activeEvent = _eventScheduler.Update(DateTimeOffset.Now, IsVisible, environment.Season);
                    var catchContext = new FishCatchContext(environment, activeEvent, _activity.Score, DateTimeOffset.Now);
                    var added = _fishBook.TryCatchCompletedTurn("completed", completedTurnId, catchContext, out var fish);
                    if (added && fish is not null) PondArt.CatchFishId = fish.FishId;
                    _fishing.Apply(CodexSignal.TurnSucceeded);
                    SetFishingStage(_fishing.Stage);
                    if (added && fish is not null)
                    {
                        UnlockAccessoriesForCatch();
                        PondStatusText.Text = $"收获 {fish.FishName}！";
                        _taskPanel?.SetStatus($"这一轮完成，钓起了 {fish.FishName}");
                        _taskPanel?.AddNotice($"鱼线收上来一条「{fish.FishName}」。这条 turn 已记入本地鱼册。");
                    }
                    else
                    {
                        PondStatusText.Text = "这条 turn 已记入鱼册";
                        _taskPanel?.SetStatus("收到重复完成事件，鱼册没有重复添加");
                    }
                    UpdateFishCount();
                    App.QueueCloudSync(_settings, _settingsStore, _fishBook);
                    _stageDeadline = DateTime.Now.AddSeconds(2.8);
                    _currentTurnId = null;
                }
                catch (Exception ex)
                {
                    PondStatusText.Text = "鱼册暂时无法保存";
                    _taskPanel?.SetStatus($"{AgentProviderCatalog.Label(agentEvent.ProviderId)} 已完成，但鱼册写入失败");
                    _taskPanel?.AddNotice($"鱼获记录失败：{ex.Message}");
                    _fishing.Apply(CodexSignal.TurnFailed);
                    SetFishingStage(FishingStage.Retracting);
                    _stageDeadline = DateTime.Now.AddSeconds(2);
                    _currentTurnId = null;
                }
            }
            return;
        }

        if (agentEvent.Kind == AgentEventKind.Cancelled)
        {
            _fishing.Apply(CodexSignal.TurnInterrupted);
            _taskPanel?.AddNotice($"{AgentProviderCatalog.Label(agentEvent.ProviderId)} turn 已中断，没有收获鱼。");
            _taskPanel?.SetStatus("任务已中断，鱼线收回");
        }
        else
        {
            _fishing.Apply(CodexSignal.TurnFailed);
            _taskPanel?.AddNotice($"{agentEvent.ProviderId} turn 未成功完成（{agentEvent.Status ?? "未知状态"}），没有收获鱼。");
            _taskPanel?.SetStatus("任务未完成，鱼线收回");
        }
        SetFishingStage(_fishing.Stage);
        _stageDeadline = DateTime.Now.AddSeconds(2);
        _currentTurnId = null;
    }

    private void OnServerRequest(CodexApprovalRequest request)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if ((!string.IsNullOrWhiteSpace(request.SessionId) && request.SessionId != _activeThreadId) ||
                (!string.IsNullOrWhiteSpace(request.TurnId) && !string.IsNullOrWhiteSpace(_currentTurnId) && request.TurnId != _currentTurnId))
            {
                _ = RejectStaleRequestAsync(request);
                return;
            }
            _pendingRequests.Enqueue(request);
            if (_pendingRequests.Count == 1)
            {
                _fishing.Apply(CodexSignal.ApprovalRequested);
                SetFishingStage(_fishing.Stage);
                _taskPanel?.ShowRequest(request);
                _taskPanel?.SetStatus("Codex 正在等待你处理请求");
                PondStatusText.Text = "鱼线停住，等待你的决定";
            }
        });
    }

    private void OnAgentApprovalPrompt(AgentApprovalPrompt prompt)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!_turnBusy || !string.Equals(prompt.ProviderId, _agentLauncher?.ProviderId, StringComparison.Ordinal)) return;
                _pendingAgentApproval = prompt;
                _fishing.Apply(CodexSignal.ApprovalRequested);
                SetFishingStage(_fishing.Stage);
                OpenTaskPanel();
                _taskPanel?.ShowExternalApproval(prompt);
                PondStatusText.Text = $"{AgentProviderCatalog.Label(prompt.ProviderId)} 等待一次性授权";
            });
        }
        catch (InvalidOperationException) { }
    }

    private async void HandleAgentApprovalDecision(AgentApprovalPrompt prompt, bool allowOnce)
    {
        if (_agentLauncher is not IAgentApprovalResponder responder ||
            !string.Equals(prompt.ProviderId, _agentLauncher.ProviderId, StringComparison.Ordinal)) return;
        try
        {
            await responder.RespondToApprovalAsync(prompt, allowOnce);
            if (ReferenceEquals(_pendingAgentApproval, prompt)) _pendingAgentApproval = null;
            _taskPanel?.ClearRequest();
            _fishing.Apply(CodexSignal.ApprovalResolved);
            SetFishingStage(_fishing.Stage);
            _taskPanel?.SetStatus(allowOnce ? "已允许当前这一次操作" : "已拒绝当前操作");
        }
        catch (Exception ex)
        {
            _taskPanel?.SetStatus("未能回应 Agent 权限请求；默认拒绝");
            _taskPanel?.AddNotice($"审批桥回应失败：{ex.Message}");
        }
    }

    private async Task RejectStaleRequestAsync(CodexApprovalRequest request)
    {
        if (_adapter is null) return;
        try { await _adapter.RejectUnsupportedRequestAsync(request); }
        catch (Exception ex) { _taskPanel?.AddNotice($"旧请求无法安全回应：{ex.Message}"); }
    }

    private async void HandleApprovalDecision(CodexApprovalRequest request, bool approve)
    {
        if (_adapter is null || !_adapter.Capabilities.CanRespondToApprovals) return;
        try
        {
            if (request.Kind == ApprovalKind.Unsupported)
                await _adapter.RejectUnsupportedRequestAsync(request);
            else
                await _adapter.RespondToApprovalAsync(request, approve);
            FinishPendingRequest(request, approve ? "已允许这一次请求" : "已拒绝或取消请求");
        }
        catch (Exception ex)
        {
            _taskPanel?.SetStatus("未能回应 Codex 请求");
            _taskPanel?.AddNotice($"请求回应失败：{ex.Message}");
        }
    }

    private async void HandleUserInput(CodexApprovalRequest request, IReadOnlyDictionary<string, string> answers)
    {
        if (_adapter is null || !_adapter.Capabilities.CanAcceptUserInput) return;
        try
        {
            await _adapter.RespondToUserInputAsync(request, answers);
            FinishPendingRequest(request, "补充信息已发送给 Codex");
        }
        catch (Exception ex)
        {
            _taskPanel?.SetStatus("未能发送补充信息");
            _taskPanel?.AddNotice($"信息发送失败：{ex.Message}");
        }
    }

    private void FinishPendingRequest(CodexApprovalRequest request, string status)
    {
        if (_pendingRequests.Count > 0 && _pendingRequests.Peek().RequestId.GetRawText() == request.RequestId.GetRawText())
            _pendingRequests.Dequeue();
        _taskPanel?.ClearRequest();
        if (_pendingRequests.Count > 0)
        {
            var next = _pendingRequests.Peek();
            _taskPanel?.ShowRequest(next);
            _taskPanel?.SetStatus("Codex 还有一项请求等待处理");
            return;
        }
        _fishing.Apply(CodexSignal.ApprovalResolved);
        SetFishingStage(_fishing.Stage);
        _taskPanel?.SetStatus(status);
        if (_turnBusy) _taskPanel?.SetBusy(true, _agentLauncher?.Capabilities.CanCancel == true && !string.IsNullOrWhiteSpace(_currentTurnId));
    }

    private void HandleAgentDisconnected(string message)
    {
        var hadActiveTurn = _turnBusy;
        _turnBusy = false;
        _inputMonitor.Stop();
        _activity.EndTurn();
        _currentTurnId = null;
        _activeThreadId = null;
        _interruptRequested = false;
        _stopRequestSent = false;
        _pendingRequests.Clear();
        _pendingAgentApproval = null;
        _taskPanel?.ClearRequest();
        _taskPanel?.SetBusy(false, false);
        if (hadActiveTurn) _taskPanel?.FinishAssistantMessage("连接中断前，没有收到完整答复。");
        _fishing.Apply(CodexSignal.Disconnected);
        SetFishingStage(_fishing.Stage);
        PondStatusText.Text = "Codex 连接中断";
        _taskPanel?.SetStatus($"{AgentProviderCatalog.Label(_agentLauncher?.ProviderId)} 连接中断");
        if (hadActiveTurn) _taskPanel?.AddNotice($"连接中断，没有记录鱼获。原因：{message}");
        _stageDeadline = DateTime.Now.AddSeconds(4);
    }

    private async void InterruptTurn()
    {
        if (!_turnBusy) return;
        if (_stopRequestSent) return;
        if (_agentLauncher?.Capabilities.CanCancel != true)
        {
            _taskPanel?.SetStatus("当前 Agent 提供方不支持取消这轮任务");
            return;
        }
        if (_currentTurnId is null || _activeThreadId is null || _agentLauncher is null)
        {
            _interruptRequested = true;
            _taskPanel?.SetStatus("正在等待 turn ID，拿到后会立即请求收线");
            return;
        }

        _interruptRequested = false;
        _stopRequestSent = true;
        _taskPanel?.SetStopPending(true);
        _taskPanel?.SetStatus(_agentLauncher.Capabilities.CancellationBoundary == AgentCancellationBoundary.OuterProcessOnly
            ? "正在中断外层会话；Work Buddy 后台流程可能继续，且不会自动记鱼"
            : $"正在请求 {AgentProviderCatalog.Label(_agentLauncher.ProviderId)} 停止；收到终态后才会收线");
        try
        {
            await _agentLauncher.InterruptTurnAsync(_activeThreadId, _currentTurnId);
        }
        catch (Exception ex)
        {
            _stopRequestSent = false;
            _taskPanel?.SetStopPending(false);
            _taskPanel?.SetStatus("停止请求未能送达");
            _taskPanel?.AddNotice($"收线请求未能送达：{ex.Message}");
        }
    }

    private void SetFishingStage(FishingStage stage)
    {
        PondArt.Stage = stage;
        if (_pendingRequests.Count > 0 && stage != FishingStage.Catching)
            PondStatusText.Text = "鱼线停住，等待你的决定";
        else
        {
            PondStatusText.Text = stage switch
            {
                FishingStage.Idle => "池塘安静，鱼线待命",
                FishingStage.Casting => "鱼钩落水，开始垂钓",
                FishingStage.Biting => "水面有动静…",
                FishingStage.Fighting => "鱼影在水下穿行",
                FishingStage.WaitingForApproval => "鱼线停住，等待你的决定",
                FishingStage.Catching => "鱼上钩了！",
                FishingStage.Retracting => "任务未完成，鱼线收回",
                FishingStage.ConnectionLost => "Codex 连接中断",
                _ => "池塘安静，鱼线待命"
            };
        }
    }

    private void UpdateFishCount()
    {
        var result = _fishBook.ReadResult();
        if (!result.CanAppend)
        {
            FishCountText.Text = "鱼册 · 需检查";
            if (_lastFishBookError != result.Error)
            {
                _lastFishBookError = result.Error;
                _taskPanel?.AddNotice($"鱼册无法安全读取，原文件未改动：{result.Error}");
            }
        }
        else
        {
            _lastFishBookError = null;
            FishCountText.Text = $"鱼册 · {result.Records.Count}";
        }
        _fishBookWindow?.Refresh(_fishBook);
    }

    private void UnlockAccessoriesForCatch()
    {
        var result = _fishBook.ReadResult();
        if (!result.CanAppend)
        {
            UpdateFishCount();
            return;
        }
        if (AccessoryUnlockRules.Apply(_settings, result.Records))
        {
            SaveSettings();
            _appearanceWindow?.RefreshAccessories();
        }
    }

    private void PlaceWindowNearPond(Window window)
    {
        var leftBound = SystemParameters.VirtualScreenLeft;
        var topBound = SystemParameters.VirtualScreenTop;
        var rightBound = leftBound + SystemParameters.VirtualScreenWidth;
        var bottomBound = topBound + SystemParameters.VirtualScreenHeight;
        var width = window.Width > 0 ? window.Width : window.ActualWidth;
        var height = window.Height > 0 ? window.Height : window.ActualHeight;
        var maxLeft = Math.Max(leftBound, rightBound - width);
        var maxTop = Math.Max(topBound, bottomBound - height);
        var preferredLeft = Left + Width - 18;
        if (preferredLeft + width > rightBound) preferredLeft = Left - width + 18;
        window.Left = Math.Clamp(preferredLeft, leftBound, maxLeft);
        window.Top = Math.Clamp(Top + 8, topBound, maxTop);
    }

    private void OpenFishBook()
    {
        if (_fishBookWindow is null)
        {
            _fishBookWindow = new FishBookWindow(this, _fishBook);
            PlaceWindowNearPond(_fishBookWindow);
            _fishBookWindow.Closed += (_, _) => _fishBookWindow = null;
        }
        else
        {
            _fishBookWindow.Refresh(_fishBook);
        }
        if (!_fishBookWindow.IsVisible) _fishBookWindow.Show();
        _fishBookWindow.Activate();
    }

    private void ShowPond()
    {
        if (!IsVisible) Show();
        Activate();
    }

    private void HidePond() => Hide();

    private void ExitApplication()
    {
        _exitRequested = true;
        _inputMonitor.Stop();
        _taskPanel?.CloseForShutdown();
        _fishBookWindow?.Close();
        _appearanceWindow?.Close();
        _gameWindow?.Close();
        Close();
        Application.Current.Shutdown();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested) return;
        e.Cancel = true;
        Hide();
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
            App.QueueCloudSync(_settings, _settingsStore, _fishBook);
            _lastSettingsError = null;
        }
        catch (Exception ex)
        {
            PondStatusText.Text = "设置暂时无法保存";
            _taskPanel?.SetStatus("设置保存失败；本次更改仍暂存在当前运行中");
            if (_lastSettingsError != ex.Message)
            {
                _lastSettingsError = ex.Message;
                _taskPanel?.AddNotice($"设置写入失败：{ex.Message}");
            }
        }
    }

    private void PondHitTarget_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pointerStart = e.GetPosition(this);
        _pondPressed = true;
        _pondDragStarted = false;
        PondHitTarget.CaptureMouse();
        e.Handled = true;
    }

    private void PondHitTarget_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pondPressed || e.LeftButton != MouseButtonState.Pressed || _pondDragStarted) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _pointerStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _pointerStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pondDragStarted = true;
        PondHitTarget.ReleaseMouseCapture();
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void PondHitTarget_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var click = _pondPressed && !_pondDragStarted;
        var point = e.GetPosition(this);
        _pondPressed = false;
        PondHitTarget.ReleaseMouseCapture();
        if (click)
        {
            if (e.ClickCount > 1 || !IsInsideWater(point)) OpenTaskPanel();
            else
            {
                PondArt.CreateRipple(point);
                if (!_turnBusy) PondStatusText.Text = "水面泛起一圈涟漪";
            }
        }
        e.Handled = true;
    }

    private bool IsInsideWater(Point point)
    {
        var centerX = _settings.Theme == PondTheme.FusionCE ? 176 : 268;
        var centerY = _settings.Theme == PondTheme.FusionCE ? 151 : 214;
        var radiusX = _settings.Theme == PondTheme.FusionCE ? 104 : 162;
        var radiusY = _settings.Theme == PondTheme.FusionCE ? 59 : 74;
        var dx = (point.X - centerX) / radiusX;
        var dy = (point.Y - centerY) / radiusY;
        return dx * dx + dy * dy <= 1;
    }

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void FishCountButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFishBook();
    }

    private void PondStatusButton_Click(object sender, RoutedEventArgs e)
    {
        OpenTaskPanel();
    }

    private void PondHitTarget_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OpenTaskPanel();
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            var center = _settings.Theme == PondTheme.FusionCE ? new Point(176, 151) : new Point(268, 214);
            PondArt.CreateRipple(center);
            if (!_turnBusy) PondStatusText.Text = "水面泛起一圈涟漪";
            e.Handled = true;
        }
    }

    private void PondHitTarget_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        PondFocusRing.Visibility = Visibility.Visible;

    private void PondHitTarget_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        PondFocusRing.Visibility = Visibility.Collapsed;

    private void HidePondButton_Click(object sender, RoutedEventArgs e) => HidePond();

    private void InitializeTrayIconCleanup()
    {
        if (_trayIcon is null) return;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        _animationTimer.Stop();
        _environmentTimer.Stop();
        _positionSaveTimer.Stop();
        _inputMonitor.Dispose();
        SavePosition();
        InitializeTrayIconCleanup();
        if (_adapter is not null)
        {
            try { _adapter.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception) { }
        }
        foreach (var launcher in _agentLaunchers.Values)
        {
            if (ReferenceEquals(launcher, _adapter)) continue;
            if (launcher is IAsyncDisposable asyncDisposable)
            {
                try { asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch (Exception) { }
            }
        }
        base.OnClosed(e);
    }

    private static System.Windows.Media.SolidColorBrush BrushColor(string color) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
}
