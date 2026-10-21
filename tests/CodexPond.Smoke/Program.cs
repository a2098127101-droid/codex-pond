using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexPond;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--render-pond")
        {
            if (args.Length < 2) throw new ArgumentException("Provide an output PNG path.");
            RenderPond(args[1]);
            return 0;
        }
        if (args.Length > 0 && args[0] == "--render-window")
        {
            if (args.Length < 2) throw new ArgumentException("Provide an output PNG path.");
            RenderWindow(args[1]);
            return 0;
        }
        if (args.Length > 0 && args[0] == "--render-themes")
        {
            if (args.Length < 2) throw new ArgumentException("Provide an output PNG path.");
            RenderThemeSheet(args[1]);
            return 0;
        }
        if (args.Length > 0 && args[0] == "--fake-app-server")
            return await RunFakeServerAsync();
        if (args.Length > 0 && args[0] == "--fake-dsh")
            return await RunFakeDshAsync(args.Contains("--fake-no-ready"), args.Contains("--fake-bad-ready"));
        if (args.Length > 0 && args[0] == "--fake-claude")
        {
            var resultMarkerIndex = Array.IndexOf(args, "--fake-result-marker");
            var resultMarker = resultMarkerIndex >= 0 && resultMarkerIndex + 1 < args.Length
                ? args[resultMarkerIndex + 1]
                : null;
            return await RunFakeClaudeAsync(args.Contains("--fake-wrong-status"), !args.Contains("--fake-no-approval"),
                args.Contains("--fake-no-result-after-first"), resultMarker);
        }

        var scratch = Path.Combine(Path.GetTempPath(), "codex-pond-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            RenderPond(Path.Combine(scratch, "pond-preview.png"));
            CheckStateMachine();
            CheckFishLedger(scratch);
            CheckEnvironmentActivityAndCatalog(scratch);
            CheckLegacyCompatibility(scratch);
            CheckPondAssets();
            CheckGenericProviderContracts();
            CheckAgentEventIsolation();
            CheckApprovalBoundary();
            CheckWorkBuddyTerminalEvidence();
            CheckDualModesAndSteam(scratch);
            await CheckProcessConnectorsAsync(scratch);
            await CheckProtocolAsync(scratch);
            Console.WriteLine("PASS: settings corruption recovery, provider/session/turn event isolation, shared Steam fish stats for Agent and manual catches, pond assets and seasons, fish ledger compatibility, fake DSH and Work Buddy process flows, approval boundaries, cloud merge/deduplication, and authoritative Codex turn IDs.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void RenderPond(string outputPath)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var scene = new Grid { Width = 500, Height = 313 };
                var art = new Image
                {
                    Width = 500,
                    Height = 313,
                    Stretch = Stretch.Fill,
                    Source = PondAssets.Theme(PondTheme.FusionCE)
                };
                RenderOptions.SetBitmapScalingMode(art, BitmapScalingMode.NearestNeighbor);
                var mask = new ImageBrush(PondAssets.Theme(PondTheme.FusionCE)) { Stretch = Stretch.Fill };
                mask.Freeze();
                var renderer = new PondRenderer { Width = 500, Height = 313, OpacityMask = mask };
                scene.Children.Add(art);
                scene.Children.Add(renderer);
                scene.Measure(new Size(500, 313));
                scene.Arrange(new Rect(0, 0, 500, 313));
                scene.UpdateLayout();
                var bitmap = new RenderTargetBitmap(500, 313, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(scene);
                var pixels = new byte[500 * 313 * 4];
                bitmap.CopyPixels(pixels, 500 * 4, 0);
                if (pixels[3] != 0) throw new InvalidOperationException("Pond corner should remain transparent.");
                var pondAlpha = pixels[(180 * 500 + 200) * 4 + 3];
                if (pondAlpha == 0) throw new InvalidOperationException("Pond water should render as an opaque pixel.");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var fullPath = Path.GetFullPath(outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                using var output = File.Create(fullPath);
                encoder.Save(output);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("Could not render pond preview.", failure);
    }

    private static void RenderWindow(string outputPath)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow();
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(window.Width, window.Height));
                root.Arrange(new Rect(0, 0, window.Width, window.Height));
                root.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var pixels = new byte[(int)window.Width * (int)window.Height * 4];
                bitmap.CopyPixels(pixels, (int)window.Width * 4, 0);
                if (pixels[3] != 0) throw new InvalidOperationException("Window corner should remain transparent.");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var fullPath = Path.GetFullPath(outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                using var output = File.Create(fullPath);
                encoder.Save(output);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("Could not render full window preview.", failure);
    }

    private static void RenderThemeSheet(string outputPath)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RenderThemeSheetCore(outputPath); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("Could not render theme preview.", failure);
    }

    private static void RenderThemeSheetCore(string outputPath)
    {
        const int tileWidth = 500;
        const int tileHeight = 313;
        const int labelHeight = 28;
        const int columns = 3;
        const int rows = 2;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(232, 229, 219)), null, new Rect(0, 0, tileWidth * columns, (tileHeight + labelHeight) * rows));
            var applyTheme = typeof(MainWindow).GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("MainWindow.ApplyTheme could not be found.");
            var updateEnvironment = typeof(MainWindow).GetMethod("UpdateEnvironmentAndEvent", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("MainWindow.UpdateEnvironmentAndEvent could not be found.");
            foreach (var (theme, index) in Enum.GetValues<PondTheme>().Select((theme, index) => (theme, index)))
            {
                var window = new MainWindow();
                applyTheme.Invoke(window, new object[] { theme, false });
                updateEnvironment.Invoke(window, new object[] { DateTimeOffset.Now });
                if (index == 0)
                {
                    var picker = new AppearanceWindow(window, new PondSettings());
                    var pickerRoot = (FrameworkElement)picker.Content;
                    pickerRoot.Measure(new Size(picker.Width, picker.Height));
                    pickerRoot.Arrange(new Rect(0, 0, picker.Width, picker.Height));
                    pickerRoot.UpdateLayout();
                }
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(tileWidth, tileHeight));
                root.Arrange(new Rect(0, 0, tileWidth, tileHeight));
                root.UpdateLayout();
                var bitmap = new RenderTargetBitmap(tileWidth, tileHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var pixels = new byte[tileWidth * tileHeight * 4];
                bitmap.CopyPixels(pixels, tileWidth * 4, 0);
                if (pixels[3] > 1) throw new InvalidOperationException($"Theme {theme} lost its transparent corner.");
                if (!pixels.Where((_, pixelIndex) => pixelIndex % 4 == 3).Any(alpha => alpha > 16))
                    throw new InvalidOperationException($"Theme {theme} did not render any scene pixels.");
                var x = (index % columns) * tileWidth;
                var y = (index / columns) * (tileHeight + labelHeight);
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(244, 241, 231)), null, new Rect(x, y, tileWidth, tileHeight + labelHeight));
                dc.DrawText(new FormattedText(PondThemeCatalog.Label(theme), System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 13,
                    new SolidColorBrush(Color.FromRgb(66, 71, 55)), 96), new Point(x + 12, y + 5));
                dc.DrawImage(bitmap, new Rect(x, y + labelHeight, tileWidth, tileHeight));
            }
        }
        var sheet = new RenderTargetBitmap(tileWidth * columns, (tileHeight + labelHeight) * rows, 96, 96, PixelFormats.Pbgra32);
        sheet.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(sheet));
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using var output = File.Create(fullPath);
        encoder.Save(output);
    }

    private static void CheckStateMachine()
    {
        var machine = new FishingStateMachine();
        Assert(machine.Apply(CodexSignal.TurnStarted) == FishingStage.Casting, "turn start should cast the line");
        Assert(machine.Apply(CodexSignal.ItemStarted) == FishingStage.Biting, "work item start should create a bite");
        Assert(machine.Apply(CodexSignal.ApprovalRequested) == FishingStage.WaitingForApproval, "approval should pause fishing");
        Assert(machine.Apply(CodexSignal.ApprovalResolved) == FishingStage.Biting, "approval response should resume fishing");
        Assert(machine.Apply(CodexSignal.ItemCompleted) == FishingStage.Fighting, "completed item should move the fish");
        Assert(machine.Apply(CodexSignal.TurnSucceeded) == FishingStage.Catching, "successful turn should land a fish");
        Assert(machine.Reset() == FishingStage.Idle, "caught animation should return to idle");
        Assert(machine.Apply(CodexSignal.TurnFailed) == FishingStage.Retracting, "failed turn should retract without catch");
        Assert(machine.Apply(CodexSignal.Disconnected) == FishingStage.ConnectionLost, "disconnect should be visible");
    }

    private static void CheckFishLedger(string scratch)
    {
        var settingsStore = new PondSettingsStore(Path.Combine(scratch, "settings"));
        var settings = settingsStore.Load();
        settings.LastWorkspace = scratch;
        settings.HasChosenWorkspace = true;
        settings.Theme = PondTheme.D;
        settings.TrackInputActivity = false;
        settings.UnlockedAccessoryIds.Add("grass-block");
        settings.RightDecorId = "spring-bloom";
        settingsStore.Save(settings);
        var restored = settingsStore.Load();
        Assert(restored.HasChosenWorkspace && restored.LastWorkspace == scratch, "settings should persist the chosen workspace");
        Assert(restored.Theme == PondTheme.D && !restored.TrackInputActivity && restored.UnlockedAccessoryIds.Contains("grass-block") &&
               restored.RightDecorId == "spring-bloom", "theme, input opt-out, unlocks and equipment should persist");

        var corruptSettingsDirectory = Path.Combine(scratch, "corrupt-settings");
        Directory.CreateDirectory(corruptSettingsDirectory);
        var corruptSettingsPath = Path.Combine(corruptSettingsDirectory, "settings.json");
        const string corruptSettingsContent = "{ not valid settings JSON";
        File.WriteAllText(corruptSettingsPath, corruptSettingsContent);
        var corruptSettingsStore = new PondSettingsStore(corruptSettingsDirectory);
        var recoveredSettings = corruptSettingsStore.Load();
        var recovery = corruptSettingsStore.LastLoadResult;
        Assert(recovery.Status == PondSettingsLoadStatus.RecoveredCorrupt && recovery.BackupPath is not null,
            "corrupt settings should return defaults only after creating a recovery backup");
        Assert(!File.Exists(corruptSettingsPath) && File.ReadAllText(recovery.BackupPath!) == corruptSettingsContent,
            "settings recovery must preserve the exact corrupt source instead of overwriting it");
        corruptSettingsStore.Save(recoveredSettings);
        Assert(new PondSettingsStore(corruptSettingsDirectory).Load().Theme == PondTheme.FusionCE,
            "the recovered defaults should be writable and readable on the next launch");

        var store = new FishBookStore(Path.Combine(scratch, "fishbook"));
        Assert(!store.TryCatchCompletedTurn("failed", "failed-turn", out _), "failed turns must not enter the fish book");
        Assert(!store.TryCatchCompletedTurn("interrupted", "cancelled-turn", out _), "interrupted turns must not enter the fish book");
        Assert(!store.TryCatchCompletedTurn("disconnected", "disconnected-turn", out _), "disconnected turns must not enter the fish book");
        Assert(!store.TryCatchCompletedTurn("inProgress", "active-turn", out _), "active or unknown turn states must not enter the fish book");
        Assert(!store.TryCatchCompletedTurn("COMPLETED", "noncanonical-turn", out _), "only the exact protocol completed state may produce a fish");
        Assert(store.ReadAll().Count == 0, "unsuccessful turns must leave the fish book empty");
        Assert(store.TryCatchCompletedTurn("completed", "turn-success-1", out var first) && first is not null, "completed turn should produce one fish");
        Assert(!store.TryCatchCompletedTurn("completed", "turn-success-1", out _), "duplicate completion must not produce a second fish");
        Assert(store.ReadAll().Count == 1, "duplicate completion must keep exactly one record");
        Assert(store.TryCatchCompletedTurn("completed", "turn-success-2", out _), "a later successful turn should produce its own fish");
        Assert(store.ReadAll().Count == 2, "each distinct successful turn should produce exactly one fish");
        var ledgerJson = File.ReadAllText(Path.Combine(scratch, "fishbook", "fish-book.json"));
        Assert(!ledgerJson.Contains("ActivityScore", StringComparison.OrdinalIgnoreCase) && !ledgerJson.Contains("KeyCode", StringComparison.OrdinalIgnoreCase),
            "anonymous per-turn activity details must never be persisted to the fish ledger");

        var corruptDirectory = Path.Combine(scratch, "corrupt-fish-book");
        Directory.CreateDirectory(corruptDirectory);
        var corruptPath = Path.Combine(corruptDirectory, "fish-book.json");
        const string corruptContent = "{ this is intentionally not valid JSON";
        File.WriteAllText(corruptPath, corruptContent);
        var corruptStore = new FishBookStore(corruptDirectory);
        Assert(corruptStore.ReadResult().Status == FishBookReadStatus.Corrupt, "corrupt fish book must not be treated as empty");
        var refusedWrite = false;
        try { corruptStore.TryCatchCompletedTurn("completed", "must-not-overwrite", out _); }
        catch (FishBookStorageException ex) when (ex.Status == FishBookReadStatus.Corrupt) { refusedWrite = true; }
        Assert(refusedWrite && File.ReadAllText(corruptPath) == corruptContent, "catch writes must preserve the corrupt source file");
    }

    private static void CheckEnvironmentActivityAndCatalog(string scratch)
    {
        Assert(Enum.GetValues<PondTheme>().Length == 6, "the appearance picker should expose six layouts");
        Assert(Enum.GetValues<PondTheme>().Select(PondThemeCatalog.Label).Distinct().Count() == 6, "all six layouts should have distinct labels");
        Assert(FishCatalog.All.Count == 20, "the fish catalog should contain twenty species");

        var preDawn = PondEnvironment.At(new DateTime(2026, 2, 28, 4, 59, 0));
        var dawn = PondEnvironment.At(new DateTime(2026, 3, 1, 5, 0, 0));
        var day = PondEnvironment.At(new DateTime(2026, 3, 1, 8, 0, 0));
        var dusk = PondEnvironment.At(new DateTime(2026, 3, 1, 17, 0, 0));
        var night = PondEnvironment.At(new DateTime(2026, 3, 1, 20, 0, 0));
        Assert(preDawn.Season == PondSeason.Winter && preDawn.Phase == DayPhase.Night, "season and phase should follow the local calendar before spring dawn");
        Assert(dawn.Season == PondSeason.Spring && dawn.Phase == DayPhase.Dawn, "March 1 at 05:00 should be spring dawn");
        Assert(day.Phase == DayPhase.Day && dusk.Phase == DayPhase.Dusk && night.Phase == DayPhase.Night, "day-phase boundaries should be 08:00, 17:00, and 20:00");
        Assert(PondEnvironment.At(new DateTime(2026, 8, 31)).Season == PondSeason.Summer, "August should be summer");
        Assert(PondEnvironment.At(new DateTime(2026, 9, 1)).Season == PondSeason.Autumn, "September should be autumn");
        Assert(PondEnvironment.At(new DateTime(2026, 12, 1)).Season == PondSeason.Winter, "December should be winter");

        var weightsLow = FishCatalog.RarityWeightsForActivity(0);
        var weightsMid = FishCatalog.RarityWeightsForActivity(60);
        var weightsHigh = FishCatalog.RarityWeightsForActivity(120);
        Assert(weightsLow.SequenceEqual(new[] { (FishRarity.Common, 70), (FishRarity.Uncommon, 25), (FishRarity.Rare, 5) }), "low activity should use 70/25/5 rarity weights");
        Assert(weightsMid.SequenceEqual(new[] { (FishRarity.Common, 60), (FishRarity.Uncommon, 32), (FishRarity.Rare, 8) }), "medium activity should use 60/32/8 rarity weights");
        Assert(weightsHigh.SequenceEqual(new[] { (FishRarity.Common, 45), (FishRarity.Uncommon, 40), (FishRarity.Rare, 15) }), "high activity should use 45/40/15 rarity weights");

        var activity = new FishingActivityTracker(enabled: false);
        var pulseTime = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        activity.BeginTurn();
        Assert(!activity.TryPulse(pulseTime), "disabled activity tracking should not award a pulse");
        activity.Enabled = true;
        Assert(activity.TryPulse(pulseTime), "an enabled active turn should accept an anonymous pulse");
        Assert(!activity.TryPulse(pulseTime.AddMilliseconds(199)), "activity pulses must be throttled to one per 200 ms");
        Assert(activity.TryPulse(pulseTime.AddMilliseconds(200)), "the throttle should reopen at 200 ms");
        for (var i = 0; i < 220; i++) activity.TryPulse(pulseTime.AddMilliseconds(400 + i * 200));
        Assert(activity.Score == 180, "activity score should cap at 180");
        activity.EndTurn();
        Assert(!activity.TryPulse(pulseTime.AddHours(1)), "inactive turns must not accumulate pulses");
        activity.Reset();
        Assert(activity.Score == 0, "reset should clear the in-memory score");

        var eventStart = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var scheduler = new PondEventScheduler(new Random(12), eventStart);
        var next = scheduler.NextEventAt;
        Assert(next >= eventStart.AddMinutes(15) && next <= eventStart.AddMinutes(30), "ambient event interval should be 15 to 30 minutes");
        Assert(scheduler.Update(next.AddSeconds(-1), true, PondSeason.Spring) is null, "events should not start before their scheduled time");
        var pondEvent = scheduler.Update(next, true, PondSeason.Spring);
        Assert(pondEvent is not null && pondEvent.Definition.Season == PondSeason.Spring, "spring should select a spring event");
        Assert(pondEvent!.EndsAt - pondEvent.StartedAt >= TimeSpan.FromMinutes(1) && pondEvent.EndsAt - pondEvent.StartedAt <= TimeSpan.FromMinutes(2), "event duration should be one to two minutes");

        var pauseScheduler = new PondEventScheduler(new Random(22), eventStart);
        var pauseDue = pauseScheduler.NextEventAt;
        var hiddenAt = eventStart.AddMinutes(5);
        pauseScheduler.Update(hiddenAt, false, PondSeason.Spring);
        var visibleAt = hiddenAt.AddHours(2);
        var beforePausedDue = pauseScheduler.Update(visibleAt, true, PondSeason.Spring);
        Assert(beforePausedDue is null && pauseScheduler.NextEventAt == pauseDue.AddHours(2), "hidden time should pause rather than consume the event timer");
        Assert(pauseScheduler.Update(pauseScheduler.NextEventAt, true, PondSeason.Spring) is not null, "the event should begin when the paused timer reaches its deadline");

        var petalFish = FishCatalog.All.Single(item => item.Id == "petal-carp");
        var springEnvironment = PondEnvironment.At(new DateTime(2026, 4, 10, 17, 0, 0));
        Assert(!petalFish.IsAvailable(springEnvironment, null), "event-limited fish must not be eligible outside their event");
        var petalEvent = new PondEvent(PondEventCatalog.All.Single(item => item.Id == "spring-petals"), pulseTime, pulseTime.AddMinutes(1));
        Assert(petalFish.IsAvailable(springEnvironment, petalEvent), "event-limited fish should enter the candidate pool during their event and season");
        Assert(FishCatalog.Choose(new FishCatchContext(springEnvironment, petalEvent, 180, pulseTime), new HighestRandom()).Id == "petal-carp",
            "an active event and eligible season should allow its limited fish to be caught");

        var unlocked = new PondSettings();
        var sampleCatches = new[]
        {
            new FishRecord("one", "sunny", "暖阳鲫", "#E7A84F", pulseTime, FishRarity.Common, PondSeason.Spring),
            new FishRecord("two", "sunny", "暖阳鲫", "#E7A84F", pulseTime, FishRarity.Common, PondSeason.Summer),
            new FishRecord("three", "petal-carp", "花瓣锦鲤", "#E8979C", pulseTime, FishRarity.Rare, PondSeason.Spring, "spring-petals")
        };
        Assert(AccessoryUnlockRules.Apply(unlocked, sampleCatches), "new catches should unlock cosmetics");
        Assert(unlocked.UnlockedAccessoryIds.Contains("grass-block") && unlocked.UnlockedAccessoryIds.Contains("spring-bloom") &&
               unlocked.UnlockedAccessoryIds.Contains("summer-shell") && unlocked.UnlockedAccessoryIds.Contains("firefly-jar"),
            "catch thresholds should unlock the grass block, seasonal, and event decorations");
        Assert(!AccessoryUnlockRules.Apply(unlocked, sampleCatches), "unlock processing should be idempotent");
    }

    private static void CheckLegacyCompatibility(string scratch)
    {
        var legacySettingsDir = Path.Combine(scratch, "legacy-settings");
        Directory.CreateDirectory(legacySettingsDir);
        File.WriteAllText(Path.Combine(legacySettingsDir, "settings.json"), """
            {"LastWorkspace":"C:\\work","CodexExecutable":"codex","HasChosenWorkspace":true,"PondLeft":24,"PondTop":32}
            """);
        var settings = new PondSettingsStore(legacySettingsDir).Load();
        Assert(settings.Theme == PondTheme.FusionCE && settings.TrackInputActivity && settings.UnlockedAccessoryIds.Contains("straw-hat"), "old settings should load with new defaults");

        var legacyFishDir = Path.Combine(scratch, "legacy-fish-book");
        Directory.CreateDirectory(legacyFishDir);
        File.WriteAllText(Path.Combine(legacyFishDir, "fish-book.json"), """
            [{"TurnId":"old-turn","FishId":"sunny","FishName":"暖阳鲫","Color":"#E7A84F","CaughtAt":"2025-01-02T03:04:05+00:00"}]
            """);
        var oldFish = new FishBookStore(legacyFishDir).ReadAll().Single();
        Assert(oldFish.IsHistorical && oldFish.Rarity is null && oldFish.Season is null, "old fish records should remain readable and be marked historical");
    }

    private static void CheckPondAssets()
    {
        var sceneHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var theme in Enum.GetValues<PondTheme>())
        {
            var scene = PondAssets.Theme(theme);
            Assert(scene.PixelWidth == 500 && scene.PixelHeight == 313, $"theme {theme} should use the compact pond canvas");
            var bytes = new byte[scene.PixelWidth * scene.PixelHeight * 4];
            scene.CopyPixels(bytes, scene.PixelWidth * 4, 0);
            sceneHashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
            Assert(bytes[3] == 0, $"theme {theme} should preserve transparent pixels outside its scene");
        }
        Assert(sceneHashes.Count == Enum.GetValues<PondTheme>().Length, "each appearance should have an independently composed scene asset");

        var fishHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fish in FishCatalog.All)
        {
            var sprite = PondAssets.FishSprite(fish.Id);
            Assert(sprite is not null && sprite.PixelWidth == 80 && sprite.PixelHeight == 48, $"fish '{fish.Id}' should have its own sprite");
            var bytes = new byte[sprite!.PixelWidth * sprite.PixelHeight * 4];
            sprite.CopyPixels(bytes, sprite.PixelWidth * 4, 0);
            fishHashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
        }
        Assert(fishHashes.Count == FishCatalog.All.Count, "fish sprites must not be palette-only copies of one image");
    }

    private static void CheckGenericProviderContracts()
    {
        Assert(AgentProviderCatalog.Available.Select(provider => provider.Id).OrderBy(id => id, StringComparer.Ordinal)
            .SequenceEqual(new[] { AgentProviderIds.Codex, AgentProviderIds.DeepSeekHarness, AgentProviderIds.WorkBuddy }.OrderBy(id => id, StringComparer.Ordinal)),
            "the provider picker should expose Codex, DeepSeek Harness and Work Buddy connectors");
        var monitor = new FakeAgentMonitor();
        AgentEvent? observed = null;
        monitor.AgentEventReceived += value => observed = value;
        monitor.ObserveExternalTaskAsync("external-task-1").GetAwaiter().GetResult();
        Assert(monitor.Capabilities.CanObserveExternalTasks && !monitor.Capabilities.CanLaunchTasks, "external observation should be a separate provider capability");
        Assert(observed?.Kind == AgentEventKind.Started && observed.SessionId == "external-task-1", "external monitor should publish provider-neutral Agent events");
    }

    private static void CheckAgentEventIsolation()
    {
        var active = new FakeAgentLauncher(AgentProviderIds.Codex);
        var previousProvider = new FakeAgentLauncher(AgentProviderIds.DeepSeekHarness);
        var otherProviderEvent = new AgentEvent(AgentEventKind.Disconnected, AgentProviderIds.DeepSeekHarness,
            "dsh-session-1", "dsh-turn-1", "late disconnect", "disconnected");
        Assert(!AgentEventIsolation.IsCurrentTurnEvent(previousProvider, active, otherProviderEvent, true, "codex-session-2", "codex-turn-2"),
            "a previous provider's disconnect must not reset the active provider's turn");

        var staleSession = new AgentEvent(AgentEventKind.StepCompleted, AgentProviderIds.Codex,
            "codex-session-1", "codex-turn-2", Status: "completed");
        Assert(!AgentEventIsolation.IsCurrentTurnEvent(active, active, staleSession, true, "codex-session-2", "codex-turn-2"),
            "an event from an earlier session must be ignored even when its turn ID matches");
        var staleTurn = new AgentEvent(AgentEventKind.Disconnected, AgentProviderIds.Codex,
            "codex-session-2", "codex-turn-1", "late disconnect", "disconnected");
        Assert(!AgentEventIsolation.IsCurrentTurnEvent(active, active, staleTurn, true, "codex-session-2", "codex-turn-2"),
            "an earlier turn's disconnect must not reset a later turn in the same session");
        var uncorrelatedDisconnect = new AgentEvent(AgentEventKind.Disconnected, AgentProviderIds.Codex,
            Text: "unscoped disconnect", Status: "disconnected");
        Assert(!AgentEventIsolation.IsCurrentTurnEvent(active, active, uncorrelatedDisconnect, true, "codex-session-2", "codex-turn-2"),
            "a disconnect without matching session and turn IDs must fail closed");
        var lateStart = new AgentEvent(AgentEventKind.Started, AgentProviderIds.Codex,
            "codex-session-2", "codex-turn-1", Status: "running");
        Assert(!AgentEventIsolation.IsCurrentTurnEvent(active, active, lateStart, true, "codex-session-2", null,
                new HashSet<string>([AgentEventIsolation.TurnKey(AgentProviderIds.Codex, "codex-turn-1")], StringComparer.Ordinal)),
            "a delayed start event for a terminal turn must not attach to a new turn");
        var dsh = new FakeAgentLauncher(AgentProviderIds.DeepSeekHarness);
        var sameRawIdDifferentProvider = new AgentEvent(AgentEventKind.Started, AgentProviderIds.DeepSeekHarness,
            "dsh-session", "codex-turn-1", Status: "running");
        Assert(AgentEventIsolation.IsCurrentTurnEvent(dsh, dsh, sameRawIdDifferentProvider, true, "dsh-session", null,
                new HashSet<string>([AgentEventIsolation.TurnKey(AgentProviderIds.Codex, "codex-turn-1")], StringComparer.Ordinal)),
            "terminal turn IDs must be scoped to their provider");
        var currentDisconnect = new AgentEvent(AgentEventKind.Disconnected, AgentProviderIds.Codex,
            "codex-session-2", "codex-turn-2", "current disconnect", "disconnected");
        Assert(AgentEventIsolation.IsCurrentTurnEvent(active, active, currentDisconnect, true, "codex-session-2", "codex-turn-2"),
            "a disconnect correlated to the active provider, session and turn should be handled");
        Assert(!AgentEventIsolation.IsCurrentTurnEvent(active, active, currentDisconnect, false, "codex-session-2", "codex-turn-2"),
            "events received outside an active turn should not change the visible turn state");
    }

    private static void CheckWorkBuddyTerminalEvidence()
    {
        Assert(WorkBuddyProtocolEvidence.ContainsWorkflowRunId("""{"workflow_run_id":"wf_1a2b3c4d"}"""),
            "Work Buddy run result should preserve its durable workflow ID");
        Assert(WorkBuddyProtocolEvidence.IndicatesCompletedWorkflowStatus("""{"workflow_run_id":"wf_1a2b3c4d","status":"completed"}"""),
            "an explicit completed workflow run should be accepted as terminal evidence");
        Assert(WorkBuddyProtocolEvidence.IndicatesCompletedWorkflowStatus("""{"workflow":{"workflow_run_id":"wf_1a2b3c4d","state":"succeeded"}}"""),
            "a named nested workflow with a run ID and successful state should be accepted");
        Assert(WorkBuddyProtocolEvidence.IndicatesUnsuccessfulWorkflowTerminal("""{"workflow_run_id":"wf_1a2b3c4d","status":"cancelled"}""", new HashSet<string>(["wf_1a2b3c4d"], StringComparer.Ordinal)),
            "a matching failed or cancelled workflow should be distinguished from an unverified status");
        Assert(!WorkBuddyProtocolEvidence.IndicatesCompletedWorkflowStatus("""{"workflow_run_id":"wf_1a2b3c4d","status":"running","gateway":{"status":"success"}}"""),
            "a nested gateway health success must not override a still-running workflow");
        Assert(!WorkBuddyProtocolEvidence.IndicatesCompletedWorkflowStatus("""{"workflow_run_id":"wf_other01","status":"completed"}""", new HashSet<string>(["wf_1a2b3c4d"], StringComparer.Ordinal)),
            "completion for a different workflow ID must not verify the observed run");
        Assert(!WorkBuddyProtocolEvidence.IndicatesCompletedWorkflowStatus("""{"gateway":{"status":"success"},"system":{"state":"completed"}}"""),
            "unrelated service and system statuses must not count as Work Buddy completion");
        Assert(WorkBuddyProtocolEvidence.ExtractWorkflowRunIds("started workflow run wf_1a2b3c4d").Contains("wf_1a2b3c4d"),
            "workflow IDs in text-form MCP responses should still be correlated");
        Assert(!WorkBuddyProtocolEvidence.IndicatesCompletedWorkflowStatus("not-json"),
            "malformed Work Buddy status must fail closed");
    }

    private static void CheckDualModesAndSteam(string scratch)
    {
        Assert(Enum.IsDefined(PondMode.AgentPond) && Enum.IsDefined(PondMode.IndependentFishing),
            "Agent and independent fishing modes should both be represented");

        var localDirectory = Path.Combine(scratch, "steam-cloud-local");
        var settingsStore = new PondSettingsStore(localDirectory);
        var fishBook = new FishBookStore(localDirectory);
        var localSettings = settingsStore.Load();
        localSettings.Theme = PondTheme.A;
        localSettings.LastMode = PondMode.AgentPond;
        settingsStore.Save(localSettings);
        var catchContext = new FishCatchContext(PondEnvironment.At(new DateTime(2026, 7, 15, 12, 0, 0)), null, 0,
            new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        Assert(fishBook.TryCatchManual(catchContext, out var localFish) && localFish is not null,
            "independent fishing should record a local catch without an Agent session");
        var modeProgressBook = new FishBookStore(Path.Combine(scratch, "steam-mode-parity"));
        var progressSteam = new FakeSteamPlatformService();
        progressSteam.Initialize();
        Assert(modeProgressBook.TryCatchManual(catchContext, out _) &&
               FishProgressReporter.Report(progressSteam, modeProgressBook),
            "manual catches should report their fish ledger to Steam stats");
        Assert(modeProgressBook.TryCatchCompletedTurn("completed", "agent-progress-turn", catchContext, out _) &&
               FishProgressReporter.Report(progressSteam, modeProgressBook),
            "Agent catches should use the same fish ledger reporting path");
        Assert(modeProgressBook.TryCatchCompletedTurn("completed", AgentEventIsolation.TurnKey(AgentProviderIds.Codex, "shared-turn-id"), catchContext, out _) &&
               modeProgressBook.TryCatchCompletedTurn("completed", AgentEventIsolation.TurnKey(AgentProviderIds.DeepSeekHarness, "shared-turn-id"), catchContext, out _),
            "providers that return the same raw turn ID must still get distinct fish records");
        FishProgressReporter.Report(progressSteam, modeProgressBook);
        var legacyTurnBook = new FishBookStore(Path.Combine(scratch, "steam-legacy-turn"));
        Assert(legacyTurnBook.TryCatchCompletedTurn("completed", "legacy-turn-id", catchContext, out _),
            "a pre-namespacing Agent turn should already exist in the local fish book");
        Assert(!legacyTurnBook.TryCatchCompletedTurn("completed", AgentEventIsolation.TurnKey(AgentProviderIds.Codex, "legacy-turn-id"),
                catchContext, out _, legacyTurnId: "legacy-turn-id") && legacyTurnBook.ReadAll().Count == 1,
            "namespaced turn IDs should still deduplicate legacy records from earlier versions");
        var modeProgressRecords = modeProgressBook.ReadAll();
        Assert(progressSteam.FishCaughtStat == modeProgressRecords.Count &&
               progressSteam.DiscoveredSpeciesStat == modeProgressRecords.Select(record => record.FishId).Distinct(StringComparer.Ordinal).Count(),
            "manual and Agent catches should produce identical authoritative Steam fish and species totals");
        var remoteFish = new FishRecord("steam-cloud-fish-2", "river-minnow", "河银鱼", "#9CC9B5",
            new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero), FishRarity.Common, PondSeason.Summer);
        var cloudSettings = new PondCloudSettings(DateTimeOffset.UtcNow.AddHours(1), PondTheme.D, "straw-hat",
            null, "summer-shell", ["straw-hat", "summer-shell"], PondMode.IndependentFishing);
        var cloud = new PondCloudSave(1, DateTimeOffset.UtcNow, [localFish!, remoteFish], cloudSettings);
        var steam = new FakeSteamPlatformService(cloudSave: cloud);
        steam.Initialize();
        steam.SyncLocalSave(localSettings, settingsStore, fishBook);
        Assert(fishBook.ReadAll().Count == 2 && fishBook.ReadAll().Select(record => record.TurnId).Distinct(StringComparer.Ordinal).Count() == 2,
            "cloud merge should preserve unique local and remote fish records");
        Assert(localSettings.Theme == PondTheme.D && localSettings.LastMode == PondMode.IndependentFishing &&
               settingsStore.Load().Theme == PondTheme.D,
            "newer cloud presentation/game settings should apply and persist locally");
        Assert(steam.CloudSave is not null && steam.CloudSave.Catches.Count == 2,
            "fake Steam sync should write the merged fish book back to its cloud snapshot");
        Assert(steam.FishCaughtStat == fishBook.ReadAll().Count &&
               steam.DiscoveredSpeciesStat == fishBook.ReadAll().Select(record => record.FishId).Distinct(StringComparer.Ordinal).Count(),
            "Steam catch stats should reconcile to the authoritative ledger after cloud merging");
        var cloudJson = JsonSerializer.Serialize(steam.CloudSave);
        foreach (var privateField in new[] { "prompt", "conversation", "workspace", "credential", "runtimeLog" })
            Assert(!cloudJson.Contains(privateField, StringComparison.OrdinalIgnoreCase),
                $"Steam cloud DTO must not serialize private Agent field '{privateField}'");

        steam.OnFishCaught(1, 1);
        Assert(steam.Achievements.Contains("POND_FIRST_CATCH"), "first-fish milestone should award its achievement");
        steam.OnFishCaught(20, 20);
        Assert(steam.Achievements.Contains("POND_CATCH_20") && steam.Achievements.Contains("POND_ALL_FISH"),
            "catch and species milestones should award their achievements");

        var offlineDirectory = Path.Combine(scratch, "steam-offline");
        var offlineStore = new FishBookStore(offlineDirectory);
        var offlineSettingsStore = new PondSettingsStore(offlineDirectory);
        var offlineSettings = offlineSettingsStore.Load();
        using var offlineSteam = new FakeSteamPlatformService(availableOnInitialize: false, cloudSave: cloud);
        offlineSteam.Initialize();
        offlineSteam.SyncLocalSave(offlineSettings, offlineSettingsStore, offlineStore);
        Assert(!offlineSteam.IsAvailable && offlineSteam.CloudSave?.Catches.Count == 2,
            "offline Steam should skip cloud operations without mutating the simulated remote save");
        Assert(offlineStore.TryCatchManual(catchContext, out _) && offlineStore.ReadAll().Count == 1,
            "offline independent play should continue writing catches to local storage");
    }

    private static async Task CheckProcessConnectorsAsync(string scratch)
    {
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var dshPatch = Path.Combine(AppContext.BaseDirectory, "DshBridge", "cordis.patch.yml");
        Assert(File.Exists(dshPatch), "the DeepSeek approval bridge patch should be bundled with the app");
        await using (var dsh = new DeepSeekHarnessAdapter("dotnet", dshPatch, [assemblyPath, "--fake-dsh"]))
        {
            var events = new ConcurrentQueue<AgentEvent>();
            var approvalSource = new TaskCompletionSource<AgentApprovalPrompt>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completeSource = new TaskCompletionSource<AgentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            string? resumedSessionId = null;
            dsh.AgentEventReceived += item =>
            {
                events.Enqueue(item);
                if (item.Kind == AgentEventKind.Completed) completeSource.TrySetResult(item);
            };
            dsh.ApprovalPromptReceived += prompt => approvalSource.TrySetResult(prompt);
            dsh.ResumableSessionResolved += (_, sessionId) => resumedSessionId = sessionId;
            var threadId = await dsh.StartThreadAsync("dotnet", scratch);
            var turnId = await dsh.StartTurnAsync(threadId, "fake process smoke", scratch);
            var approval = await approvalSource.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert(approval.CanAllowOnce && approval.CanReject && approval.Scope.Contains("仅允许这一次", StringComparison.Ordinal),
                "DeepSeek approval should be an explicit one-shot allow or reject");
            await dsh.RespondToApprovalAsync(approval, allowOnce: true);
            var completed = await completeSource.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert(completed.TurnId == turnId && completed.Status == "completed" && completed.Text == "模拟 DSH 完成",
                "DeepSeek should produce a verified completion event only after successful exit and final turn_end");
            Assert(resumedSessionId == "fake-dsh-session" && events.Any(item => item.Kind == AgentEventKind.StepStarted) &&
                   events.Any(item => item.Kind == AgentEventKind.StepCompleted),
                "DeepSeek session ID and tool lifecycle events should be mapped from the JSON stream");
        }

        await using (var dshWithoutBridge = new DeepSeekHarnessAdapter("dotnet", dshPatch, [assemblyPath, "--fake-dsh", "--fake-no-ready"],
            TimeSpan.FromMilliseconds(450)))
        {
            var terminalSource = new TaskCompletionSource<AgentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            dshWithoutBridge.AgentEventReceived += item =>
            {
                if (item.Kind is AgentEventKind.Completed or AgentEventKind.Failed) terminalSource.TrySetResult(item);
            };
            var threadId = await dshWithoutBridge.StartThreadAsync("dotnet", scratch);
            var refused = false;
            try { await dshWithoutBridge.StartTurnAsync(threadId, "bridge missing smoke", scratch); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("审批桥", StringComparison.Ordinal)) { refused = true; }
            catch (TimeoutException) { refused = true; }
            var failed = await terminalSource.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(refused && failed.Kind == AgentEventKind.Failed && failed.Status == "approval_bridge_not_ready",
                "a DSH process without the approval bridge handshake must be refused and can never emit successful completion");
        }

        await using (var dshIncompatible = new DeepSeekHarnessAdapter("dotnet", dshPatch, [assemblyPath, "--fake-dsh", "--fake-bad-ready"],
            TimeSpan.FromMilliseconds(450)))
        {
            var threadId = await dshIncompatible.StartThreadAsync("dotnet", scratch);
            var refused = false;
            try { await dshIncompatible.StartTurnAsync(threadId, "incompatible bridge smoke", scratch); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("协议版本不兼容", StringComparison.Ordinal)) { refused = true; }
            Assert(refused, "a mismatched approval-bridge protocol version must be rejected");
        }

        await using (var missingDsh = new DeepSeekHarnessAdapter(Path.Combine(scratch, "missing-dsh.exe"), dshPatch))
        {
            var threadId = await missingDsh.StartThreadAsync("dsh", scratch);
            var refused = false;
            try { await missingDsh.StartTurnAsync(threadId, "missing executable smoke", scratch); }
            catch (System.ComponentModel.Win32Exception) { refused = true; }
            Assert(refused, "a missing DeepSeek CLI should fail immediately without installation or implicit fallback");
        }

        await using (var missingClaude = new ClaudeCodeAgentAdapter(Path.Combine(scratch, "missing-claude.exe"), "CodexPond.exe"))
        {
            var threadId = await missingClaude.StartThreadAsync("claude", scratch);
            var refused = false;
            try { await missingClaude.StartTurnAsync(threadId, "missing Claude CLI smoke", scratch); }
            catch (System.ComponentModel.Win32Exception) { refused = true; }
            Assert(refused, "a missing Claude Code CLI should fail immediately without changing MCP configuration");
        }

        await using (var claude = new ClaudeCodeAgentAdapter("dotnet", "CodexPond.exe", [assemblyPath, "--fake-claude"]))
        {
            var approvalSource = new TaskCompletionSource<AgentApprovalPrompt>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completeSource = new TaskCompletionSource<AgentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            claude.ApprovalPromptReceived += prompt => approvalSource.TrySetResult(prompt);
            claude.AgentEventReceived += item =>
            {
                if (item.Kind == AgentEventKind.Completed) completeSource.TrySetResult(item);
            };
            var threadId = await claude.StartThreadAsync("dotnet", scratch);
            await claude.StartTurnAsync(threadId, "fake Claude / Work Buddy smoke", scratch);
            var permission = await approvalSource.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert(permission.ProviderId == AgentProviderIds.WorkBuddy && permission.CanAllowOnce,
                "Claude outer-tool permissions should be explicitly handled in the pond");
            await claude.RespondToApprovalAsync(permission, allowOnce: true);
            var completed = await completeSource.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert(completed.Status == "completed" && completed.ProviderPayload is WorkBuddyRunEvidence
            {
                InitializationSucceeded: true,
                DurableWorkflowObserved: true,
                VerifiedWorkflowCompletion: true
            }, "Work Buddy should require wb_init, a durable run ID and matching wb_status success before fish credit");
        }

        await using (var claude = new ClaudeCodeAgentAdapter("dotnet", "CodexPond.exe", [assemblyPath, "--fake-claude", "--fake-wrong-status", "--fake-no-approval"]))
        {
            var completeSource = new TaskCompletionSource<AgentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            claude.AgentEventReceived += item => { if (item.Kind == AgentEventKind.Completed) completeSource.TrySetResult(item); };
            var threadId = await claude.StartThreadAsync("dotnet", scratch);
            await claude.StartTurnAsync(threadId, "mismatched workflow smoke", scratch);
            var completed = await completeSource.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert(completed.ProviderPayload is WorkBuddyRunEvidence { InitializationSucceeded: true, DurableWorkflowObserved: true, VerifiedWorkflowCompletion: false },
                "a status result for a different workflow ID must not count as terminal evidence");
        }

        var resultMarker = Path.Combine(scratch, "claude-result-marker.txt");
        await using (var claude = new ClaudeCodeAgentAdapter("dotnet", "CodexPond.exe",
            [assemblyPath, "--fake-claude", "--fake-no-approval", "--fake-no-result-after-first", "--fake-result-marker", resultMarker]))
        {
            var terminalCount = 0;
            var firstTerminal = new TaskCompletionSource<AgentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondTerminal = new TaskCompletionSource<AgentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            claude.AgentEventReceived += item =>
            {
                if (item.Kind is not (AgentEventKind.Completed or AgentEventKind.Failed or AgentEventKind.Cancelled or AgentEventKind.Disconnected)) return;
                if (Interlocked.Increment(ref terminalCount) == 1) firstTerminal.TrySetResult(item);
                else secondTerminal.TrySetResult(item);
            };

            var threadId = await claude.StartThreadAsync("dotnet", scratch);
            await claude.StartTurnAsync(threadId, "first Work Buddy result smoke", scratch);
            var first = await firstTerminal.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert(first.Kind == AgentEventKind.Completed && first.Text == "模拟 Work Buddy 任务完成",
                "the first Work Buddy turn should establish a successful result and final text");

            await claude.StartTurnAsync(threadId, "second Work Buddy missing-result smoke", scratch);
            var second = await secondTerminal.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert(second.Kind == AgentEventKind.Failed && second.Status == "exit_0" && second.Text is null,
                "a later Work Buddy process without a result event must not reuse the prior turn's success or final text");
        }
    }

    private static async Task<int> RunFakeDshAsync(bool skipReadyHandshake, bool sendBadReadyHandshake)
    {
        var readyPipeName = Environment.GetEnvironmentVariable("CODEX_POND_APPROVAL_READY_PIPE");
        if (!skipReadyHandshake)
        {
            if (string.IsNullOrWhiteSpace(readyPipeName)) return 6;
            var protocolVersion = sendBadReadyHandshake ? 2 : 1;
            var handshakeResponse = await ExchangeNamedPipeAsync(readyPipeName, $"{{\"type\":\"bridge_ready\",\"protocolVersion\":{protocolVersion}}}");
            if (!handshakeResponse.Contains("\"ready\":true", StringComparison.Ordinal)) return 11;
        }
        WriteStreamEvent(new { type = "session", sessionId = "fake-dsh-session" });
        WriteStreamEvent(new { type = "status", phase = "turn_start", turn = "fake-dsh-turn-1" });
        if (skipReadyHandshake)
        {
            WriteStreamEvent(new { type = "status", phase = "turn_end", turn = "fake-dsh-turn-1", reason = "completed" });
            WriteStreamEvent(new { type = "final", text = "不应由未握手的审批桥完成" });
            return 0;
        }
        WriteStreamEvent(new { type = "tool_call", callId = "fake-call-1", tool = "shell", input = new { command = "echo smoke" } });
        var pipeName = Environment.GetEnvironmentVariable("CODEX_POND_APPROVAL_PIPE");
        if (string.IsNullOrWhiteSpace(pipeName)) return 7;
        var response = await ExchangeNamedPipeAsync(pipeName, "{\"toolName\":\"shell\",\"reason\":\"fake approval smoke\"}");
        if (!response.Contains("allow", StringComparison.Ordinal)) return 8;
        WriteStreamEvent(new { type = "tool_result", callId = "fake-call-1", status = "success", result = "ok" });
        WriteStreamEvent(new { type = "status", phase = "turn_end", turn = "fake-dsh-turn-1", reason = "completed" });
        WriteStreamEvent(new { type = "final", text = "模拟 DSH 完成" });
        return 0;
    }

    private static async Task<int> RunFakeClaudeAsync(bool wrongStatus, bool requestApproval, bool noResultAfterFirst, string? resultMarker)
    {
        _ = await Console.In.ReadLineAsync();
        WriteStreamEvent(new { type = "system", subtype = "init", session_id = "fake-claude-session" });
        if (requestApproval)
        {
            var pipeName = Environment.GetEnvironmentVariable("CODEX_POND_APPROVAL_PIPE");
            if (string.IsNullOrWhiteSpace(pipeName)) return 9;
            var response = await ExchangeNamedPipeAsync(pipeName, "{\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"echo smoke\"}}");
            if (!response.Contains("allow", StringComparison.Ordinal)) return 10;
        }

        WriteWorkBuddyToolUse("init-1", "mcp__work-buddy__wb_init");
        WriteWorkBuddyToolResult("init-1", "{\"ok\":true}");
        WriteWorkBuddyToolUse("run-1", "mcp__work-buddy__wb_run");
        WriteWorkBuddyToolResult("run-1", "workflow started wf_1a2b3c4d");
        WriteWorkBuddyToolUse("status-1", "mcp__work-buddy__wb_status");
        var statusId = wrongStatus ? "wf_deadbeef" : "wf_1a2b3c4d";
        WriteWorkBuddyToolResult("status-1", $"{{\"workflow_run_id\":\"{statusId}\",\"status\":\"completed\"}}");
        var omitResult = noResultAfterFirst && resultMarker is not null && File.Exists(resultMarker);
        if (noResultAfterFirst && resultMarker is not null && !omitResult)
            File.WriteAllText(resultMarker, "first turn emitted a result");
        if (!omitResult)
            WriteStreamEvent(new { type = "result", result = "模拟 Work Buddy 任务完成", session_id = "fake-claude-session", is_error = false });
        return 0;
    }

    private static void WriteWorkBuddyToolUse(string id, string name) => WriteStreamEvent(new
    {
        type = "assistant",
        message = new { content = new[] { new { type = "tool_use", id, name, input = new { } } } }
    });

    private static void WriteWorkBuddyToolResult(string id, string content) => WriteStreamEvent(new
    {
        type = "user",
        message = new { content = new[] { new { type = "tool_result", tool_use_id = id, content } } }
    });

    private static void WriteStreamEvent(object value)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(value));
        Console.Out.Flush();
    }

    private static async Task<string> ExchangeNamedPipeAsync(string pipeName, string request)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                client.Connect(250);
                using var reader = new StreamReader(client, leaveOpen: true);
                using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(request);
                return await reader.ReadLineAsync() ?? "";
            }
            catch (TimeoutException) { await Task.Delay(50); }
            catch (IOException) { await Task.Delay(50); }
        }
        return "";
    }

    private static void CheckApprovalBoundary()
    {
        using var idDocument = JsonDocument.Parse("17");
        using var paramsDocument = JsonDocument.Parse("""{"availableDecisions":["acceptForSession","decline"]}""");
        var request = new CodexApprovalRequest(
            idDocument.RootElement.Clone(),
            "item/commandExecution/requestApproval",
            paramsDocument.RootElement.Clone());
        Assert(!request.CanGrantOnce, "a session-only approval option must not be treated as a one-time grant");
    }

    private static async Task CheckProtocolAsync(string scratch)
    {
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var arguments = new[] { assemblyPath, "--fake-app-server", "app-server", "--stdio" };
        await using var adapter = new CodexAppServerAdapter(arguments);
        var events = new ConcurrentQueue<CodexServerEvent>();
        var agentEvents = new ConcurrentQueue<AgentEvent>();
        var firstComplete = new TaskCompletionSource<CodexServerEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<CodexServerEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondComplete = new TaskCompletionSource<CodexServerEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new TaskCompletionSource<CodexApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var permissionRequest = new TaskCompletionSource<CodexApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var genericComplete = new TaskCompletionSource<AgentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var genericCancelled = new TaskCompletionSource<AgentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var genericApproval = new TaskCompletionSource<AgentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.AgentEventReceived += agentEvent =>
        {
            agentEvents.Enqueue(agentEvent);
            if (agentEvent.Kind == AgentEventKind.Completed && agentEvent.TurnId == "fake-turn-1") genericComplete.TrySetResult(agentEvent);
            if (agentEvent.Kind == AgentEventKind.Cancelled && agentEvent.TurnId == "fake-turn-2") genericCancelled.TrySetResult(agentEvent);
            if (agentEvent.Kind == AgentEventKind.ApprovalRequired) genericApproval.TrySetResult(agentEvent);
        };
        adapter.EventReceived += serverEvent =>
        {
            events.Enqueue(serverEvent);
            if (serverEvent.Method == "turn/started" && serverEvent.TurnId == "fake-turn-2")
                secondStarted.TrySetResult(serverEvent);
            if (serverEvent.Method == "turn/completed" && serverEvent.TurnId == "fake-turn-1")
                firstComplete.TrySetResult(serverEvent);
            if (serverEvent.Method == "turn/completed" && serverEvent.TurnId == "fake-turn-2")
                secondComplete.TrySetResult(serverEvent);
        };
        adapter.RequestReceived += serverRequest =>
        {
            if (serverRequest.Kind == ApprovalKind.CommandExecution) request.TrySetResult(serverRequest);
            if (serverRequest.Kind == ApprovalKind.Permissions) permissionRequest.TrySetResult(serverRequest);
        };

        IAgentTaskLauncher launcher = adapter;
        Assert(launcher.ProviderId == AgentProviderIds.Codex && launcher.Capabilities.CanLaunchTasks && launcher.Capabilities.CanCancel &&
               launcher.Capabilities.CanRespondToApprovals && !launcher.Capabilities.CanObserveExternalTasks,
            "Codex should expose its current launch, cancel, and approval capabilities");
        var threadId = await launcher.StartThreadAsync("dotnet", scratch);
        Assert(threadId == "fake-thread-1", "thread/start should return its thread ID");
        var firstTurnId = await launcher.StartTurnAsync(threadId, "test prompt", scratch);
        Assert(firstTurnId == "fake-turn-1", "turn/start response should provide the authoritative turn ID");

        var approval = await request.Task.WaitAsync(TimeSpan.FromSeconds(8));
        var genericApprovalEvent = await genericApproval.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert(genericApprovalEvent.ProviderPayload is CodexApprovalRequest, "generic approval event should retain provider payload for explicit handling");
        Assert(approval.Kind == ApprovalKind.CommandExecution, "command approval request should be classified");
        Assert(approval.Details.Contains("dotnet --version", StringComparison.Ordinal), "approval details should expose the requested command");
        await adapter.RespondToApprovalAsync(approval, approve: true);

        var completed = await firstComplete.Task.WaitAsync(TimeSpan.FromSeconds(8));
        var genericCompletedEvent = await genericComplete.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert(completed.Status == "completed", "the first protocol turn should complete");
        Assert(genericCompletedEvent.Kind == AgentEventKind.Completed && genericCompletedEvent.ProviderId == AgentProviderIds.Codex, "App Server completion should map to a generic Agent event");
        Assert(completed.Text == "协议测试成功", "the adapter should extract the final assistant message");
        Assert(events.Any(item => item.Method == "item/started"), "item events should reach the adapter subscriber");
        Assert(agentEvents.Any(item => item.Kind == AgentEventKind.StepStarted) && agentEvents.Any(item => item.Kind == AgentEventKind.StepCompleted) &&
               agentEvents.Any(item => item.Kind == AgentEventKind.OutputDelta), "App Server work and output events should map to the generic contract");

        var secondThread = await launcher.StartThreadAsync("dotnet", scratch);
        Assert(secondThread == "fake-thread-2", "a new thread should start in the already-running App Server");
        var secondTurnId = await launcher.StartTurnAsync(secondThread, "interrupt test", scratch);
        Assert(secondTurnId == "fake-turn-2", "each subsequent turn/start response should provide its own turn ID");
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(8));
        var permission = await permissionRequest.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await adapter.RespondToApprovalAsync(permission, approve: true);
        await launcher.InterruptTurnAsync(secondThread, "fake-turn-2");
        var interrupted = await secondComplete.Task.WaitAsync(TimeSpan.FromSeconds(8));
        var genericInterrupted = await genericCancelled.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert(interrupted.Status == "interrupted", "turn/interrupt should finish as interrupted");
        Assert(genericInterrupted.Kind == AgentEventKind.Cancelled, "App Server cancellation should map to a generic Agent event");
        Assert(adapter.IsRunning, "the fake App Server should remain alive until disposal");
    }

    private static async Task<int> RunFakeServerAsync()
    {
        var threadCount = 0;
        var turnCount = 0;
        string? currentThread = null;
        string? currentTurn = null;

        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var method = root.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;

            if (root.TryGetProperty("id", out var id) && method is null)
            {
                if (id.ValueKind == JsonValueKind.Number && id.GetInt64() == 9002)
                {
                    var result = root.GetProperty("result");
                    if (result.GetProperty("scope").GetString() != "turn" || result.GetProperty("permissions").GetProperty("fileSystem").GetProperty("readWriteAccess")[0].GetString() != Environment.CurrentDirectory) return 4;
                    continue;
                }
                if (id.ValueKind == JsonValueKind.Number && id.GetInt64() == 9001)
                {
                    var decision = root.TryGetProperty("result", out var result) &&
                                   result.TryGetProperty("decision", out var decisionElement)
                        ? decisionElement.GetString()
                        : null;
                    if (decision != "accept") return 3;
                    SendNotification("item/completed", new { threadId = currentThread, turnId = currentTurn, item = new { id = "command-1", type = "commandExecution" } });
                    SendNotification("item/agentMessage/delta", new { threadId = currentThread, turnId = currentTurn, delta = "池塘协议测试完成" });
                    SendNotification("turn/completed", new
                    {
                        threadId = currentThread,
                        turn = new
                        {
                            id = currentTurn,
                            status = "completed",
                            items = new[] { new { type = "agentMessage", text = "协议测试成功" } }
                        }
                    });
                    continue;
                }
                if (root.TryGetProperty("result", out _) && turnCount == 2)
                {
                    SendNotification("turn/completed", new
                    {
                        threadId = currentThread,
                        turn = new { id = currentTurn, status = "interrupted", items = Array.Empty<object>() }
                    });
                }
                continue;
            }

            if (method == "initialize")
            {
                SendResponse(root.GetProperty("id"), new { serverInfo = new { name = "Codex Pond smoke fake", version = "1" } });
            }
            else if (method == "thread/start")
            {
                threadCount++;
                currentThread = $"fake-thread-{threadCount}";
                SendResponse(root.GetProperty("id"), new { thread = new { id = currentThread } });
            }
            else if (method == "turn/start")
            {
                turnCount++;
                currentThread = root.GetProperty("params").GetProperty("threadId").GetString();
                currentTurn = $"fake-turn-{turnCount}";
                SendResponse(root.GetProperty("id"), new { turn = new { id = currentTurn, status = "inProgress" } });
                SendNotification("turn/started", new { threadId = currentThread, turn = new { id = currentTurn, status = "inProgress" } });
                SendNotification("item/started", new { threadId = currentThread, turnId = currentTurn, item = new { id = "command-1", type = "commandExecution" } });
                if (turnCount == 1)
                {
                    SendRequest(9001, "item/commandExecution/requestApproval", new
                    {
                        threadId = currentThread,
                        turnId = currentTurn,
                        itemId = "command-1",
                        command = "dotnet --version",
                        cwd = Environment.CurrentDirectory,
                        reason = "protocol smoke request",
                        availableDecisions = new[] { "accept", "decline" }
                    });
                }
                else if (turnCount == 2)
                {
                    SendRequest(9002, "item/permissions/requestApproval", new
                    {
                        threadId = currentThread,
                        turnId = currentTurn,
                        itemId = "permission-1",
                        reason = "smoke permission request",
                        permissions = new { fileSystem = new { readWriteAccess = new[] { Environment.CurrentDirectory } }, network = (object?)null }
                    });
                }
            }
            else if (method == "turn/interrupt")
            {
                SendResponse(root.GetProperty("id"), new { });
                SendNotification("turn/completed", new
                {
                    threadId = root.GetProperty("params").GetProperty("threadId").GetString(),
                    turn = new { id = currentTurn, status = "interrupted", items = Array.Empty<object>() }
                });
            }
        }
        return 0;
    }

    private static void SendResponse(JsonElement id, object result) =>
        Send(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id.Clone(), ["result"] = result });

    private static void SendRequest(long id, string method, object parameters) =>
        Send(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });

    private static void SendNotification(string method, object parameters) =>
        Send(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });

    private static void Send(object message)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(message));
        Console.Out.Flush();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class FakeAgentMonitor : IAgentTaskMonitor
{
    public string ProviderId => "fake-monitor";
    public AgentProviderCapabilities Capabilities { get; } = new(
        CanLaunchTasks: false,
        CanObserveExternalTasks: true,
        CanCancel: false,
        CanRespondToApprovals: false,
        CanAcceptUserInput: false);
    public event Action<AgentEvent>? AgentEventReceived;
    public void Emit(AgentEvent agentEvent) => AgentEventReceived?.Invoke(agentEvent);

    public Task ObserveExternalTaskAsync(string externalTaskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(externalTaskId)) throw new ArgumentException("An external task ID is required.", nameof(externalTaskId));
        AgentEventReceived?.Invoke(new AgentEvent(AgentEventKind.Started, ProviderId, externalTaskId, externalTaskId, Status: "observing"));
        return Task.CompletedTask;
    }
}

internal sealed class FakeAgentLauncher : IAgentTaskLauncher
{
    public FakeAgentLauncher(string providerId) => ProviderId = providerId;
    public string ProviderId { get; }
    public AgentProviderCapabilities Capabilities { get; } = new(
        CanLaunchTasks: true,
        CanObserveExternalTasks: false,
        CanCancel: true,
        CanRespondToApprovals: false,
        CanAcceptUserInput: false);
    public event Action<AgentEvent>? AgentEventReceived;
    public void Emit(AgentEvent agentEvent) => AgentEventReceived?.Invoke(agentEvent);
    public Task<string> StartThreadAsync(string executable, string cwd, string? resumeThreadId = null) =>
        Task.FromResult(resumeThreadId ?? Guid.NewGuid().ToString("N"));
    public Task<string> StartTurnAsync(string threadId, string prompt, string cwd) => Task.FromResult(Guid.NewGuid().ToString("N"));
    public Task InterruptTurnAsync(string threadId, string turnId) => Task.CompletedTask;
}

internal sealed class HighestRandom : Random
{
    public override int Next(int maxValue) => Math.Max(0, maxValue - 1);
}
