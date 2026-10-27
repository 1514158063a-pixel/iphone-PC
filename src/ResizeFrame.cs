using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace IPhonePC {
    // A nearly transparent, owned input frame stays above the foreign video HWND.
    // The center remains fully transparent so playback controls are unaffected.
    internal sealed class ResizeFrame : IDisposable {
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr GetCapture();
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
        [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);

        private readonly Window owner, frame;
        private readonly Border border;
        private readonly Func<bool> enabled;
        private readonly Action<bool> onDragging;
        private HwndSource source;
        private IntPtr ownerHandle, frameHandle;
        private bool disposed, dragging;
        private int direction, startX, startY;
        private Native.RECT startBounds;

        internal IntPtr Handle { get { return frameHandle; } }
        internal bool IsVisible { get { return frame.IsVisible; } }
        internal bool ContainsScreenPoint(IntPtr coordinates) {
            POINT point=Coordinates(coordinates);
            return frame.IsVisible && HitTest(point.X,point.Y)!=0;
        }

        public ResizeFrame(Window ownerWindow, Func<bool> canResize, Action<bool> draggingChanged) {
            owner = ownerWindow; enabled = canResize; onDragging = draggingChanged;
            border = new Border { BorderBrush = new SolidColorBrush(Color.FromArgb(1,0,0,0)) };
            frame = new Window {
                Title = "iPhone-PC · 调整窗口", Content = border, Background = Brushes.Transparent,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
                ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual
            };
            frame.SourceInitialized += delegate {
                frameHandle = new WindowInteropHelper(frame).Handle;
                source = HwndSource.FromHwnd(frameHandle);
                if (source != null) source.AddHook(WindowMessages);
            };
        }

        public void Sync() {
            if (disposed) return;
            ownerHandle = new WindowInteropHelper(owner).Handle;
            bool show = ownerHandle != IntPtr.Zero && owner.IsVisible && owner.WindowState == WindowState.Normal && enabled();
            if (!show) {
                EndDrag();
                if (frame.IsVisible) frame.Hide();
                return;
            }
            Native.RECT bounds;
            if (!Native.GetWindowRect(ownerHandle,out bounds)) return;
            HwndSource ownerSource = HwndSource.FromHwnd(ownerHandle);
            if (ownerSource == null || ownerSource.CompositionTarget == null) return;
            Matrix fromDevice = ownerSource.CompositionTarget.TransformFromDevice;
            Point origin = fromDevice.Transform(new Point(bounds.Left,bounds.Top));
            Vector size = fromDevice.Transform(new Vector(bounds.Right-bounds.Left,bounds.Bottom-bounds.Top));
            Vector edge = fromDevice.Transform(new Vector(HorizontalBorder(),VerticalBorder()));
            border.BorderThickness = new Thickness(edge.X,edge.Y,edge.X,edge.Y);
            if (!frame.IsVisible) {
                frame.Left = origin.X; frame.Top = origin.Y;
                frame.Width = Math.Max(1,size.X); frame.Height = Math.Max(1,size.Y);
                if (frame.Owner == null) frame.Owner = owner;
                frame.Show();
            }
            Native.SetWindowPos(frameHandle,IntPtr.Zero,bounds.Left,bounds.Top,
                Math.Max(1,bounds.Right-bounds.Left),Math.Max(1,bounds.Bottom-bounds.Top),Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        private static int HorizontalBorder() { return Math.Max(1,GetSystemMetrics(32)+GetSystemMetrics(92)); }
        private static int VerticalBorder() { return Math.Max(1,GetSystemMetrics(33)+GetSystemMetrics(92)); }
        private int HitTest(int x, int y) {
            Native.RECT bounds;
            if (!Native.GetWindowRect(ownerHandle,out bounds) || x < bounds.Left || x >= bounds.Right || y < bounds.Top || y >= bounds.Bottom) return 0;
            bool left=x<bounds.Left+HorizontalBorder(), right=x>=bounds.Right-HorizontalBorder();
            bool top=y<bounds.Top+VerticalBorder(), bottom=y>=bounds.Bottom-VerticalBorder();
            if(top) return left?13:right?14:12;
            if(bottom) return left?16:right?17:15;
            return left?10:right?11:0;
        }
        private static POINT Coordinates(IntPtr value) {
            long packed=value.ToInt64();
            return new POINT { X=unchecked((short)(packed&0xFFFF)), Y=unchecked((short)((packed>>16)&0xFFFF)) };
        }
        private POINT ScreenCoordinates(IntPtr value) {
            POINT point=Coordinates(value); ClientToScreen(frameHandle,ref point); return point;
        }
        private void UpdateCursor(int hit) {
            int resource = hit==10 || hit==11 ? 32644 : hit==12 || hit==15 ? 32645 :
                hit==13 || hit==17 ? 32642 : hit==14 || hit==16 ? 32643 : 0;
            if(resource!=0) SetCursor(LoadCursor(IntPtr.Zero,new IntPtr(resource)));
        }
        private IntPtr WindowMessages(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled) {
            if(message==0x0084) {
                POINT point=Coordinates(l); handled=true;
                return new IntPtr(HitTest(point.X,point.Y)==0 ? -1 : 1);
            }
            if(message==0x0021) { handled=true; return new IntPtr(3); } // Do not activate the transparent frame.
            if(message==0x0020) {
                POINT point; if(GetCursorPos(out point)) { int hit=dragging?direction:HitTest(point.X,point.Y); if(hit!=0) { UpdateCursor(hit); handled=true; return new IntPtr(1); } }
            }
            if(message==0x0201 && !dragging) {
                POINT point=ScreenCoordinates(l); int hit=HitTest(point.X,point.Y);
                if(hit!=0 && Native.GetWindowRect(ownerHandle,out startBounds)) {
                    direction=hit; startX=point.X; startY=point.Y; dragging=true;
                    Native.SetForegroundWindow(ownerHandle); SetCapture(frameHandle);
                    if(onDragging!=null) onDragging(true);
                    handled=true;
                }
            } else if(message==0x0200) {
                POINT point=ScreenCoordinates(l);
                if(dragging) { ResizeTo(point.X,point.Y); UpdateCursor(direction); handled=true; }
                else { int hit=HitTest(point.X,point.Y); if(hit!=0) { UpdateCursor(hit); handled=true; } }
            } else if(message==0x0202 && dragging) { EndDrag(); handled=true; }
            else if((message==0x0215 || message==0x001F) && dragging) EndDrag();
            return IntPtr.Zero;
        }
        private void ResizeTo(int x, int y) {
            HwndSource ownerSource=HwndSource.FromHwnd(ownerHandle);
            if(ownerSource==null || ownerSource.CompositionTarget==null) return;
            Matrix transform=ownerSource.CompositionTarget.TransformToDevice;
            int minimumWidth=(int)Math.Ceiling(owner.MinWidth*transform.M11);
            int minimumHeight=(int)Math.Ceiling(owner.MinHeight*transform.M22);
            int dx=x-startX, dy=y-startY;
            int left=startBounds.Left, top=startBounds.Top, right=startBounds.Right, bottom=startBounds.Bottom;
            if(direction==10 || direction==13 || direction==16) left=Math.Min(startBounds.Left+dx,right-minimumWidth);
            if(direction==11 || direction==14 || direction==17) right=Math.Max(startBounds.Right+dx,left+minimumWidth);
            if(direction==12 || direction==13 || direction==14) top=Math.Min(startBounds.Top+dy,bottom-minimumHeight);
            if(direction==15 || direction==16 || direction==17) bottom=Math.Max(startBounds.Bottom+dy,top+minimumHeight);
            Native.SetWindowPos(ownerHandle,IntPtr.Zero,left,top,right-left,bottom-top,Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        private void EndDrag() {
            if(!dragging) return;
            dragging=false;
            if(GetCapture()==frameHandle) ReleaseCapture();
            if(onDragging!=null) onDragging(false);
        }
        public void Dispose() {
            if(disposed) return;
            EndDrag(); disposed=true;
            if(source!=null && !source.IsDisposed) source.RemoveHook(WindowMessages);
            frame.Close(); source=null;
        }
    }
}
