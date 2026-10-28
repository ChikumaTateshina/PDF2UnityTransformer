using System;
using System.Threading;
using System.Threading.Tasks;
using PDF2Unity.Core;

namespace PDF2Unity.Editor
{
    /// <summary>
    /// Runs the C# compiler on a worker thread (no external process). The compiler core does not touch
    /// Unity APIs; asset generation happens afterwards on the main thread. Poll <see cref="Tick"/>.
    /// </summary>
    public sealed class ConversionRunner : IDisposable
    {
        public enum State { Running, Succeeded, Failed, Cancelled }

        readonly CancellationTokenSource cts = new CancellationTokenSource();
        readonly Task<CompileResult> task;
        public readonly Job Job;
        public readonly CompileOptions Options;

        public State Current { get; private set; } = State.Running;
        public CompileResult Result { get; private set; }
        public string ErrorCode { get; private set; }
        public string ErrorMessage { get; private set; }

        public ConversionRunner(CompileOptions options)
        {
            Options = options;
            Job = new Job(cts.Token, options.Limits.TimeoutSec);
            task = Task.Run(() => Compiler.Compile(options, Job), cts.Token);
        }

        /// <summary>Returns true while the conversion is still running.</summary>
        public bool Tick()
        {
            if (Current != State.Running) return false;
            if (!task.IsCompleted) return true;
            if (task.IsCanceled) Current = State.Cancelled;
            else if (task.IsFaulted)
            {
                var e = task.Exception?.GetBaseException();
                if (e is OperationCanceledException) Current = State.Cancelled;
                else
                {
                    Current = State.Failed;
                    if (e is CompileException ce) { ErrorCode = ce.Code; ErrorMessage = ce.Message; }
                    else { ErrorCode = "internal"; ErrorMessage = e?.ToString(); }
                }
            }
            else
            {
                Result = task.Result;
                Current = State.Succeeded;
            }
            return false;
        }

        public void Cancel()
        {
            if (Current == State.Running) cts.Cancel();
        }

        public void Dispose()
        {
            cts.Cancel();
            cts.Dispose();
        }
    }
}
