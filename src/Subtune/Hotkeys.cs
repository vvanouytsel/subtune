using static Subtune.Native;

namespace Subtune;

/// <summary>Global hotkeys, so the overlay can be controlled while another app has focus.</summary>
sealed class Hotkeys : NativeWindow, IDisposable
{
    readonly Dictionary<int, Action> _actions = [];

    public Hotkeys() => CreateHandle(new CreateParams { Parent = HWND_MESSAGE });

    /// <summary>
    /// Registers Ctrl+Alt+key, or Ctrl+Alt+Shift+key when another app already owns that.
    /// Returns the combo that was registered, or null if both were taken.
    /// </summary>
    public string? Register(Keys key, Action action)
    {
        int id = _actions.Count + 1;
        foreach (var (modifiers, label) in new[] { (MOD_CONTROL | MOD_ALT, "Ctrl+Alt+"), (MOD_CONTROL | MOD_ALT | MOD_SHIFT, "Ctrl+Alt+Shift+") })
        {
            if (RegisterHotKey(Handle, id, modifiers | MOD_NOREPEAT, (uint)key))
            {
                _actions[id] = action;
                return label + key;
            }
        }
        Log.Write($"no free hotkey for {key}");
        return null;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && _actions.TryGetValue((int)m.WParam, out var action))
            action();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys)
            UnregisterHotKey(Handle, id);
        DestroyHandle();
    }
}
