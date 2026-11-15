using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace RotrTrainer;

/// <summary>System-wide hotkeys through RegisterHotKey only; never a keyboard hook.</summary>
internal sealed class GlobalHotkeys(HwndSource window) : IDisposable
{
    private const int HotkeyMessage = 0x0312;

    private readonly Dictionary<int, Action> _actionsById = [];
    private bool _isHooked;
    private int _lastId;

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);

    /// <summary>False when another program already owns the key.</summary>
    public bool Register(Key key, Action action)
    {
        if (!_isHooked)
        {
            window.AddHook(OnMessage);
            _isHooked = true;
        }

        int id = ++_lastId;
        if (!RegisterHotKey(window.Handle, id, 0, (uint)KeyInterop.VirtualKeyFromKey(key)))
            return false;
        _actionsById[id] = action;
        return true;
    }

    public void Dispose()
    {
        foreach (int id in _actionsById.Keys)
            UnregisterHotKey(window.Handle, id);
        _actionsById.Clear();
        window.RemoveHook(OnMessage);
    }

    private nint OnMessage(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == HotkeyMessage && _actionsById.TryGetValue((int)wParam, out var action))
        {
            action();
            handled = true;
        }
        return 0;
    }
}
