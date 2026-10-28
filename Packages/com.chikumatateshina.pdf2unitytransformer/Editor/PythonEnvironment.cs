using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PDF2Unity.Editor
{
    /// <summary>
    /// Creates / checks the per-user Python environment of the compiler:
    ///   1. find a system Python >= 3.10 (py launcher or python on PATH, or a configured path)
    ///   2. python -m venv %LOCALAPPDATA%/PDF2UnityTransformer/python-env
    ///   3. pip install -r Compiler~/requirements.txt
    /// Runs only when the user presses the setup button. Poll <see cref="Tick"/> from the editor loop.
    /// </summary>
    public sealed class PythonEnvironment : IDisposable
    {
        public const int MinMajor = 3, MinMinor = 10;
        const string ImportCheck = "import pikepdf, pypdfium2, pyclipper, mapbox_earcut, fontTools, numpy, PIL; print('ok')";

        public bool Running { get; private set; } = true;
        public bool Succeeded { get; private set; }
        public string Step { get; private set; } = "";
        public readonly StringBuilder Log = new StringBuilder();

        readonly Queue<(string label, string exe, List<string> args)> steps = new Queue<(string, string, List<string>)>();
        readonly ConcurrentQueue<string> output = new ConcurrentQueue<string>();
        Process current;

        PythonEnvironment() { }

        /// <summary>Starts the setup (venv + dependencies). Throws if no suitable Python is found.</summary>
        public static PythonEnvironment Setup()
        {
            var basePy = FindBasePython(out var report);
            if (basePy == null)
                throw new InvalidOperationException(
                    $"Python {MinMajor}.{MinMinor} 以上が見つかりません。https://www.python.org/ からインストールし" +
                    $"（「Add python.exe to PATH」を有効に）、Unity を再起動するか「詳細設定」で python.exe を指定してください。\n{report}");
            var env = new PythonEnvironment();
            var venvPy = PdfUnitySettings.ManagedPythonExe;
            var req = Path.Combine(PdfUnitySettings.BundledCompilerDir, "requirements.txt");
            if (!File.Exists(venvPy))
                env.steps.Enqueue(("仮想環境を作成中", basePy,
                    new List<string> { "-m", "venv", PdfUnitySettings.EnvironmentDir }));
            env.steps.Enqueue(("pip を更新中", venvPy,
                new List<string> { "-m", "pip", "install", "--disable-pip-version-check", "--upgrade", "pip" }));
            env.steps.Enqueue(("ライブラリを導入中", venvPy,
                new List<string> { "-m", "pip", "install", "--disable-pip-version-check", "-r", req }));
            env.steps.Enqueue(("動作を確認中", venvPy, new List<string> { "-c", ImportCheck }));
            env.Log.AppendLine($"元にする Python: {basePy}");
            env.Next();
            return env;
        }

        /// <summary>Quick synchronous check that the configured interpreter can run the compiler.</summary>
        public static bool IsReady(out string message)
        {
            var py = PdfUnitySettings.PythonExe;
            if (Path.IsPathRooted(py) && !File.Exists(py))
            {
                message = "変換環境はまだ準備されていません。";
                return false;
            }
            var (code, outp) = RunSync(py, new List<string> { "-c", ImportCheck }, 60000);
            message = code == 0 ? "変換環境は準備できています。" : $"変換環境が不完全です: {Last(outp)}";
            return code == 0;
        }

        public static string FindBasePython(out string report)
        {
            var sb = new StringBuilder();
            var candidates = new List<(string exe, List<string> pre)>();
            if (!string.IsNullOrEmpty(PdfUnitySettings.BasePythonExe))
                candidates.Add((PdfUnitySettings.BasePythonExe, new List<string>()));
            candidates.Add(("py", new List<string> { $"-{MinMajor}" }));
            candidates.Add(("python", new List<string>()));
            candidates.Add(("python3", new List<string>()));
            foreach (var (exe, pre) in candidates)
            {
                var args = new List<string>(pre)
                {
                    "-c",
                    $"import sys; print(sys.executable if sys.version_info >= ({MinMajor}, {MinMinor}) else 'OLD ' + sys.version)",
                };
                var (code, outp) = RunSync(exe, args, 20000);
                var line = Last(outp);
                sb.AppendLine($"{exe}: {(code == 0 ? line : "見つかりません")}");
                if (code == 0 && !line.StartsWith("OLD") && File.Exists(line)) { report = sb.ToString(); return line; }
            }
            report = sb.ToString();
            return null;
        }

        static string Last(string s)
        {
            var lines = (s ?? "").Trim().Split('\n');
            return lines[lines.Length - 1].Trim();
        }

        static (int code, string output) RunSync(string exe, List<string> args, int timeoutMs)
        {
            try
            {
                using (var p = Process.Start(MakeStartInfo(exe, args)))
                {
                    var o = p.StandardOutput.ReadToEndAsync();
                    var e = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (InvalidOperationException) { } return (-1, "timeout"); }
                    return (p.ExitCode, o.Result + e.Result);
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                return (-1, ex.Message);
            }
        }

        static ProcessStartInfo MakeStartInfo(string exe, List<string> args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = CompilerProcess.JoinArgs(args),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["PYTHONNOUSERSITE"] = "1";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONUTF8"] = "1";
            psi.EnvironmentVariables.Remove("PYTHONPATH");
            psi.EnvironmentVariables.Remove("PYTHONHOME");
            return psi;
        }

        void Next()
        {
            if (steps.Count == 0)
            {
                Running = false;
                Succeeded = true;
                Step = "完了";
                return;
            }
            var (label, exe, args) = steps.Dequeue();
            Step = label;
            Log.AppendLine($"> {label}: {exe} {CompilerProcess.JoinArgs(args)}");
            current = new Process { StartInfo = MakeStartInfo(exe, args), EnableRaisingEvents = true };
            current.OutputDataReceived += (_, e) => { if (e.Data != null) output.Enqueue(e.Data); };
            current.ErrorDataReceived += (_, e) => { if (e.Data != null) output.Enqueue(e.Data); };
            try
            {
                current.Start();
                current.BeginOutputReadLine();
                current.BeginErrorReadLine();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                Log.AppendLine(ex.Message);
                Running = false;
            }
        }

        /// <summary>Returns true while setup is still running.</summary>
        public bool Tick()
        {
            while (output.TryDequeue(out var l)) Log.AppendLine(l);
            if (!Running) return false;
            if (current != null && current.HasExited)
            {
                current.WaitForExit();
                while (output.TryDequeue(out var l)) Log.AppendLine(l);
                var code = current.ExitCode;
                current.Dispose();
                current = null;
                if (code != 0)
                {
                    Log.AppendLine($"{Step}に失敗しました（終了コード {code}）。");
                    Running = false;
                    return false;
                }
                Next();
            }
            return Running;
        }

        public void Dispose()
        {
            try { if (current != null && !current.HasExited) current.Kill(); }
            catch (InvalidOperationException) { }
            current?.Dispose();
            Running = false;
        }
    }
}
