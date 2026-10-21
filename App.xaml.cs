using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace CodexPond;

public partial class App : Application
{
    private readonly DispatcherTimer _steamCallbackTimer = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _cloudSyncTimer = new(DispatcherPriority.Background);
    private PondSettingsStore? _settingsStore;
    private FishBookStore? _fishBook;
    private PondSettings? _settings;
    private bool _steamLaunched;

    public static ISteamPlatformService? Platform { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Any(argument => string.Equals(argument, "--permission-mcp-stdio", StringComparison.Ordinal)))
        {
            try
            {
                await ClaudePermissionMcpServer.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput()).ConfigureAwait(true);
            }
            catch (Exception) { }
            Shutdown();
            return;
        }

        _steamLaunched = e.Args.Any(argument => string.Equals(argument, "--steam", StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SteamAppId"));
        _settingsStore = new PondSettingsStore();
        _fishBook = new FishBookStore();
        _settings = _settingsStore.Load();
        Platform = new SteamPlatformService();
        Platform.Initialize();
        Platform.SyncLocalSave(_settings, _settingsStore, _fishBook);
        var startupFishRead = _fishBook.ReadResult();
        if (startupFishRead.CanAppend)
            Platform.OnFishCaught(startupFishRead.Records.Count, startupFishRead.Records.Select(record => record.FishId).Distinct(StringComparer.Ordinal).Count());
        _steamCallbackTimer.Interval = TimeSpan.FromMilliseconds(100);
        _steamCallbackTimer.Tick += (_, _) => Platform?.PumpCallbacks();
        _steamCallbackTimer.Start();
        _cloudSyncTimer.Interval = TimeSpan.FromMilliseconds(900);
        _cloudSyncTimer.Tick += (_, _) =>
        {
            _cloudSyncTimer.Stop();
            if (_settings is not null && _settingsStore is not null && _fishBook is not null)
                Platform?.SyncLocalSave(_settings, _settingsStore, _fishBook);
        };

        var main = new MainWindow();
        MainWindow = main;
        var startIndependent = _steamLaunched || _settings.LastMode == PondMode.IndependentFishing;
        if (!startIndependent)
        {
            main.Show();
            return;
        }

        main.Hide();
        ShowFishingGame(main, launchedFromSteam: _steamLaunched);
    }

    public static void QueueCloudSync(PondSettings settings, PondSettingsStore settingsStore, FishBookStore fishBook)
    {
        if (Current is not App app || Platform?.IsAvailable != true) return;
        app._settings = settings;
        app._settingsStore = settingsStore;
        app._fishBook = fishBook;
        app._cloudSyncTimer.Stop();
        app._cloudSyncTimer.Start();
    }

    private void ShowFishingGame(MainWindow main, bool launchedFromSteam)
    {
        var game = new FishingGameWindow(_settings!, _settingsStore!, _fishBook!, launchedFromSteam);
        bool returnToAgentPond = !launchedFromSteam;
        game.AgentModeRequested += () =>
        {
            returnToAgentPond = true;
            _settings!.LastMode = PondMode.AgentPond;
            _settingsStore!.Save(_settings);
            main.Show();
            main.Activate();
            game.Close();
        };
        game.Closed += (_, _) =>
        {
            if (returnToAgentPond)
            {
                main.Show();
                main.Activate();
            }
            else Shutdown();
        };
        game.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _steamCallbackTimer.Stop();
        _cloudSyncTimer.Stop();
        Platform?.Dispose();
        Platform = null;
        base.OnExit(e);
    }
}
