using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace IPhonePC {
    public sealed class Settings {
        public string Resolution = "3840x2160";
        public int Fps = 60;
        public bool Detached = false;
        public int ViewScale = 100;
        // A portable release keeps the receiver beside this executable.  Keep the
        // installed-location fallback so existing local deployments continue to work.
        public string ReceiverPath = Storage.DefaultReceiverPath;
        public string ReceiverName = "iPhone-PC";
        public string Password = "6817";
        public static bool TryValidateConnection(string name, string password, out string normalizedName, out string error) {
            normalizedName = (name ?? "").Trim();
            error = null;
            if (normalizedName.Length == 0) { error = "请输入接收名称。"; return false; }
            foreach (char character in name) {
                if (char.IsControl(character) || character == '\u2028' || character == '\u2029' || character == '"' || character == '%') {
                    error = "接收名称不能包含换行、控制字符、双引号或百分号。";
                    return false;
                }
            }
            if (normalizedName.StartsWith("-", StringComparison.Ordinal)) { error = "接收名称不能以减号开头。"; return false; }
            try {
                // RAOP prefixes the name with a 12-character hardware address and '@'.
                if (new UTF8Encoding(false, true).GetByteCount(normalizedName) > 50) {
                    error = "接收名称过长，请缩短后重试（最多约 16 个汉字或 50 个英文字母）。";
                    return false;
                }
            } catch (EncoderFallbackException) { error = "接收名称包含无效字符，请重新输入。"; return false; }
            if (!Regex.IsMatch(password ?? "", @"\A[a-zA-Z0-9]{4,32}\z")) { error = "连接密码需为 4–32 位英文字母或数字。"; return false; }
            return true;
        }
        public void Validate() {
            if (!new [] {"1280x720","1920x1080","2560x1440","3840x2160"}.Contains(Resolution)) Resolution = "3840x2160";
            if (!new [] {24,30,60}.Contains(Fps)) Fps = 60;
            if (!new [] {50,75,100}.Contains(ViewScale)) ViewScale = 100;
            string normalizedName, error;
            if (!TryValidateConnection(ReceiverName, Password, out normalizedName, out error)) throw new ArgumentException(error);
            ReceiverName = normalizedName;
        }
        public string Arguments() {
            Validate();
            // This wrapper uses QProcess::splitCommand, not shell/Windows argv escaping.
            // It expands %environment variables% first, so validation also rejects '%'.
            // Double quotes group spaces; the validated name cannot contain a quote.
            return "-n \"" + ReceiverName + "\" -nh -h265 -s " + Resolution + "@60 -fps " + Fps + " -p 35000 -pw " + Password;
        }
    }
    internal static class Storage {
        public static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string Data = Path.Combine(Root,"data");
        public static readonly string SettingsPath = Path.Combine(Data,"settings.json");
        public static readonly string BundledReceiverPath = Path.Combine(Root,"engine","uxplay-windows.exe");
        public static readonly string InstalledReceiverPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","uxplay-windows","uxplay-windows.exe");
        public static readonly string ReceiverArguments = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"leapbtw","uxplay-windows","arguments.txt");
        public static readonly string MachineArguments = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"uxplay-windows","arguments.txt");
        public static string DefaultReceiverPath {
            get { return File.Exists(BundledReceiverPath) ? BundledReceiverPath : InstalledReceiverPath; }
        }
        public static Settings Load() {
            Directory.CreateDirectory(Data);
            Settings result = new Settings();
            if (File.Exists(SettingsPath)) {
                try { result = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(SettingsPath)); } catch(Exception ex) { Log("设置读取失败，使用默认设置: " + ex.Message); }
            // A portable folder starts with its own defaults.  Do not import the
            // settings of a separately installed receiver on the same PC.
            } else if (!File.Exists(BundledReceiverPath) && File.Exists(ReceiverArguments)) {
                result = ParseArguments(File.ReadAllText(ReceiverArguments));
            }
            if(result == null) result = new Settings();
            // The workbench does not expose a custom engine picker.  A portable
            // release must always follow its own bundled engine, even after the
            // folder is copied or moved while an older copy still exists.
            if(File.Exists(BundledReceiverPath)) result.ReceiverPath=BundledReceiverPath;
            result.Validate(); return result;
        }
        internal static Settings ParseArguments(string content) {
            var result = new Settings();
            string[] arguments = SplitArguments(content);
            for (int index = 0; index + 1 < arguments.Length; index++) {
                string option = arguments[index];
                if (option != "-n" && option != "-pw" && option != "-s" && option != "-fps") continue;
                string value = arguments[++index];
                if (option == "-n") result.ReceiverName = value;
                else if (option == "-pw") result.Password = value;
                else if (option == "-s") result.Resolution = value.Split('@')[0];
                else {
                    int fps;
                    if (int.TryParse(value, out fps)) result.Fps = fps;
                }
            }
            return result;
        }
        internal static string[] SplitArguments(string content) {
            // Match Qt 6.11.2 QProcess::splitCommand, including its triple-quote rule.
            // In particular, -pw/-fps text inside a quoted name is not an option.
            var arguments = new List<string>();
            var token = new StringBuilder();
            int quoteCount = 0;
            bool inQuote = false;
            foreach (char character in content ?? "") {
                if (character == '"') {
                    if (++quoteCount == 3) { quoteCount = 0; token.Append(character); }
                    continue;
                }
                if (quoteCount != 0) {
                    if (quoteCount == 1) inQuote = !inQuote;
                    quoteCount = 0;
                }
                if (!inQuote && char.IsWhiteSpace(character)) {
                    if (token.Length != 0) { arguments.Add(token.ToString()); token.Clear(); }
                } else token.Append(character);
            }
            if (token.Length != 0) arguments.Add(token.ToString());
            return arguments.ToArray();
        }
        public static void Save(Settings settings) { settings.Validate(); AtomicWrite(SettingsPath,new JavaScriptSerializer().Serialize(settings)); }
        public static void AtomicWrite(string path, string content) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            File.WriteAllText(temporary,content,new UTF8Encoding(false));
            if(File.Exists(path)) File.Replace(temporary,path,null); else File.Move(temporary,path);
        }
        public static void Log(string message) {
            try {
                Directory.CreateDirectory(Data);
                string file=Path.Combine(Data,"controller.log");
                if(File.Exists(file) && new FileInfo(file).Length>1024*1024) File.Move(file,Path.Combine(Data,"controller-"+DateTime.Now.ToString("yyyyMMdd-HHmmssfff")+".log"));
                File.AppendAllText(file,DateTime.Now.ToString("s")+" "+message+Environment.NewLine);
            } catch { }
        }
    }
    internal sealed class Receiver {
        public Process Process { get; private set; }
        public bool StartedHere { get; private set; }
        public string LogPath { get; private set; }
        public bool Alive { get { try { return Process != null && !Process.HasExited; } catch { return false; } } }
        public int Pid { get { return Alive ? Process.Id : 0; } }
        public bool Listening { get { try { return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint=>endpoint.Port==35001); } catch { return false; } } }
        public bool ClientConnected { get { try { return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Any(connection=>connection.LocalEndPoint.Port>=35000 && connection.LocalEndPoint.Port<=35002 && connection.State==System.Net.NetworkInformation.TcpState.Established); } catch { return false; } } }
        public bool MirrorConnected { get { try { return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Any(connection=>connection.LocalEndPoint.Port==35000 && connection.State==System.Net.NetworkInformation.TcpState.Established); } catch { return false; } } }
        private readonly Settings settings;
        public Receiver(Settings current) { settings=current; }
        private Process FindExisting() {
            foreach(var candidate in System.Diagnostics.Process.GetProcessesByName("uxplay-windows")) {
                try { if(string.Equals(candidate.MainModule.FileName,settings.ReceiverPath,StringComparison.OrdinalIgnoreCase)) return candidate; } catch { }
                candidate.Dispose();
            }
            return null;
        }
        private async Task RetireInstalledReceiver() {
            // Old shortcuts can still start the previously installed engine.
            // Only migrate that exact per-user installation in this session;
            // unrelated programs occupying the port must remain untouched.
            if(!string.Equals(Path.GetFullPath(settings.ReceiverPath),Path.GetFullPath(Storage.BundledReceiverPath),StringComparison.OrdinalIgnoreCase)) return;
            bool retired=false;
            int session=System.Diagnostics.Process.GetCurrentProcess().SessionId;
            foreach(var candidate in System.Diagnostics.Process.GetProcessesByName("uxplay-windows")) {
                using(candidate) {
                    bool legacy=false;
                    try {
                        legacy=candidate.SessionId==session && string.Equals(candidate.MainModule.FileName,Storage.InstalledReceiverPath,StringComparison.OrdinalIgnoreCase);
                    } catch(InvalidOperationException) { }
                    catch(System.ComponentModel.Win32Exception) { }
                    if(!legacy) continue;
                    int legacyPid=candidate.Id;
                    try {
                        await Task.Run(delegate {
                            try { candidate.Kill(); } catch(InvalidOperationException) { return; }
                            if(!candidate.WaitForExit(5000)) throw new InvalidOperationException("旧版投屏接收端尚未退出，请关闭旧版后重试。");
                        });
                    } catch(System.ComponentModel.Win32Exception error) {
                        throw new InvalidOperationException("旧版投屏接收端正在运行，当前无法将其关闭。请退出旧版投屏程序后重试。",error);
                    }
                    retired=true;
                    Storage.Log("切换到工作区接收核心，关闭旧版接收端 PID="+legacyPid);
                }
            }
            if(retired) await Task.Delay(2000);
        }
        public async Task Start(bool restart) {
            string desiredArguments=settings.Arguments();
            if(!File.Exists(settings.ReceiverPath)) throw new FileNotFoundException("找不到投屏接收核心。请确认程序文件夹中的 engine 目录完整。",settings.ReceiverPath);
            if(Process==null || !Alive) Process=FindExisting();
            if(Alive && AudioDiagnostics.Enabled && !StartedHere) restart=true;
            if(Alive && !restart) {
                string activeArguments=File.Exists(Storage.ReceiverArguments)?File.ReadAllText(Storage.ReceiverArguments).Trim():"";
                if(activeArguments==desiredArguments) { Storage.Log("接管现有接收进程 PID="+Pid); FindLog(); return; }
                restart=true;
            }
            if(File.Exists(Storage.MachineArguments))
                throw new InvalidOperationException("系统存在优先级更高的接收配置："+Storage.MachineArguments+"。请先移除此覆盖配置后重试。");
            if(Alive) await Stop(true);
            await RetireInstalledReceiver();
            if(Listening) throw new InvalidOperationException("投屏端口 35001 被其他程序占用。请关闭其他接收端后重试。");
            Directory.CreateDirectory(Path.GetDirectoryName(Storage.ReceiverArguments));
            string backup=Path.Combine(Storage.Data,"receiver-arguments-before.txt");
            if(File.Exists(Storage.ReceiverArguments) && !File.Exists(backup)) File.Copy(Storage.ReceiverArguments,backup);
            Storage.AtomicWrite(Storage.ReceiverArguments,desiredArguments+Environment.NewLine);
            using(var key=Registry.CurrentUser.CreateSubKey(@"Software\leapbtw\uxplay-windows")) {
                key.SetValue("ble_enabled","false"); key.SetValue("renderer_mode","d3d11");
            }
            var start = new ProcessStartInfo(settings.ReceiverPath) { WorkingDirectory=Path.GetDirectoryName(settings.ReceiverPath), UseShellExecute=false, WindowStyle=ProcessWindowStyle.Normal };
            AudioDiagnostics.ApplyEnvironment(start);
            Process=System.Diagnostics.Process.Start(start); StartedHere=true; LogPath=null;
            AudioDiagnostics.RecordReceiver(Process,null);
            Storage.Log("接收端启动 PID="+Process.Id+" 请求="+settings.Resolution+" "+settings.Fps+"fps");
            // On a fresh portable installation the receiver can briefly restart
            // while it installs its bundled Bonjour service after the UAC prompt.
            // Adopt that replacement process instead of failing on the first exit.
            int attempts=File.Exists(Storage.BundledReceiverPath)?240:40;
            for(int attempt=0; attempt<attempts; attempt++) {
                await Task.Delay(250);
                if(!Alive) {
                    Process replacement=FindExisting();
                    if(replacement!=null) { Process=replacement; StartedHere=true; }
                    else continue;
                }
                if(Listening) { FindLog(); return; }
            }
            FindLog(); throw new InvalidOperationException("接收端尚未建立监听，请检查 Bonjour 服务是否运行，再点击重启接收。");
        }
        public async Task Stop(bool force,bool waitForRestart=true) {
            if(Alive && (StartedHere || force)) {
                int id=Pid;
                await Task.Run(delegate { try { Process.Kill(); Process.WaitForExit(5000); } catch(InvalidOperationException) { } });
                Storage.Log("停止接收进程 PID="+id);
                // A replacement receiver needs time to release discovery and
                // listening resources. Application exit needs no such pause.
                if(waitForRestart) await Task.Delay(2000);
            }
            if(Process!=null) Process.Dispose();
            Process=null; StartedHere=false; LogPath=null;
        }
        public void FindLog() {
            if(!Alive) return;
            string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"uxplay-windows","logs");
            if(!Directory.Exists(folder)) return;
            LogPath=Directory.GetFiles(folder,"*-"+Pid+"-*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            AudioDiagnostics.RecordReceiver(Process,LogPath);
        }
        public string ReadRecentLog() {
            try {
                if(LogPath==null) FindLog();
                if(LogPath==null) return "";
                using(var stream=new FileStream(LogPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
                    stream.Seek(Math.Max(0,stream.Length-32768),SeekOrigin.Begin);
                    using(var reader=new StreamReader(stream,Encoding.UTF8)) return reader.ReadToEnd();
                }
            } catch { return ""; }
        }
    }
}
