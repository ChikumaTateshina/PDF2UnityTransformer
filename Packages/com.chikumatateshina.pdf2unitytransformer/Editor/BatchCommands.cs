using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PDF2Unity.Editor
{
    /// <summary>
    /// Command line entry points for CI / regression (run without -nographics so capture works):
    ///   Unity -batchmode -quit -projectPath P -executeMethod PDF2Unity.Editor.BatchCommands.ImportAndCapture
    ///         -p2uPackage &lt;dir&gt; -p2uTarget pc -p2uCaptureDir &lt;dir&gt; -p2uDpi 110
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

        /// <summary>Creates/updates the per-user compiler environment (same as the window's Set up button).</summary>
        public static void SetupEnvironment()
        {
            int code = 0;
            try
            {
                using (var env = PythonEnvironment.Setup())
                {
                    while (env.Tick()) System.Threading.Thread.Sleep(200);
                    Debug.Log("[PDF2UnityTransformer] " + env.Log);
                    code = env.Succeeded ? 0 : 1;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[PDF2UnityTransformer] " + e.Message);
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
                var target = Arg("-p2uTarget", "pc");
                var captureDir = Arg("-p2uCaptureDir");
                var dpi = float.Parse(Arg("-p2uDpi", "110"), System.Globalization.CultureInfo.InvariantCulture);
                var pkg = new ExchangePackage(pkgDir);
                var rep = AssetGenerator.Generate(pkg, target, new GenerateOptions { OverwriteUserEdits = Arg("-p2uForce") == "1" });
                Debug.Log($"[PDF2UnityTransformer] generated {rep.Written.Count} asset(s) in {rep.Folder}");
                foreach (var m in rep.Messages) Debug.LogWarning("[PDF2UnityTransformer] " + m);
                if (!string.IsNullOrEmpty(captureDir))
                {
                    foreach (var n in pkg.PageNumbers())
                    {
                        var page = pkg.LoadPage(n, target);
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{rep.Folder}/Pages/Page_{n:D4}.prefab");
                        // Same pixel size as the reference: display size in points * dpi / 72.
                        float ptW = page.UnitySize.x * 72f, ptH = page.UnitySize.y * 72f;
                        int w = Mathf.Max(1, Mathf.RoundToInt(ptW * dpi / 72f));
                        int h = Mathf.Max(1, Mathf.RoundToInt(ptH * dpi / 72f));
                        var outPng = Path.Combine(captureDir, $"page-{n:D4}.png");
                        CaptureTool.CapturePage(prefab, page.UnitySize, w, h, outPng);
                        Debug.Log($"[PDF2UnityTransformer] captured {outPng} ({w}x{h})");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[PDF2UnityTransformer] " + e);
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }
    }
}
