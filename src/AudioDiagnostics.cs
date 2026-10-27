using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace IPhonePC {
    // Opt-in, bounded, per-launch metadata logging. No audio/video payload is
    // recorded, no synchronization/quality setting is changed or persisted.
    internal static class AudioDiagnostics {
        internal const long MaximumBytes = 24L*1024*1024;
        private static readonly Stopwatch elapsed = new Stopwatch();
        private sealed class ReceiverRun {
            internal int Pid;
            internal string StartedUtc, NativeLog;
        }
        private sealed class LogSegment {
            internal int Number;
            internal string PreparedUtc, FileTemplate;
            internal readonly List<ReceiverRun> Receivers = new List<ReceiverRun>();
        }
        private static readonly List<LogSegment> segments = new List<LogSegment>();
        private static long firstConnectionMilliseconds = -1;
        private static string startedUtc, connectedUtc, endedUtc, completionReason, folder, sessionName;
        internal static bool Enabled { get; private set; }
        internal static string LogPath { get; private set; }
        internal static string SummaryPath { get; private set; }

        internal static void Configure(string[] arguments, string dataDirectory) {
            elapsed.Reset(); segments.Clear();
            firstConnectionMilliseconds = -1;
            LogPath = SummaryPath = null;
            startedUtc = connectedUtc = endedUtc = completionReason = folder = sessionName = null;
            Enabled = Array.IndexOf(arguments,"--diagnose-audio") >= 0;
            if (!Enabled) return;
            folder = Path.Combine(Path.GetFullPath(dataDirectory),"diagnostics");
            Directory.CreateDirectory(folder);
            sessionName = "audio-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")+"-"+Guid.NewGuid().ToString("N").Substring(0,8);
            SummaryPath = Path.Combine(folder,sessionName+".json");
            startedUtc = DateTime.UtcNow.ToString("o"); connectedUtc = endedUtc = completionReason = null;
            firstConnectionMilliseconds = -1; elapsed.Restart();
            WriteSummary();
        }
        internal static void ApplyEnvironment(ProcessStartInfo start) {
            start.EnvironmentVariables.Remove("GST_DEBUG");
            start.EnvironmentVariables.Remove("GST_DEBUG_FILE");
            start.EnvironmentVariables.Remove("GST_DEBUG_NO_COLOR");
            if (!Enabled) return;
            // Each launch gets a new template. GStreamer expands %p to the actual
            // process ID, also preserving logs if a receiver restarts itself.
            var segment = new LogSegment {
                Number=segments.Count+1, PreparedUtc=DateTime.UtcNow.ToString("o"),
                FileTemplate=Path.Combine(folder,sessionName+"-"+(segments.Count+1).ToString("D3")+"-%p.log")
            };
            segments.Add(segment); LogPath=null;
            // Input queue metadata and sink clock/render metadata only.
            start.EnvironmentVariables["GST_DEBUG"]="0,appsrc:6,basesink:5,audiobasesink:5,ringbuffer:5,wasapi2*:4";
            start.EnvironmentVariables["GST_DEBUG_FILE"]=segment.FileTemplate;
            start.EnvironmentVariables["GST_DEBUG_NO_COLOR"]="1";
            WriteSummary();
        }
        internal static void RecordReceiver(Process receiver, string nativeLog) {
            if (!Enabled || segments.Count==0 || receiver==null) return;
            try {
                int pid=receiver.Id;
                LogSegment segment=segments[segments.Count-1];
                ReceiverRun run=segment.Receivers.Find(candidate=>candidate.Pid==pid);
                if (run==null) {
                    run=new ReceiverRun {Pid=pid,StartedUtc=receiver.StartTime.ToUniversalTime().ToString("o")};
                    segment.Receivers.Add(run);
                }
                if (nativeLog!=null) run.NativeLog=nativeLog;
                LogPath=segment.FileTemplate.Replace("%p",pid.ToString());
                WriteSummary();
            } catch(InvalidOperationException) { }
            catch(Win32Exception) { }
        }
        internal static string CompletionDue(bool connected) {
            return CompletionDue(connected,elapsed.ElapsedMilliseconds);
        }
        internal static string CompletionDue(bool connected, long elapsedMilliseconds) {
            if (!Enabled) return null;
            if (connected && firstConnectionMilliseconds < 0) {
                firstConnectionMilliseconds=elapsedMilliseconds;
                connectedUtc=DateTime.UtcNow.ToString("o"); WriteSummary();
            }
            if (ByteCount >= MaximumBytes) return "size-limit";
            if (firstConnectionMilliseconds >= 0 && elapsedMilliseconds-firstConnectionMilliseconds >= 60000) return "capture-complete";
            if (elapsedMilliseconds >= 180000) return "connection-wait-limit";
            return null;
        }
        internal static bool End(string reason) {
            if (!Enabled) return false;
            Enabled=false; elapsed.Stop(); endedUtc=DateTime.UtcNow.ToString("o"); completionReason=reason;
            return true;
        }
        private static long ByteCount {
            get {
                long total=0;
                foreach(string path in LogFiles(sessionName+"-*.log")) total+=FileBytes(path);
                return total;
            }
        }
        private static string[] LogFiles(string pattern) {
            try { return folder!=null ? Directory.GetFiles(folder,pattern) : new string[0]; }
            catch(IOException) { return new string[0]; }
            catch(UnauthorizedAccessException) { return new string[0]; }
        }
        private static long FileBytes(string path) {
            try { return new FileInfo(path).Length; }
            catch(IOException) { return 0; }
            catch(UnauthorizedAccessException) { return 0; }
        }
        internal static void WriteSummary() {
            if (SummaryPath==null) return;
            try {
                var segmentSummaries=new List<object>();
                foreach(LogSegment segment in segments) {
                    var files=new List<object>();
                    foreach(string path in LogFiles(Path.GetFileName(segment.FileTemplate).Replace("%p","*"))) {
                        int pid;
                        string pidText=Path.GetFileNameWithoutExtension(path).Substring((sessionName+"-"+segment.Number.ToString("D3")+"-").Length);
                        files.Add(new {metadataLog=path,receiverPid=int.TryParse(pidText,out pid)?(int?)pid:null,bytes=FileBytes(path)});
                    }
                    var receivers=new List<object>();
                    foreach(ReceiverRun run in segment.Receivers) receivers.Add(new {receiverPid=run.Pid,receiverStartedUtc=run.StartedUtc,nativeLog=run.NativeLog});
                    segmentSummaries.Add(new {number=segment.Number,preparedUtc=segment.PreparedUtc,fileTemplate=segment.FileTemplate,receivers=receivers,files=files});
                }
                File.WriteAllText(SummaryPath,new JavaScriptSerializer().Serialize(new {
                    purpose="iPhone mirror audio seek diagnostics", active=Enabled, startedUtc=startedUtc,
                    firstConnectionUtc=connectedUtc, endedUtc=endedUtc, completionReason=completionReason,
                    elapsedMilliseconds=elapsed.ElapsedMilliseconds, metadataLog=LogPath, bytes=ByteCount,
                    segments=segmentSummaries,
                    synchronizedPlayback=true, audioOrVideoPayloadRecorded=false,
                    limits=new {afterFirstConnectionSeconds=60, maximumWaitSeconds=180, maximumBytes=MaximumBytes},
                    interpretation="audio_source logs mark compressed buffers entering the renderer, not raw network arrival. Sink render logs precede physical speaker output. Correlate gaps/PTS waits; absence of a buffer does not prove sender fault."
                }),new UTF8Encoding(false));
            } catch(IOException) { }
            catch(UnauthorizedAccessException) { }
        }
    }
}
