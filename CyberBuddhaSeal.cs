using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace CodexPond;

/// <summary>
/// Code-level project provenance mark. The runtime hash identifies the current binary;
/// compare it with the SHA-256 manifest published in the official GitHub release.
/// The marker and hash are not a digital signature and do not prevent tampering.
/// </summary>
public static class CyberBuddhaSeal
{
    public const string MarkId = "CODEX-POND-CYBER-BUDDHA-01";
    // This source-level ASCII easter egg follows the user's code-photo reference.
    // It is a joke, not a claim that software can be guaranteed bug-free.
    public const string SourceMark = """
                                _..-------.._
                            .-''  _     _   ``-.
                          .'     (_)   (_)      `.
                         /     .-........-.       \
                        /     /  .------.  \       \
                       ;     |  /  .--.  \  |       ;
                       |     | |  (o  o) | |       |
                       |     | |    /\   | |       |
                       ;      \ \  `--' / /       ;
                        \      `-.____.-'        /
                         `-.       ||       _.-'
                            `--..__||__..--'
                         ________ /  \ ________
                     _.-'        / /\ \        `-._
                   .'    .------' /  \ `------.    `.
                  /    .'   .----'    `----.   `.    \
                 ;    /   .'    .------.    `.   \    ;
                 |   |   /     /  ____  \     \   |   |
                 |   |  |     |  / __ \  |     |  |   |
                 |   |  |     | | |  | | |     |  |   |
                  \  |  |     | | |__| | |     |  |  /
                   \  \  \     \ \____/ /     /  / /
                    `._`. `-.    `----'    .-' .'_.'
                       `-._ `-..________..-' _.-'
                            `--.________.--'
                        __/|  /|  /  \  |\  |\__
                    _.-'   | / | /    \ | \ |   `-._
                   /_______|/___/______\___\|_______\
                       /    /   /      \   \    \
                  ____/____/___/________\___\____\____
                 /_____________________________________\
                    \    _.-'  /    \  `-._    /
                     `--'     /      \     `--'
                            _/  /\    \ \_
                           /___/  \____\__\
                              /____\

                         电子佛祖
                      保佑永无 BUG
        """;

    public static bool IsStampRequest(string[] args) =>
        Array.Exists(args, argument => string.Equals(argument, "--cyber-buddha-stamp", StringComparison.OrdinalIgnoreCase));

    public static string DescribeRunningBuild()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(CyberBuddhaSeal).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        var executablePath = Environment.ProcessPath;
        var appHostPath = Path.Combine(AppContext.BaseDirectory, "CodexPond.exe");
        if (File.Exists(appHostPath) && !string.Equals(Path.GetFileName(executablePath), "CodexPond.exe", StringComparison.OrdinalIgnoreCase))
            executablePath = appHostPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            executablePath = assembly.Location;

        var fingerprint = "unavailable";
        if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
        {
            using var stream = File.OpenRead(executablePath);
            fingerprint = Convert.ToHexString(SHA256.HashData(stream));
        }

        var targetName = string.IsNullOrWhiteSpace(executablePath) ? "unknown" : Path.GetFileName(executablePath);
        return $"{SourceMark}\n\n{MarkId}\nVersion: {version}\nFile: {targetName}\nSHA-256: {fingerprint}\n\n此处是源码级玩笑彩蛋，不构成无 Bug 保证。仅将指纹与同版本官方 Release 校验清单对照；本地源码构建与发行版哈希通常不同。哈希不是数字签名，也不能单独证明文件来源。";
    }
}
