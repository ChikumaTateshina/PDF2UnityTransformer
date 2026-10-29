using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PDF2Unity.Core;
using PDF2Unity.Core.Geometry;
using UnityEditor;
using UnityEngine;

namespace PDF2Unity.Editor
{
    /// <summary>
    /// Editor UI (FR-01, SPEC §8): one button converts the PDF in-process (C#) and generates prefabs into
    /// the chosen folder. Diagnostics and target status are shown afterwards.
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
            "文字・図形・画像はそのまま変換し、直接描けない効果だけを、その効果が変化させた部分に限って画像化します。",
            "直接再現できない描画が 1 つでもあれば変換を中止します。",
            "画像化が必要なページはページ全体を画像にします（ベクター表示は失われます）。",
        };
        static readonly string[] ImageQualities = { "light", "small", "faithful" };
        static readonly GUIContent[] ImageQualityNames = { new GUIContent("軽量（既定）"), new GUIContent("最小（容量優先）"), new GUIContent("忠実") };
        static readonly string[] ImageQualityHelp =
        {
            "見た目が変わらない範囲で画像を縮小し、写真は JPEG に、小さな画像はアトラス（1 枚のテクスチャ）にまとめます。容量と描画負荷が下がります。",
            "ワールドのダウンロード容量を優先します。画像の密度を下げ（130dpi 相当）、Crunch 圧縮をかけます。近くで見ると写真がやや粗く見えることがあります。",
            "画像を元の解像度（上限まで）のまま PNG で出力します。",
        };
        static readonly string[] PcProfiles = { "pc-standard", "pc-high" };
        static readonly GUIContent[] PcProfileNames = { new GUIContent("標準"), new GUIContent("高品質") };

        [SerializeField] string pdfPath = "";
        [SerializeField] string pages = "all";
        [SerializeField] int modeIndex;
        [SerializeField] bool targetPc = true;
        [SerializeField] bool targetQuest;
        [SerializeField] int pcProfile;
        [SerializeField] int imageQuality;
        [SerializeField] int curveDetail = CurvePreviewWindow.DefaultDetail;
        [SerializeField] string outputFolder = PdfUnitySettings.DefaultOutputFolder;
        [SerializeField] string substituteFonts = "";
        [SerializeField] bool fontRightsDeclared;
        [SerializeField] bool precomposeTranslucent;
        [SerializeField] float worldScale = 1f;
        [SerializeField] bool overwriteUserEdits;
        [SerializeField] Color paperColor = Color.white;
        [SerializeField] bool advanced;
        [SerializeField] bool showInfo;
        [SerializeField] bool placeAfterGenerate;

        ConversionRunner running;
        string packageDir;
        ExchangePackage loaded;
        Vector2 scroll;
        string lastError;
        readonly List<string> generatedMessages = new List<string>();
        readonly Dictionary<string, string> generatedPrefabs = new Dictionary<string, string>();
        readonly Dictionary<string, string> measuredSizes = new Dictionary<string, string>();

        [MenuItem(PdfUnitySettings.MenuRoot + "PDF 変換")]
        public static void Open() => GetWindow<PdfCompilerWindow>(PdfUnitySettings.ProductName);

        void OnDisable()
        {
            running?.Dispose();
            running = null;
        }

        void Update()
        {
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

            Section("2. 出力");
            using (new EditorGUILayout.HorizontalScope())
            {
                targetPc = EditorGUILayout.ToggleLeft("PC（Windows）", targetPc, GUILayout.Width(130));
                using (new EditorGUI.DisabledScope(!targetPc))
                    pcProfile = EditorGUILayout.Popup(pcProfile, PcProfileNames);
            }
            targetQuest = EditorGUILayout.ToggleLeft("Quest（Android）", targetQuest);
            imageQuality = EditorGUILayout.Popup(new GUIContent("画像"), imageQuality, ImageQualityNames);
            EditorGUILayout.HelpBox(ImageQualityHelp[imageQuality], MessageType.None);
            using (new EditorGUILayout.HorizontalScope())
            {
                curveDetail = EditorGUILayout.Popup(
                    new GUIContent("曲線の細かさ", "文字や図形の曲線を何個の三角形で近似するか。粗くすると三角面数（描画負荷）が減ります。"),
                    curveDetail, CurvePreviewWindow.DetailNames);
                using (new EditorGUI.DisabledScope(PreviewBlocker() != null))
                    if (GUILayout.Button(new GUIContent("プレビュー…", "変換前に、選んだ細かさでの見た目と三角面数を確認します"), GUILayout.Width(90)))
                    {
                        var targets = Targets();
                        CurvePreviewWindow.Open(Options(Path.GetFullPath(PathUtil.Clean(pdfPath)), null, targets), targets, curveDetail, d => { curveDetail = d; Repaint(); });
                        GUIUtility.ExitGUI();
                    }
            }
            OutputFolderField();
            worldScale = EditorGUILayout.FloatField(
                new GUIContent("Prefab の大きさ", "1 のとき 72pt = 1 ユニット。0.0254 で実寸（メートル）になります。"), worldScale);
            paperColor = EditorGUILayout.ColorField("紙の色", paperColor);
            placeAfterGenerate = EditorGUILayout.ToggleLeft("作成後にシーンへ配置する", placeAfterGenerate);

            Section("3. 色・フォント・権利");
            substituteFonts = FolderField("代替フォントのフォルダ", "任意。埋め込まれていないフォントの代わりに使う、利用許諾のある .ttf/.otf を入れたフォルダ", substituteFonts);
            precomposeTranslucent = EditorGUILayout.ToggleLeft(
                new GUIContent("半透明を画像化する", "Linear 色空間では半透明の重なりの色が PDF とわずかに異なります。オンにすると該当部分を画像化して一致させます。"),
                precomposeTranslucent);
            fontRightsDeclared = EditorGUILayout.ToggleLeft("埋め込みフォントの輪郭をワールドで使う権利を持っている", fontRightsDeclared);
            EditorGUILayout.HelpBox(
                "このツールは、PDF やその画像・フォントを公開してよいかを判断しません。権利は必ずご自身で確認してください。" +
                "利用が制限されたフォントが埋め込まれている場合は、権利を宣言するか代替フォントを指定しない限り変換を中止します。",
                MessageType.Info);
            overwriteUserEdits = EditorGUILayout.ToggleLeft("手で編集した生成アセットも上書きする", overwriteUserEdits);

            EditorGUILayout.Space(8);
            if (running != null)
            {
                var r = EditorGUILayout.GetControlRect(false, 22);
                var job = running.Job;
                EditorGUI.ProgressBar(r, job.Progress, $"{L.Stage(job.Stage)}  {job.Progress * 100:0}%  {job.Detail}");
                if (GUILayout.Button("中止")) running.Cancel();
            }
            else
            {
                var reason = Blocker();
                using (new EditorGUI.DisabledScope(reason != null))
                    if (GUILayout.Button("変換して Prefab を作成", GUILayout.Height(32))) StartConvert();
                if (reason != null) EditorGUILayout.LabelField(reason, EditorStyles.miniLabel);
            }
            if (!string.IsNullOrEmpty(lastError)) EditorGUILayout.HelpBox(lastError, MessageType.Error);
            if (loaded != null) DrawResult();
            foreach (var m in generatedMessages) EditorGUILayout.HelpBox(m, MessageType.Warning);

            advanced = EditorGUILayout.Foldout(advanced, "詳細設定", true);
            if (advanced)
            {
                PdfUnitySettings.TimeoutSec = EditorGUILayout.IntField("制限時間（秒）", PdfUnitySettings.TimeoutSec);
                if (GUILayout.Button("変換済みのデータから Prefab だけ作り直す…"))
                {
                    var d = EditorUtility.OpenFolderPanel("変換済みデータのフォルダを選択", PathUtil.InitialDirectory(PdfUnitySettings.CacheRoot), "");
                    if (!string.IsNullOrEmpty(d) && LoadPackage(d)) GenerateAll();
                    GUIUtility.ExitGUI();
                }
            }
            EditorGUILayout.EndScrollView();
        }

        static void Section(string title)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        string PreviewBlocker()
        {
            if (string.IsNullOrWhiteSpace(pdfPath)) return "PDF ファイルを選んでください。";
            if (!File.Exists(PathUtil.Clean(pdfPath))) return "PDF ファイルが見つかりません。";
            if (!(targetPc || targetQuest)) return "出力先（PC / Quest）を 1 つ以上選んでください。";
            return null;
        }

        string Blocker()
        {
            var r = PreviewBlocker();
            if (r != null) return r;
            if (!PdfUnitySettings.IsAssetsFolder(outputFolder)) return "出力先フォルダは Assets フォルダ内を指定してください。";
            if (Application.platform != RuntimePlatform.WindowsEditor) return "現在は Windows 版 Unity Editor のみ対応しています。";
            return null;
        }

        void PdfField()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                pdfPath = EditorGUILayout.TextField(new GUIContent("PDF ファイル", "ここに PDF をドラッグ＆ドロップすることもできます"), pdfPath);
                HandleDrop(GUILayoutUtility.GetLastRect());
                if (GUILayout.Button("参照…", GUILayout.Width(64)))
                {
                    var p = EditorUtility.OpenFilePanelWithFilters("PDF ファイルを選択", PathUtil.InitialDirectory(pdfPath),
                                                                    new[] { "PDF ファイル", "pdf", "すべてのファイル", "*" });
                    if (!string.IsNullOrEmpty(p))
                    {
                        pdfPath = p;
                        GUI.FocusControl(null);
                    }
                    GUIUtility.ExitGUI();
                }
            }
        }

        void OutputFolderField()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                outputFolder = EditorGUILayout.TextField(new GUIContent("出力先フォルダ", "Assets 内のフォルダ。<フォルダ>/<文書名>/PC|Quest/ に Prefab を作成します"), outputFolder);
                var rect = GUILayoutUtility.GetLastRect();
                var e = Event.current;
                if ((e.type == EventType.DragUpdated || e.type == EventType.DragPerform) && rect.Contains(e.mousePosition))
                {
                    var path = DragAndDrop.paths?.FirstOrDefault(p => AssetDatabase.IsValidFolder(p));
                    if (path != null)
                    {
                        DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                        if (e.type == EventType.DragPerform) { DragAndDrop.AcceptDrag(); outputFolder = path; GUI.FocusControl(null); }
                        e.Use();
                    }
                }
                if (GUILayout.Button("参照…", GUILayout.Width(64)))
                {
                    var start = AssetDatabase.IsValidFolder(outputFolder) ? Path.GetFullPath(outputFolder) : Application.dataPath;
                    var p = EditorUtility.OpenFolderPanel("Prefab の出力先フォルダ（Assets 内）", start, "");
                    if (!string.IsNullOrEmpty(p))
                    {
                        var rel = PdfUnitySettings.ToProjectRelative(p);
                        if (rel == null) EditorUtility.DisplayDialog(PdfUnitySettings.ProductName, "出力先は、このプロジェクトの Assets フォルダ内を選んでください。", "OK");
                        else { outputFolder = rel; GUI.FocusControl(null); }
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

        static string FolderField(string label, string tooltip, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                value = EditorGUILayout.TextField(new GUIContent(label, tooltip), value);
                if (GUILayout.Button("参照…", GUILayout.Width(64)))
                {
                    var p = EditorUtility.OpenFolderPanel(label, PathUtil.InitialDirectory(value), "");
                    if (!string.IsNullOrEmpty(p)) { value = p; GUI.FocusControl(null); }
                    GUIUtility.ExitGUI();
                }
            }
            return value;
        }

        // ------------------------------------------------------------------------------------
        void StartConvert()
        {
            lastError = null;
            loaded = null;
            generatedMessages.Clear();
            generatedPrefabs.Clear();
            var input = Path.GetFullPath(PathUtil.Clean(pdfPath));
            var name = AssetGenerator.FileName(Path.GetFileNameWithoutExtension(input));
            packageDir = Path.GetFullPath(Path.Combine(PdfUnitySettings.CacheRoot, name));
            Directory.CreateDirectory(Path.GetDirectoryName(packageDir));
            try { running = new ConversionRunner(Options(input, packageDir, Targets())); }
            catch (Exception e) { lastError = $"変換を開始できませんでした: {e.Message}"; running = null; }
        }

        List<Profile> Targets()
        {
            var targets = new List<Profile>();
            if (targetPc) targets.Add(Profile.ByName(PcProfiles[pcProfile]));
            if (targetQuest) targets.Add(Profile.QuestStandard());
            return targets;
        }

        CompileOptions Options(string input, string output, List<Profile> targets)
        {
            return new CompileOptions
            {
                Input = input,
                Output = output,
                Pages = pages,
                Mode = Modes[modeIndex],
                Targets = targets,
                SubstituteFontDir = string.IsNullOrWhiteSpace(substituteFonts) ? null : PathUtil.Clean(substituteFonts),
                FontRightsDeclared = fontRightsDeclared,
                PrecomposeTranslucent = precomposeTranslucent,
                ImageQuality = ImageQualities[imageQuality],
                CurveDetail = CurvePreviewWindow.Details[Mathf.Clamp(curveDetail, 0, CurvePreviewWindow.Details.Length - 1)],
                WorldScale = worldScale,
                CacheDir = Path.GetFullPath(PdfUnitySettings.ToolCache),
                WriteReference = false, // reference renders are only used by the regression tools
                ReferenceDpi = 72,
                Limits = new Limits { TimeoutSec = PdfUnitySettings.TimeoutSec },
            };
        }

        void OnFinished()
        {
            var r = running;
            running = null;
            if (r == null) return;
            switch (r.Current)
            {
                case ConversionRunner.State.Succeeded:
                    if (LoadPackage(packageDir)) GenerateAll();
                    break;
                case ConversionRunner.State.Cancelled:
                    lastError = "変換を中止しました。";
                    break;
                default:
                    lastError = $"変換に失敗しました。{L.Code(r.ErrorCode)}\n[{r.ErrorCode}] {r.ErrorMessage}";
                    Debug.LogError($"[{PdfUnitySettings.ProductName}] {lastError}");
                    break;
            }
            r.Dispose();
        }

        bool LoadPackage(string dir)
        {
            try
            {
                loaded = new ExchangePackage(dir);
                packageDir = dir;
                return true;
            }
            catch (Exception e)
            {
                loaded = null;
                lastError = $"変換データを読み込めません: {e.Message}";
                return false;
            }
        }

        void GenerateAll()
        {
            generatedMessages.Clear();
            measuredSizes.Clear();
            generatedPrefabs.Clear();
            foreach (var t in loaded.Targets.Values)
            {
                if (t.Status == "fail")
                {
                    generatedMessages.Add($"{L.Target(t.Name)}: 負荷や品質の確認に通らなかったため Prefab を作成しませんでした。");
                    continue;
                }
                try
                {
                    var rep = AssetGenerator.Generate(loaded, t.Name, new GenerateOptions
                    {
                        OverwriteUserEdits = overwriteUserEdits,
                        PaperColor = paperColor,
                        OutputRoot = outputFolder,
                    });
                    generatedMessages.AddRange(rep.Messages);
                    generatedPrefabs[t.Name] = rep.DocumentPrefab;
                    Debug.Log($"[{PdfUnitySettings.ProductName}] {L.Target(t.Name)}: {rep.DocumentPrefab} を作成しました（{rep.Written.Count} 件を書き込み）。");
                }
                catch (Exception e)
                {
                    lastError = $"Prefab の作成に失敗しました（{L.Target(t.Name)}）: {e.Message}";
                    Debug.LogException(e);
                }
            }
            var first = generatedPrefabs.TryGetValue(TargetSwitcher.ActiveTargetFolder == "Quest" ? "quest" : "pc", out var p) ? p : generatedPrefabs.Values.FirstOrDefault();
            var asset = first != null ? AssetDatabase.LoadAssetAtPath<GameObject>(first) : null;
            if (asset != null)
            {
                EditorGUIUtility.PingObject(asset);
                if (placeAfterGenerate) Place(asset);
            }
        }

        void MeasureSize(string target, string prefabPath)
        {
            try
            {
                long bytes = WorldSize.Measure(prefabPath, target);
                measuredSizes[target] = $"ダウンロード容量（この Prefab の分）: {bytes / 1048576.0:0.00} MB";
            }
            catch (Exception e)
            {
                measuredSizes[target] = $"容量を測れませんでした: {e.Message}";
                Debug.LogException(e);
            }
        }

        static void Place(GameObject prefab)
        {
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            Undo.RegisterCreatedObjectUndo(inst, "PDF 文書を配置");
            Selection.activeGameObject = inst;
        }

        void DrawResult()
        {
            Section($"結果: {loaded.SourceName}");
            foreach (var t in loaded.Targets.Values)
            {
                var type = t.Status == "pass" ? MessageType.Info : t.Status == "fail" ? MessageType.Error : MessageType.Warning;
                var lines = new List<string> { $"{L.Target(t.Name)}: {L.Status(t.Status)}（{t.Profile}）" };
                if (t.Status == "fail")
                {
                    lines.AddRange(t.BudgetFailures);
                    if (t.BudgetFailures.Any(f => f.Contains("triangles")))
                        lines.Add("「曲線の細かさ」を粗くすると三角面数を減らせます（「プレビュー…」で事前に確認できます）。");
                }
                else if (t.ReviewReasons.Count > 0) lines.Add("確認事項: " + string.Join("、", t.ReviewReasons.Select(L.Reason)));
                if (t.Name == "quest")
                    lines.Add(t.Status == "fail" ? "このままでは Quest には対応できません。設定を変えて再変換するか、この文書は PC 専用にしてください。"
                                                 : "実機で表示と負荷を確認してから「実機確認済み」にしてください。");
                if (measuredSizes.TryGetValue(t.Name, out var size)) lines.Add(size);
                EditorGUILayout.HelpBox(string.Join("\n", lines), type);
                generatedPrefabs.TryGetValue(t.Name, out var prefabPath);
                var doc = prefabPath != null ? AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) : null;
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField(doc, typeof(GameObject), false);
                    using (new EditorGUI.DisabledScope(doc == null))
                        if (GUILayout.Button("シーンに配置", GUILayout.Width(100))) Place(doc);
                    using (new EditorGUI.DisabledScope(doc == null || !WorldSize.Supported(t.Name)))
                        if (GUILayout.Button(new GUIContent("容量を測る", "この Prefab がワールドのダウンロード容量に加える大きさを、実際にビルドして測ります（1 分ほどかかります）"), GUILayout.Width(90)))
                        {
                            MeasureSize(t.Name, prefabPath);
                            GUIUtility.ExitGUI();
                        }
                    if (t.Name == "quest" && doc != null)
                    {
                        var folder = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
                        var rec = AssetGenerator.Record.Load(folder);
                        var v = GUILayout.Toggle(rec.DeviceVerified, "実機確認済み", "Button", GUILayout.Width(100));
                        if (v != rec.DeviceVerified) { rec.DeviceVerified = v; rec.Save(folder); }
                    }
                }
            }
            DrawDiagnostics();
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
            var count = (int)J.Num(m, "occurrences", 1);
            var head = (page > 0 ? $"{page} ページ: " : "") + (L.Code(code) ?? J.Str(m, "message"));
            if (count > 1) head += $"（{count} 回）";
            EditorGUILayout.HelpBox($"{head}\n[{code}] {J.Str(m, "message")}",
                sev == "error" ? MessageType.Error : sev == "warning" ? MessageType.Warning : MessageType.None);
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
