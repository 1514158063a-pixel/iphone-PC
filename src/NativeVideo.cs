using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace IPhonePC {
    internal static class Native {
        public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        public const long WS_CHILD = 0x40000000L, WS_POPUP = 0x80000000L, WS_CAPTION = 0x00C00000L, WS_THICKFRAME = 0x00040000L;
        public const long WS_MINIMIZEBOX = 0x00020000L, WS_MAXIMIZEBOX = 0x00010000L, WS_SYSMENU = 0x00080000L;
        public const int SW_SHOW = 5, SW_RESTORE = 9;
        public const uint SWP_NOZORDER = 4, SWP_NOACTIVATE = 16, SWP_FRAMECHANGED = 32;
        public delegate bool EnumProc(IntPtr hwnd, IntPtr param);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr param);
        [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr param);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder value, int count);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder value, int count);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SetParent(IntPtr hwnd, IntPtr parent);
        [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint="SetWindowLongPtrW", SetLastError=true)] public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr CreateWindowEx(int exStyle, string className, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
        [DllImport("kernel32.dll")] public static extern void SetLastError(uint error);

        public static IntPtr FindVideo(int pid) {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr hwnd, IntPtr unused) {
                uint owner; GetWindowThreadProcessId(hwnd, out owner);
                if (owner != pid || !IsWindowVisible(hwnd)) return true;
                var name = new StringBuilder(256); GetClassName(hwnd, name, name.Capacity);
                if (!name.ToString().StartsWith("GSTD3D", StringComparison.Ordinal)) return true;
                RECT rect; GetClientRect(hwnd, out rect);
                if (rect.Right <= 0 || rect.Bottom <= 0) return true;
                found = hwnd; return false;
            }, IntPtr.Zero);
            return found;
        }
    }

    internal sealed class VideoHost : HwndHost {
        private const string WindowClass = "iPhonePC.BlackVideoHost";
        private static readonly object ClassLock = new object();
        private static readonly WindowProcedure Procedure = DefaultWindowProcedure;
        private static bool classRegistered;
        private delegate IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] private struct WindowClassInfo {
            public uint Size, Style;
            public WindowProcedure Procedure;
            public int ClassExtra, WindowExtra;
            public IntPtr Instance, Icon, Cursor, Background;
            public string MenuName, ClassName;
            public IntPtr SmallIcon;
        }
        [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern ushort RegisterClassEx(ref WindowClassInfo info);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string moduleName);
        [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int objectIndex);

        public IntPtr Container { get; private set; }
        public Action OnLayout;
        protected override HandleRef BuildWindowCore(HandleRef parent) {
            EnsureWindowClass();
            Container = Native.CreateWindowEx(0, WindowClass, "iPhone-PC Video Host", unchecked((int)0x56000000), 0, 0, 1, 1, parent.Handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (Container == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            return new HandleRef(this, Container);
        }
        private static void EnsureWindowClass() {
            lock (ClassLock) {
                if (classRegistered) return;
                var info = new WindowClassInfo {
                    Size = (uint)Marshal.SizeOf(typeof(WindowClassInfo)), Style = 3,
                    Procedure = Procedure, Instance = GetModuleHandle(null),
                    // SS_BLACKRECT uses the theme's window-frame color. A stock
                    // BLACK_BRUSH keeps every exposed scaling margin RGB black.
                    Background = GetStockObject(4), ClassName = WindowClass
                };
                if (RegisterClassEx(ref info) == 0) throw new System.ComponentModel.Win32Exception();
                classRegistered = true;
            }
        }
        private static IntPtr DefaultWindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam) {
            return DefWindowProc(hwnd, message, wParam, lParam);
        }
        protected override void DestroyWindowCore(HandleRef hwnd) { Native.DestroyWindow(hwnd.Handle); Container = IntPtr.Zero; }
        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) {
            if (msg == 5 && OnLayout != null) OnLayout();
            return base.WndProc(hwnd, msg, w, l, ref handled);
        }
    }

    internal sealed class VideoAttachment {
        public IntPtr Handle { get; private set; }
        private long originalStyle, originalExStyle;
        private IntPtr originalParent;
        private Native.RECT originalRect;
        private VideoHost host;
        private bool needsFrameUpdate;
        private int viewScale = 100;
        public int ViewScale {
            get { return viewScale; }
            set {
                if (value != 50 && value != 75 && value != 100) throw new ArgumentOutOfRangeException("value");
                if (viewScale == value) return;
                viewScale = value; needsFrameUpdate = true; Resize();
            }
        }
        public bool Alive { get { return Handle != IntPtr.Zero && Native.IsWindow(Handle); } }

        public void Attach(IntPtr hwnd, VideoHost target) {
            if (target.Container == IntPtr.Zero) throw new InvalidOperationException("视频区域尚未就绪");
            if (Handle != hwnd) {
                Restore();
                originalStyle = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
                originalExStyle = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                originalParent = Native.GetParent(hwnd);
                Native.GetWindowRect(hwnd, out originalRect);
                Handle = hwnd;
            }
            host = target;
            long style = (originalStyle | Native.WS_CHILD) & ~(Native.WS_POPUP | Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX | Native.WS_SYSMENU);
            Native.SetWindowLongPtr(hwnd, Native.GWL_STYLE, new IntPtr(style));
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(originalExStyle & ~0x00040300L));
            Native.SetLastError(0);
            IntPtr previous = Native.SetParent(hwnd, target.Container);
            int error = Marshal.GetLastWin32Error();
            if (previous == IntPtr.Zero && error != 0) { Restore(); throw new System.ComponentModel.Win32Exception(error, "无法嵌入镜像画面"); }
            Native.ShowWindow(hwnd, Native.SW_SHOW);
            needsFrameUpdate = true;
            Resize();
        }
        public void Resize() {
            if (!Alive || host == null || host.Container == IntPtr.Zero) return;
            Native.RECT rect; Native.GetClientRect(host.Container, out rect);
            int width = Math.Max(1, (int)Math.Round(rect.Right * viewScale / 100.0));
            int height = Math.Max(1, (int)Math.Round(rect.Bottom * viewScale / 100.0));
            int left = (rect.Right - width) / 2, top = (rect.Bottom - height) / 2;
            Native.RECT current; Native.GetWindowRect(Handle, out current);
            Native.RECT container; Native.GetWindowRect(host.Container, out container);
            if (!needsFrameUpdate && current.Right-current.Left == width && current.Bottom-current.Top == height &&
                current.Left == container.Left+left && current.Top == container.Top+top) return;
            Native.SetWindowPos(Handle, IntPtr.Zero, left, top, width, height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
            Native.InvalidateRect(host.Container, IntPtr.Zero, true);
            needsFrameUpdate = false;
        }
        public void Restore() {
            if (Alive) {
                Native.SetParent(Handle, originalParent);
                Native.SetWindowLongPtr(Handle, Native.GWL_STYLE, new IntPtr(originalStyle));
                Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, new IntPtr(originalExStyle));
                Native.SetWindowPos(Handle, IntPtr.Zero, originalRect.Left, originalRect.Top, Math.Max(320,originalRect.Right-originalRect.Left), Math.Max(200,originalRect.Bottom-originalRect.Top), Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
            }
            Handle = IntPtr.Zero; host = null;
        }
        public void Forget() { Handle = IntPtr.Zero; host = null; }
    }
}
