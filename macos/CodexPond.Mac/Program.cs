using Avalonia;
using CodexPond;

namespace CodexPond.Mac;

internal static class Program
{
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        if (args.Any(argument => string.Equals(argument, "--permission-mcp-stdio", StringComparison.Ordinal)))
        {
            try { await ClaudePermissionMcpServer.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput()).ConfigureAwait(false); }
            catch (Exception) { }
            return 0;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
