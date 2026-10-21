using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexPond;

/// <summary>
/// Low-level hooks are installed only while an Agent turn is active. The callback reads only
/// whether an input was a key-down or mouse-button-down and never stores key codes or coordinates.
/// </summary>
public sealed class GlobalInputPulseMonitor : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;
    private const int WmXButtonDown = 0x020B;
    private readonly FishingActivityTracker _tracker;
    private readonly HookProc _keyboardCallback;
    private readonly HookProc _mouseCallback;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private bool _disposed;

    public GlobalInputPulseMonitor(FishingActivityTracker tracker)
    {
        _tracker = tracker;
        _keyboardCallback = KeyboardHook;
        _mouseCallback = MouseHook;
    }

    public event Action? PulseAccepted;
    public bool IsInstalled => _keyboardHook != IntPtr.Zero || _mouseHook != IntPtr.Zero;

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GlobalInputPulseMonitor));
        if (!_tracker.Enabled || IsInstalled) return;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = GetModuleHandle(module?.ModuleName);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardCallback, moduleHandle, 0);
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseCallback, moduleHandle, 0);
        if (_keyboardHook == IntPtr.Zero && _mouseHook == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启用匿名键鼠活动计数。");
    }

    public void Stop()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam.ToInt32() == WmKeyDown || wParam.ToInt32() == WmSysKeyDown)) RecordPulse();
        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        var message = wParam.ToInt32();
        if (code >= 0 && message is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmXButtonDown) RecordPulse();
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void RecordPulse()
    {
        if (_tracker.TryPulse(DateTimeOffset.Now)) PulseAccepted?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
