using System.Runtime.InteropServices;

namespace Quartermaster.App;

/// <summary>
/// Screenshot runs only (QUARTERMASTER_SHOT_OFFSCREEN=1, set by build/wiki-screenshots.ps1): every top-level window of this process is created far
/// outside every screen, kept there whatever the app asks for, left out of the taskbar and never activated, so a run never shows anything on the
/// desktop or takes the keyboard focus. Pictures are always drawn from the windows themselves (DrawToBitmap / Skia), so they do not need the screen.
/// Two thread hooks on the UI thread do it: a CBT hook (window creation and activation) and a call-window-proc hook (position changes).
/// </summary>
static class OffscreenShots
{
    public const string EnvVar = "QUARTERMASTER_SHOT_OFFSCREEN";
    const int Far = -30000;

    const int WH_CBT = 5, WH_CALLWNDPROC = 4;
    const int HCBT_CREATEWND = 3, HCBT_ACTIVATE = 5;
    const uint WS_CHILD = 0x40000000;
    const uint WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_APPWINDOW = 0x40000;
    const int WM_WINDOWPOSCHANGING = 0x46, GWL_STYLE = -16;
    const uint SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    // kept in statics so the delegates are never collected while hooked
    static HookProc? _cbt, _wnd;
    static IntPtr _cbtHook, _wndHook;

    public static bool Active { get; private set; }

    /// <summary>Installs the hooks when the environment variable is "1". Call on the UI thread before any window exists.</summary>
    public static void InstallIfRequested()
    {
        if (Environment.GetEnvironmentVariable(EnvVar) != "1" || Active) return;
        _cbt = CbtProc;
        _wnd = WndProc;
        var thread = GetCurrentThreadId();
        _cbtHook = SetWindowsHookEx(WH_CBT, _cbt, IntPtr.Zero, thread);
        _wndHook = SetWindowsHookEx(WH_CALLWNDPROC, _wnd, IntPtr.Zero, thread);
        Active = _cbtHook != IntPtr.Zero && _wndHook != IntPtr.Zero;
        Program.AppLog("off-screen screenshot mode " + (Active ? "on" : "FAILED to install"));
    }

    static bool TopLevel(IntPtr hwnd) => (GetWindowLong(hwnd, GWL_STYLE) & WS_CHILD) == 0;

    static IntPtr CbtProc(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code == HCBT_CREATEWND)
            {
                var cbt = Marshal.PtrToStructure<CbtCreateWnd>(lParam);
                var cs = Marshal.PtrToStructure<CreateStruct>(cbt.lpcs);
                if ((cs.style & WS_CHILD) == 0)
                {
                    cs.x = Far;
                    cs.y = Far;
                    cs.dwExStyle = (cs.dwExStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~WS_EX_APPWINDOW;
                    Marshal.StructureToPtr(cs, cbt.lpcs, false);
                }
            }
            else if (code == HCBT_ACTIVATE && TopLevel(wParam)) return (IntPtr)1; // refuse: nothing of ours takes the focus
        }
        catch (Exception) { /* a hook must never throw into the window system */ }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    static IntPtr WndProc(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                var m = Marshal.PtrToStructure<CallWndProcStruct>(lParam);
                if (m.message == WM_WINDOWPOSCHANGING && TopLevel(m.hwnd))
                {
                    var p = Marshal.PtrToStructure<WindowPos>(m.lParam);
                    p.x = Far;
                    p.y = Far;
                    p.flags = (p.flags & ~SWP_NOMOVE) | SWP_NOACTIVATE;
                    Marshal.StructureToPtr(p, m.lParam, false);
                }
            }
        }
        catch (Exception) { /* see above */ }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)] struct CbtCreateWnd { public IntPtr lpcs; public IntPtr hwndInsertAfter; }

    [StructLayout(LayoutKind.Sequential)]
    struct CreateStruct
    {
        public IntPtr lpCreateParams, hInstance, hMenu, hwndParent;
        public int cy, cx, y, x;
        public uint style;
        public IntPtr lpszName, lpszClass;
        public uint dwExStyle;
    }

    [StructLayout(LayoutKind.Sequential)] struct CallWndProcStruct { public IntPtr lParam, wParam; public uint message; public IntPtr hwnd; }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowPos { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    static uint GetWindowLong(IntPtr hwnd, int index) => (uint)GetWindowLongPtr(hwnd, index);
}
