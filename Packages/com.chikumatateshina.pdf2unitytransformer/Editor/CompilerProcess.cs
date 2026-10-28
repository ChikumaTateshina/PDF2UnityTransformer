using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PDF2Unity.Editor
{
    public sealed class CompileRequest
    {
        public string InputPdf;
        public string OutputDir;
        public string Pages = "all";
        public string Mode = "hybrid";               // strict | hybrid | page-fallback
        public List<string> Targets = new List<string> { "pc" };
        public string ProfilePc = "pc-standard";
        public string ProfileQuest = "quest-standard";
        public string CmykProfile;
        public string SubstituteFonts;
        public bool FontRightsDeclared;
        public float WorldScale = 1f;
        public int TimeoutSec = 1800;
    }

    /// <summary>
    /// Runs the external compiler CLI (SPEC §8): isolated working directory, no shell,
    /// stdout JSON-lines protocol for progress/result, cancellation and timeout.
    /// Poll <see cref="Tick"/> from the editor update loop.
    /// </summary>
    public sealed class CompilerProcess : IDisposable
    {
        public enum State { Running, Succeeded, Failed, Cancelled, TimedOut }

        public State Current { get; private set; } = State.Running;
        public string Stage { get; private set; } = "starting";
        public float Progress01 { get; private set; }
        public string ErrorCode { get; private set; }
        public string ErrorMessage { get; private set; }
        public Dictionary<string, object> Result { get; private set; }
        public readonly StringBuilder Log = new StringBuilder();

        readonly Process process;
        readonly ConcurrentQueue<string> lines = new ConcurrentQueue<string>();
        readonly ConcurrentQueue<string> errLines = new ConcurrentQueue<string>();
        readonly DateTime deadline;
        readonly string workDir;

        public CompilerProcess(CompileRequest req)
        {
            if (!File.Exists(req.InputPdf)) throw new FileNotFoundException("PDF not found", req.InputPdf);
            workDir = Path.Combine(Path.GetTempPath(), "pdf2unity-run-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            var args = new List<string>
            {
                "-s", "-B", "-m", "pdf2unity", "compile",
                "--input", req.InputPdf,
                "--output", req.OutputDir,
                "--pages", string.IsNullOrWhiteSpace(req.Pages) ? "all" : req.Pages.Trim(),
                "--mode", req.Mode,
                "--target", string.Join(",", req.Targets),
                "--profile-pc", req.ProfilePc,
                "--profile-quest", req.ProfileQuest,
                "--world-scale", req.WorldScale.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--timeout", req.TimeoutSec.ToString(),
                "--font-rights", req.FontRightsDeclared ? "declared" : "verify",
            };
            if (!string.IsNullOrEmpty(req.CmykProfile)) { args.Add("--cmyk-profile"); args.Add(req.CmykProfile); }
            if (!string.IsNullOrEmpty(req.SubstituteFonts)) { args.Add("--substitute-fonts"); args.Add(req.SubstituteFonts); }

            var psi = new ProcessStartInfo
            {
                FileName = PdfUnitySettings.PythonExe,
                Arguments = JoinArgs(args),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                WorkingDirectory = workDir,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["PYTHONPATH"] = PdfUnitySettings.CompilerDir;
            psi.EnvironmentVariables["PYTHONNOUSERSITE"] = "1";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONUTF8"] = "1";
            Log.AppendLine($"> {psi.FileName} {psi.Arguments}");
            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) lines.Enqueue(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) errLines.Enqueue(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            deadline = DateTime.UtcNow.AddSeconds(req.TimeoutSec + 30);
        }

        /// <summary>Windows command line quoting (MSVCRT rules).</summary>
        public static string JoinArgs(IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            foreach (var a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) { sb.Append(a); continue; }
                sb.Append('"');
                int bs = 0;
                foreach (var c in a)
                {
                    if (c == '\\') { bs++; continue; }
                    if (c == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; continue; }
                    sb.Append('\\', bs); bs = 0; sb.Append(c);
                }
                sb.Append('\\', bs * 2);
                sb.Append('"');
            }
            return sb.ToString();
        }

        /// <summary>Process queued output. Returns true while the compiler is still running.</summary>
        public bool Tick()
        {
            while (errLines.TryDequeue(out var e)) Log.AppendLine(e);
            while (lines.TryDequeue(out var line)) Handle(line);
            if (Current != State.Running) return false;
            if (DateTime.UtcNow > deadline)
            {
                Kill();
                Current = State.TimedOut;
                ErrorCode = "timeout";
                ErrorMessage = "compiler did not finish within the timeout";
                return false;
            }
            if (process.HasExited)
            {
                process.WaitForExit();
                while (errLines.TryDequeue(out var e)) Log.AppendLine(e);
                while (lines.TryDequeue(out var line)) Handle(line);
                if (Current == State.Running)
                {
                    if (process.ExitCode == 0 && Result != null) Current = State.Succeeded;
                    else
                    {
                        Current = State.Failed;
                        ErrorCode = ErrorCode ?? $"exit-{process.ExitCode}";
                        ErrorMessage = ErrorMessage ?? "compiler exited without a result";
                    }
                }
                Cleanup();
                return false;
            }
            return true;
        }

        void Handle(string line)
        {
            Log.AppendLine(line);
            Dictionary<string, object> ev;
            try { ev = Json.Parse(line) as Dictionary<string, object>; }
            catch (FormatException) { return; }
            if (ev == null) return;
            switch (J.Str(ev, "event"))
            {
                case "progress":
                    Stage = J.Str(ev, "stage", "");
                    var total = J.Num(ev, "total", 1);
                    Progress01 = total > 0 ? (float)(J.Num(ev, "current") / total) : 0f;
                    break;
                case "result":
                    Result = ev;
                    Current = State.Succeeded;
                    break;
                case "error":
                    ErrorCode = J.Str(ev, "code", "error");
                    ErrorMessage = J.Str(ev, "message", "");
                    Current = State.Failed;
                    break;
            }
        }

        public void Cancel()
        {
            if (Current != State.Running) return;
            Kill();
            Current = State.Cancelled;
            Cleanup();
        }

        void Kill()
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
        }

        void Cleanup()
        {
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void Dispose()
        {
            Kill();
            Cleanup();
            process.Dispose();
        }
    }
}
