using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace PDF2Unity.Editor
{
    /// <summary>
    /// Editor UI (FR-01, SPEC §8): choose PDF, pages, mode, targets and profiles, run the
    /// external compiler, review diagnostics and generate prefabs.
    /// </summary>
    public sealed class PdfCompilerWindow : EditorWindow
    {
        static readonly string[] Modes = { "hybrid", "strict", "page-fallback" };
        static readonly string[] ModeLabels =
        {
            "Hybrid (default): vectors/text/images native, complex effects precomposited per region",
            "Strict: fail if anything cannot be reproduced natively",
            "Page fallback: rasterise whole pages that need precomposition (explicit opt-in)",
        };

        [SerializeField] string pdfPath = "";
        [SerializeField] string pages = "all";
        [SerializeField] int modeIndex;
        [SerializeField] bool targetPc = true;
        [SerializeField] bool targetQuest;
        [SerializeField] string profilePc = "pc-standard";
        [SerializeField] string profileQuest = "quest-standard";
        [SerializeField] string cmykProfile = "";
        [SerializeField] string substituteFonts = "";
        [SerializeField] bool fontRightsDeclared;
        [SerializeField] float worldScale = 1f;
        [SerializeField] bool overwriteUserEdits;
        [SerializeField] Color paperColor = Color.white;
        [SerializeField] bool advanced;

        CompilerProcess running;
        string packageDir;
        ExchangePackage loaded;
        Vector2 scroll, logScroll;
        string lastError;
        readonly List<string> generatedMessages = new List<string>();

        PythonEnvironment envSetup;
        string envStatus;
        bool envReady;

        [MenuItem(PdfUnitySettings.MenuRoot + "PDF Compiler")]
        public static void Open() => GetWindow<PdfCompilerWindow>(PdfUnitySettings.ProductName);

        void OnEnable() => RefreshEnvironment();

        void OnDisable()
        {
            running?.Dispose();
            running = null;
            envSetup?.Dispose();
            envSetup = null;
        }

        void RefreshEnvironment() => envReady = PythonEnvironment.IsReady(out envStatus);

        void Update()
        {
            if (envSetup != null)
            {
                if (!envSetup.Tick())
                {
                    if (!envSetup.Succeeded)
                    {
                        lastError = $"Environment setup failed at '{envSetup.Step}'. See the Console for details.";
                        Debug.LogError($"[{PdfUnitySettings.ProductName}] {lastError}\n{envSetup.Log}");
                    }
                    envSetup.Dispose();
                    envSetup = null;
                    RefreshEnvironment();
                }
                Repaint();
            }
            if (running == null) return;
            if (!running.Tick()) OnFinished();
            Repaint();
        }

        void DrawEnvironment()
        {
            EditorGUILayout.LabelField("Compiler environment (Python)", EditorStyles.boldLabel);
            if (envSetup != null)
            {
                var r = EditorGUILayout.GetControlRect(false, 20);
                EditorGUI.ProgressBar(r, 0.5f, envSetup.Step + "…");
                return;
            }
            EditorGUILayout.HelpBox(envStatus + (envReady ? "" :
                $"\nPress 'Set up' to create a private Python environment in {PdfUnitySettings.EnvironmentDir} " +
                $"and install the compiler dependencies from PyPI (requires Python {PythonEnvironment.MinMajor}.{PythonEnvironment.MinMinor}+)."),
                envReady ? MessageType.Info : MessageType.Warning);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(envReady ? "Reinstall / update" : "Set up"))
                {
                    lastError = null;
                    try { envSetup = PythonEnvironment.Setup(); }
                    catch (Exception e) { lastError = e.Message; }
                }
                if (GUILayout.Button("Check", GUILayout.Width(80))) RefreshEnvironment();
            }
        }

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                pdfPath = EditorGUILayout.TextField("PDF", pdfPath);
                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    var p = EditorUtility.OpenFilePanel("Select PDF", Path.GetDirectoryName(pdfPath) ?? "", "pdf");
                    if (!string.IsNullOrEmpty(p)) pdfPath = p;
                }
            }
            pages = EditorGUILayout.TextField(new GUIContent("Pages", "'all' or e.g. 1-3,5"), pages);
            modeIndex = EditorGUILayout.Popup("Mode", modeIndex, Modes);
            EditorGUILayout.HelpBox(ModeLabels[modeIndex], MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Targets", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                targetPc = EditorGUILayout.ToggleLeft("PC (Windows)", targetPc, GUILayout.Width(120));
                profilePc = EditorGUILayout.TextField(profilePc);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                targetQuest = EditorGUILayout.ToggleLeft("Quest (Android)", targetQuest, GUILayout.Width(120));
                profileQuest = EditorGUILayout.TextField(profileQuest);
            }
            worldScale = EditorGUILayout.FloatField(new GUIContent("Prefab scale", "72 pt = 1 unit at scale 1; 0.0254 = physical size in metres"), worldScale);
            paperColor = EditorGUILayout.ColorField("Paper colour", paperColor);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Colour / fonts / rights", EditorStyles.boldLabel);
            cmykProfile = PathField("CMYK ICC profile (optional)", cmykProfile, "icc,icm", false);
            substituteFonts = PathField("Substitute fonts (dir or json)", substituteFonts, "", true);
            fontRightsDeclared = EditorGUILayout.ToggleLeft(
                "I hold the rights to use the embedded fonts' outlines in this world", fontRightsDeclared);
            EditorGUILayout.HelpBox(
                "The compiler never checks whether you may publish the PDF, its images or fonts. " +
                "Fonts whose embedding permission forbids outline use stop the compile unless you declare the rights " +
                "or provide a licensed substitute.", MessageType.Info);
            overwriteUserEdits = EditorGUILayout.ToggleLeft("Overwrite generated assets I edited manually", overwriteUserEdits);

            EditorGUILayout.Space();
            DrawEnvironment();
            advanced = EditorGUILayout.Foldout(advanced, "Advanced");
            if (advanced)
            {
                PdfUnitySettings.BasePythonExe = EditorGUILayout.TextField(new GUIContent("Base Python", "System python.exe used to create the environment (empty = auto)"), PdfUnitySettings.BasePythonExe);
                PdfUnitySettings.PythonExe = EditorGUILayout.TextField("Compiler Python", PdfUnitySettings.PythonExe);
                PdfUnitySettings.CompilerDir = EditorGUILayout.TextField("Compiler dir", PdfUnitySettings.CompilerDir);
                PdfUnitySettings.TimeoutSec = EditorGUILayout.IntField("Timeout (s)", PdfUnitySettings.TimeoutSec);
            }

            EditorGUILayout.Space();
            if (running != null)
            {
                var r = EditorGUILayout.GetControlRect(false, 20);
                EditorGUI.ProgressBar(r, running.Progress01, $"{running.Stage} {running.Progress01 * 100:0}%");
                if (GUILayout.Button("Cancel")) { running.Cancel(); OnFinished(); }
            }
            else
            {
                using (new EditorGUI.DisabledScope(!envReady || envSetup != null || !File.Exists(pdfPath) || !(targetPc || targetQuest)))
                    if (GUILayout.Button("Compile", GUILayout.Height(28))) StartCompile();
                if (GUILayout.Button("Load existing package…"))
                {
                    var d = EditorUtility.OpenFolderPanel("Asset Exchange Package", PdfUnitySettings.CacheRoot, "");
                    if (!string.IsNullOrEmpty(d)) LoadPackage(d);
                }
            }
            if (!string.IsNullOrEmpty(lastError)) EditorGUILayout.HelpBox(lastError, MessageType.Error);
            if (loaded != null) DrawPackage();
            foreach (var m in generatedMessages) EditorGUILayout.HelpBox(m, MessageType.Warning);
            if (running != null || loaded != null)
            {
                EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
                logScroll = EditorGUILayout.BeginScrollView(logScroll, GUILayout.Height(120));
                EditorGUILayout.TextArea(running != null ? running.Log.ToString() : "", EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndScrollView();
            }
            EditorGUILayout.EndScrollView();
        }

        static string PathField(string label, string value, string ext, bool folderAllowed)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                value = EditorGUILayout.TextField(label, value);
                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    var p = folderAllowed && EditorUtility.DisplayDialog(label, "Select a folder or a file?", "Folder", "File")
                        ? EditorUtility.OpenFolderPanel(label, "", "")
                        : EditorUtility.OpenFilePanel(label, "", ext);
                    if (!string.IsNullOrEmpty(p)) value = p;
                }
            }
            return value;
        }

        void StartCompile()
        {
            lastError = null;
            loaded = null;
            generatedMessages.Clear();
            var name = AssetGenerator.SafeName(Path.GetFileNameWithoutExtension(pdfPath));
            packageDir = Path.GetFullPath(Path.Combine(PdfUnitySettings.CacheRoot, name));
            Directory.CreateDirectory(Path.GetDirectoryName(packageDir));
            var req = new CompileRequest
            {
                InputPdf = Path.GetFullPath(pdfPath),
                OutputDir = packageDir,
                Pages = pages,
                Mode = Modes[modeIndex],
                Targets = new List<string>(),
                ProfilePc = profilePc,
                ProfileQuest = profileQuest,
                CmykProfile = string.IsNullOrWhiteSpace(cmykProfile) ? null : cmykProfile,
                SubstituteFonts = string.IsNullOrWhiteSpace(substituteFonts) ? null : substituteFonts,
                FontRightsDeclared = fontRightsDeclared,
                WorldScale = worldScale,
                TimeoutSec = PdfUnitySettings.TimeoutSec,
            };
            if (targetPc) req.Targets.Add("pc");
            if (targetQuest) req.Targets.Add("quest");
            try { running = new CompilerProcess(req); }
            catch (Exception e) { lastError = $"Could not start the compiler: {e.Message}"; running = null; }
        }

        void OnFinished()
        {
            var r = running;
            running = null;
            if (r == null) return;
            switch (r.Current)
            {
                case CompilerProcess.State.Succeeded:
                    LoadPackage(packageDir);
                    break;
                case CompilerProcess.State.Cancelled:
                    lastError = "Compile cancelled.";
                    break;
                default:
                    lastError = $"Compile failed [{r.ErrorCode}]: {r.ErrorMessage}";
                    Debug.LogError($"[{PdfUnitySettings.ProductName}] " + lastError + "\n" + r.Log);
                    break;
            }
            r.Dispose();
        }

        void LoadPackage(string dir)
        {
            try
            {
                loaded = new ExchangePackage(dir);
                packageDir = dir;
            }
            catch (Exception e)
            {
                loaded = null;
                lastError = $"Invalid package: {e.Message}";
            }
        }

        void DrawPackage()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Package: {loaded.SourceName}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Source", loaded.SourceHash, EditorStyles.miniLabel);
            foreach (var t in loaded.Targets.Values)
            {
                var type = t.Status == "pass" ? MessageType.Info : t.Status == "fail" ? MessageType.Error : MessageType.Warning;
                var detail = t.Status == "fail" ? string.Join("\n", t.BudgetFailures)
                    : t.ReviewReasons.Count > 0 ? "Review: " + string.Join(", ", t.ReviewReasons) : "";
                var label = t.Name == "quest" && t.Status != "fail"
                    ? "Quest output generated; mark it supported only after an on-device test."
                    : t.Name == "quest" ? "Not supported on Quest: keep this document PC-only." : "";
                EditorGUILayout.HelpBox($"{t.Name.ToUpperInvariant()}: {t.Status} ({t.Profile})\n{detail}\n{label}".Trim(), type);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(t.Status == "fail"))
                        if (GUILayout.Button($"Generate {t.Name.ToUpperInvariant()} prefabs")) Generate(t.Name);
                    var folder = AssetGenerator.FolderFor(loaded, t.Name);
                    var doc = AssetDatabase.LoadAssetAtPath<GameObject>($"{folder}/Document.prefab");
                    using (new EditorGUI.DisabledScope(doc == null))
                        if (GUILayout.Button("Place in scene"))
                        {
                            var inst = (GameObject)PrefabUtility.InstantiatePrefab(doc);
                            Undo.RegisterCreatedObjectUndo(inst, "Place PDF document");
                            Selection.activeGameObject = inst;
                        }
                    if (t.Name == "quest" && doc != null)
                    {
                        var rec = AssetGenerator.Record.Load(folder);
                        var v = GUILayout.Toggle(rec.DeviceVerified, "Device test passed", "Button");
                        if (v != rec.DeviceVerified) { rec.DeviceVerified = v; rec.Save(folder); }
                    }
                }
            }
            var msgs = J.Arr(loaded.Diagnostics, "messages");
            if (msgs != null && msgs.Count > 0)
            {
                EditorGUILayout.LabelField($"Diagnostics ({msgs.Count})", EditorStyles.boldLabel);
                foreach (var m in msgs.Take(200))
                {
                    var sev = J.Str(m, "severity");
                    var page = J.Num(m, "page", 0);
                    var fb = J.Str(m, "fallback");
                    var text = $"{(page > 0 ? $"p{page}: " : "")}[{J.Str(m, "code")}] {J.Str(m, "message")}" +
                               (fb != null ? $" -> {fb}" : "") +
                               (J.Num(m, "occurrences", 1) > 1 ? $" (x{J.Num(m, "occurrences")})" : "");
                    EditorGUILayout.HelpBox(text, sev == "error" ? MessageType.Error : sev == "warning" ? MessageType.Warning : MessageType.None);
                }
            }
            if (GUILayout.Button("Open package folder")) EditorUtility.RevealInFinder(Path.Combine(packageDir, "manifest.json"));
        }

        void Generate(string target)
        {
            generatedMessages.Clear();
            try
            {
                var rep = AssetGenerator.Generate(loaded, target, new GenerateOptions
                {
                    OverwriteUserEdits = overwriteUserEdits,
                    PaperColor = paperColor,
                });
                generatedMessages.AddRange(rep.Messages);
                Debug.Log($"[{PdfUnitySettings.ProductName}] {target}: wrote {rep.Written.Count}, removed {rep.Removed.Count}, " +
                          $"kept {rep.SkippedUserEdited.Count} user-edited asset(s) in {rep.Folder}");
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(rep.DocumentPrefab));
            }
            catch (Exception e)
            {
                lastError = $"Generation failed: {e.Message}";
                Debug.LogException(e);
            }
        }
    }
}
