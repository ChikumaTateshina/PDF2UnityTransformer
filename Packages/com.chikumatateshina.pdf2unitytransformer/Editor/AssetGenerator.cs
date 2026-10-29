using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace PDF2Unity.Editor
{
    public sealed class GenerateOptions
    {
        public Color PaperColor = Color.white;
        public Color PaperBackColor = new Color(0.85f, 0.85f, 0.85f, 1f);
        public bool PaperBack = true;
        public float ContentOffset = 0.0005f;   // Unity units in front of the paper (towards -Z)
        public int SortingOrderBase = 0;
        public bool OverwriteUserEdits = false;
        public bool AllowFailedTarget = false;
        public string OutputRoot = PdfUnitySettings.DefaultOutputFolder; // "Assets/..." folder chosen by the user
    }

    public sealed class GenerateReport
    {
        public string Folder;
        public string DocumentPrefab;
        public readonly List<string> Written = new List<string>();
        public readonly List<string> SkippedUserEdited = new List<string>();
        public readonly List<string> Removed = new List<string>();
        public readonly List<string> Messages = new List<string>();
    }

    /// <summary>
    /// Generates Unity assets from an Asset Exchange Package (SPEC §8):
    /// Textures/, Materials/, Meshes/, Pages/Page_NNNN.prefab and Document.prefab under
    /// Assets/PDF2Unity/Generated/&lt;Document&gt;/&lt;Target&gt;/. Existing assets are updated in place
    /// (GUIDs and scene references survive recompiles). Files whose content no longer matches
    /// the hash recorded at generation time were edited by the user and are never overwritten
    /// unless explicitly requested.
    /// </summary>
    public static class AssetGenerator
    {
        public const string RecordName = "_generated.json";
        public const string ShaderSolid = "PDF2Unity/VertexColor";
        public const string ShaderTextured = "PDF2Unity/Textured";
        public const string ShaderPaper = "PDF2Unity/Paper";

        public static string SafeName(string s)
        {
            var n = Regex.Replace(s ?? "Document", @"[^A-Za-z0-9_\-]+", "_").Trim('_');
            return string.IsNullOrEmpty(n) ? "Document" : (n.Length > 48 ? n.Substring(0, 48) : n);
        }

        public static string TargetFolderName(string target) => target == "quest" ? "Quest" : "PC";

        public static string FolderFor(string outputRoot, ExchangePackage pkg, string target)
        {
            var root = string.IsNullOrEmpty(outputRoot) ? PdfUnitySettings.DefaultOutputFolder : outputRoot.Replace("\\", "/").TrimEnd('/');
            return $"{root}/{SafeName(pkg.SourceName)}/{TargetFolderName(target)}";
        }

        public static GenerateReport Generate(ExchangePackage pkg, string target, GenerateOptions opt)
        {
            if (!pkg.Targets.TryGetValue(target, out var tinfo))
                throw new InvalidOperationException($"変換データに {L.Target(target)} 用の出力がありません。");
            if (tinfo.Status == "fail" && !opt.AllowFailedTarget)
                throw new InvalidOperationException(
                    $"{L.Target(target)} の確認に通らなかったため生成しません（{string.Join("; ", tinfo.BudgetFailures)}）。");
            if (!PdfUnitySettings.IsAssetsFolder(opt.OutputRoot))
                throw new InvalidOperationException("出力先は Assets フォルダ内を指定してください。");
            var rep = new GenerateReport { Folder = FolderFor(opt.OutputRoot, pkg, target) };
            var folder = rep.Folder;
            foreach (var sub in new[] { "", "/Textures", "/Materials", "/Meshes", "/Pages" }) EnsureFolder(folder + sub);
            var record = Record.Load(folder);
            var produced = new HashSet<string>();
            var pagesData = pkg.PageNumbers().Select(n => pkg.LoadPage(n, target)).ToList();

            // Import settings are applied during the first import (TextureImport.Pending), so each texture is
            // imported once; textures already imported with the same settings are not touched.
            var texSpecs = new Dictionary<string, TextureSpec>();
            foreach (var b in pagesData.SelectMany(p => p.Batches).Where(b => b.Kind == "textured"))
            {
                var dest = $"{folder}/Textures/{Path.GetFileName(b.Texture)}";
                if (!texSpecs.ContainsKey(dest)) texSpecs[dest] = TextureSpec.For(dest, tinfo, b.Composite);
            }
            foreach (var kv in texSpecs) TextureImport.Pending[kv.Key] = kv.Value;
            try
            {
                AssetDatabase.StartAssetEditing();
                // ---- textures (copied, then imported) -------------------------------------
                var texPaths = new Dictionary<string, string>();
                foreach (var b in pagesData.SelectMany(p => p.Batches).Where(b => b.Kind == "textured"))
                {
                    if (texPaths.ContainsKey(b.Texture)) continue;
                    var dest = $"{folder}/Textures/{Path.GetFileName(b.Texture)}";
                    texPaths[b.Texture] = dest;
                    produced.Add(dest);
                    var bytes = pkg.ReadVerified(b.Texture);
                    if (File.Exists(dest) && ExchangePackage.Sha256Hex(File.ReadAllBytes(dest)) == ExchangePackage.Sha256Hex(bytes))
                        continue;
                    if (!record.CanWrite(dest, opt, rep)) continue;
                    File.WriteAllBytes(dest, bytes);
                    rep.Written.Add(dest);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            try
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                foreach (var kv in texSpecs)
                {
                    var imp = AssetImporter.GetAtPath(kv.Key) as TextureImporter;
                    if (imp != null && kv.Value.Apply(imp)) imp.SaveAndReimport();
                }
            }
            finally
            {
                TextureImport.Pending.Clear();
            }

            var texAssets = new Dictionary<string, string>();
            foreach (var b in pagesData.SelectMany(p => p.Batches).Where(b => b.Kind == "textured"))
                texAssets[b.Texture] = $"{folder}/Textures/{Path.GetFileName(b.Texture)}";

            // ---- materials ---------------------------------------------------------------
            var solidMat = WriteMaterial($"{folder}/Materials/PDF_Solid.mat", ShaderSolid, null, null, record, opt, rep, produced);
            var paperMat = WriteMaterial($"{folder}/Materials/PDF_Paper.mat", ShaderPaper, null, opt.PaperColor, record, opt, rep, produced);
            var backMat = opt.PaperBack
                ? WriteMaterial($"{folder}/Materials/PDF_PaperBack.mat", ShaderPaper, null, opt.PaperBackColor, record, opt, rep, produced)
                : null;
            var texMats = new Dictionary<string, Material>();
            foreach (var kv in texAssets)
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(kv.Value);
                var name = Path.GetFileNameWithoutExtension(kv.Value);
                texMats[kv.Key] = WriteMaterial($"{folder}/Materials/Tex_{name}.mat", ShaderTextured, tex, null, record, opt, rep, produced);
            }

            // ---- meshes + page prefabs ----------------------------------------------------
            var pagePrefabs = new List<(PageData page, GameObject prefab)>();
            foreach (var page in pagesData)
            {
                var pname = $"Page_{page.Page:D4}";
                var paperMesh = WriteMesh($"{folder}/Meshes/{pname}_Paper.asset", QuadMesh(page.UnitySize, false), record, opt, rep, produced);
                var backMesh = opt.PaperBack
                    ? WriteMesh($"{folder}/Meshes/{pname}_PaperBack.asset", QuadMesh(page.UnitySize, true), record, opt, rep, produced)
                    : null;
                var batchMeshes = new List<Mesh>();
                foreach (var b in page.Batches)
                    batchMeshes.Add(WriteMesh($"{folder}/Meshes/{pname}_B{b.Order:D3}.asset", BatchMesh(b, $"{pname}_B{b.Order:D3}"), record, opt, rep, produced));

                var root = new GameObject(pname);
                try
                {
                    AddRenderer(root.transform, "Paper", paperMesh, paperMat, 0, Vector3.zero);
                    if (backMesh != null) AddRenderer(root.transform, "PaperBack", backMesh, backMat, 0, Vector3.zero);
                    var content = new GameObject("Content").transform;
                    content.SetParent(root.transform, false);
                    content.localPosition = new Vector3(0, 0, -opt.ContentOffset);
                    for (int k = 0; k < page.Batches.Count; k++)
                    {
                        var b = page.Batches[k];
                        if (batchMeshes[k] == null) continue;
                        var mat = b.Kind == "textured" ? texMats[b.Texture] : solidMat;
                        var label = b.Composite ? "Composite" : b.Atlas ? "Atlas" : b.Kind == "textured" ? "Image" : "Vector";
                        AddRenderer(content, $"B{b.Order:D3}_{label}", batchMeshes[k], mat, opt.SortingOrderBase + 1 + b.Order, Vector3.zero);
                    }
                    var path = $"{folder}/Pages/{pname}.prefab";
                    produced.Add(path);
                    if (record.CanWrite(path, opt, rep))
                    {
                        PrefabUtility.SaveAsPrefabAsset(root, path);
                        rep.Written.Add(path);
                    }
                    pagePrefabs.Add((page, AssetDatabase.LoadAssetAtPath<GameObject>(path)));
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
            }

            // ---- Document.prefab (nested page prefabs, layout from the compiler) -----------
            var docRoot = new GameObject($"PDF_{SafeName(pkg.SourceName)}");
            try
            {
                docRoot.transform.localScale = Vector3.one * pkg.WorldScale;
                foreach (var (page, prefab) in pagePrefabs)
                {
                    if (prefab == null) continue;
                    var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    inst.transform.SetParent(docRoot.transform, false);
                    inst.transform.localPosition = page.LayoutPosition;
                }
                var docPath = $"{folder}/Document.prefab";
                produced.Add(docPath);
                if (record.CanWrite(docPath, opt, rep))
                {
                    PrefabUtility.SaveAsPrefabAsset(docRoot, docPath);
                    rep.Written.Add(docPath);
                }
                rep.DocumentPrefab = docPath;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(docRoot);
            }
            AssetDatabase.SaveAssets();

            // ---- remove stale outputs of previous compiles (only if unmodified) ------------
            foreach (var old in record.Files.Keys.ToList())
            {
                if (produced.Contains(old) || !File.Exists(old)) continue;
                if (record.IsUnmodified(old) || opt.OverwriteUserEdits)
                {
                    AssetDatabase.DeleteAsset(old);
                    rep.Removed.Add(old);
                }
                else rep.SkippedUserEdited.Add(old);
            }

            record.SourceHash = pkg.SourceHash;
            record.Target = target;
            record.Status = tinfo.Status;
            record.ReviewReasons = tinfo.ReviewReasons;
            record.Files.Clear();
            foreach (var p in produced.Where(File.Exists)) record.Files[p] = ExchangePackage.Sha256Hex(File.ReadAllBytes(p));
            record.Save(folder);
            AssetDatabase.Refresh();
            foreach (var s in rep.SkippedUserEdited)
                rep.Messages.Add($"生成後に編集されたため上書きしませんでした: {s}（調整は Prefab Variant で行うことをおすすめします）");
            return rep;
        }

        // ------------------------------------------------------------------------------------
        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        /// <summary>Import settings of a generated texture.</summary>
        public struct TextureSpec
        {
            public int MaxSize;
            public bool Quest;
            public bool Opaque; // light mode, opaque photographic image (JPEG): DXT1 on PC, no alpha channel

            public static TextureSpec For(string path, TargetInfo t, bool composite) => new TextureSpec
            {
                MaxSize = NearestSize(t.MaxTextureSize),
                Quest = t.Name == "quest",
                Opaque = t.ImageQuality == "light" && !composite && path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase),
            };

            string Signature => $"pdf2unity:1:{MaxSize}:{(Quest ? "q" : "p")}:{(Opaque ? "o" : "a")}";

            /// <summary>Applies the settings; false if the importer already had exactly these settings.</summary>
            public bool Apply(TextureImporter imp)
            {
                if (imp.userData == Signature) return false;
                imp.textureType = TextureImporterType.Default;
                imp.sRGBTexture = true;
                // Delta composites and masks carry alpha (only the pixels changed by an effect are opaque).
                imp.alphaSource = Opaque ? TextureImporterAlphaSource.None : TextureImporterAlphaSource.FromInput;
                imp.alphaIsTransparency = !Opaque;
                imp.mipmapEnabled = true;
                imp.mipmapFilter = TextureImporterMipFilter.KaiserFilter;
                imp.wrapMode = TextureWrapMode.Clamp;
                imp.filterMode = FilterMode.Trilinear;
                imp.anisoLevel = 4;
                imp.npotScale = TextureImporterNPOTScale.None;
                imp.isReadable = false;
                imp.maxTextureSize = MaxSize;
                // PC: DXT1 (4 bpp) for opaque photos in the light mode, BC7 (8 bpp) otherwise.
                imp.textureCompression = Opaque ? TextureImporterCompression.Compressed : TextureImporterCompression.CompressedHQ;
                imp.crunchedCompression = false;
                if (Quest)
                {
                    imp.SetPlatformTextureSettings(new TextureImporterPlatformSettings
                    {
                        name = "Android", overridden = true, maxTextureSize = MaxSize,
                        format = TextureImporterFormat.ASTC_6x6, compressionQuality = 100,
                    });
                }
                imp.userData = Signature;
                return true;
            }
        }

        /// <summary>Applies <see cref="TextureSpec"/>s while textures are imported for the first time.</summary>
        internal sealed class TextureImport : AssetPostprocessor
        {
            internal static readonly Dictionary<string, TextureSpec> Pending = new Dictionary<string, TextureSpec>();

            void OnPreprocessTexture()
            {
                if (Pending.TryGetValue(assetPath, out var spec) && assetImporter is TextureImporter imp) spec.Apply(imp);
            }
        }

        static int NearestSize(int v)
        {
            int s = 32;
            while (s < v && s < 16384) s *= 2;
            return s;
        }

        static Material WriteMaterial(string path, string shaderName, Texture tex, Color? color, Record record,
                                      GenerateOptions opt, GenerateReport rep, HashSet<string> produced)
        {
            produced.Add(path);
            var shader = Shader.Find(shaderName);
            if (shader == null) throw new InvalidOperationException($"シェーダー {shaderName} が見つかりません。パッケージが正しく導入されているか確認してください。");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null && !record.CanWrite(path, opt, rep)) return mat;
            bool created = mat == null;
            if (created) mat = new Material(shader);
            mat.shader = shader;
            if (tex != null) mat.mainTexture = tex;
            if (color.HasValue) mat.SetColor("_Color", color.Value);
            mat.enableInstancing = false;
            if (created) AssetDatabase.CreateAsset(mat, path);
            else EditorUtility.SetDirty(mat);
            rep.Written.Add(path);
            return mat;
        }

        static Mesh WriteMesh(string path, Mesh src, Record record, GenerateOptions opt, GenerateReport rep, HashSet<string> produced)
        {
            produced.Add(path);
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null && !record.CanWrite(path, opt, rep))
            {
                UnityEngine.Object.DestroyImmediate(src);
                return existing;
            }
            if (existing == null)
            {
                AssetDatabase.CreateAsset(src, path);
                SetReadable(src, false);
                rep.Written.Add(path);
                return src;
            }
            // Update in place so the GUID (and every reference to it) is preserved.
            SetReadable(existing, true);
            existing.Clear();
            existing.indexFormat = src.indexFormat;
            existing.vertices = src.vertices;
            existing.colors32 = src.colors32;
            existing.uv = src.uv;
            existing.SetIndices(src.GetIndices(0), MeshTopology.Triangles, 0, true);
            existing.bounds = src.bounds;
            existing.name = src.name;
            SetReadable(existing, false);
            EditorUtility.SetDirty(existing);
            UnityEngine.Object.DestroyImmediate(src);
            rep.Written.Add(path);
            return existing;
        }

        /// <summary>
        /// Non-readable meshes keep no CPU-side copy in the player (about half the mesh memory).
        /// Nothing reads the generated meshes at runtime.
        /// </summary>
        static void SetReadable(Mesh m, bool readable)
        {
            var so = new SerializedObject(m);
            var p = so.FindProperty("m_IsReadable");
            if (p == null || p.boolValue == readable) return;
            p.boolValue = readable;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Batch geometry. Triangle order = PDF painting order; never optimised/reordered.</summary>
        public static Mesh BatchMesh(BatchData b, string name)
        {
            var m = new Mesh { name = name };
            m.indexFormat = b.Vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            m.vertices = b.Vertices;
            m.colors32 = b.Colors;
            if (b.Uvs != null) m.uv = b.Uvs;
            // No normals: the unlit shaders do not use them (saves 12 bytes per vertex).
            m.SetIndices(b.Indices, MeshTopology.Triangles, 0, true);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Page-sized quad centred at the origin; front faces -Z (or +Z for the back).</summary>
        public static Mesh QuadMesh(Vector2 size, bool back)
        {
            float w = size.x / 2, h = size.y / 2;
            var m = new Mesh { name = back ? "PaperBack" : "Paper" };
            m.vertices = new[] { new Vector3(-w, -h, 0), new Vector3(-w, h, 0), new Vector3(w, h, 0), new Vector3(w, -h, 0) };
            m.uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
            m.triangles = back ? new[] { 0, 2, 1, 0, 3, 2 } : new[] { 0, 1, 2, 0, 2, 3 };
            m.RecalculateBounds();
            return m;
        }

        static void AddRenderer(Transform parent, string name, Mesh mesh, Material mat, int sortingOrder, Vector3 pos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.sortingOrder = sortingOrder;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            r.allowOcclusionWhenDynamic = true;
            // Batching Static would merge meshes and can break the painting order.
            GameObjectUtility.SetStaticEditorFlags(go, 0);
        }

        // ------------------------------------------------------------------------------------
        /// <summary>Hashes of generated files, used to detect user edits (never overwritten).</summary>
        public sealed class Record
        {
            public string SourceHash = "";
            public string Target = "";
            public string Status = "";
            public bool DeviceVerified;
            public List<string> ReviewReasons = new List<string>();
            public readonly Dictionary<string, string> Files = new Dictionary<string, string>();

            public static Record Load(string folder)
            {
                var r = new Record();
                var path = $"{folder}/{RecordName}";
                if (!File.Exists(path)) return r;
                var j = Json.Parse(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
                r.SourceHash = J.Str(j, "sourceHash", "");
                r.Target = J.Str(j, "target", "");
                r.Status = J.Str(j, "status", "");
                r.DeviceVerified = J.Bool(j, "deviceVerified");
                foreach (var x in J.Arr(j, "reviewReasons") ?? new List<object>()) r.ReviewReasons.Add(x as string);
                var files = J.Obj(j, "files");
                if (files != null) foreach (var kv in files) r.Files[kv.Key] = kv.Value as string;
                return r;
            }

            public bool IsUnmodified(string path)
                => Files.TryGetValue(path, out var h) && File.Exists(path) && ExchangePackage.Sha256Hex(File.ReadAllBytes(path)) == h;

            public bool CanWrite(string path, GenerateOptions opt, GenerateReport rep)
            {
                if (!File.Exists(path) || opt.OverwriteUserEdits || IsUnmodified(path)) return true;
                if (!rep.SkippedUserEdited.Contains(path)) rep.SkippedUserEdited.Add(path);
                return false;
            }

            public void Save(string folder)
            {
                var sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append($"  \"generator\": \"{PdfUnitySettings.PackageName}\",\n");
                sb.Append($"  \"sourceHash\": \"{Esc(SourceHash)}\",\n");
                sb.Append($"  \"target\": \"{Esc(Target)}\",\n");
                sb.Append($"  \"status\": \"{Esc(Status)}\",\n");
                sb.Append($"  \"deviceVerified\": {(DeviceVerified ? "true" : "false")},\n");
                sb.Append($"  \"reviewReasons\": [{string.Join(", ", ReviewReasons.Select(x => $"\"{Esc(x)}\""))}],\n");
                sb.Append("  \"files\": {\n");
                var keys = Files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                for (int k = 0; k < keys.Count; k++)
                    sb.Append($"    \"{Esc(keys[k])}\": \"{Files[keys[k]]}\"{(k + 1 < keys.Count ? "," : "")}\n");
                sb.Append("  }\n}\n");
                File.WriteAllText($"{folder}/{RecordName}", sb.ToString(), new UTF8Encoding(false));
            }

            static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
