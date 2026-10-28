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
        static readonly GUIContent[] ModeNames =
        {
            new GUIContent("ハイブリッド（既定）"),
            new GUIContent("厳密"),
            new GUIContent("ページ画像化"),
        };
        static readonly string[] ModeHelp =
        {
            "文字・図形・画像はそのまま変換し、直接描けない効果だけをその領域で画像化します。",
            "直接再現できない描画が 1 つでもあれば変換を中止します。",
            "画像化が必要なページはページ全体を画像にします（ベクター表示は失われます）。",
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
        [SerializeField] bool precomposeTranslucent;
        [SerializeField] float worldScale = 1f;
        [SerializeField] bool overwriteUserEdits;
        [SerializeField] Color paperColor = Color.white;
        [SerializeField] bool advanced;
        [SerializeField] bool showInfo;

        CompilerProcess running;
        string packageDir;
        ExchangePackage loaded;
        Vector2 scroll, logScroll;
        string lastError;
        readonly List<string> generatedMessages = new List<string>();

        PythonEnvironment envSetup;
        string envStatus;
        bool envReady;

        [MenuItem(PdfUnitySettings.MenuRoot + "PDF 変換")]
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
                        lastError = $"Python 環境の準備に失敗しました（{envSetup.Step}）。詳細はコンソールを確認してください。";
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

        // ------------------------------------------------------------------------------------
        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            Section("1. PDF");
            PdfField();
            pages = EditorGUILayout.TextField(new GUIContent("ページ範囲", "all（すべて）または 1-3,5 のように指定"), pages);
            modeIndex = EditorGUILayout.Popup(new GUIContent("変換モード"), modeIndex, ModeNames);
            EditorGUILayout.HelpBox(ModeHelp[modeIndex], MessageType.None);

            Section("2. 出力先");
            using (new EditorGUILayout.HorizontalScope())
            {
                targetPc = EditorGUILayout.ToggleLeft("PC（Windows）", targetPc, GUILayout.Width(130));
                using (new EditorGUI.DisabledScope(!targetPc))
                    profilePc = EditorGUILayout.TextField(new GUIContent("", "品質プロファイル（pc-standard / pc-high）"), profilePc);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                targetQuest = EditorGUILayout.ToggleLeft("Quest（Android）", targetQuest, GUILayout.Width(130));
                using (new EditorGUI.DisabledScope(!targetQuest))
                    profileQuest = EditorGUILayout.TextField(new GUIContent("", "品質プロファイル（quest-standard）"), profileQuest);
            }
            worldScale = EditorGUILayout.FloatField(
                new GUIContent("Prefab の大きさ", "1 のとき 72pt = 1 ユニット。0.0254 で実寸（メートル）になります。"), worldScale);
            paperColor = EditorGUILayout.ColorField("紙の色", paperColor);

            Section("3. 色・フォント・権利");
            cmykProfile = PathField("CMYK の ICC プロファイル", "任意。指定すると CMYK をこのプロファイルで変換します。",
                                    cmykProfile, new[] { "ICC プロファイル", "icc,icm" }, false);
            substituteFonts = PathField("代替フォント", "任意。利用許諾のある .ttf/.otf を入れたフォルダ、または substitutes.json",
                                        substituteFonts, new[] { "JSON", "json" }, true);
            precomposeTranslucent = EditorGUILayout.ToggleLeft(
                new GUIContent("半透明を画像化する", "Linear 色空間では半透明の重なりの色が PDF とわずかに異なります。オンにすると該当領域を画像化して一致させます。"),
                precomposeTranslucent);
            fontRightsDeclared = EditorGUILayout.ToggleLeft("埋め込みフォントの輪郭をワールドで使う権利を持っている", fontRightsDeclared);
            EditorGUILayout.HelpBox(
                "このツールは、PDF やその画像・フォントを公開してよいかを判断しません。権利は必ずご自身で確認してください。" +
                "利用が制限されたフォントが埋め込まれている場合は、権利を宣言するか代替フォントを指定しない限り変換を中止します。",
                MessageType.Info);
            overwriteUserEdits = EditorGUILayout.ToggleLeft("手で編集した生成アセットも上書きする", overwriteUserEdits);

            Section("4. 変換環境（Python）");
            DrawEnvironment();

            EditorGUILayout.Space(8);
            if (running != null)
            {
                var r = EditorGUILayout.GetControlRect(false, 22);
                EditorGUI.ProgressBar(r, running.Progress01, $"{L.Stage(running.Stage)}  {running.Progress01 * 100:0}%");
                if (GUILayout.Button("中止")) { running.Cancel(); OnFinished(); }
            }
            else
            {
                var reason = CompileBlocker();
                using (new EditorGUI.DisabledScope(reason != null))
                    if (GUILayout.Button("変換する", GUILayout.Height(30))) StartCompile();
                if (reason != null) EditorGUILayout.LabelField(reason, EditorStyles.miniLabel);
                if (GUILayout.Button("変換済みのデータを開く…"))
                {
                    var d = EditorUtility.OpenFolderPanel("変換済みデータのフォルダを選択", PathUtil.InitialDirectory(PdfUnitySettings.CacheRoot), "");
                    if (!string.IsNullOrEmpty(d)) LoadPackage(d);
                    GUIUtility.ExitGUI();
                }
            }
            if (!string.IsNullOrEmpty(lastError)) EditorGUILayout.HelpBox(lastError, MessageType.Error);
            if (loaded != null) DrawPackage();
            foreach (var m in generatedMessages) EditorGUILayout.HelpBox(m, MessageType.Warning);
            if (running != null)
            {
                EditorGUILayout.LabelField("ログ", EditorStyles.boldLabel);
                logScroll = EditorGUILayout.BeginScrollView(logScroll, GUILayout.Height(120));
                EditorGUILayout.TextArea(running.Log.ToString(), EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndScrollView();
            }

            advanced = EditorGUILayout.Foldout(advanced, "詳細設定", true);
            if (advanced)
            {
                PdfUnitySettings.BasePythonExe = EditorGUILayout.TextField(
                    new GUIContent("元にする Python", "環境の作成に使う python.exe（空欄なら自動検出）"), PdfUnitySettings.BasePythonExe);
                PdfUnitySettings.PythonExe = EditorGUILayout.TextField(
                    new GUIContent("変換に使う Python", "通常は自動で作成した環境を使います"), PdfUnitySettings.PythonExe);
                PdfUnitySettings.CompilerDir = EditorGUILayout.TextField(
                    new GUIContent("コンパイラのフォルダ", "通常はパッケージ同梱の Compiler~"), PdfUnitySettings.CompilerDir);
                PdfUnitySettings.TimeoutSec = EditorGUILayout.IntField("制限時間（秒）", PdfUnitySettings.TimeoutSec);
            }
            EditorGUILayout.EndScrollView();
        }

        static void Section(string title)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        string CompileBlocker()
        {
            if (envSetup != null) return "Python 環境を準備しています…";
            if (!envReady) return "先に「4. 変換環境」で準備を行ってください。";
            if (string.IsNullOrWhiteSpace(pdfPath)) return "PDF ファイルを選んでください。";
            if (!File.Exists(PathUtil.Clean(pdfPath))) return "PDF ファイルが見つかりません。";
            if (!(targetPc || targetQuest)) return "出力先を 1 つ以上選んでください。";
            return null;
        }

        void PdfField()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                pdfPath = EditorGUILayout.TextField(new GUIContent("PDF ファイル", "ここに PDF をドラッグ＆ドロップすることもできます"), pdfPath);
                var fieldRect = GUILayoutUtility.GetLastRect();
                HandleDrop(fieldRect);
                if (GUILayout.Button("参照…", GUILayout.Width(64)))
                {
                    var p = EditorUtility.OpenFilePanelWithFilters("PDF ファイルを選択", PathUtil.InitialDirectory(pdfPath),
                                                                    new[] { "PDF ファイル", "pdf", "すべてのファイル", "*" });
                    if (!string.IsNullOrEmpty(p))
                    {
                        pdfPath = p;
                        GUI.FocusControl(null);   // otherwise a focused text field keeps its old text
                    }
                    GUIUtility.ExitGUI();
                }
            }
        }

        void HandleDrop(Rect rect)
        {
            var e = Event.current;
            if ((e.type != EventType.DragUpdated && e.type != EventType.DragPerform) || !rect.Contains(e.mousePosition)) return;
            var pdf = DragAndDrop.paths?.FirstOrDefault(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
            if (pdf == null) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                pdfPath = Path.GetFullPath(pdf);
                GUI.FocusControl(null);
            }
            e.Use();
        }

        static string PathField(string label, string tooltip, string value, string[] filters, bool folderAllowed)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                value = EditorGUILayout.TextField(new GUIContent(label, tooltip), value);
                if (GUILayout.Button("参照…", GUILayout.Width(64)))
                {
                    var start = PathUtil.InitialDirectory(value);
                    string p;
                    if (folderAllowed)
                    {
                        var choice = EditorUtility.DisplayDialogComplex(label, "フォルダとファイルのどちらを選びますか？", "フォルダ", "キャンセル", "ファイル");
                        p = choice == 0 ? EditorUtility.OpenFolderPanel(label, start, "")
                            : choice == 2 ? EditorUtility.OpenFilePanelWithFilters(label, start, filters) : null;
                    }
                    else p = EditorUtility.OpenFilePanelWithFilters(label, start, filters);
                    if (!string.IsNullOrEmpty(p))
                    {
                        value = p;
                        GUI.FocusControl(null);
                    }
                    GUIUtility.ExitGUI();
                }
            }
            return value;
        }

        void DrawEnvironment()
        {
            if (envSetup != null)
            {
                var r = EditorGUILayout.GetControlRect(false, 22);
                EditorGUI.ProgressBar(r, 0.5f, envSetup.Step + "…");
                return;
            }
            EditorGUILayout.HelpBox(envReady ? envStatus :
                envStatus + $"\n「準備する」を押すと、{PdfUnitySettings.EnvironmentDir} に専用の Python 環境を作り、" +
                $"必要なライブラリを PyPI から導入します（Python {PythonEnvironment.MinMajor}.{PythonEnvironment.MinMinor} 以上が必要です）。",
                envReady ? MessageType.Info : MessageType.Warning);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(envReady ? "再インストール / 更新" : "準備する"))
                {
                    lastError = null;
                    try { envSetup = PythonEnvironment.Setup(); }
                    catch (Exception e) { lastError = e.Message; }
                }
                if (GUILayout.Button("状態を確認", GUILayout.Width(90))) RefreshEnvironment();
            }
        }

        // ------------------------------------------------------------------------------------
        void StartCompile()
        {
            lastError = null;
            loaded = null;
            generatedMessages.Clear();
            var input = Path.GetFullPath(PathUtil.Clean(pdfPath));
            var name = AssetGenerator.SafeName(Path.GetFileNameWithoutExtension(input));
            packageDir = Path.GetFullPath(Path.Combine(PdfUnitySettings.CacheRoot, name));
            Directory.CreateDirectory(Path.GetDirectoryName(packageDir));
            var req = new CompileRequest
            {
                InputPdf = input,
                OutputDir = packageDir,
                Pages = pages,
                Mode = Modes[modeIndex],
                Targets = new List<string>(),
                ProfilePc = profilePc,
                ProfileQuest = profileQuest,
                CmykProfile = string.IsNullOrWhiteSpace(cmykProfile) ? null : PathUtil.Clean(cmykProfile),
                SubstituteFonts = string.IsNullOrWhiteSpace(substituteFonts) ? null : PathUtil.Clean(substituteFonts),
                FontRightsDeclared = fontRightsDeclared,
                PrecomposeTranslucent = precomposeTranslucent,
                WorldScale = worldScale,
                TimeoutSec = PdfUnitySettings.TimeoutSec,
            };
            if (targetPc) req.Targets.Add("pc");
            if (targetQuest) req.Targets.Add("quest");
            try { running = new CompilerProcess(req); }
            catch (Exception e) { lastError = $"コンパイラを起動できませんでした: {e.Message}"; running = null; }
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
                    lastError = "変換を中止しました。";
                    break;
                default:
                    var jp = L.Code(r.ErrorCode);
                    lastError = $"変換に失敗しました。{jp}\n[{r.ErrorCode}] {r.ErrorMessage}";
                    Debug.LogError($"[{PdfUnitySettings.ProductName}] {lastError}\n{r.Log}");
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
                lastError = $"変換済みデータを読み込めません: {e.Message}";
            }
        }

        void DrawPackage()
        {
            Section($"結果: {loaded.SourceName}");
            foreach (var t in loaded.Targets.Values)
            {
                var type = t.Status == "pass" ? MessageType.Info : t.Status == "fail" ? MessageType.Error : MessageType.Warning;
                var lines = new List<string> { $"{L.Target(t.Name)}: {L.Status(t.Status)}（{t.Profile}）" };
                if (t.Status == "fail") lines.AddRange(t.BudgetFailures);
                else if (t.ReviewReasons.Count > 0) lines.Add("確認事項: " + string.Join("、", t.ReviewReasons.Select(L.Reason)));
                if (t.Name == "quest")
                    lines.Add(t.Status == "fail" ? "Quest には対応できません。この文書は PC 専用にしてください。"
                                                 : "実機で表示と負荷を確認してから「実機確認済み」にしてください。");
                EditorGUILayout.HelpBox(string.Join("\n", lines), type);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(t.Status == "fail"))
                        if (GUILayout.Button($"{L.Target(t.Name)} 用 Prefab を生成")) Generate(t.Name);
                    var folder = AssetGenerator.FolderFor(loaded, t.Name);
                    var doc = AssetDatabase.LoadAssetAtPath<GameObject>($"{folder}/Document.prefab");
                    using (new EditorGUI.DisabledScope(doc == null))
                        if (GUILayout.Button("シーンに配置"))
                        {
                            var inst = (GameObject)PrefabUtility.InstantiatePrefab(doc);
                            Undo.RegisterCreatedObjectUndo(inst, "PDF 文書を配置");
                            Selection.activeGameObject = inst;
                        }
                    if (t.Name == "quest" && doc != null)
                    {
                        var rec = AssetGenerator.Record.Load(folder);
                        var v = GUILayout.Toggle(rec.DeviceVerified, "実機確認済み", "Button");
                        if (v != rec.DeviceVerified) { rec.DeviceVerified = v; rec.Save(folder); }
                    }
                }
            }
            DrawDiagnostics();
            if (GUILayout.Button("変換データのフォルダを開く")) EditorUtility.RevealInFinder(Path.Combine(packageDir, "manifest.json"));
        }

        void DrawDiagnostics()
        {
            var msgs = J.Arr(loaded.Diagnostics, "messages");
            if (msgs == null || msgs.Count == 0) return;
            var important = msgs.Where(m => J.Str(m, "severity") != "info").ToList();
            var infos = msgs.Where(m => J.Str(m, "severity") == "info").ToList();
            Section($"診断（警告・エラー {important.Count} 件）");
            foreach (var m in important.Take(200)) DrawDiagnostic(m);
            if (infos.Count > 0)
            {
                showInfo = EditorGUILayout.Foldout(showInfo, $"情報 {infos.Count} 件", true);
                if (showInfo) foreach (var m in infos.Take(200)) DrawDiagnostic(m);
            }
        }

        static void DrawDiagnostic(object m)
        {
            var sev = J.Str(m, "severity");
            var code = J.Str(m, "code");
            var page = (int)J.Num(m, "page", 0);
            var fb = L.Fallback(J.Str(m, "fallback"));
            var count = (int)J.Num(m, "occurrences", 1);
            var head = (page > 0 ? $"{page} ページ: " : "") + (L.Code(code) ?? J.Str(m, "message"));
            if (fb != null && L.Code(code) == null) head += $"（{fb}）";
            if (count > 1) head += $"（{count} 回）";
            var text = $"{head}\n[{code}] {J.Str(m, "message")}";
            EditorGUILayout.HelpBox(text, sev == "error" ? MessageType.Error : sev == "warning" ? MessageType.Warning : MessageType.None);
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
                Debug.Log($"[{PdfUnitySettings.ProductName}] {L.Target(target)}: {rep.Written.Count} 件を書き込み、" +
                          $"{rep.Removed.Count} 件を削除、手動編集された {rep.SkippedUserEdited.Count} 件を保持しました（{rep.Folder}）");
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(rep.DocumentPrefab));
            }
            catch (Exception e)
            {
                lastError = $"Prefab の生成に失敗しました: {e.Message}";
                Debug.LogException(e);
            }
        }
    }

    /// <summary>Path helpers that never throw on empty or malformed user input.</summary>
    public static class PathUtil
    {
        /// <summary>Trims whitespace and surrounding quotes (paths pasted from Explorer).</summary>
        public static string Clean(string path) => (path ?? "").Trim().Trim('"').Trim();

        /// <summary>Folder to open a file dialog in: the file's folder, the folder itself, or Documents.</summary>
        public static string InitialDirectory(string path)
        {
            try
            {
                var p = Clean(path);
                if (p.Length > 0)
                {
                    if (Directory.Exists(p)) return Path.GetFullPath(p);
                    var dir = Path.GetDirectoryName(Path.GetFullPath(p));
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
                }
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }
}
