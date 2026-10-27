using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace IPhonePC {
    internal static class Program {
        public static Window LoadWindow() {
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) return (Window)XamlReader.Load(stream);
        }
        [STAThread] public static int Main(string[] args) {
            try {
#if SELFTEST
                if(args.Length>0 && args[0]=="--self-test") return SelfTests.Run(args.Length>1?args[1]:System.IO.Path.Combine(Storage.Root,"self-test.json"));
                if(args.Length>0 && args[0]=="--test-video") return SelfTests.RunVideoHelper();
                if(args.Length>1 && args[0]=="--render-controls") return SelfTests.RenderPlaybackControls(args[1]);
#endif
                var app=new Application();
                app.ShutdownMode=ShutdownMode.OnMainWindowClose;
                app.DispatcherUnhandledException+=delegate(object sender,DispatcherUnhandledExceptionEventArgs e) { Storage.Log(e.Exception.ToString()); MessageBox.Show(e.Exception.Message,"iPhone-PC",MessageBoxButton.OK,MessageBoxImage.Error); e.Handled=true; };
                if(args.Length>1 && args[0]=="--render-preview") {
                    var preview=LoadWindow();
                    preview.Left=-20000; preview.Top=-20000; preview.ShowActivated=false; preview.ShowInTaskbar=false; preview.Show(); preview.UpdateLayout();
                    var content=(FrameworkElement)preview.Content;
                    var bitmap=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32); bitmap.Render(content);
                    var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using(var file=File.Create(args[1])) encoder.Save(file);
                    preview.Close();
                    return 0;
                }
                bool created;
                using(var mutex=new Mutex(true,@"Local\IPhonePC.Workbench",out created)) {
                    if(!created) {
                        foreach(var process in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName)) {
                            try { if(process.Id!=Process.GetCurrentProcess().Id && process.MainWindowHandle!=IntPtr.Zero) { Native.ShowWindow(process.MainWindowHandle,Native.SW_RESTORE); Native.SetForegroundWindow(process.MainWindowHandle); } } catch { }
                        }
                        return 0;
                    }
                    AudioDiagnostics.Configure(args,Storage.Data);
                    var controller=new Controller(LoadWindow()); app.MainWindow=controller.Window; app.Run(controller.Window);
                    mutex.ReleaseMutex();
                }
                return 0;
            } catch(Exception error) {
                Storage.Log(error.ToString());
                MessageBox.Show(error.Message,"iPhone-PC 无法启动",MessageBoxButton.OK,MessageBoxImage.Error); return 1;
            }
        }
    }

    internal sealed class Controller {
        public readonly Window Window;
        private readonly Settings settings;
        private readonly Receiver receiver;
        private readonly VideoAttachment attachment=new VideoAttachment();
        private readonly VideoHost inlineHost=new VideoHost();
        private readonly DispatcherTimer timer=new DispatcherTimer();
        private Window detachedWindow;
        private VideoHost detachedHost;
        private TextBlock detachedWaiting;
        private Button detachedFullScreen;
        private AutoHideChrome detachedChrome;
        private VideoScaleSelector scaleSelector, detachedScaleSelector;
        private bool busy,closing,allowClose,fullScreen,detached;
        private WindowState beforeFullScreen;
        private string lastStatus="";
        private string errorMessage="";
        private DateTime lastAttachAttempt=DateTime.MinValue;
        private readonly Grid slot;
        private readonly FrameworkElement waiting;

        private T Find<T>(string name) where T:class { return Window.FindName(name) as T; }
        private void Text(string name,string value) { Find<TextBlock>(name).Text=value; }
        public Controller(Window window) {
            Window=window; settings=Storage.Load(); receiver=new Receiver(settings); detached=settings.Detached;
            attachment.ViewScale=settings.ViewScale;
            scaleSelector=new VideoScaleSelector(Find<Button>("ScaleButton"),ApplyViewScale);
            scaleSelector.SetValue(settings.ViewScale);
            slot=Find<Grid>("VideoSlot"); waiting=Find<FrameworkElement>("WaitPanel");
            slot.Children.Insert(0,inlineHost); inlineHost.Visibility=Visibility.Collapsed; inlineHost.OnLayout=attachment.Resize;
            var resolution=Find<ComboBox>("ResolutionCombo");
            foreach(ComboBoxItem item in resolution.Items) if((string)item.Tag==settings.Resolution) resolution.SelectedItem=item;
            Find<RadioButton>("Fps"+settings.Fps).IsChecked=true;
            Find<TextBox>("ReceiverNameInput").Text=settings.ReceiverName;
            Find<TextBox>("PasswordInput").Text=settings.Password;
            Find<TextBox>("ReceiverNameInput").TextChanged+=ConnectionEdited;
            Find<TextBox>("PasswordInput").TextChanged+=ConnectionEdited;
            Find<Button>("ApplyButton").Click+=async delegate { await Apply(); };
            Find<Button>("RestartButton").Click+=async delegate { await ApplyConnection(); };
            Find<Button>("DetachButton").Click+=delegate { SetDetached(!detached,true); };
            Find<Button>("FullscreenButton").Click+=delegate { SetDetached(true,true); ToggleFullScreen(); };
            Window.Loaded+=async delegate { UpdateMode(); if(detached) EnsureDetached(); await Start(false); };
            Window.SizeChanged+=delegate { attachment.Resize(); };
            Window.Closing+=OnClosing;
            timer.Interval=TimeSpan.FromMilliseconds(750); timer.Tick+=delegate { Poll(); }; timer.Start();
        }
        private async Task Apply() {
            if(busy) return;
            var selection=Find<ComboBox>("ResolutionCombo").SelectedItem as ComboBoxItem;
            settings.Resolution=(string)selection.Tag;
            settings.Fps=Find<RadioButton>("Fps24").IsChecked==true?24:Find<RadioButton>("Fps30").IsChecked==true?30:60;
            await Start(true);
            if(string.IsNullOrEmpty(errorMessage)) Text("FeedbackText","设置已应用。请在 iPhone 上重新连接「"+settings.ReceiverName+"」。");
        }
        private void ApplyViewScale(int percent) {
            settings.ViewScale=percent; attachment.ViewScale=percent;
            scaleSelector.SetValue(percent);
            if(detachedScaleSelector!=null) detachedScaleSelector.SetValue(percent);
            if(detachedChrome!=null) detachedChrome.Reveal();
            try { Storage.Save(settings); }
            catch(Exception error) { Storage.Log("保存画面缩放失败: "+error); Text("FeedbackText","画面缩放已生效，保存设置失败："+error.Message); }
            Poll();
        }
        private void ConnectionFeedback(string message,bool error) {
            Text("ConnectionFeedbackText",message);
            Text("FeedbackText",message);
            Find<TextBlock>("FeedbackText").Foreground=new SolidColorBrush((Color)ColorConverter.ConvertFromString(error?"#B42318":"#657183"));
        }
        private void ConnectionEdited(object sender,TextChangedEventArgs args) {
            bool changed=Find<TextBox>("ReceiverNameInput").Text!=settings.ReceiverName || Find<TextBox>("PasswordInput").Text!=settings.Password;
            ConnectionFeedback(changed?"尚未保存。点击下方按钮后生效。":"保存后需在 iPhone 上重新连接。",false);
        }
        private async Task ApplyConnection() {
            if(busy || closing) return;
            string name,validationError;
            string password=Find<TextBox>("PasswordInput").Text;
            if(!Settings.TryValidateConnection(Find<TextBox>("ReceiverNameInput").Text,password,out name,out validationError)) {
                ConnectionFeedback(validationError,true); return;
            }
            string previousName=settings.ReceiverName,previousPassword=settings.Password;
            settings.ReceiverName=name; settings.Password=password;
            try { Storage.Save(settings); }
            catch(Exception error) {
                settings.ReceiverName=previousName; settings.Password=previousPassword;
                Storage.Log("保存连接设置失败: "+error);
                ConnectionFeedback("保存失败："+error.Message,true); return;
            }
            Find<TextBox>("ReceiverNameInput").Text=settings.ReceiverName;
            if(detachedWindow!=null) detachedWindow.Title=settings.ReceiverName+" · 镜像画面";
            if(detachedWaiting!=null) detachedWaiting.Text="等待 iPhone 画面\n在手机的屏幕镜像中选择「"+settings.ReceiverName+"」";
            ConnectionFeedback("已保存，正在重启接收端…",false);
            await Start(true);
            ConnectionFeedback(string.IsNullOrEmpty(errorMessage)?"已保存。请重新连接「"+settings.ReceiverName+"」。":"设置已保存，但启动失败。"+errorMessage,!string.IsNullOrEmpty(errorMessage));
        }
        private void Busy(bool value) {
            busy=value;
            Find<Button>("ApplyButton").IsEnabled=!value;
            Find<Button>("RestartButton").IsEnabled=!value;
            Find<TextBox>("ReceiverNameInput").IsEnabled=!value;
            Find<TextBox>("PasswordInput").IsEnabled=!value;
            Find<ComboBox>("ResolutionCombo").IsEnabled=!value;
            foreach(string name in new [] {"Fps24","Fps30","Fps60"}) Find<RadioButton>(name).IsEnabled=!value;
        }
        private async Task Start(bool restart) {
            if(busy || closing) return;
            Busy(true); errorMessage="";
            Find<TextBlock>("FeedbackText").Foreground=new SolidColorBrush((Color)ColorConverter.ConvertFromString("#657183"));
            Text("StatusText",restart?"正在重启接收端":"正在启动接收端");
            Text("EngineText","正在准备 AirPlay 接收");
            Text("FeedbackText",restart?"正在应用设置，手机需要重新连接…":"正在启动，请稍候…");
            if(restart) { attachment.Restore(); inlineHost.Visibility=Visibility.Collapsed; if(detachedHost!=null) detachedHost.Visibility=Visibility.Collapsed; waiting.Visibility=Visibility.Visible; }
            try {
                await receiver.Start(restart); Storage.Save(settings);
                Text("FeedbackText",AudioDiagnostics.Enabled?"音频诊断已就绪。请重新连接，用相册视频拖动三次；连接后最多 1 分钟自动恢复普通模式。":restart?"接收端已就绪，请重新连接「"+settings.ReceiverName+"」。":"接收端已就绪，在 iPhone 的屏幕镜像中选择「"+settings.ReceiverName+"」。");
            } catch(Exception error) { errorMessage=error.Message; Storage.Log(error.ToString()); Text("FeedbackText",error.Message); }
            finally { Busy(false); Poll(); }
        }
        private void Poll() {
            if(closing || busy) return;
            try {
                bool alive=receiver.Alive, listening=alive && receiver.Listening, client=alive && receiver.ClientConnected, streaming=alive && receiver.MirrorConnected;
                if(!attachment.Alive) attachment.Forget();
                if(streaming && !attachment.Alive && DateTime.UtcNow-lastAttachAttempt>TimeSpan.FromSeconds(3)) {
                    IntPtr video=Native.FindVideo(receiver.Pid);
                    if(video!=IntPtr.Zero) {
                        lastAttachAttempt=DateTime.UtcNow;
                        try {
                            if(detached) EnsureDetached();
                            VideoHost target=detached?detachedHost:inlineHost;
                            target.Visibility=Visibility.Visible; target.UpdateLayout(); Window.UpdateLayout();
                            attachment.Attach(video,target);
                            Storage.Log("已嵌入视频 HWND="+video.ToInt64()+" mode="+(detached?"window":"inline"));
                        } catch(Exception error) { Storage.Log(error.ToString()); Text("FeedbackText","画面嵌入失败，可使用接收端原生视频窗口。"+error.Message); }
                    }
                }
                bool hasVideo=streaming && attachment.Alive;
                if(Find<TextBlock>("WaitTitle")!=null) {
                    Text("WaitTitle",hasVideo && detached?"画面正在独立窗口播放":client?"正在接收手机画面":"等待 iPhone 连接");
                    Text("WaitInstruction",hasVideo && detached?"点击「返回工作台」可将画面放回这里。":client?"首次连接可能需要几秒钟。":"在 iPhone 打开控制中心\n选择「屏幕镜像」→「"+settings.ReceiverName+"」");
                    Text("WaitNetworkText",hasVideo && detached?"独立窗口可自由移动、缩放和全屏":"手机和电脑需连接同一局域网");
                }
                inlineHost.Visibility=hasVideo && !detached?Visibility.Visible:Visibility.Collapsed;
                waiting.Visibility=hasVideo && !detached?Visibility.Collapsed:Visibility.Visible;
                if(detachedHost!=null) detachedHost.Visibility=hasVideo && detached?Visibility.Visible:Visibility.Collapsed;
                if(detachedWaiting!=null) detachedWaiting.Visibility=hasVideo?Visibility.Collapsed:Visibility.Visible;
                string status=hasVideo?"iPhone 已连接":client?"已连接，等待手机画面":listening?"接收端已就绪":"接收端未启动";
                if(errorMessage.Length>0) status="接收端需要处理";
                Text("StatusText",status);
                Find<System.Windows.Shapes.Ellipse>("StatusDot").Fill=new SolidColorBrush((Color)ColorConverter.ConvertFromString(hasVideo?"#16865A":listening?"#3271D9":"#C18621"));
                string info="实际接收：等待连接";
                if(hasVideo) {
                    string log=receiver.ReadRecentLog();
                    info=log.Contains("video format is h265")?"已收到 HEVC 画面 · 保持原始比例":"已收到手机画面 · 保持原始比例";
                } else if(client) info="已建立连接，等待视频流";
                Text("ActualText",info);
                Text("EngineText",AudioDiagnostics.Enabled?"音频诊断中 · 稍后自动恢复":alive?"AirPlay 接收运行中":"AirPlay 接收已停止");
                Text("CurrentModeText",detached?"画面使用独立窗口":"画面在工作台内显示");
                if(hasVideo) attachment.Resize();
                SaveStatus(status,client,hasVideo);
                string diagnosticEnd=AudioDiagnostics.CompletionDue(client);
                if(diagnosticEnd!=null) FinishAudioDiagnostics(diagnosticEnd);
            } catch(Exception error) { Storage.Log("状态检查: "+error); }
        }
        private async void FinishAudioDiagnostics(string reason) {
            if(!AudioDiagnostics.End(reason)) return;
            try {
                await Start(true);
                Text("FeedbackText",string.IsNullOrEmpty(errorMessage)?"音频诊断已结束，已恢复普通模式。请在 iPhone 上重新连接。":"音频诊断已结束。"+errorMessage);
            } catch(Exception error) { Storage.Log("结束音频诊断: "+error); }
            finally { AudioDiagnostics.WriteSummary(); }
        }
        private void SaveStatus(string status,bool client,bool video) {
            var state=new { Status=status, ReceiverName=settings.ReceiverName, ReceiverPid=receiver.Pid, ClientConnected=client, VideoAttached=video, VideoHandle=attachment.Handle.ToInt64(), Mode=detached?"window":"inline", RequestedResolution=settings.Resolution, RequestedFps=settings.Fps, ViewScale=settings.ViewScale, AudioDiagnostics=AudioDiagnostics.Enabled, AudioDiagnosticLog=AudioDiagnostics.LogPath, Error=errorMessage };
            string json=new JavaScriptSerializer().Serialize(state);
            if(json!=lastStatus) { Storage.AtomicWrite(System.IO.Path.Combine(Storage.Data,"status.json"),json); lastStatus=json; }
        }
        private void EnsureDetached(bool show=true) {
            if(detachedWindow==null) {
                var surface=new Grid { Background=Brushes.Black };
                var toolbar=new DockPanel { LastChildFill=false, Margin=new Thickness(0), Height=36 };
                var back=new Button { Content="返回工作台",Margin=new Thickness(8,4,6,4), Height=28 };
                detachedFullScreen=new Button { Content="全屏",Margin=new Thickness(0,4,6,4), Height=28 };
                var scaleButton=new Button { Margin=new Thickness(0,4,8,4), Height=28, MinWidth=142 };
                back.Style=Window.TryFindResource("FloatingButtonStyle") as Style;
                detachedFullScreen.Style=back.Style; scaleButton.Style=back.Style;
                detachedScaleSelector=new VideoScaleSelector(scaleButton,ApplyViewScale);
                detachedScaleSelector.SetValue(settings.ViewScale);
                back.Click+=delegate { SetDetached(false,true); };
                detachedFullScreen.Click+=delegate { ToggleFullScreen(); };
                toolbar.Children.Add(back); toolbar.Children.Add(detachedFullScreen); toolbar.Children.Add(scaleButton);
                var videoGrid=new Grid(); surface.Children.Add(videoGrid);
                detachedWaiting=new TextBlock { Text="等待 iPhone 画面\n在手机的屏幕镜像中选择「"+settings.ReceiverName+"」",TextAlignment=TextAlignment.Center,Foreground=Brushes.LightGray,FontSize=16,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center };
                detachedHost=new VideoHost { Visibility=Visibility.Collapsed }; detachedHost.OnLayout=attachment.Resize;
                videoGrid.Children.Add(detachedHost); videoGrid.Children.Add(detachedWaiting);
                detachedWindow=new Window { Title=settings.ReceiverName+" · 镜像画面",Width=1100,Height=620,MinWidth=420,MinHeight=280,Content=surface,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI"),FontSize=13,WindowStartupLocation=WindowStartupLocation.CenterScreen,Background=Brushes.Black };
                detachedChrome=new AutoHideChrome(detachedWindow,toolbar,delegate { return fullScreen; },attachment.Resize,delegate { return detachedScaleSelector.IsOpen; });
                detachedScaleSelector.Closed+=delegate { detachedChrome.Reveal(); };
                detachedWindow.Closing+=delegate(object sender,CancelEventArgs e) { if(!closing) { e.Cancel=true; SetDetached(false,true); } };
                detachedWindow.SizeChanged+=delegate { attachment.Resize(); };
                KeyEventHandler playbackKeys=delegate(object sender,KeyEventArgs e) { if(e.Key==Key.Escape && fullScreen) { ToggleFullScreen(); e.Handled=true; } else if(e.Key==Key.F11) { ToggleFullScreen(); e.Handled=true; } };
                detachedWindow.PreviewKeyDown+=playbackKeys; detachedChrome.OverlayWindow.PreviewKeyDown+=playbackKeys;
            }
            if(show && !detachedWindow.IsVisible) detachedWindow.Show();
            detachedWindow.UpdateLayout();
        }
        private void SetDetached(bool value,bool persist) {
            try {
                if(value) {
                    EnsureDetached();
                    if(attachment.Alive) { detachedHost.Visibility=Visibility.Visible; detachedWindow.UpdateLayout(); attachment.Attach(attachment.Handle,detachedHost); }
                    detached=true; detachedWindow.Activate();
                } else {
                    if(attachment.Alive) { inlineHost.Visibility=Visibility.Visible; Window.UpdateLayout(); attachment.Attach(attachment.Handle,inlineHost); }
                    detached=false;
                    if(detachedWindow!=null) { if(fullScreen) ToggleFullScreen(); detachedWindow.Hide(); }
                    Window.Activate();
                }
                settings.Detached=detached; if(persist) Storage.Save(settings);
                UpdateMode(); Poll();
            } catch(Exception error) { Storage.Log(error.ToString()); Text("FeedbackText","切换画面窗口失败："+error.Message); }
        }
        private void UpdateMode() { Find<Button>("DetachButton").Content=detached?"返回工作台":"独立窗口"; Text("CurrentModeText",detached?"画面使用独立窗口":"画面在工作台内显示"); }
        private void ToggleFullScreen() {
            EnsureDetached();
            if(!fullScreen) { beforeFullScreen=detachedWindow.WindowState; fullScreen=true; detachedWindow.WindowState=WindowState.Normal; detachedWindow.WindowStyle=WindowStyle.None; detachedWindow.ResizeMode=ResizeMode.NoResize; detachedWindow.WindowState=WindowState.Maximized; }
            else { fullScreen=false; detachedWindow.WindowState=WindowState.Normal; detachedWindow.ResizeMode=ResizeMode.NoResize; detachedWindow.WindowState=beforeFullScreen; }
            detachedChrome.RefreshFullScreen();
            detachedFullScreen.Content=fullScreen?"退出全屏":"全屏"; attachment.Resize();
        }
        private async void OnClosing(object sender,CancelEventArgs args) {
            if(allowClose) return;
            args.Cancel=true;
            if(closing) return;
            closing=true; timer.Stop();
            bool diagnosticStopped=AudioDiagnostics.End("workbench-closed");
            if(detachedChrome!=null) detachedChrome.Dispose();
            // Restore the foreign window before either owned HwndHost is destroyed.
            attachment.Restore();
            Text("EngineText","正在关闭接收端…");
            while(busy) await Task.Delay(100);
            try { await receiver.Stop(false,false); } catch(Exception error) { Storage.Log(error.ToString()); }
            if(diagnosticStopped) AudioDiagnostics.WriteSummary();
            if(detachedWindow!=null) detachedWindow.Close();
            Storage.AtomicWrite(System.IO.Path.Combine(Storage.Data,"status.json"),"{\"Status\":\"已关闭\",\"ClientConnected\":false,\"VideoAttached\":false}");
            // Stop can finish synchronously when no receiver is running. Wait
            // until this Closing event has unwound before requesting it again.
            Window.Dispatcher.BeginInvoke(new Action(delegate { allowClose=true; Window.Close(); }));
        }
    }
}
