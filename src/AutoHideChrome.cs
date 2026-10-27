using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace IPhonePC {
    internal sealed class ChromeVisibilityPolicy {
        private long lastActivity;
        private bool heldFromInside;
        public bool Visible { get; private set; }
        public ChromeVisibilityPolicy() { Visible = true; }
        public void Reset(long now) { lastActivity = now; heldFromInside = false; Visible = true; }
        public bool Update(long now, bool inside, bool moved, bool pressed, bool manipulating) {
            if (!pressed) heldFromInside = false;
            else if (inside) heldFromInside = true;
            if (manipulating || heldFromInside || (inside && moved)) { lastActivity = now; Visible = true; }
            else if (!inside || now - lastActivity >= 2000) Visible = false;
            return Visible;
        }
    }

    // Keep the owner's client rectangle fixed. A separate owned HWND places the
    // controls above the foreign video HWND without changing its layout/airspace.
    internal sealed class AutoHideChrome : IDisposable {
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO {
            public int Size;
            public Native.RECT Monitor, Work;
            public uint Flags;
        }
        [StructLayout(LayoutKind.Sequential)] private struct MINMAXINFO {
            public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
        }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out uint value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);

        private readonly Window window, overlay;
        private readonly ResizeFrame resizeFrame;
        private readonly FrameworkElement toolbar;
        private readonly Button maximize;
        private readonly Func<bool> isFullScreen, keepVisible;
        private readonly ChromeVisibilityPolicy policy = new ChromeVisibilityPolicy();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private HwndSource source, overlaySource;
        private IntPtr handle, overlayHandle;
        private POINT previous;
        private bool hasPrevious, manipulating, disposed;
        private uint normalBorderColor = 0xFFFFFFFF, normalCornerPreference;
        public bool Visible { get { return policy.Visible; } }
        internal Window OverlayWindow { get { return overlay; } }
        internal ResizeFrame InputFrame { get { return resizeFrame; } }

        public AutoHideChrome(Window target, FrameworkElement controls, Func<bool> fullScreen, Action changed, Func<bool> pendingInteraction = null) {
            window = target; toolbar = controls; isFullScreen = fullScreen; keepVisible = pendingInteraction;
            window.WindowStyle = WindowStyle.None;
            var surface = new Grid { Background = new SolidColorBrush(Color.FromRgb(28,36,48)) };
            surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
            surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            var caption = new DockPanel { LastChildFill = true };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var minimize = CaptionButton("\uE921", "最小化", false);
            maximize = CaptionButton("\uE922", "最大化 / 还原", false);
            var close = CaptionButton("\uE8BB", "返回工作台", true);
            minimize.Click += delegate { window.WindowState = WindowState.Minimized; };
            maximize.Click += delegate { ToggleMaximize(); };
            close.Click += delegate { window.Close(); };
            actions.Children.Add(minimize); actions.Children.Add(maximize); actions.Children.Add(close);
            DockPanel.SetDock(actions, Dock.Right); caption.Children.Add(actions);
            var title = new TextBlock { Foreground = Brushes.White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            title.SetBinding(TextBlock.TextProperty, new Binding("Title") { Source = window });
            var dragArea = new Border { Background = Brushes.Transparent, Padding = new Thickness(12,0,0,0), Child = title };
            dragArea.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args) {
                if (args.ClickCount == 2) ToggleMaximize();
                else if (!isFullScreen()) {
                    manipulating = true;
                    try { window.DragMove(); } catch (InvalidOperationException) { }
                    finally { manipulating = false; Reveal(); }
                }
                args.Handled = true;
            };
            caption.Children.Add(dragArea); surface.Children.Add(caption);
            Grid.SetRow(toolbar,1); surface.Children.Add(toolbar);
            overlay = new Window {
                Title = "iPhone-PC · 播放控制", Content = surface, Height = 64, Width = 600,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowActivated = false, ShowInTaskbar = false, Background = surface.Background,
                FontFamily = window.FontFamily, FontSize = 12, WindowStartupLocation = WindowStartupLocation.Manual
            };
            overlay.SourceInitialized += delegate {
                overlayHandle = new WindowInteropHelper(overlay).Handle;
                overlaySource = HwndSource.FromHwnd(overlayHandle);
                if (overlaySource != null) overlaySource.AddHook(OverlayMessages);
                SetFrameAppearance(overlayHandle, true);
            };
            overlay.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs args) {
                // Alt+F4 on a focused toolbar follows the video window's close
                // behavior; never destroy the reusable overlay on its own.
                if (!disposed) { args.Cancel = true; window.Close(); }
            };
            resizeFrame = new ResizeFrame(window,delegate { return !isFullScreen(); },delegate(bool active) { manipulating=active; Reveal(); });
            timer.Interval = TimeSpan.FromMilliseconds(100);
            timer.Tick += Tick;
            window.SourceInitialized += SourceInitialized;
            window.IsVisibleChanged += VisibilityChanged;
            window.StateChanged += StateChanged;
            window.LocationChanged += OwnerMoved;
            window.SizeChanged += OwnerResized;
            window.Closed += Closed;
            if (new WindowInteropHelper(window).Handle != IntPtr.Zero) AttachSource();
        }

        private static Button CaptionButton(string glyph, string tooltip, bool close) {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            style.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border Background='{TemplateBinding Background}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border></ControlTemplate>")));
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(close ? Color.FromRgb(196,43,28) : Color.FromRgb(56,68,83))));
            style.Triggers.Add(hover);
            return new Button { Content = glyph, ToolTip = tooltip, Width = 42, Height = 28, Style = style, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Focusable = false };
        }
        private void ToggleMaximize() {
            if (isFullScreen()) return;
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        private void SourceInitialized(object sender, EventArgs args) { AttachSource(); }
        private void AttachSource() {
            if (source != null) return;
            handle = new WindowInteropHelper(window).Handle;
            source = HwndSource.FromHwnd(handle);
            if (source != null) {
                source.AddHook(WindowMessages);
                try {
                    uint value;
                    if (DwmGetWindowAttribute(handle,34,out value,4) == 0) normalBorderColor = value;
                    if (DwmGetWindowAttribute(handle,33,out value,4) == 0) normalCornerPreference = value;
                } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
                SetFrameAppearance(handle,true);
                Native.SetWindowPos(handle,IntPtr.Zero,0,0,0,0,Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED | 3);
            }
        }
        private IntPtr WindowMessages(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled) {
            if (message == 0x0083) { handled = true; return IntPtr.Zero; }
            if (message == 0x0084) { handled = true; return new IntPtr(ResizeHitTest(l)); }
            if (message == 0x0024 && l != IntPtr.Zero) {
                var monitor = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (GetMonitorInfo(MonitorFromWindow(hwnd,2),ref monitor)) {
                    Native.RECT area = isFullScreen() ? monitor.Monitor : monitor.Work;
                    var limits = (MINMAXINFO)Marshal.PtrToStructure(l,typeof(MINMAXINFO));
                    limits.MaxPosition = new POINT { X = area.Left-monitor.Monitor.Left, Y = area.Top-monitor.Monitor.Top };
                    limits.MaxSize = new POINT { X = area.Right-area.Left, Y = area.Bottom-area.Top };
                    double dpi = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformToDevice.M11 : 1;
                    limits.MinTrackSize = new POINT { X = (int)Math.Ceiling(window.MinWidth*dpi), Y = (int)Math.Ceiling(window.MinHeight*dpi) };
                    Marshal.StructureToPtr(limits,l,false); handled = true;
                }
            }
            if (message == 0x0231 || message == 0x0211) { manipulating = true; Reveal(); }
            if (message == 0x0232 || message == 0x0212) { manipulating = false; Reveal(); }
            return IntPtr.Zero;
        }
        private IntPtr OverlayMessages(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled) {
            if (message == 0x0084 && resizeFrame.ContainsScreenPoint(l)) { handled = true; return new IntPtr(-1); }
            return IntPtr.Zero;
        }
        private int ResizeHitTest(IntPtr location) {
            if (isFullScreen() || window.WindowState != WindowState.Normal ||
                (window.ResizeMode != ResizeMode.CanResize && window.ResizeMode != ResizeMode.CanResizeWithGrip)) return 1;
            Native.RECT bounds;
            if (!Native.GetWindowRect(handle,out bounds)) return 1;
            long packed = location.ToInt64();
            int x = unchecked((short)(packed & 0xFFFF)), y = unchecked((short)((packed >> 16) & 0xFFFF));
            if (x < bounds.Left || x >= bounds.Right || y < bounds.Top || y >= bounds.Bottom) return 0;
            int horizontal = Math.Max(1,GetSystemMetrics(32)+GetSystemMetrics(92));
            int vertical = Math.Max(1,GetSystemMetrics(33)+GetSystemMetrics(92));
            bool left = x < bounds.Left+horizontal, right = x >= bounds.Right-horizontal;
            bool top = y < bounds.Top+vertical, bottom = y >= bounds.Bottom-vertical;
            if (top) return left ? 13 : right ? 14 : 12;
            if (bottom) return left ? 16 : right ? 17 : 15;
            return left ? 10 : right ? 11 : 1;
        }
        private void SetFrameAppearance(IntPtr target, bool suppress) {
            try {
                uint border = suppress ? 0xFFFFFFFE : normalBorderColor;
                uint corner = suppress ? 1 : normalCornerPreference;
                DwmSetWindowAttribute(target,34,ref border,4);
                DwmSetWindowAttribute(target,33,ref corner,4);
            } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        }
        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) {
            if (window.IsVisible) { hasPrevious = false; Reveal(); timer.Start(); }
            else { timer.Stop(); manipulating = false; overlay.Hide(); }
            resizeFrame.Sync();
        }
        private void StateChanged(object sender, EventArgs args) {
            if (window.WindowState != WindowState.Minimized && window.IsVisible) Reveal();
            else overlay.Hide();
            resizeFrame.Sync();
        }
        private void OwnerMoved(object sender, EventArgs args) { PositionOverlay(); resizeFrame.Sync(); }
        private void OwnerResized(object sender, SizeChangedEventArgs args) { PositionOverlay(); resizeFrame.Sync(); }
        private void PositionOverlay() {
            if (disposed || !overlay.IsVisible || handle == IntPtr.Zero || overlayHandle == IntPtr.Zero) return;
            Native.RECT bounds;
            if (!Native.GetWindowRect(handle,out bounds)) return;
            double dpi = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformToDevice.M22 : 1;
            Native.SetWindowPos(overlayHandle,IntPtr.Zero,bounds.Left,bounds.Top,Math.Max(1,bounds.Right-bounds.Left),(int)Math.Round(64*dpi),Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        private void Tick(object sender, EventArgs args) {
            if (disposed || !window.IsVisible || window.WindowState == WindowState.Minimized || handle == IntPtr.Zero) return;
            POINT point;
            if (!GetCursorPos(out point)) return;
            bool moved = hasPrevious && (Math.Abs((long)point.X-previous.X) >= 2 || Math.Abs((long)point.Y-previous.Y) >= 2);
            if (moved || !hasPrevious) previous = point;
            hasPrevious = true;
            IntPtr underPointer = WindowFromPoint(point);
            IntPtr root = underPointer != IntPtr.Zero ? GetAncestor(underPointer,2) : IntPtr.Zero;
            bool inside = root == handle || root == overlayHandle || (underPointer != IntPtr.Zero && GetAncestor(underPointer,3) == handle);
            bool pressed = (GetAsyncKeyState(1) & 0x8000) != 0 || (GetAsyncKeyState(2) & 0x8000) != 0 ||
                (GetAsyncKeyState(4) & 0x8000) != 0 || (GetAsyncKeyState(5) & 0x8000) != 0 || (GetAsyncKeyState(6) & 0x8000) != 0;
            bool before = policy.Visible;
            bool after = policy.Update(clock.ElapsedMilliseconds,inside,moved,pressed,manipulating || (keepVisible != null && keepVisible()));
            if (after != before) ApplyVisibility();
        }
        public void Reveal() {
            if (disposed) return;
            policy.Reset(clock.ElapsedMilliseconds); ApplyVisibility();
        }
        public void RefreshFullScreen() { resizeFrame.Sync(); Reveal(); }
        private void ApplyVisibility() {
            if (disposed) return;
            toolbar.Visibility = policy.Visible ? Visibility.Visible : Visibility.Collapsed;
            maximize.IsEnabled = !isFullScreen();
            maximize.Content = window.WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            if (policy.Visible && window.IsVisible && window.WindowState != WindowState.Minimized) {
                if (overlay.Owner == null) overlay.Owner = window;
                if (!overlay.IsVisible) {
                    Native.RECT bounds;
                    if (Native.GetWindowRect(handle,out bounds)) {
                        Matrix transform = source.CompositionTarget.TransformFromDevice;
                        Point origin = transform.Transform(new Point(bounds.Left,bounds.Top));
                        overlay.Left = origin.X; overlay.Top = origin.Y;
                        overlay.Width = Math.Max(1,transform.Transform(new Vector(bounds.Right-bounds.Left,0)).X);
                    }
                    overlay.Show();
                }
                PositionOverlay();
            } else overlay.Hide();
            // No owner style, geometry, video host or layout mutation here.
        }
        private void Closed(object sender, EventArgs args) { Dispose(); }
        public void Dispose() {
            if (disposed) return;
            disposed = true; timer.Stop(); timer.Tick -= Tick;
            window.SourceInitialized -= SourceInitialized;
            window.IsVisibleChanged -= VisibilityChanged;
            window.StateChanged -= StateChanged;
            window.LocationChanged -= OwnerMoved;
            window.SizeChanged -= OwnerResized;
            window.Closed -= Closed;
            resizeFrame.Dispose();
            if (source != null && !source.IsDisposed) source.RemoveHook(WindowMessages);
            if (overlaySource != null && !overlaySource.IsDisposed) overlaySource.RemoveHook(OverlayMessages);
            var panel = toolbar.Parent as Panel;
            if (panel != null) panel.Children.Remove(toolbar);
            overlay.Close();
            if (Native.IsWindow(handle)) SetFrameAppearance(handle,false);
            source = null;
        }
    }
}
