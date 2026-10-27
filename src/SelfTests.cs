using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace IPhonePC {
    internal static class SelfTests {
        private const string HelperPipeVariable = "IPHONE_PC_SELF_TEST_PIPE";
        private static readonly string[] Resolutions = { "1280x720", "1920x1080", "2560x1440", "3840x2160" };
        private static readonly int[] FrameRates = { 24, 30, 60 };

        [StructLayout(LayoutKind.Sequential)] private struct ClientPoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct TestMonitorInfo {
            public int Size;
            public Native.RECT Monitor, Work;
            public int Flags;
        }
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr handle, ref ClientPoint point);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref TestMonitorInfo info);

        private sealed class CheckResult {
            public string name;
            public bool passed;
            public string message;
            public long milliseconds;
        }

        private sealed class Report {
            public string suite = "iPhone-PC self-test";
            public string startedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            public string completedUtc;
            public string outputPath;
            public bool passed;
            public int exitCode;
            public int checkCount;
            public int passedCount;
            public List<string> failures = new List<string>();
            public List<CheckResult> checks = new List<CheckResult>();
            public Dictionary<string, object> evidence = new Dictionary<string, object>();
        }

        public static int Run(string outputPath) {
            var report = new Report();
            Application application = null;
            string reportPath = null;
            try {
                reportPath = ResolveReportPath(outputPath);
                report.outputPath = reportPath;
                report.evidence["processId"] = Process.GetCurrentProcess().Id;
                report.evidence["is64BitProcess"] = Environment.Is64BitProcess;
                report.evidence["apartment"] = Thread.CurrentThread.GetApartmentState().ToString();
                TestSettings(report);
                TestConnectionSettings(report);
                TestChromePolicy(report);
                Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "WPF self-tests must run on the STA entry thread.");
                Require(Application.Current == null, "Run self-tests before creating the application instance.");
                application = new Application();
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                TestXaml(report);
                TestVideoAttachment(report);
            } catch (Exception error) {
                AddFailure(report, "selfTest.infrastructure", error);
            } finally {
                if (application != null) {
                    try { application.Shutdown(); }
                    catch (Exception error) { AddFailure(report, "selfTest.applicationShutdown", error); }
                }
            }
            report.completedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            report.checkCount = report.checks.Count;
            foreach (CheckResult check in report.checks) if (check.passed) report.passedCount++;
            report.passed = report.failures.Count == 0;
            report.exitCode = report.passed ? 0 : 1;
            try {
                if (reportPath == null) throw new InvalidOperationException("No permitted report path was supplied.");
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
                File.WriteAllText(reportPath, new JavaScriptSerializer().Serialize(report), new UTF8Encoding(false));
            } catch (Exception error) {
                try { Console.Error.WriteLine("Self-test report could not be written: " + error.Message); } catch { }
                return 1;
            }
            return report.exitCode;
        }

        private static string ResolveReportPath(string outputPath) {
            if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("A JSON report path is required.");
            string applicationDirectory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
            string workspace = applicationDirectory;
            for (DirectoryInfo directory = new DirectoryInfo(applicationDirectory); directory != null; directory = directory.Parent) {
                if (File.Exists(Path.Combine(directory.FullName, "src", "NativeVideo.cs")) &&
                    File.Exists(Path.Combine(directory.FullName, "src", "Program.cs"))) {
                    workspace = directory.FullName;
                    break;
                }
            }
            string path = Path.GetFullPath(outputPath);
            string prefix = workspace.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Require(path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase), "The report must stay inside the workspace/application directory.");
            return path;
        }

        public static int RenderPlaybackControls(string outputPath) {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var workbench = Program.LoadWindow();
            var controller = new Controller(workbench);
            var type = typeof(Controller);
            BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            Window playback = null;
            AutoHideChrome chrome = null;
            try {
                type.GetMethod("EnsureDetached",fields).Invoke(controller,new object[] {false});
                playback = (Window)type.GetField("detachedWindow",fields).GetValue(controller);
                chrome = (AutoHideChrome)type.GetField("detachedChrome",fields).GetValue(controller);
                playback.WindowStartupLocation=WindowStartupLocation.Manual;
                playback.Left=-24000; playback.Top=-24000; playback.ShowActivated=false; playback.ShowInTaskbar=false;
                playback.Show(); chrome.Reveal(); SettleLayout(playback);
                var content = (FrameworkElement)chrome.OverlayWindow.Content;
                chrome.OverlayWindow.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using(var stream = File.Create(ResolveReportPath(outputPath))) encoder.Save(stream);
                return 0;
            } finally {
                ((DispatcherTimer)type.GetField("timer",fields).GetValue(controller)).Stop();
                type.GetField("closing",fields).SetValue(controller,true);
                type.GetField("allowClose",fields).SetValue(controller,true);
                if(chrome!=null) chrome.Dispose();
                if(playback!=null) playback.Close();
                workbench.Close(); application.Shutdown();
            }
        }

        private static void TestSettings(Report report) {
            Check(report, "settings.viewScale", delegate {
                Require(new Settings().ViewScale == 100, "View scale must default to 100%.");
                foreach (int percent in new [] {50,75,100}) {
                    var settings = new Settings { ViewScale = percent }; settings.Validate();
                    Require(settings.ViewScale == percent, "An allowed view scale must be retained.");
                    Require(new JavaScriptSerializer().Deserialize<Settings>(new JavaScriptSerializer().Serialize(settings)).ViewScale == percent, "Scale must survive saving settings.");
                    VerifyArguments(settings.Arguments(),"3840x2160",60);
                }
                foreach (int percent in new [] {-1,0,1,49,51,76,120}) {
                    var settings = new Settings { ViewScale = percent }; settings.Validate();
                    Require(settings.ViewScale == 100, "An invalid view scale must fall back to 100%.");
                }
            });
            Check(report, "settings.defaults.4k60", delegate {
                var settings = new Settings();
                Require(settings.Resolution == "3840x2160" && settings.Fps == 60, "New settings must default to 3840x2160 at 60 fps.");
                VerifyArguments(settings.Arguments(), "3840x2160", 60);
            });
            foreach (string resolutionValue in Resolutions) {
                foreach (int fpsValue in FrameRates) {
                    string resolution = resolutionValue;
                    int fps = fpsValue;
                    Check(report, "settings.allowed." + resolution + "." + fps, delegate {
                        var settings = new Settings { Resolution = resolution, Fps = fps };
                        settings.Validate();
                        Require(settings.Resolution == resolution && settings.Fps == fps, "Validation changed an allowed resolution/frame-rate pair.");
                        VerifyArguments(settings.Arguments(), resolution, fps);
                    });
                }
            }
            string[] invalidResolutions = { null, "", "640x480", "1920x1200", "7680x4320", "720p", "4K", "1280X720", " 1280x720", "3840x2160@60", "1280x720 -fps 120" };
            for (int index = 0; index < invalidResolutions.Length; index++) {
                string value = invalidResolutions[index];
                Check(report, "settings.rejectResolution." + index, delegate {
                    var validated = new Settings { Resolution = value, Fps = 30 };
                    validated.Validate();
                    Require(validated.Resolution == "3840x2160" && validated.Fps == 30, "An unsupported resolution must fall back to 4K while retaining an allowed frame rate.");
                    VerifyArguments(new Settings { Resolution = value, Fps = 30 }.Arguments(), "3840x2160", 30);
                });
            }
            int[] invalidFrameRates = { int.MinValue, -1, 0, 1, 23, 25, 29, 31, 59, 61, 120, int.MaxValue };
            foreach (int invalidValue in invalidFrameRates) {
                int value = invalidValue;
                Check(report, "settings.rejectFps." + value, delegate {
                    var validated = new Settings { Resolution = "1920x1080", Fps = value };
                    validated.Validate();
                    Require(validated.Resolution == "1920x1080" && validated.Fps == 60, "An unsupported frame rate must fall back to 60 while retaining an allowed resolution.");
                    VerifyArguments(new Settings { Resolution = "1920x1080", Fps = value }.Arguments(), "1920x1080", 60);
                });
            }
        }

        private static void TestConnectionSettings(Report report) {
            Check(report, "connection.defaults", delegate {
                var settings = new Settings();
                Require(settings.ReceiverName == "iPhone-PC" && settings.Password == "6817", "Connection settings must retain the default receiver name and password.");
                VerifyConnectionArguments(settings, "iPhone-PC", "6817");
            });
            string[] allowedNames = {
                "  客厅 Pat's PC  ", " " + new string('A', 50) + " ", new string('客', 16) + "ab",
                "iPhone " + char.ConvertFromUtf32(0x1F4F1), @"C:\Room & TV", "Living -pw 1234 -fps 30 room"
            };
            string[] allowedPasswords = { "0007", new string('A', 28) + "09az", "a1B2", "Ab09", "Aa0123", "00Az" };
            for (int index = 0; index < allowedNames.Length; index++) {
                string name = allowedNames[index], password = allowedPasswords[index];
                Check(report, "connection.allowed." + index, delegate {
                    string normalized, error;
                    Require(Settings.TryValidateConnection(name, password, out normalized, out error), "An allowed connection name/password pair must validate.");
                    Require(normalized == name.Trim(), "Connection validation must trim only the name's outer whitespace.");
                    var settings = new Settings { ReceiverName = name, Password = password, Resolution = "1920x1080", Fps = 24 };
                    settings.Validate();
                    Require(settings.ReceiverName == normalized && settings.Password == password, "Validation must normalize the name while preserving password text, case and leading zeroes.");
                    VerifyConnectionArguments(settings, normalized, password);
                });
            }
            string[] rejectedNames = {
                null, "", "   ", new string('A', 51), new string('客', 17), "Living \"Room\"", "PC%1",
                "PC\t", "PC\n", "PC\r", "PC\0", "PC\u007f", "PC\u0085", "PC\u2028", "PC\u2029",
                "-Receiver", "  -Receiver", "PC\ud800", "PC\udc00"
            };
            for (int index = 0; index < rejectedNames.Length; index++) {
                string name = rejectedNames[index];
                Check(report, "connection.rejectName." + index, delegate {
                    RequireConnectionRejected(new Settings { ReceiverName = name, Password = "0042" });
                });
            }
            string[] rejectedPasswords = {
                null, "", "123", new string('A', 33), " 1234", "1234 ", "12 34", "12\t34", "1234\n",
                "12-34", "12_34", "a'b4", "Ab%4", "\uff11\uff12\uff13\uff14", "密码12"
            };
            for (int index = 0; index < rejectedPasswords.Length; index++) {
                string password = rejectedPasswords[index];
                Check(report, "connection.rejectPassword." + index, delegate {
                    RequireConnectionRejected(new Settings { ReceiverName = "Living Room", Password = password });
                });
            }
            Check(report, "connection.legacyJson", delegate {
                var settings = new JavaScriptSerializer().Deserialize<Settings>("{\"Resolution\":\"1920x1080\",\"Fps\":30,\"Password\":\"0042\"}");
                Require(settings != null, "Legacy settings JSON must deserialize.");
                settings.Validate();
                Require(settings.ReceiverName == "iPhone-PC" && settings.Password == "0042" && settings.Resolution == "1920x1080" && settings.Fps == 30,
                    "Legacy JSON without ReceiverName must retain the default name and existing password/quality settings.");
                VerifyConnectionArguments(settings, "iPhone-PC", "0042");
            });
            Check(report, "connection.jsonRoundTrip", delegate {
                var settings = new Settings { ReceiverName = "  客厅 Pat's PC  ", Password = "00Ab19", Resolution = "2560x1440", Fps = 30 };
                settings.Validate();
                var serializer = new JavaScriptSerializer();
                var restored = serializer.Deserialize<Settings>(serializer.Serialize(settings));
                Require(restored != null, "Connection settings must survive JSON serialization.");
                restored.Validate();
                Require(restored.ReceiverName == "客厅 Pat's PC" && restored.Password == "00Ab19" && restored.Resolution == "2560x1440" && restored.Fps == 30,
                    "JSON must preserve the edited connection name/password and quality settings.");
                VerifyConnectionArguments(restored, "客厅 Pat's PC", "00Ab19");
            });
            Check(report, "connection.importQuotedNameWithOptions", delegate {
                const string name = "Living -pw 1234 -fps 30 room";
                var settings = Storage.ParseArguments("-n \"" + name + "\" -s 1920x1080@60 -fps 24 -pw 00Az");
                settings.Validate();
                Require(settings.ReceiverName == name && settings.Password == "00Az" && settings.Resolution == "1920x1080" && settings.Fps == 24,
                    "Import must read real options after a quoted name while preserving option-like text inside that name.");
                VerifyConnectionArguments(settings, name, "00Az");
            });
            Check(report, "connection.importNameDoesNotSupplyOptions", delegate {
                const string name = "Living -pw 1234 -fps 30 -s 1280x720 room";
                var settings = Storage.ParseArguments("-n \"" + name + "\"");
                settings.Validate();
                Require(settings.ReceiverName == name && settings.Password == "6817" && settings.Resolution == "3840x2160" && settings.Fps == 60,
                    "Options appearing only inside the name must not change imported password or quality defaults.");
                VerifyConnectionArguments(settings, name, "6817");
            });
        }

        private static void RequireConnectionRejected(Settings settings) {
            string normalized, error;
            string password = settings.Password;
            Require(!Settings.TryValidateConnection(settings.ReceiverName, settings.Password, out normalized, out error) && !string.IsNullOrWhiteSpace(error),
                "Invalid connection input must be rejected with a validation message.");
            bool rejectedValidation = false, rejectedArguments = false;
            try { settings.Validate(); } catch (ArgumentException) { rejectedValidation = true; }
            try { settings.Arguments(); } catch (ArgumentException) { rejectedArguments = true; }
            Require(rejectedValidation && rejectedArguments, "Validate and Arguments must reject invalid connection input instead of silently falling back.");
            Require(settings.Password == password, "Rejecting connection settings must not replace the entered password.");
        }

        private static void VerifyConnectionArguments(Settings settings, string name, string password) {
            string arguments = settings.Arguments();
            string[] tokens = Storage.SplitArguments(arguments);
            Require(SingleArgumentValue(tokens, "-n") == name, "The receiver name must reach the engine as one Qt command-line argument.");
            Require(SingleArgumentValue(tokens, "-pw") == password, "Engine arguments must preserve the exact password text.");
            VerifyArguments(arguments, settings.Resolution, settings.Fps);
        }

        private static void TestChromePolicy(Report report) {
            Check(report, "chrome.initialAndReset", delegate {
                var policy = new ChromeVisibilityPolicy();
                Require(policy.Visible, "Chrome must initially be visible.");
                policy.Reset(100);
                Require(policy.Visible && policy.Update(2099, true, false, false, false), "Chrome must stay visible before the 2000 ms idle timeout.");
                Require(!policy.Update(2100, true, false, false, false) && !policy.Visible, "Chrome must hide at the 2000 ms idle timeout.");
                policy.Reset(5000);
                Require(policy.Visible, "Reset must reveal previously hidden chrome.");
                Require(policy.Update(6999, true, false, false, false), "Reset must restart the idle timeout.");
                Require(!policy.Update(7000, true, false, false, false), "Reset chrome must hide after 2000 ms without activity.");
            });
            Check(report, "chrome.movementRefreshesTimeout", delegate {
                var policy = new ChromeVisibilityPolicy();
                policy.Reset(0);
                Require(policy.Update(1500, true, true, false, false), "Movement inside the window must keep chrome visible.");
                Require(policy.Update(2000, true, false, false, false), "The original timeout must not hide chrome after movement.");
                Require(policy.Update(3499, true, false, false, false), "Inside movement must start a new 2000 ms idle period.");
                Require(!policy.Update(3500, true, false, false, false), "Stationary chrome must hide 2000 ms after the last inside movement.");
                Require(policy.Update(4000, true, true, false, false) && policy.Visible, "Inside movement must reveal hidden chrome.");
                Require(policy.Update(5999, true, false, false, false), "Revealed chrome must retain the full idle period.");
                Require(!policy.Update(6000, true, false, false, false), "Revealed chrome must hide again when movement stops.");
            });
            Check(report, "chrome.leavingAndReturning", delegate {
                var policy = new ChromeVisibilityPolicy();
                policy.Reset(0);
                Require(!policy.Update(50, false, false, false, false) && !policy.Visible, "Leaving the window must hide chrome immediately.");
                Require(!policy.Update(100, false, true, false, false), "Movement outside the window must not reveal chrome.");
                Require(!policy.Update(300, false, true, true, false), "Holding the mouse outside the window must not reveal chrome.");
                Require(policy.Update(400, true, true, false, false) && policy.Visible, "Returning with inside movement must reveal chrome.");
                Require(policy.Update(2399, true, false, false, false), "Returning movement must refresh the idle timeout.");
                Require(!policy.Update(2400, true, false, false, false), "Chrome must hide after the return movement becomes idle.");
                Require(!policy.Update(5000, false, true, false, false), "Later outside movement must keep idle chrome hidden.");
            });
            Check(report, "chrome.mouseHeldAndReleased", delegate {
                var policy = new ChromeVisibilityPolicy();
                policy.Reset(0);
                Require(!policy.Update(2000, true, false, false, false), "The mouse-hold fixture must begin with hidden chrome.");
                Require(policy.Update(3000, true, false, true, false), "Holding the mouse inside must reveal chrome without movement.");
                Require(policy.Update(6000, true, false, true, false), "A stationary mouse hold must keep chrome visible beyond the idle timeout.");
                Require(policy.Update(6000, true, false, false, false), "Releasing the mouse must leave chrome visible initially.");
                Require(policy.Update(7999, true, false, false, false), "A mouse hold must refresh the idle period used after release.");
                Require(!policy.Update(8000, true, false, false, false) && !policy.Visible, "Chrome must hide after 2000 ms idle following mouse release.");
            });
            Check(report, "chrome.mouseHeldAcrossBoundary", delegate {
                var policy = new ChromeVisibilityPolicy();
                policy.Reset(0);
                Require(policy.Update(100, true, false, true, false), "A mouse hold beginning inside must keep chrome visible.");
                Require(policy.Update(3000, false, true, true, false), "Dragging a held mouse outside must keep chrome visible.");
                Require(policy.Update(6000, false, false, true, false), "An inside-origin mouse hold must stay visible outside beyond the idle timeout.");
                Require(!policy.Update(6001, false, false, false, false), "Releasing the held mouse outside must hide chrome immediately.");
                Require(!policy.Update(7000, false, false, true, false), "A new mouse hold beginning outside must not reveal hidden chrome.");
                Require(policy.Update(8000, true, false, true, false), "Returning with the mouse held must reveal chrome inside.");
                policy.Reset(8100);
                Require(policy.Visible && !policy.Update(8101, false, false, true, false), "Reset must clear prior inside-origin mouse-hold protection.");
            });
            Check(report, "chrome.windowManipulation", delegate {
                var policy = new ChromeVisibilityPolicy();
                policy.Reset(0);
                Require(!policy.Update(2000, true, false, false, false), "The manipulation fixture must begin with hidden chrome.");
                Require(policy.Update(3000, false, false, false, true), "Window manipulation must reveal chrome even when the pointer is outside.");
                Require(policy.Update(7000, false, false, false, true), "Ongoing window manipulation must keep chrome visible beyond the idle timeout.");
                Require(!policy.Update(7001, false, false, false, false), "Ending manipulation outside must hide chrome immediately.");
                Require(policy.Update(10000, true, false, false, true), "Manipulation inside must reveal chrome without mouse movement.");
                Require(policy.Update(10000, true, false, false, false), "Ending manipulation inside must leave chrome visible initially.");
                Require(policy.Update(11999, true, false, false, false), "Window manipulation must refresh the idle timeout.");
                Require(!policy.Update(12000, true, false, false, false), "Chrome must hide after 2000 ms idle following manipulation.");
            });
        }

        private static void VerifyArguments(string arguments, string resolution, int fps) {
            string[] tokens = Storage.SplitArguments(arguments);
            string size = SingleArgumentValue(tokens, "-s"), rate = SingleArgumentValue(tokens, "-fps");
            string[] mode = size.Split('@');
            Require(mode[0] == resolution && Array.IndexOf(Resolutions, mode[0]) >= 0, "Arguments contain an unexpected resolution: " + arguments);
            Require(mode.Length == 1 || (mode.Length == 2 && mode[1] == "60"), "Arguments contain an unsupported advertised refresh rate: " + arguments);
            Require(rate == fps.ToString(CultureInfo.InvariantCulture) && Array.IndexOf(FrameRates, fps) >= 0, "Arguments contain an unexpected frame-rate limit: " + arguments);
        }

        private static string SingleArgumentValue(IList<string> arguments, string option) {
            string value = null;
            int count = 0;
            for (int index = 0; index < arguments.Count; index++) {
                if (arguments[index] != option) continue;
                Require(index + 1 < arguments.Count, "The engine option " + option + " must have a value.");
                value = arguments[index + 1];
                count++;
            }
            Require(count == 1, "Engine arguments must contain exactly one " + option + " option.");
            return value;
        }

        private static void TestXaml(Report report) {
            Window window = null;
            try {
                window = Program.LoadWindow();
                Require(window != null, "The embedded MainWindow.xaml did not produce a Window.");
                var required = new Dictionary<string, Type> {
                    { "VideoSlot", typeof(Grid) }, { "WaitPanel", typeof(FrameworkElement) },
                    { "ResolutionCombo", typeof(ComboBox) },
                    { "Fps24", typeof(RadioButton) }, { "Fps30", typeof(RadioButton) }, { "Fps60", typeof(RadioButton) },
                    { "ApplyButton", typeof(Button) }, { "RestartButton", typeof(Button) },
                    { "DetachButton", typeof(Button) }, { "FullscreenButton", typeof(Button) }, { "ScaleButton", typeof(Button) },
                    { "StatusDot", typeof(System.Windows.Shapes.Ellipse) },
                    { "ReceiverNameInput", typeof(TextBox) }, { "PasswordInput", typeof(TextBox) },
                    { "ConnectionFeedbackText", typeof(TextBlock) },
                    { "StatusText", typeof(TextBlock) }, { "EngineText", typeof(TextBlock) },
                    { "FeedbackText", typeof(TextBlock) }, { "ActualText", typeof(TextBlock) }, { "CurrentModeText", typeof(TextBlock) }
                };
                foreach (KeyValuePair<string, Type> entry in required) {
                    string name = entry.Key;
                    Type type = entry.Value;
                    Check(report, "xaml.control." + name, delegate {
                        object control = window.FindName(name);
                        Require(control != null && type.IsInstanceOfType(control), name + " must be a named " + type.Name + ".");
                    });
                }
                Check(report, "xaml.editableConnectionSettings", delegate {
                    var name = window.FindName("ReceiverNameInput") as TextBox;
                    var password = window.FindName("PasswordInput") as TextBox;
                    Require(name != null && password != null, "Connection settings must expose text inputs for the name and password.");
                    Require(name.Text == "iPhone-PC" && password.Text == "6817", "Connection inputs must retain their default name and password.");
                    Require(name.IsEnabled && password.IsEnabled && !name.IsReadOnly && !password.IsReadOnly, "Both connection inputs must be enabled and editable.");
                    Require(password.MaxLength == 32, "The password input must limit entry to 32 characters.");
                    string originalName = name.Text, originalPassword = password.Text;
                    try {
                        name.Text = "Living Room PC";
                        password.Text = "0042";
                        Require(name.Text == "Living Room PC" && password.Text == "0042", "Editing connection inputs must retain the entered text and leading password zeroes.");
                    } finally {
                        name.Text = originalName;
                        password.Text = originalPassword;
                    }
                });
                Check(report, "xaml.scaleMenu", delegate {
                    var button = (Button)window.FindName("ScaleButton");
                    int selected = 0;
                    var selector = new VideoScaleSelector(button,delegate(int value) { selected=value; });
                    Require(button.ContextMenu.Items.Count == 3, "Scale menu must have exactly three options.");
                    foreach (int percent in new [] {50,75,100}) {
                        selector.SetValue(percent);
                        Require(button.Content.ToString().Contains(percent+"%"), "Button must display the active scale.");
                        int checkCount = 0;
                        foreach (MenuItem item in button.ContextMenu.Items) {
                            if (item.IsChecked) checkCount++;
                            if (item.Header.ToString() == percent+"%") item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                        }
                        Require(checkCount == 1 && selected == percent, "Selecting a scale must invoke the change immediately and select one option.");
                    }
                });
                Check(report, "xaml.resolutionOptions", delegate {
                    var combo = window.FindName("ResolutionCombo") as ComboBox;
                    Require(combo != null && combo.Items.Count == Resolutions.Length, "The resolution selector must contain exactly four items.");
                    var found = new HashSet<string>(StringComparer.Ordinal);
                    foreach (object value in combo.Items) {
                        var item = value as ComboBoxItem;
                        Require(item != null && item.Tag is string, "Resolution items must expose string Tags.");
                        string tag = (string)item.Tag;
                        Require(Array.IndexOf(Resolutions, tag) >= 0 && found.Add(tag), "Unexpected or repeated resolution Tag: " + tag);
                    }
                    var selected = combo.SelectedItem as ComboBoxItem;
                    Require(selected != null && (string)selected.Tag == "3840x2160", "The XAML default resolution must be 4K.");
                });
                Check(report, "xaml.fpsDefaultAndSelection", delegate {
                    var fps24 = window.FindName("Fps24") as RadioButton;
                    var fps30 = window.FindName("Fps30") as RadioButton;
                    var fps60 = window.FindName("Fps60") as RadioButton;
                    Require(fps24 != null && fps30 != null && fps60 != null, "All three frame-rate controls must exist.");
                    Require(fps60.IsChecked == true && fps24.IsChecked != true && fps30.IsChecked != true, "The XAML must select only 60 fps by default.");
                    try {
                        fps24.IsChecked = true;
                        Require(fps24.IsChecked == true && fps30.IsChecked != true && fps60.IsChecked != true, "Selecting 24 fps must clear the other rates.");
                        fps30.IsChecked = true;
                        Require(fps30.IsChecked == true && fps24.IsChecked != true && fps60.IsChecked != true, "Selecting 30 fps must clear the other rates.");
                        fps60.IsChecked = true;
                        Require(fps60.IsChecked == true && fps24.IsChecked != true && fps30.IsChecked != true, "Selecting 60 fps must clear the other rates.");
                    } finally { fps60.IsChecked = true; }
                });
            } catch (Exception error) { AddFailure(report, "xaml.load", error); }
            finally {
                if (window != null) {
                    try { window.Close(); }
                    catch (Exception error) { AddFailure(report, "xaml.cleanup", error); }
                }
            }
        }

        private static void TestVideoAttachment(Report report) {
            Window window = null;
            VideoHost first = null, second = null;
            VideoHelper helper = null;
            var attachment = new VideoAttachment();
            try {
                helper = VideoHelper.Start();
                IntPtr source = helper.Handle;
                WindowSnapshot original = Snapshot(source);
                report.evidence["videoHelperProcessId"] = helper.ProcessId;
                report.evidence["videoOriginal"] = original;
                Check(report, "video.independentSource", delegate {
                    uint processId;
                    Native.GetWindowThreadProcessId(source, out processId);
                    Require(processId == helper.ProcessId && processId != Process.GetCurrentProcess().Id, "The test source must belong only to the independently started helper.");
                    Require(original.parent == 0, "The test fixture must begin as an independent top-level window.");
                });

                var surface = new Grid();
                var toolbarRow = new RowDefinition { Height = new GridLength(0) };
                surface.RowDefinitions.Add(toolbarRow);
                surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                surface.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
                surface.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var toolbar = new Border { Height = 32 };
                Grid.SetColumnSpan(toolbar, 2);
                // Toolbar is owned by a separate overlay, outside the video layout.
                var firstSlot = new Grid { Margin = new Thickness(12) };
                var secondSlot = new Grid { Margin = new Thickness(12) };
                Grid.SetRow(firstSlot, 1); Grid.SetRow(secondSlot, 1);
                Grid.SetColumn(secondSlot, 1);
                surface.Children.Add(firstSlot); surface.Children.Add(secondSlot);
                first = new VideoHost(); second = new VideoHost();
                first.OnLayout = attachment.Resize; second.OnLayout = attachment.Resize;
                firstSlot.Children.Add(first); secondSlot.Children.Add(second);
                window = new Window {
                    Title = "iPhone-PC self-test hosts", Width = 900, Height = 520,
                    Left = -30000, Top = -30000, WindowStartupLocation = WindowStartupLocation.Manual,
                    WindowStyle = WindowStyle.SingleBorderWindow, ResizeMode = ResizeMode.CanResize,
                    ShowActivated = false, ShowInTaskbar = false, Content = surface
                };
                window.Show();
                SettleLayout(window);
                Require(first.Container != IntPtr.Zero && second.Container != IntPtr.Zero, "Both native host containers must be created.");
                Check(report, "video.hostsCreated", delegate {
                    Require(Native.IsWindow(first.Container) && Native.IsWindow(second.Container) && first.Container != second.Container, "The two hosts must own distinct live containers.");
                });
                CheckHostBackground(report, "video.first.emptyBackground", first);
                CheckHostBackground(report, "video.second.emptyBackground", second);

                attachment.Attach(source, first);
                SettleLayout(window);
                CheckAttached(report, "video.first", attachment, source, first);
                TestChromeLayoutTransitions(report, "video.first.chrome", window, toolbar, toolbarRow, attachment, source, first, second, helper);
                Native.RECT beforeResize = ClientRect(first.Container);
                window.Width = 1037; window.Height = 613;
                surface.ColumnDefinitions[0].Width = new GridLength(413);
                SettleLayout(window);
                attachment.Resize();
                SettleLayout(window);
                Check(report, "video.resizeChangesContainer", delegate {
                    Native.RECT after = ClientRect(first.Container);
                    Require(Width(after) != Width(beforeResize) && Height(after) != Height(beforeResize), "The resize fixture must change both container dimensions.");
                });
                CheckAttached(report, "video.resized", attachment, source, first);

                attachment.Attach(source, second);
                SettleLayout(window);
                CheckAttached(report, "video.second", attachment, source, second);
                TestChromeLayoutTransitions(report, "video.second.chrome", window, toolbar, toolbarRow, attachment, source, second, first, helper);
                foreach(int percent in new [] {50,75,100}) {
                    attachment.ViewScale=percent;
                    attachment.Attach(source,first); SettleLayout(window);
                    CheckAttached(report,"video.transfer.scale"+percent+".first",attachment,source,first);
                    attachment.Attach(source,second); SettleLayout(window);
                    CheckAttached(report,"video.transfer.scale"+percent+".second",attachment,source,second);
                }
                attachment.Attach(source, first);
                SettleLayout(window);
                CheckAttached(report, "video.returnedToFirst", attachment, source, first);
                helper.Ping();

                attachment.Restore();
                SettleLayout(window);
                WindowSnapshot restored = Snapshot(source);
                report.evidence["videoRestored"] = restored;
                Check(report, "video.restoreClearsAttachment", delegate {
                    Require(attachment.Handle == IntPtr.Zero && !attachment.Alive, "Restore must clear attachment ownership.");
                    Require(Native.IsWindow(source), "Restore must leave the foreign HWND alive.");
                });
                Check(report, "video.restoreParent", delegate { Require(restored.parent == original.parent, "Restore changed the original parent."); });
                Check(report, "video.restoreStyle", delegate { Require(restored.style == original.style, "Restore changed the original window style."); });
                Check(report, "video.restoreExStyle", delegate { Require(restored.exStyle == original.exStyle, "Restore changed the original extended style."); });
                Check(report, "video.restoreRect", delegate {
                    Require(restored.left == original.left && restored.top == original.top && restored.width == original.width && restored.height == original.height,
                        "Restore changed the original window rectangle. Expected " + RectText(original) + "; actual " + RectText(restored) + ".");
                });
                attachment.Restore();
                Check(report, "video.restoreIsIdempotent", delegate {
                    WindowSnapshot again = Snapshot(source);
                    Require(again.parent == original.parent && again.style == original.style && again.exStyle == original.exStyle &&
                        again.left == original.left && again.top == original.top && again.width == original.width && again.height == original.height,
                        "Calling Restore twice must keep the foreign window unchanged.");
                });

                IntPtr firstContainer = first.Container, secondContainer = second.Container;
                first.OnLayout = null; second.OnLayout = null;
                first.Dispose(); second.Dispose();
                window.Close(); window = null;
                PumpDispatcher();
                Check(report, "video.hostDestructionPreservesSource", delegate {
                    Require(!Native.IsWindow(firstContainer) && !Native.IsWindow(secondContainer), "Disposing the owned hosts must destroy their containers.");
                    Require(Native.IsWindow(source), "Destroying the host containers must not destroy the restored foreign window.");
                    helper.Ping();
                });
            } catch (Exception error) { AddFailure(report, "video.fixture", error); }
            finally {
                try { attachment.Restore(); }
                catch (Exception error) { AddFailure(report, "video.cleanup.restore", error); }
                if (first != null) {
                    try { first.OnLayout = null; first.Dispose(); }
                    catch (Exception error) { AddFailure(report, "video.cleanup.firstHost", error); }
                }
                if (second != null) {
                    try { second.OnLayout = null; second.Dispose(); }
                    catch (Exception error) { AddFailure(report, "video.cleanup.secondHost", error); }
                }
                if (window != null) {
                    try { window.Close(); }
                    catch (Exception error) { AddFailure(report, "video.cleanup.window", error); }
                }
                if (helper != null) {
                    Check(report, "video.helperCleanExit", delegate { helper.Close(); });
                    helper.Dispose();
                }
            }
        }

        private static void TestChromeLayoutTransitions(Report report, string name, Window window, FrameworkElement toolbar, RowDefinition toolbarRow,
            VideoAttachment attachment, IntPtr source, VideoHost host, VideoHost other, VideoHelper helper) {
            bool fullScreen = false;
            WindowState originalState = window.WindowState;
            ResizeMode originalResizeMode = window.ResizeMode;
            IntPtr windowHandle = new WindowInteropHelper(window).Handle;
            Native.RECT originalBounds = WindowRect(windowHandle);
            WindowSnapshot originalWindow = Snapshot(windowHandle);
            IntPtr hostHandle = host.Container, otherHandle = other.Container;
            // Construct after Show so the pointer-polling timer never starts. Drive only
            // the visibility policy and its layout application, without real mouse input.
            using (var chrome = new AutoHideChrome(window, toolbar, delegate { return fullScreen; }, attachment.Resize)) {
                var policyField = typeof(AutoHideChrome).GetField("policy", BindingFlags.Instance | BindingFlags.NonPublic);
                var applyVisibility = typeof(AutoHideChrome).GetMethod("ApplyVisibility", BindingFlags.Instance | BindingFlags.NonPublic);
                Require(policyField != null && applyVisibility != null, "The deterministic chrome-layout fixture could not access visibility application.");
                var policy = policyField.GetValue(chrome) as ChromeVisibilityPolicy;
                Require(policy != null, "The chrome-layout fixture must use the same policy as the controller.");
                try {
                    for (int combination = 0; combination < 9; combination++) {
                        int scenario = combination / 3;
                        int percent = new [] {50,75,100}[combination % 3];
                        attachment.ViewScale = percent;
                        fullScreen = scenario == 2;
                        string scenarioName = name + (scenario == 0 ? ".normal" : scenario == 1 ? ".maximized" : ".fullScreen") + ".scale" + percent;
                        if (fullScreen) {
                            window.WindowState = WindowState.Normal;
                            window.ResizeMode = ResizeMode.NoResize;
                        } else window.ResizeMode = originalResizeMode;
                        window.WindowState = scenario == 0 ? WindowState.Normal : WindowState.Maximized;
                        chrome.RefreshFullScreen();
                        SettleLayout(window);
                        WindowStyle shownStyle = WindowStyle.None;
                        Native.RECT outerBefore = WindowRect(windowHandle);
                        Native.RECT expectedHiddenOuter = outerBefore;
                        Native.RECT clientBefore = ClientScreenRect(windowHandle);
                        Native.RECT hostBefore = ClientRect(hostHandle), otherBefore = ClientRect(otherHandle);
                        WindowSnapshot shownWindow = Snapshot(windowHandle);
                        Native.RECT sourceBefore = WindowRect(source);
                        Check(report, scenarioName + ".shown", delegate {
                            Require(window.WindowStyle == shownStyle && toolbar.Visibility == Visibility.Visible && toolbarRow.ActualHeight == 0,
                                "The overlay must reserve no video layout space in any window mode.");
                            Require(chrome.OverlayWindow.IsVisible && chrome.OverlayWindow.Owner == window && new WindowInteropHelper(chrome.OverlayWindow).Handle != windowHandle,
                                "The controls must use a visible separate HWND owned by the video window.");
                            Native.RECT overlayBounds = WindowRect(new WindowInteropHelper(chrome.OverlayWindow).Handle);
                            Require(overlayBounds.Left == outerBefore.Left && overlayBounds.Top == outerBefore.Top && Width(overlayBounds) == Width(outerBefore), "Overlay must follow the owner's top edge and width.");
                            Require(window.ResizeMode == (fullScreen ? ResizeMode.NoResize : originalResizeMode), "Visible chrome must retain the window mode's resize behavior.");
                            Require(SameRect(clientBefore,outerBefore), "The video client must fill the window even when controls are visible.");
                            Require(scenario != 1 || SameRect(outerBefore,MonitorWorkRect(windowHandle)), "Maximized video must exactly fill the monitor work area.");
                            if(scenario==2) {
                                var monitor = new TestMonitorInfo { Size = Marshal.SizeOf(typeof(TestMonitorInfo)) };
                                Require(GetMonitorInfo(MonitorFromWindow(windowHandle,2),ref monitor) && SameRect(outerBefore,monitor.Monitor), "Full-screen video must exactly fill the whole monitor.");
                            }
                        });
                        WindowSnapshot hiddenWindow = null;
                        object hiddenClientEvidence = null;
                        try {
                            policy.Update(0, false, false, false, false);
                            applyVisibility.Invoke(chrome, null);
                            SettleLayout(window);
                            attachment.Resize();
                            SettleLayout(window);
                            hiddenWindow = Snapshot(windowHandle);
                            Native.RECT outerHidden = WindowRect(windowHandle), clientHidden = ClientScreenRect(windowHandle);
                            hiddenClientEvidence = FrameEvidence(outerHidden, clientHidden);
                            Check(report, scenarioName + ".hiddenClientFillsWindow", delegate {
                                Require(SameRect(outerHidden, clientHidden), "Hidden chrome must leave zero non-client gaps. Outer " + RectText(outerHidden) + "; client " + RectText(clientHidden) + ".");
                            });
                            Check(report, scenarioName + ".hiddenResizeHitTests", delegate {
                                bool resizeExpected = !fullScreen && window.WindowState == WindowState.Normal &&
                                    (originalResizeMode == ResizeMode.CanResize || originalResizeMode == ResizeMode.CanResizeWithGrip);
                                VerifyHiddenHitTests(windowHandle, outerHidden, resizeExpected);
                            });
                            Check(report, scenarioName + ".hidden", delegate {
                                Require(window.WindowStyle == WindowStyle.None && toolbar.Visibility == Visibility.Collapsed && !chrome.OverlayWindow.IsVisible && toolbarRow.ActualHeight == 0,
                                    "Hidden chrome must hide only the overlay and keep the video layout unchanged.");
                                Require(SameRect(expectedHiddenOuter, WindowRect(windowHandle)), "Hidden chrome must retain normal/full-screen bounds or fill the maximized monitor work area. Expected " + RectText(expectedHiddenOuter) + "; actual " + RectText(WindowRect(windowHandle)) + ".");
                                Require(window.WindowState == (scenario == 0 ? WindowState.Normal : WindowState.Maximized), "Hiding chrome must retain the current normal/maximized window state.");
                                Require(new WindowInteropHelper(window).Handle == windowHandle && host.Container == hostHandle && other.Container == otherHandle &&
                                    Native.IsWindow(hostHandle) && Native.IsWindow(otherHandle), "Hiding chrome must preserve the window and both native host HWNDs.");
                                Require(SameRect(ClientRect(hostHandle),hostBefore) && SameRect(ClientRect(otherHandle),otherBefore) && SameRect(sourceBefore,WindowRect(source)),
                                    "Hiding the controls must preserve the exact source position, dimensions and both host rectangles.");
                            });
                            CheckAttached(report, scenarioName + ".hiddenVideo", attachment, source, host);
                        } finally {
                            chrome.Reveal();
                            SettleLayout(window);
                            attachment.Resize();
                            SettleLayout(window);
                        }
                        Check(report, scenarioName + ".restored", delegate {
                            Require(window.WindowStyle == shownStyle && toolbar.Visibility == Visibility.Visible && chrome.OverlayWindow.IsVisible && toolbarRow.ActualHeight == 0,
                                "Revealing the overlay must reserve no video layout space.");
                            Require(SameRect(outerBefore, WindowRect(windowHandle)), "Revealing chrome must preserve the outer window rectangle.");
                            Require(new WindowInteropHelper(window).Handle == windowHandle && host.Container == hostHandle && other.Container == otherHandle &&
                                Native.IsWindow(hostHandle) && Native.IsWindow(otherHandle), "Revealing chrome must preserve the window and both native host HWNDs.");
                            Require(SameRect(hostBefore, ClientRect(hostHandle)) && SameRect(otherBefore, ClientRect(otherHandle)) && SameRect(sourceBefore,WindowRect(source)),
                                "Revealing chrome must restore both native hosts to their original client sizes.");
                        });
                        Native.RECT outerRestored = WindowRect(windowHandle), clientRestored = ClientScreenRect(windowHandle);
                        Check(report, scenarioName + ".restoredClientFrame", delegate {
                            Require(SameRect(clientBefore, clientRestored), "Revealing chrome must restore the native client origin, dimensions and title-bar spacing. Before " + RectText(clientBefore) + "; restored " + RectText(clientRestored) + ".");
                            Require(window.ResizeMode == (fullScreen ? ResizeMode.NoResize : originalResizeMode), "Revealing chrome must restore the original resize behavior.");
                            helper.Ping();
                        });
                        CheckAttached(report, scenarioName + ".restoredVideo", attachment, source, host);
                        report.evidence[scenarioName] = new {
                            shownWindow = shownWindow, hiddenWindow = hiddenWindow, restoredWindow = Snapshot(windowHandle),
                            shownClient = FrameEvidence(outerBefore, clientBefore), hiddenClient = hiddenClientEvidence,
                            restoredClient = FrameEvidence(outerRestored, clientRestored), expectedHiddenOuter = expectedHiddenOuter
                        };
                    }
                } finally {
                    fullScreen = false;
                    attachment.ViewScale = 100;
                    window.WindowState = originalState;
                    window.ResizeMode = originalResizeMode;
                    chrome.Reveal();
                    SettleLayout(window);
                }
                Check(report, name + ".returnsToOriginalBounds", delegate {
                    Require(window.WindowState == originalState && SameRect(originalBounds, WindowRect(windowHandle)),
                        "Returning from maximized/full-screen must preserve the original normal restore bounds. Before " + RectText(originalBounds) + "; returned " + RectText(WindowRect(windowHandle)) + ".");
                });
                report.evidence[name + ".returnedWindow"] = new { original = originalWindow, returned = Snapshot(windowHandle) };
                Check(report,name+".overlayCloseKeepsControlsReusable",delegate {
                    bool closeRequested=false;
                    System.ComponentModel.CancelEventHandler onClose=delegate(object sender,System.ComponentModel.CancelEventArgs args) { closeRequested=true; args.Cancel=true; };
                    window.Closing+=onClose;
                    try {
                        chrome.OverlayWindow.Close();
                        Require(closeRequested && Native.IsWindow(new WindowInteropHelper(chrome.OverlayWindow).Handle), "Closing the overlay must forward to the owner and retain its reusable HWND when owner closing is cancelled.");
                        chrome.Reveal();
                        Require(chrome.OverlayWindow.IsVisible,"The overlay must remain reusable after a cancelled close.");
                    } finally { window.Closing-=onClose; }
                });
                Check(report,name+".minimizeHidesOverlay",delegate {
                    window.WindowState=WindowState.Minimized; SettleLayout(window);
                    Require(!chrome.OverlayWindow.IsVisible,"Minimizing the video window must hide the controls too.");
                    window.WindowState=originalState; chrome.Reveal(); SettleLayout(window);
                    Require(chrome.OverlayWindow.IsVisible,"Restoring the video window must restore its controls.");
                });
            }
        }

        private static bool SameRect(Native.RECT first, Native.RECT second) {
            return first.Left == second.Left && first.Top == second.Top && first.Right == second.Right && first.Bottom == second.Bottom;
        }

        private static void VerifyHiddenHitTests(IntPtr handle, Native.RECT bounds, bool resizeExpected) {
            int left = bounds.Left + 1, top = bounds.Top + 1, right = bounds.Right - 2, bottom = bounds.Bottom - 2;
            int middleX = bounds.Left + Width(bounds) / 2, middleY = bounds.Top + Height(bounds) / 2;
            int[,] samples = {
                { left, middleY, 10 }, { right, middleY, 11 }, { middleX, top, 12 }, { middleX, bottom, 15 },
                { left, top, 13 }, { right, top, 14 }, { left, bottom, 16 }, { right, bottom, 17 },
                { middleX, middleY, 1 }
            };
            for (int index = 0; index < samples.GetLength(0); index++) {
                int x = samples[index, 0], y = samples[index, 1];
                int expected = resizeExpected ? samples[index, 2] : 1;
                int coordinates = unchecked((y << 16) | (x & 0xffff));
                long actual = SendMessage(handle, 0x0084, IntPtr.Zero, new IntPtr(coordinates)).ToInt64();
                Require(actual == expected, "Hidden window hit-test at " + x + "," + y + " must return " + expected + "; actual " + actual + ".");
            }
        }

        private static object FrameEvidence(Native.RECT outer, Native.RECT client) {
            return new {
                left = client.Left, top = client.Top, width = Width(client), height = Height(client),
                leftGap = client.Left - outer.Left, topGap = client.Top - outer.Top,
                rightGap = outer.Right - client.Right, bottomGap = outer.Bottom - client.Bottom
            };
        }

        private static void CheckAttached(Report report, string name, VideoAttachment attachment, IntPtr source, VideoHost host) {
            Check(report, name + ".parentAndStyle", delegate {
                Require(attachment.Alive && attachment.Handle == source && Native.IsWindow(source), "Attach must retain the original live foreign HWND.");
                Require(Native.GetParent(source) == host.Container, "The foreign HWND has the wrong container parent.");
                long style = Native.GetWindowLongPtr(source, Native.GWL_STYLE).ToInt64();
                Require((style & Native.WS_CHILD) != 0 && (style & (Native.WS_POPUP | Native.WS_CAPTION | Native.WS_THICKFRAME)) == 0, "The embedded window must be a borderless child.");
            });
            Check(report, name + ".sizeAndPosition", delegate {
                Native.RECT hostClient = ClientRect(host.Container), sourceClient = ClientRect(source);
                Native.RECT hostRect = WindowRect(host.Container), sourceRect = WindowRect(source);
                Require(Width(hostClient) > 0 && Height(hostClient) > 0, "The host client area must be nonempty.");
                int width = (int)Math.Round(Width(hostClient)*attachment.ViewScale/100.0), height = (int)Math.Round(Height(hostClient)*attachment.ViewScale/100.0);
                Require(Width(sourceClient) == width && Height(sourceClient) == height, "Video client must match the selected scale of the unmodified host client.");
                Require(sourceRect.Left == hostRect.Left+(Width(hostClient)-width)/2 && sourceRect.Top == hostRect.Top+(Height(hostClient)-height)/2, "Scaled video must stay centered in the host.");
            });
            CheckHostBackground(report, name + ".blackBackground", host);
            report.evidence[name] = new { source = Snapshot(source), container = Snapshot(host.Container) };
        }

        private static void CheckHostBackground(Report report, string name, VideoHost host) {
            Check(report, name, delegate {
                long style = Native.GetWindowLongPtr(host.Container, Native.GWL_STYLE).ToInt64();
                Require((style & 0x02000000L) != 0, "The host must clip child video while repainting its background.");
                // Ask only our own offscreen host to erase into a memory canvas.
                // This exercises the real native background painter without
                // reading the desktop or capturing any video/other application.
                using (var canvas = new System.Drawing.Bitmap(16, 16)) {
                    using (var graphics = System.Drawing.Graphics.FromImage(canvas)) {
                        graphics.Clear(System.Drawing.Color.Magenta);
                        IntPtr dc = graphics.GetHdc();
                        try {
                            Require(Native.SendMessage(host.Container, 0x0014, dc, IntPtr.Zero) != IntPtr.Zero,
                                "The host must erase its background through its native window procedure.");
                        } finally { graphics.ReleaseHdc(dc); }
                    }
                    for (int y = 0; y < canvas.Height; y++) for (int x = 0; x < canvas.Width; x++) {
                        System.Drawing.Color pixel = canvas.GetPixel(x, y);
                        Require(pixel.R == 0 && pixel.G == 0 && pixel.B == 0,
                            "Host background must be RGB(0,0,0); received " + pixel.R + "," + pixel.G + "," + pixel.B + ".");
                    }
                }
            });
        }

        private sealed class WindowSnapshot {
            public long handle, parent, style, exStyle;
            public int left, top, width, height;
        }

        private static WindowSnapshot Snapshot(IntPtr handle) {
            Require(Native.IsWindow(handle), "The test HWND no longer exists: " + handle.ToInt64());
            Native.RECT rect = WindowRect(handle);
            return new WindowSnapshot {
                handle = handle.ToInt64(), parent = Native.GetParent(handle).ToInt64(),
                style = Native.GetWindowLongPtr(handle, Native.GWL_STYLE).ToInt64(),
                exStyle = Native.GetWindowLongPtr(handle, Native.GWL_EXSTYLE).ToInt64(),
                left = rect.Left, top = rect.Top, width = Width(rect), height = Height(rect)
            };
        }

        private static Native.RECT WindowRect(IntPtr handle) {
            Native.RECT rect;
            Require(Native.GetWindowRect(handle, out rect), "GetWindowRect failed for the test HWND.");
            return rect;
        }

        private static Native.RECT ClientRect(IntPtr handle) {
            Native.RECT rect;
            Require(Native.GetClientRect(handle, out rect), "GetClientRect failed for the test HWND.");
            return rect;
        }

        private static Native.RECT ClientScreenRect(IntPtr handle) {
            Native.RECT client = ClientRect(handle);
            var origin = new ClientPoint { X = client.Left, Y = client.Top };
            var corner = new ClientPoint { X = client.Right, Y = client.Bottom };
            Require(ClientToScreen(handle, ref origin) && ClientToScreen(handle, ref corner), "ClientToScreen failed for the test HWND.");
            return new Native.RECT { Left = origin.X, Top = origin.Y, Right = corner.X, Bottom = corner.Y };
        }

        private static Native.RECT MonitorWorkRect(IntPtr handle) {
            var info = new TestMonitorInfo { Size = Marshal.SizeOf(typeof(TestMonitorInfo)) };
            IntPtr monitor = MonitorFromWindow(handle, 2);
            Require(monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info), "The test window's monitor work area must be available.");
            return info.Work;
        }

        private static int Width(Native.RECT rect) { return rect.Right - rect.Left; }
        private static int Height(Native.RECT rect) { return rect.Bottom - rect.Top; }
        private static string RectText(WindowSnapshot rect) { return rect.left + "," + rect.top + " " + rect.width + "x" + rect.height; }
        private static string RectText(Native.RECT rect) { return rect.Left + "," + rect.Top + " " + Width(rect) + "x" + Height(rect); }

        private static void SettleLayout(Window window) {
            window.UpdateLayout();
            PumpDispatcher();
            window.UpdateLayout();
        }

        private static void PumpDispatcher() {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { frame.Continue = false; }));
            Dispatcher.PushFrame(frame);
        }

        private static void Require(bool condition, string message) {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Check(Report report, string name, Action action) {
            var watch = Stopwatch.StartNew();
            try {
                action();
                report.checks.Add(new CheckResult { name = name, passed = true, message = "Passed", milliseconds = watch.ElapsedMilliseconds });
            } catch (Exception error) {
                AddFailure(report, name, error, watch.ElapsedMilliseconds);
            }
        }

        private static void AddFailure(Report report, string name, Exception error) { AddFailure(report, name, error, 0); }
        private static void AddFailure(Report report, string name, Exception error, long milliseconds) {
            var aggregate = error as AggregateException;
            if (aggregate != null) error = aggregate.Flatten().InnerExceptions[0];
            string message = error.GetType().Name + ": " + error.Message;
            report.checks.Add(new CheckResult { name = name, passed = false, message = message, milliseconds = milliseconds });
            report.failures.Add(name + ": " + message);
        }

        private static string ReadLine(StreamReader reader, int timeoutMilliseconds) {
            Task<string> task = Task.Run(delegate { return reader.ReadLine(); });
            if (!task.Wait(timeoutMilliseconds)) throw new TimeoutException("The test-video helper did not reply within " + timeoutMilliseconds + " ms.");
            string line = task.Result;
            if (line == null) throw new EndOfStreamException("The test-video helper disconnected.");
            return line;
        }

        private sealed class VideoHelper : IDisposable {
            private NamedPipeServerStream pipe;
            private StreamReader reader;
            private StreamWriter writer;
            private Process process;
            public IntPtr Handle { get; private set; }
            public int ProcessId { get { return process.Id; } }

            public static VideoHelper Start() {
                var helper = new VideoHelper();
                try {
                    string pipeName = "iPhonePC.SelfTest." + Guid.NewGuid().ToString("N");
                    helper.pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--test-video") {
                        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                    };
                    start.EnvironmentVariables[HelperPipeVariable] = pipeName;
                    helper.process = Process.Start(start);
                    Require(helper.process != null, "The test-video helper could not start.");
                    IAsyncResult connection = helper.pipe.BeginWaitForConnection(null, null);
                    using (WaitHandle wait = connection.AsyncWaitHandle) {
                        if (!wait.WaitOne(10000)) throw new TimeoutException("The test-video helper did not connect. Program.Main must dispatch --test-video before creating a WPF Application.");
                        helper.pipe.EndWaitForConnection(connection);
                    }
                    helper.reader = new StreamReader(helper.pipe, Encoding.UTF8, false, 1024, true);
                    helper.writer = new StreamWriter(helper.pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                    string[] ready = ReadLine(helper.reader, 5000).Split('|');
                    Require(ready.Length == 3 && ready[0] == "READY", "The test-video helper sent an invalid handshake.");
                    Require(int.Parse(ready[1], CultureInfo.InvariantCulture) == helper.process.Id, "The test-video helper process ID did not match.");
                    helper.Handle = new IntPtr(long.Parse(ready[2], CultureInfo.InvariantCulture));
                    Require(Native.IsWindow(helper.Handle), "The helper handshake did not contain a live HWND.");
                    return helper;
                } catch { helper.Dispose(); throw; }
            }

            public void Ping() {
                Require(!process.HasExited, "The independent test-video process exited unexpectedly.");
                writer.WriteLine("PING");
                Require(ReadLine(reader, 5000) == "ALIVE|" + Handle.ToInt64().ToString(CultureInfo.InvariantCulture), "The independent test-video window is no longer alive.");
            }

            public void Close() {
                if (!process.HasExited) {
                    writer.WriteLine("CLOSE");
                    if (!process.WaitForExit(4000)) throw new TimeoutException("The test-video helper did not close within 4000 ms.");
                }
                Require(process.ExitCode == 0, "The test-video helper returned exit code " + process.ExitCode + ".");
            }

            public void Dispose() {
                if (writer != null) { try { writer.WriteLine("CLOSE"); } catch { } }
                if (pipe != null) { try { pipe.Dispose(); } catch { } }
                if (reader != null) { try { reader.Dispose(); } catch { } }
                if (writer != null) { try { writer.Dispose(); } catch { } }
                if (process != null) {
                    try {
                        if (!process.HasExited && !process.WaitForExit(1500)) { process.Kill(); process.WaitForExit(3000); }
                    } catch { }
                    process.Dispose(); process = null;
                }
            }
        }

        private sealed class TestVideoForm : Forms.Form {
            protected override bool ShowWithoutActivation { get { return true; } }
        }

        // This entry point is only dispatched by Program.Main --test-video.
        // It creates a disposable test window and never constructs the receiver/controller.
        public static int RunVideoHelper() {
            string pipeName = Environment.GetEnvironmentVariable(HelperPipeVariable);
            if (string.IsNullOrEmpty(pipeName) || !pipeName.StartsWith("iPhonePC.SelfTest.", StringComparison.Ordinal)) return 1;
            int failed = 0;
            try {
                using (var form = new TestVideoForm())
                using (var lifetime = new Forms.Timer()) {
                    form.Text = "iPhone-PC self-test video source";
                    form.ShowInTaskbar = false;
                    form.StartPosition = Forms.FormStartPosition.Manual;
                    form.AutoScaleMode = Forms.AutoScaleMode.None;
                    form.Location = new System.Drawing.Point(-28000, -28000);
                    form.Size = new System.Drawing.Size(640, 360);
                    form.FormBorderStyle = Forms.FormBorderStyle.Sizable;
                    var watch = Stopwatch.StartNew();
                    lifetime.Interval = 500;
                    lifetime.Tick += delegate {
                        if (watch.ElapsedMilliseconds > 60000) { Interlocked.Exchange(ref failed, 1); form.Close(); }
                    };
                    form.Shown += delegate {
                        IntPtr handle = form.Handle;
                        Native.SetWindowPos(handle, IntPtr.Zero, -28000, -28000, 640, 360, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                        var control = new Thread(delegate() {
                            try {
                                using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut)) {
                                    client.Connect(10000);
                                    using (var input = new StreamReader(client, Encoding.UTF8, false, 1024, true))
                                    using (var output = new StreamWriter(client, new UTF8Encoding(false), 1024, true) { AutoFlush = true }) {
                                        output.WriteLine("READY|" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + "|" + handle.ToInt64().ToString(CultureInfo.InvariantCulture));
                                        string command;
                                        while ((command = input.ReadLine()) != null) {
                                            if (command == "CLOSE") break;
                                            if (command == "PING") output.WriteLine((Native.IsWindow(handle) ? "ALIVE|" : "DEAD|") + handle.ToInt64().ToString(CultureInfo.InvariantCulture));
                                            else throw new InvalidOperationException("Unknown test helper command.");
                                        }
                                    }
                                }
                            } catch { Interlocked.Exchange(ref failed, 1); }
                            finally {
                                try { form.BeginInvoke(new Action(delegate { form.Close(); })); } catch { }
                            }
                        });
                        control.IsBackground = true;
                        control.Name = "iPhone-PC self-test pipe";
                        control.Start();
                        lifetime.Start();
                    };
                    Forms.Application.Run(form);
                    lifetime.Stop();
                }
            } catch { return 1; }
            return failed == 0 ? 0 : 1;
        }
    }
}
