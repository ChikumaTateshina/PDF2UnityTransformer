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
    /// Editor UI (FR-01, SPEC §8): one button converts the selected PDFs one after another in-process (C#) and
    /// generates prefabs into the chosen folder. Diagnostics and target status are shown per document afterwards.
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

        [SerializeField] string pdfPath = ""; // 0.2.1 and earlier: the single PDF (moved into pdfPaths)
        [SerializeField] List<string> pdfPaths = new List<string>();
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
        [SerializeField] bool placeAfterGenerate;

        /// <summary>Outcome of one document of a batch.</summary>
        sealed class DocResult
        {
            public string Input;
            public string PackageDir;
            public ExchangePackage Package;
            public string Error;
            public bool Open;
            public bool ShowInfo;
            public readonly List<string> Messages = new List<string>();
            public readonly Dictionary<string, string> Prefabs = new Dictionary<string, string>();
            public readonly Dictionary<string, string> Sizes = new Dictionary<string, string>();
            public string Name => Package?.SourceName ?? Path.GetFileNameWithoutExtension(Input);
        }

        ConversionRunner running;
        DocResult current;
        readonly Queue<string> queue = new Queue<string>();
        int batchTotal;
        readonly List<DocResult> results = new List<DocResult>();
        Vector2 scroll;
        string lastError;

        [MenuItem(PdfUnitySettings.MenuRoot + "PDF 変換")]
        public static void Open() => GetWindow<PdfCompilerWindow>(PdfUnitySettings.ProductName);

        void OnEnable()
        {
            if (pdfPaths == null) pdfPaths = new List<string>();
            if (pdfPaths.Count == 0 && !string.IsNullOrWhiteSpace(pdfPath)) pdfPaths.Add(PathUtil.Clean(pdfPath));
            pdfPath = "";
        }

        void OnDisable()
        {
            queue.Clear();
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
            PdfList();
            pages = EditorGUILayout.TextField(new GUIContent("ページ範囲", "all（すべて）または 1-3,5 のように指定。複数の PDF ではすべてに同じ範囲を使います"), pages);
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
                        var existing = pdfPaths.Where(File.Exists).ToList();
                        if (existing.Count == 1) OpenPreview(existing[0]);
                        else
                        {
                            var menu = new GenericMenu();
                            foreach (var path in existing) menu.AddItem(new GUIContent(Path.GetFileName(path).Replace('/', '∕')), false, () => OpenPreview(path));
                            menu.ShowAsContext();
                        }
                        GUIUtility.ExitGUI();
                    }
            }
            OutputFolderField();
            worldScale = EditorGUILayout.FloatField(
                new GUIContent("Prefab の大きさ", "1 のとき 72pt = 1 ユニット。0.0254 で実寸（メートル）になります。"), worldScale);
            paperColor = EditorGUILayout.ColorField("紙の色", paperColor);
            placeAfterGenerate = EditorGUILayout.ToggleLeft(
                new GUIContent("作成後にシーンへ配置する", "複数の PDF を変換したときは、横に並べて配置します"), placeAfterGenerate);

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
                var doc = batchTotal > 1 ? $"({results.Count}/{batchTotal}) {Path.GetFileName(current.Input)}  " : "";
                EditorGUI.ProgressBar(r, job.Progress, $"{doc}{L.Stage(job.Stage)}  {job.Progress * 100:0}%  {job.Detail}");
                if (GUILayout.Button(batchTotal > 1 ? "中止（残りの PDF も変換しません）" : "中止"))
                {
                    queue.Clear();
                    running.Cancel();
                }
            }
            else
            {
                var reason = Blocker();
                using (new EditorGUI.DisabledScope(reason != null))
                    if (GUILayout.Button(pdfPaths.Count > 1 ? $"{pdfPaths.Count} 件の PDF を変換して Prefab を作成" : "変換して Prefab を作成", GUILayout.Height(32)))
                        StartConvert();
                if (reason != null) EditorGUILayout.LabelField(reason, EditorStyles.miniLabel);
            }
            if (!string.IsNullOrEmpty(lastError)) EditorGUILayout.HelpBox(lastError, MessageType.Error);
            DrawResults();

            advanced = EditorGUILayout.Foldout(advanced, "詳細設定", true);
            if (advanced)
            {
                PdfUnitySettings.TimeoutSec = EditorGUILayout.IntField("制限時間（秒、PDF 1 件あたり）", PdfUnitySettings.TimeoutSec);
                using (new EditorGUI.DisabledScope(running != null))
                    if (GUILayout.Button("変換済みのデータから Prefab だけ作り直す…"))
                    {
                        var d = EditorUtility.OpenFolderPanel("変換済みデータのフォルダを選択", PathUtil.InitialDirectory(PdfUnitySettings.CacheRoot), "");
                        if (!string.IsNullOrEmpty(d)) Rebuild(d);
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
            if (pdfPaths.Count == 0) return "PDF ファイルを追加してください。";
            if (!pdfPaths.Any(File.Exists)) return "PDF ファイルが見つかりません。";
            if (!(targetPc || targetQuest)) return "出力先（PC / Quest）を 1 つ以上選んでください。";
            return null;
        }

        string Blocker()
        {
            var r = PreviewBlocker();
            if (r != null) return r;
            var missing = pdfPaths.FirstOrDefault(p => !File.Exists(p));
            if (missing != null) return $"見つからない PDF があります: {Path.GetFileName(missing)}";
            // Output and cache folders are named after the file, so equal names would overwrite each other.
            var dup = pdfPaths.GroupBy(p => AssetGenerator.FileName(Path.GetFileNameWithoutExtension(p)), StringComparer.OrdinalIgnoreCase)
                              .FirstOrDefault(g => g.Count() > 1);
            if (dup != null) return $"同じ名前の PDF が複数あります（出力先が重なります）: {dup.Key}";
            if (!PdfUnitySettings.IsAssetsFolder(outputFolder)) return "出力先フォルダは Assets フォルダ内を指定してください。";
            if (Application.platform != RuntimePlatform.WindowsEditor) return "現在は Windows 版 Unity Editor のみ対応しています。";
            return null;
        }

        void PdfList()
        {
            var area = EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            if (pdfPaths.Count == 0)
                EditorGUILayout.LabelField("PDF を追加してください（ここへ PDF やフォルダをドラッグ＆ドロップすることもできます）", EditorStyles.wordWrappedMiniLabel);
            for (int k = 0; k < pdfPaths.Count; k++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var name = Path.GetFileName(pdfPaths[k]);
                    EditorGUILayout.LabelField(new GUIContent(File.Exists(pdfPaths[k]) ? name : $"（見つかりません）{name}", pdfPaths[k]));
                    using (new EditorGUI.DisabledScope(running != null))
                        if (GUILayout.Button(new GUIContent("×", "一覧から外す"), GUILayout.Width(24)))
                        {
                            pdfPaths.RemoveAt(k);
                            GUIUtility.ExitGUI();
                        }
                }
            }
            EditorGUILayout.EndVertical();
            HandleDrop(area);
            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(running != null))
            {
                if (GUILayout.Button(new GUIContent("PDF を追加…", "Ctrl や Shift を押しながら選ぶと、複数の PDF をまとめて追加できます")))
                {
                    var start = PathUtil.InitialDirectory(pdfPaths.LastOrDefault());
                    var picked = NativeFileDialog.OpenPdfs("PDF ファイルを選択（複数選択可）", start);
                    if (picked == null)
                    {
                        var p = EditorUtility.OpenFilePanelWithFilters("PDF ファイルを選択", start, new[] { "PDF ファイル", "pdf", "すべてのファイル", "*" });
                        picked = string.IsNullOrEmpty(p) ? new List<string>() : new List<string> { p };
                    }
                    AddPdfs(picked);
                    GUIUtility.ExitGUI();
                }
                if (GUILayout.Button(new GUIContent("フォルダから追加…", "選んだフォルダ直下の PDF をすべて追加します")))
                {
                    var d = EditorUtility.OpenFolderPanel("PDF の入ったフォルダを選択", PathUtil.InitialDirectory(pdfPaths.LastOrDefault()), "");
                    if (!string.IsNullOrEmpty(d)) AddPdfs(new[] { d });
                    GUIUtility.ExitGUI();
                }
                using (new EditorGUI.DisabledScope(pdfPaths.Count == 0))
                    if (GUILayout.Button("すべて外す", GUILayout.Width(80))) pdfPaths.Clear();
            }
        }

        /// <summary>Adds PDF files, and the PDFs directly inside folders, skipping ones already listed.</summary>
        void AddPdfs(IEnumerable<string> paths)
        {
            foreach (var raw in paths)
            {
                var p = Path.GetFullPath(PathUtil.Clean(raw));
                var files = Directory.Exists(p)
                    ? Directory.GetFiles(p, "*.pdf").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    : p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? new[] { p } : Enumerable.Empty<string>();
                foreach (var f in files)
                    if (!pdfPaths.Contains(f, StringComparer.OrdinalIgnoreCase)) pdfPaths.Add(f);
            }
            GUI.FocusControl(null);
            Repaint();
        }

        void OpenPreview(string input)
        {
            var targets = Targets();
            CurvePreviewWindow.Open(Options(Path.GetFullPath(input), null, targets), targets, curveDetail, d => { curveDetail = d; Repaint(); });
        }

        void OutputFolderField()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                outputFolder = EditorGUILayout.TextField(new GUIContent("出力先フォルダ", "Assets 内のフォルダ。<フォルダ>/<元のファイル名>/PC|Quest/ に Prefab を作成します"), outputFolder);
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
            if ((e.type != EventType.DragUpdated && e.type != EventType.DragPerform) || !rect.Contains(e.mousePosition) || running != null) return;
            var dropped = DragAndDrop.paths?.Where(p => Directory.Exists(p) || p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();
            if (dropped == null || dropped.Count == 0) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                AddPdfs(dropped);
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
            results.Clear();
            queue.Clear();
            foreach (var p in pdfPaths) queue.Enqueue(p);
            batchTotal = queue.Count;
            StartNext();
        }

        /// <summary>Starts the next queued PDF; when none is left, pings (and places) the batch's prefabs.</summary>
        void StartNext()
        {
            while (queue.Count > 0)
            {
                var input = Path.GetFullPath(queue.Dequeue());
                current = new DocResult { Input = input, Open = batchTotal == 1 };
                results.Add(current);
                var name = AssetGenerator.FileName(Path.GetFileNameWithoutExtension(input));
                current.PackageDir = Path.GetFullPath(Path.Combine(PdfUnitySettings.CacheRoot, name));
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(current.PackageDir));
                    running = new ConversionRunner(Options(input, current.PackageDir, Targets()));
                    return;
                }
                catch (Exception e)
                {
                    current.Error = $"変換を開始できませんでした: {e.Message}";
                    current.Open = true;
                }
            }
            current = null;
            FinishBatch();
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
            var d = current;
            switch (r.Current)
            {
                case ConversionRunner.State.Succeeded:
                    if (LoadPackage(d, d.PackageDir)) GenerateAll(d);
                    break;
                case ConversionRunner.State.Cancelled:
                    d.Error = "変換を中止しました。";
                    if (results.Count < batchTotal) lastError = $"中止したため、残りの {batchTotal - results.Count} 件は変換していません。";
                    break;
                default:
                    d.Error = $"変換に失敗しました。{L.Code(r.ErrorCode)}\n[{r.ErrorCode}] {r.ErrorMessage}";
                    Debug.LogError($"[{PdfUnitySettings.ProductName}] {Path.GetFileName(d.Input)}: {d.Error}");
                    break;
            }
            if (d.Error != null) d.Open = true;
            r.Dispose();
            StartNext();
        }

        void Rebuild(string packageDir)
        {
            lastError = null;
            results.Clear();
            batchTotal = 1;
            var d = new DocResult { Input = packageDir, PackageDir = packageDir, Open = true };
            results.Add(d);
            if (LoadPackage(d, packageDir)) GenerateAll(d);
            FinishBatch();
        }

        static bool LoadPackage(DocResult d, string dir)
        {
            try
            {
                d.Package = new ExchangePackage(dir);
                return true;
            }
            catch (Exception e)
            {
                d.Error = $"変換データを読み込めません: {e.Message}";
                return false;
            }
        }

        void GenerateAll(DocResult d)
        {
            d.Messages.Clear();
            d.Sizes.Clear();
            d.Prefabs.Clear();
            foreach (var t in d.Package.Targets.Values)
            {
                if (t.Status == "fail")
                {
                    d.Messages.Add($"{L.Target(t.Name)}: 負荷や品質の確認に通らなかったため Prefab を作成しませんでした。");
                    continue;
                }
                try
                {
                    var rep = AssetGenerator.Generate(d.Package, t.Name, new GenerateOptions
                    {
                        OverwriteUserEdits = overwriteUserEdits,
                        PaperColor = paperColor,
                        OutputRoot = outputFolder,
                    });
                    d.Messages.AddRange(rep.Messages);
                    d.Prefabs[t.Name] = rep.DocumentPrefab;
                    Debug.Log($"[{PdfUnitySettings.ProductName}] {L.Target(t.Name)}: {rep.DocumentPrefab} を作成しました（{rep.Written.Count} 件を書き込み）。");
                }
                catch (Exception e)
                {
                    d.Error = $"Prefab の作成に失敗しました（{L.Target(t.Name)}）: {e.Message}";
                    Debug.LogException(e);
                }
            }
        }

        /// <summary>Pings the generated prefabs (for the active build target) and places them side by side if requested.</summary>
        void FinishBatch()
        {
            var active = TargetSwitcher.ActiveTargetFolder == "Quest" ? "quest" : "pc";
            var assets = results
                .Select(d => d.Prefabs.TryGetValue(active, out var p) ? p : d.Prefabs.Values.FirstOrDefault())
                .Where(p => p != null)
                .Select(p => AssetDatabase.LoadAssetAtPath<GameObject>(p))
                .Where(a => a != null)
                .ToList();
            if (assets.Count == 0) return;
            EditorGUIUtility.PingObject(assets[assets.Count - 1]);
            if (!placeAfterGenerate) return;
            var placed = new List<GameObject>();
            float x = 0;
            foreach (var a in assets)
            {
                var inst = Place(a);
                if (assets.Count > 1 && RendererBounds(inst) is Bounds b)
                {
                    inst.transform.position += new Vector3(x - b.min.x, 0, 0);
                    x += b.size.x * 1.1f;
                }
                placed.Add(inst);
            }
            Selection.objects = placed.ToArray();
        }

        static Bounds? RendererBounds(GameObject go)
        {
            Bounds? bounds = null;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (bounds is Bounds b) { b.Encapsulate(r.bounds); bounds = b; }
                else bounds = r.bounds;
            }
            return bounds;
        }

        static void MeasureSize(DocResult d, string target, string prefabPath)
        {
            try
            {
                long bytes = WorldSize.Measure(prefabPath, target);
                d.Sizes[target] = $"ダウンロード容量（この Prefab の分）: {bytes / 1048576.0:0.00} MB";
            }
            catch (Exception e)
            {
                d.Sizes[target] = $"容量を測れませんでした: {e.Message}";
                Debug.LogException(e);
            }
        }

        static GameObject Place(GameObject prefab)
        {
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            Undo.RegisterCreatedObjectUndo(inst, "PDF 文書を配置");
            Selection.activeGameObject = inst;
            return inst;
        }

        void DrawResults()
        {
            if (results.Count == 0) return;
            if (batchTotal == 1)
            {
                Section($"結果: {results[0].Name}");
                DrawResult(results[0]);
                return;
            }
            int made = results.Count(d => d.Prefabs.Count > 0);
            Section(running != null ? $"結果（{batchTotal} 件中 {results.Count} 件目を変換中）" : $"結果（{results.Count} 件中 {made} 件で Prefab を作成）");
            foreach (var d in results)
            {
                d.Open = EditorGUILayout.Foldout(d.Open, $"{d.Name}  —  {Summary(d)}", true);
                if (!d.Open) continue;
                using (new EditorGUI.IndentLevelScope())
                    DrawResult(d);
            }
        }

        string Summary(DocResult d)
        {
            if (d == current && running != null) return "変換中";
            if (d.Package == null) return "失敗";
            var s = string.Join(" / ", d.Package.Targets.Values.Select(t => $"{L.Target(t.Name)}: {L.Status(t.Status)}"));
            return d.Error != null ? s + "（エラーあり）" : s;
        }

        void DrawResult(DocResult d)
        {
            if (d.Error != null) EditorGUILayout.HelpBox(d.Error, MessageType.Error);
            if (d.Package == null) return;
            foreach (var t in d.Package.Targets.Values)
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
                if (d.Sizes.TryGetValue(t.Name, out var size)) lines.Add(size);
                EditorGUILayout.HelpBox(string.Join("\n", lines), type);
                d.Prefabs.TryGetValue(t.Name, out var prefabPath);
                var doc = prefabPath != null ? AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) : null;
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField(doc, typeof(GameObject), false);
                    using (new EditorGUI.DisabledScope(doc == null))
                        if (GUILayout.Button("シーンに配置", GUILayout.Width(100))) Place(doc);
                    using (new EditorGUI.DisabledScope(doc == null || running != null || !WorldSize.Supported(t.Name)))
                        if (GUILayout.Button(new GUIContent("容量を測る", "この Prefab がワールドのダウンロード容量に加える大きさを、実際にビルドして測ります（1 分ほどかかります）"), GUILayout.Width(90)))
                        {
                            MeasureSize(d, t.Name, prefabPath);
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
            foreach (var m in d.Messages) EditorGUILayout.HelpBox(m, MessageType.Warning);
            DrawDiagnostics(d);
        }

        static void DrawDiagnostics(DocResult d)
        {
            var msgs = J.Arr(d.Package.Diagnostics, "messages");
            if (msgs == null || msgs.Count == 0) return;
            var important = msgs.Where(m => J.Str(m, "severity") != "info").ToList();
            var infos = msgs.Where(m => J.Str(m, "severity") == "info").ToList();
            Section($"診断（警告・エラー {important.Count} 件）");
            foreach (var m in important.Take(200)) DrawDiagnostic(m);
            if (infos.Count > 0)
            {
                d.ShowInfo = EditorGUILayout.Foldout(d.ShowInfo, $"情報 {infos.Count} 件", true);
                if (d.ShowInfo) foreach (var m in infos.Take(200)) DrawDiagnostic(m);
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

        /// <summary>Test hook: converts <paramref name="inputs"/> through the window's batch flow, blocking until done.</summary>
        internal static List<string> ConvertBatchForTest(IEnumerable<string> inputs, string outputFolder)
        {
            var w = CreateInstance<PdfCompilerWindow>();
            try
            {
                w.pdfPaths = new List<string>();
                w.outputFolder = outputFolder;
                w.AddPdfs(inputs);
                if (w.Blocker() is string reason) throw new InvalidOperationException(reason);
                w.StartConvert();
                while (w.running != null)
                {
                    System.Threading.Thread.Sleep(50);
                    if (!w.running.Tick()) w.OnFinished();
                }
                return w.results.Select(d => $"{d.Name}: {(d.Error ?? string.Join(", ", d.Prefabs.Values))}").ToList();
            }
            finally
            {
                DestroyImmediate(w);
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
