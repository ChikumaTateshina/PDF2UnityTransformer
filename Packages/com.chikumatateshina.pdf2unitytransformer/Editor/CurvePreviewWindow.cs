using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PDF2Unity.Core;
using PDF2Unity.Core.Geometry;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace PDF2Unity.Editor
{
    /// <summary>
    /// Shows the tessellated pages before converting, so the curve detail (and with it the triangle count) can be
    /// checked and chosen. The PDF is read once; changing the detail only re-tessellates.
    /// </summary>
    public sealed class CurvePreviewWindow : EditorWindow
    {
        public static readonly double[] Details = { 0.5, 1, 2, 4, 8 };
        public static readonly GUIContent[] DetailNames =
        {
            new GUIContent("細かい（×0.5）"), new GUIContent("標準"), new GUIContent("やや粗い（×2）"),
            new GUIContent("粗い（×4）"), new GUIContent("とても粗い（×8）"),
        };
        public const int DefaultDetail = 1;

        CompileOptions options;
        List<Profile> profiles;
        Action<int> apply;
        int detail = DefaultDetail, target, page;
        bool wireframe;

        CancellationTokenSource cts;
        Task<PreviewDocument> loading;
        PreviewDocument doc;
        Task<PreviewPage> lowering;
        (int detail, int target, int page) shown = (-1, -1, -1), requested;
        PreviewPage current;
        Task<long> totaling;
        (int detail, int target) totalKey = (-1, -1);
        long total = -1;
        string error;

        Mesh mesh;
        Material fill, wire;
        RenderTexture rt;
        Vector2 center;   // view centre, page points (y down)
        float zoom = 1;   // screen pixels per page point (relative to fit)
        bool fitted;

        public static void Open(CompileOptions options, List<Profile> profiles, int detail, Action<int> apply)
        {
            var w = GetWindow<CurvePreviewWindow>(true, "曲線の細かさのプレビュー");
            w.minSize = new Vector2(560, 420);
            w.Begin(options, profiles, detail, apply);
        }

        void Begin(CompileOptions o, List<Profile> profs, int d, Action<int> onApply)
        {
            Cancel();
            options = o;
            profiles = profs;
            apply = onApply;
            detail = Mathf.Clamp(d, 0, Details.Length - 1);
            target = Mathf.Clamp(target, 0, profs.Count - 1);
            page = 0;
            doc = null;
            current = null;
            shown = (-1, -1, -1);
            totalKey = (-1, -1);
            total = -1;
            error = null;
            fitted = false;
            cts = new CancellationTokenSource();
            var job = new Job(cts.Token, o.Limits.TimeoutSec);
            loading = Task.Run(() => PreviewDocument.Load(o, job), cts.Token);
        }

        void Cancel()
        {
            cts?.Cancel();
            cts?.Dispose();
            cts = null;
            loading = null;
            lowering = null;
            totaling = null;
        }

        void OnDisable()
        {
            Cancel();
            if (mesh != null) DestroyImmediate(mesh);
            if (fill != null) DestroyImmediate(fill);
            if (wire != null) DestroyImmediate(wire);
            if (rt != null) { rt.Release(); DestroyImmediate(rt); }
        }

        void Update()
        {
            if (options == null) return;
            if (loading != null && loading.IsCompleted)
            {
                if (loading.Status == TaskStatus.RanToCompletion) doc = loading.Result;
                else error = Message(loading.Exception);
                loading = null;
                Repaint();
            }
            if (doc == null) return;
            if (lowering != null && lowering.IsCompleted)
            {
                if (lowering.Status == TaskStatus.RanToCompletion) { current = lowering.Result; shown = requested; BuildMesh(); }
                else error = Message(lowering.Exception);
                lowering = null;
                Repaint();
            }
            if (lowering == null && shown != (detail, target, page))
            {
                requested = (detail, target, page);
                var (d, t, p) = requested;
                var job = new Job(cts.Token, options.Limits.TimeoutSec);
                lowering = Task.Run(() => doc.Lower(p, profiles[t], Details[d], job), cts.Token);
                Repaint();
            }
            // Whole-document count (Quest also limits the total), after the visible page.
            if (totaling != null && totaling.IsCompleted)
            {
                if (totaling.Status == TaskStatus.RanToCompletion && totalKey == (detail, target)) total = totaling.Result;
                else totalKey = (-1, -1);
                totaling = null;
                Repaint();
            }
            if (doc.PageCount > 1 && lowering == null && totaling == null && totalKey != (detail, target))
            {
                totalKey = (detail, target);
                total = -1;
                var (d, t) = totalKey;
                var job = new Job(cts.Token, options.Limits.TimeoutSec);
                totaling = Task.Run(() =>
                {
                    long sum = 0;
                    for (int k = 0; k < doc.PageCount; k++) sum += doc.Lower(k, profiles[t], Details[d], job).Triangles;
                    return sum;
                }, cts.Token);
            }
        }

        static string Message(AggregateException e)
        {
            var b = e?.GetBaseException();
            if (b is OperationCanceledException) return "中止しました。";
            return b is CompileException ce ? $"{L.Code(ce.Code) ?? ce.Message}\n[{ce.Code}] {ce.Message}" : b?.Message;
        }

        // ------------------------------------------------------------------------------------
        void OnGUI()
        {
            if (options == null) { EditorGUILayout.HelpBox("変換ウィンドウの「プレビュー…」から開いてください。", MessageType.Info); return; }
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var names = new GUIContent[profiles.Count];
                for (int i = 0; i < names.Length; i++) names[i] = new GUIContent(L.Target(profiles[i].Target));
                target = EditorGUILayout.Popup(target, names, EditorStyles.toolbarPopup, GUILayout.Width(90));
                if (doc != null && doc.PageCount > 1)
                {
                    var pages = new GUIContent[doc.PageCount];
                    for (int i = 0; i < pages.Length; i++) pages[i] = new GUIContent($"{doc.PageNumber(i)} ページ");
                    page = EditorGUILayout.Popup(page, pages, EditorStyles.toolbarPopup, GUILayout.Width(90));
                }
                GUILayout.FlexibleSpace();
                wireframe = GUILayout.Toggle(wireframe, "三角形を表示", EditorStyles.toolbarButton);
                if (GUILayout.Button("全体を表示", EditorStyles.toolbarButton)) fitted = false;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                detail = EditorGUILayout.Popup(new GUIContent("曲線の細かさ"), detail, DetailNames);
                if (GUILayout.Button("この細かさで変換する", GUILayout.Width(150)))
                {
                    apply?.Invoke(detail);
                    Close();
                    GUIUtility.ExitGUI();
                }
            }
            var prof = profiles[target];
            double tolMm = prof.CurveTolerancePt * Details[detail] * 25.4 / 72;
            EditorGUILayout.LabelField($"曲線の許容誤差: {tolMm:0.###} mm（実寸の PDF 上）", EditorStyles.miniLabel);
            EditorGUILayout.HelpBox(Status(prof), StatusType(prof));
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);

            var area = GUILayoutUtility.GetRect(10, 10, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            if (current == null)
            {
                EditorGUI.DrawRect(area, new Color(0.18f, 0.18f, 0.18f));
                GUI.Label(area, loading != null ? "PDF を読み込み中…" : "三角形分割中…", CenteredLabel);
                return;
            }
            View(area);
            if (lowering != null) GUI.Label(new Rect(area.x + 6, area.y + 4, 200, 18), "更新中…", EditorStyles.whiteMiniLabel);
            EditorGUILayout.LabelField("ホイールで拡大・縮小、ドラッグで移動。灰色は画像、橙色は画像化される部分（三角形分割されません）です。", EditorStyles.miniLabel);
        }

        static GUIStyle centered;
        static GUIStyle CenteredLabel => centered ?? (centered = new GUIStyle(EditorStyles.whiteLabel) { alignment = TextAnchor.MiddleCenter });

        string Status(Profile prof)
        {
            if (current == null || shown.detail != detail || shown.target != target) return "三角面数を数えています…";
            var lines = new List<string> { $"このページの三角面数: {current.Triangles:N0}" + (prof.EnforceBudgets ? $"（上限 {prof.MaxTrianglesPerPage:N0}）" : "") };
            if (doc.PageCount > 1)
                lines.Add(Total >= 0 ? $"文書全体: {Total:N0}" + (prof.EnforceBudgets ? $"（上限 {prof.MaxTrianglesTotal:N0}）" : "") : "文書全体: 計算中…");
            if (prof.EnforceBudgets && Over(prof)) lines.Add("上限を超えています。曲線の細かさを粗くすると減らせます（文字の多い文書では減り方に限りがあります）。");
            return string.Join("\n", lines);
        }

        long Total => totalKey == (detail, target) && totaling == null ? total : -1;

        bool Over(Profile prof) => current.Triangles > prof.MaxTrianglesPerPage || Total > prof.MaxTrianglesTotal;

        MessageType StatusType(Profile prof) =>
            current != null && shown.detail == detail && shown.target == target && prof.EnforceBudgets && Over(prof) ? MessageType.Warning : MessageType.Info;

        // ------------------------------------------------------------------------------------
        void BuildMesh()
        {
            if (mesh == null) mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            mesh.Clear();
            var p = current;
            int n = p.Positions.Length / 2;
            var v = new Vector3[n];
            var c = new Color32[n];
            for (int i = 0; i < n; i++)
            {
                v[i] = new Vector3(p.Positions[2 * i], p.Positions[2 * i + 1], 0);
                c[i] = new Color32(p.Colors[4 * i], p.Colors[4 * i + 1], p.Colors[4 * i + 2], p.Colors[4 * i + 3]);
            }
            mesh.vertices = v;
            mesh.colors32 = c;
            mesh.SetIndices(p.Indices, MeshTopology.Triangles, 0, false);
            mesh.bounds = new Bounds(new Vector3((float)p.Width / 2, (float)p.Height / 2, 0), new Vector3((float)p.Width, (float)p.Height, 1));
        }

        static Material ColoredMaterial(Color tint)
        {
            var m = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            m.SetInt("_Cull", (int)CullMode.Off);
            m.SetInt("_ZWrite", 0);
            m.SetInt("_ZTest", (int)CompareFunction.Always);
            m.SetColor("_Color", tint);
            return m;
        }

        void View(Rect area)
        {
            var e = Event.current;
            float fit = Mathf.Min(area.width / (float)current.Width, area.height / (float)current.Height) * 0.95f;
            if (!fitted) { center = new Vector2((float)current.Width / 2, (float)current.Height / 2); zoom = 1; fitted = true; }
            float scale = fit * zoom;
            if (e.type == EventType.ScrollWheel && area.Contains(e.mousePosition))
            {
                var before = center + (e.mousePosition - area.center) / scale;
                zoom = Mathf.Clamp(zoom * Mathf.Pow(1.15f, -e.delta.y / 3f), 0.5f, 400f);
                scale = fit * zoom;
                center = before - (e.mousePosition - area.center) / scale;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && area.Contains(e.mousePosition))
            {
                center -= e.delta / scale;
                e.Use();
                Repaint();
            }
            if (e.type != EventType.Repaint) return;
            float ppp = EditorGUIUtility.pixelsPerPoint;
            int w = Mathf.Max(1, (int)(area.width * ppp)), h = Mathf.Max(1, (int)(area.height * ppp));
            Render(current, mesh, center, scale, area.width, area.height, w, h, wireframe);
            GUI.DrawTexture(area, rt, ScaleMode.StretchToFill, false);
        }

        /// <summary>Draws the page (and optionally its triangle edges) into the window's render texture.</summary>
        void Render(PreviewPage p, Mesh m, Vector2 c, float scale, float viewW, float viewH, int w, int h, bool edges)
        {
            if (rt == null || rt.width != w || rt.height != h)
            {
                if (rt != null) { rt.Release(); DestroyImmediate(rt); }
                rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.HideAndDontSave, antiAliasing = 4 };
            }
            if (fill == null) fill = ColoredMaterial(Color.white);
            if (wire == null) wire = ColoredMaterial(new Color(0, 0, 0, 0.45f));
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.PushMatrix();
            float hw = viewW / 2 / scale, hh = viewH / 2 / scale;
            // Page space is y down: top of the view = smaller y.
            GL.LoadProjectionMatrix(Matrix4x4.Ortho(c.x - hw, c.x + hw, c.y + hh, c.y - hh, -1, 1));
            GL.modelview = Matrix4x4.identity;
            GL.Clear(true, true, new Color(0.22f, 0.22f, 0.22f));
            fill.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(Color.white);
            GL.Vertex3(0, 0, 0); GL.Vertex3((float)p.Width, 0, 0); GL.Vertex3((float)p.Width, (float)p.Height, 0); GL.Vertex3(0, (float)p.Height, 0);
            GL.End();
            Graphics.DrawMeshNow(m, Matrix4x4.identity);
            if (edges)
            {
                wire.SetPass(0);
                GL.wireframe = true;
                Graphics.DrawMeshNow(m, Matrix4x4.identity);
                GL.wireframe = false;
            }
            // Precomposited regions: translucent orange fill and outline.
            fill.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(new Color(1f, 0.55f, 0.1f, 0.15f));
            foreach (var r in p.Composites)
            {
                GL.Vertex3((float)r.X0, (float)r.Y0, 0); GL.Vertex3((float)r.X1, (float)r.Y0, 0);
                GL.Vertex3((float)r.X1, (float)r.Y1, 0); GL.Vertex3((float)r.X0, (float)r.Y1, 0);
            }
            GL.End();
            GL.Begin(GL.LINES);
            GL.Color(new Color(1f, 0.55f, 0.1f));
            foreach (var r in p.Composites)
            {
                float x0 = (float)r.X0, y0 = (float)r.Y0, x1 = (float)r.X1, y1 = (float)r.Y1;
                GL.Vertex3(x0, y0, 0); GL.Vertex3(x1, y0, 0); GL.Vertex3(x1, y0, 0); GL.Vertex3(x1, y1, 0);
                GL.Vertex3(x1, y1, 0); GL.Vertex3(x0, y1, 0); GL.Vertex3(x0, y1, 0); GL.Vertex3(x0, y0, 0);
            }
            GL.End();
            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        /// <summary>Test hook: renders page <paramref name="k"/> of a PDF at a detail level to a PNG (batch mode).</summary>
        internal static void RenderToPng(string pdf, int k, Profile prof, double detail, int width, string outPng, bool edges, Vector2? focus = null, float zoom = 1)
        {
            var o = new CompileOptions { Input = pdf, CacheDir = System.IO.Path.GetFullPath(PdfUnitySettings.ToolCache) };
            var job = new Job(CancellationToken.None, 600);
            var d = PreviewDocument.Load(o, job);
            var w = CreateInstance<CurvePreviewWindow>();
            try
            {
                w.current = d.Lower(k, prof, detail, job);
                w.BuildMesh();
                int height = (int)(width * w.current.Height / w.current.Width);
                float scale = width / (float)w.current.Width * zoom;
                var c = focus ?? new Vector2((float)w.current.Width / 2, (float)w.current.Height / 2);
                w.Render(w.current, w.mesh, c, scale, width, height, width, height, edges);
                var prev = RenderTexture.active;
                RenderTexture.active = w.rt;
                var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                RenderTexture.active = prev;
                System.IO.File.WriteAllBytes(outPng, tex.EncodeToPNG());
                DestroyImmediate(tex);
                Debug.Log($"[preview] {outPng}: {w.current.Triangles} triangles");
            }
            finally { DestroyImmediate(w); }
        }
    }
}
