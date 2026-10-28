using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using PDF2Unity.Core;
using PDF2Unity.Core.Geometry;
using UnityEditor;
using UnityEngine;

namespace PDF2Unity.Editor
{
    /// <summary>
    /// Command line entry points for CI / regression (run without -nographics so capture works):
    ///   Unity -batchmode -quit -projectPath P -executeMethod PDF2Unity.Editor.BatchCommands.ConvertAndCapture
    ///         -p2uPdf &lt;file.pdf&gt; [-p2uTargets pc,quest] [-p2uOut Assets/...] [-p2uPackage &lt;dir&gt;]
    ///         [-p2uCaptureDir &lt;dir&gt;] [-p2uDpi 110] [-p2uForce 1]
    ///   ... -executeMethod PDF2Unity.Editor.BatchCommands.ImportAndCapture -p2uPackage &lt;dir&gt; -p2uTarget pc ...
    /// </summary>
    public static class BatchCommands
    {
        static string Arg(string name, string def = null)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return def;
        }

        static float Dpi => float.Parse(Arg("-p2uDpi", "110"), System.Globalization.CultureInfo.InvariantCulture);

        public static void ConvertAndCapture()
        {
            int code = 0;
            try
            {
                var pdf = Arg("-p2uPdf") ?? throw new ArgumentException("-p2uPdf required");
                var targets = new List<Profile>();
                foreach (var t in Arg("-p2uTargets", "pc").Split(',')) targets.Add(t.Trim() == "quest" ? Profile.QuestStandard() : Profile.PcStandard());
                var name = AssetGenerator.SafeName(Path.GetFileNameWithoutExtension(pdf));
                var pkgDir = Path.GetFullPath(Arg("-p2uPackage", Path.Combine(PdfUnitySettings.CacheRoot, name)));
                var opts = new CompileOptions
                {
                    Input = Path.GetFullPath(pdf), Output = pkgDir, Targets = targets,
                    CacheDir = Path.GetFullPath(PdfUnitySettings.ToolCache), ReferenceDpi = Dpi,
                };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var res = Compiler.Compile(opts, new Job(CancellationToken.None, 3600));
                Debug.Log($"[PDF2UnityTransformer] converted {pdf} in {sw.Elapsed.TotalSeconds:0.0}s: {string.Join(", ", res.TargetStatus)} (warnings {res.Warnings})");
                var pkg = new ExchangePackage(pkgDir);
                foreach (var t in targets)
                {
                    if (pkg.Targets.TryGetValue(t.Target, out var info) && info.Status == "fail")
                    {
                        Debug.LogWarning($"[PDF2UnityTransformer] {t.Target}: not generated ({string.Join("; ", info.BudgetFailures)})");
                        continue;
                    }
                    GenerateAndCapture(pkg, t.Target, Arg("-p2uCaptureDir") is string cd ? Path.Combine(cd, t.Target) : null);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[PDF2UnityTransformer] " + e);
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        public static void ImportAndCapture()
        {
            int code = 0;
            try
            {
                var pkgDir = Arg("-p2uPackage") ?? throw new ArgumentException("-p2uPackage required");
                GenerateAndCapture(new ExchangePackage(pkgDir), Arg("-p2uTarget", "pc"), Arg("-p2uCaptureDir"));
            }
            catch (Exception e)
            {
                Debug.LogError("[PDF2UnityTransformer] " + e);
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        static void GenerateAndCapture(ExchangePackage pkg, string target, string captureDir)
        {
            var rep = AssetGenerator.Generate(pkg, target, new GenerateOptions
            {
                OverwriteUserEdits = Arg("-p2uForce") == "1",
                OutputRoot = Arg("-p2uOut", PdfUnitySettings.DefaultOutputFolder),
            });
            Debug.Log($"[PDF2UnityTransformer] generated {rep.Written.Count} asset(s) in {rep.Folder}");
            foreach (var m in rep.Messages) Debug.LogWarning("[PDF2UnityTransformer] " + m);
            if (string.IsNullOrEmpty(captureDir)) return;
            foreach (var n in pkg.PageNumbers())
            {
                var page = pkg.LoadPage(n, target);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{rep.Folder}/Pages/Page_{n:D4}.prefab");
                float ptW = page.UnitySize.x * 72f, ptH = page.UnitySize.y * 72f;
                int w = Mathf.Max(1, Mathf.RoundToInt(ptW * Dpi / 72f));
                int h = Mathf.Max(1, Mathf.RoundToInt(ptH * Dpi / 72f));
                var outPng = Path.Combine(captureDir, $"page-{n:D4}.png");
                CaptureTool.CapturePage(prefab, page.UnitySize, w, h, outPng);
                Debug.Log($"[PDF2UnityTransformer] captured {outPng} ({w}x{h})");
            }
        }
    }
}
