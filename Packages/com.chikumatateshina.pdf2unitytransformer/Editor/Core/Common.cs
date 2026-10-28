using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PDF2Unity.Core
{
    /// <summary>Fatal error: the document (or the requested mode) cannot be compiled.</summary>
    public sealed class CompileException : Exception
    {
        public readonly string Code;
        public CompileException(string code, string message) : base(message) { Code = code; }
    }

    /// <summary>A construct that cannot be drawn natively; becomes a precomposited region.</summary>
    public sealed class UnsupportedException : Exception
    {
        public readonly string Code;
        public UnsupportedException(string code, string message) : base(message) { Code = code; }
    }

    public sealed class Limits
    {
        public long MaxInputBytes = 1024L * 1024 * 1024;
        public int MaxPages = 2000;
        public long MaxStreamBytes = 512L * 1024 * 1024;
        public long MaxImagePixels = 200_000_000;
        public int MaxFormDepth = 32;
        public int MaxOpsPerPage = 5_000_000;
        public double TimeoutSec = 3600;
    }

    public enum Severity { Info, Warning, Error }

    public sealed class Diagnostic
    {
        public string Code;
        public Severity Severity;
        public string Message;
        public int Page; // 1-based, 0 = document
        public string Fallback;
        public int Occurrences = 1;
    }

    /// <summary>Thread-safe diagnostic collector; identical messages are counted.</summary>
    public sealed class Diagnostics
    {
        readonly List<Diagnostic> items = new List<Diagnostic>();
        readonly Dictionary<string, Diagnostic> seen = new Dictionary<string, Diagnostic>();
        readonly object gate = new object();

        public void Add(string code, Severity sev, string message, int page = 0, string fallback = null)
        {
            lock (gate)
            {
                var key = $"{code}|{sev}|{message}|{page}|{fallback}";
                if (seen.TryGetValue(key, out var d)) { d.Occurrences++; return; }
                d = new Diagnostic { Code = code, Severity = sev, Message = message, Page = page, Fallback = fallback };
                seen[key] = d;
                items.Add(d);
            }
        }

        public void Info(string code, string msg, int page = 0, string fallback = null) => Add(code, Severity.Info, msg, page, fallback);
        public void Warn(string code, string msg, int page = 0, string fallback = null) => Add(code, Severity.Warning, msg, page, fallback);
        public void Error(string code, string msg, int page = 0) => Add(code, Severity.Error, msg, page);

        public List<Diagnostic> Items { get { lock (gate) return new List<Diagnostic>(items); } }

        public int Count(Severity s)
        {
            lock (gate)
            {
                int n = 0;
                foreach (var d in items) if (d.Severity == s) n++;
                return n;
            }
        }

        public bool Has(string code)
        {
            lock (gate)
            {
                foreach (var d in items) if (d.Code == code) return true;
                return false;
            }
        }
    }

    /// <summary>Progress / cancellation / deadline shared by all stages.</summary>
    public sealed class Job
    {
        public readonly CancellationToken Token;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly double timeout;
        public volatile string Stage = "";
        public volatile float Progress;
        public volatile string Detail = "";

        public Job(CancellationToken token, double timeoutSec)
        {
            Token = token;
            timeout = timeoutSec;
        }

        public void Check()
        {
            Token.ThrowIfCancellationRequested();
            if (clock.Elapsed.TotalSeconds > timeout)
                throw new CompileException("limit.timeout", $"compile exceeded {timeout:0}s");
        }

        public void Report(string stage, float progress, string detail = "")
        {
            Stage = stage;
            Progress = progress;
            Detail = detail;
            Check();
        }
    }
}
