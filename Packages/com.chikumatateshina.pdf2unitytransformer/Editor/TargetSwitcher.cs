using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PDF2Unity.Editor
{
    /// <summary>
    /// PC and Quest outputs are separate prefabs in sibling folders ".../&lt;Document&gt;/PC" and ".../Quest"
    /// (SPEC §9). Generated folders are recognised by their _generated.json record, so any output
    /// location works. Swapping runs only on user request; the pre-build hook only warns.
    /// </summary>
    public static class TargetSwitcher
    {
        static readonly Regex PagePrefab = new Regex("^Pages/Page_\\d{4}\\.prefab$");

        public static string ActiveTargetFolder =>
            EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android ? "Quest" : "PC";

        public struct Placement
        {
            public GameObject Instance;
            public string DocFolder;   // ".../<Document>"
            public string Target;      // "PC" | "Quest"
            public string Rest;        // "Document.prefab" | "Pages/Page_0001.prefab"
            public string AssetPath;
        }

        /// <summary>Splits a prefab path into (document folder, target folder, relative rest) if it was generated.</summary>
        public static bool TryParse(string path, out string docFolder, out string target, out string rest)
        {
            docFolder = target = rest = null;
            if (string.IsNullOrEmpty(path)) return false;
            path = path.Replace('\\', '/');
            string targetFolder;
            if (path.EndsWith("/Document.prefab")) { targetFolder = path.Substring(0, path.Length - "/Document.prefab".Length); rest = "Document.prefab"; }
            else
            {
                var dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
                if (dir == null || !dir.EndsWith("/Pages")) return false;
                targetFolder = dir.Substring(0, dir.Length - "/Pages".Length);
                rest = "Pages/" + Path.GetFileName(path);
                if (!PagePrefab.IsMatch(rest)) return false;
            }
            target = Path.GetFileName(targetFolder);
            if (target != "PC" && target != "Quest") return false;
            if (!File.Exists($"{targetFolder}/{AssetGenerator.RecordName}")) return false;
            docFolder = Path.GetDirectoryName(targetFolder)?.Replace('\\', '/');
            return docFolder != null;
        }

        public static List<Placement> FindPlacements()
        {
            var list = new List<Placement>();
            for (int s = 0; s < EditorSceneManager.sceneCount; s++)
            {
                var scene = EditorSceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects()) Collect(root.transform, list);
            }
            return list;
        }

        static void Collect(Transform t, List<Placement> list)
        {
            var go = t.gameObject;
            if (PrefabUtility.IsOutermostPrefabInstanceRoot(go))
            {
                var path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                if (TryParse(path, out var doc, out var target, out var rest))
                {
                    list.Add(new Placement { Instance = go, DocFolder = doc, Target = target, Rest = rest, AssetPath = path });
                    return;
                }
            }
            foreach (Transform c in t) Collect(c, list);
        }

        [MenuItem(PdfUnitySettings.MenuRoot + "配置済み文書をビルドターゲットに切り替え")]
        public static void SwitchAll()
        {
            var want = ActiveTargetFolder;
            int swapped = 0;
            foreach (var p in FindPlacements())
            {
                if (p.Target == want) continue;
                var folder = $"{p.DocFolder}/{want}";
                var counterpart = $"{folder}/{p.Rest}";
                var rec = AssetGenerator.Record.Load(folder);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(counterpart);
                var doc = Path.GetFileName(p.DocFolder);
                if (prefab == null || rec.Status == "fail")
                {
                    Debug.LogWarning($"[PDF2UnityTransformer] 「{doc}」には使用できる {want} 用の出力がありません（PC 専用）。" +
                                     "Quest ビルドから外すか、Quest を出力先にして変換し直してください。", p.Instance);
                    continue;
                }
                if (want == "Quest" && !rec.DeviceVerified)
                    Debug.LogWarning($"[PDF2UnityTransformer] 「{doc}」の Quest 出力はまだ実機確認されていません。", p.Instance);
                var old = p.Instance.transform;
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, old.gameObject.scene);
                Undo.RegisterCreatedObjectUndo(inst, "PDF 文書のターゲット切り替え");
                inst.transform.SetParent(old.parent, false);
                inst.transform.localPosition = old.localPosition;
                inst.transform.localRotation = old.localRotation;
                inst.transform.localScale = old.localScale;
                inst.transform.SetSiblingIndex(old.GetSiblingIndex());
                inst.name = old.name;
                inst.SetActive(old.gameObject.activeSelf);
                Undo.DestroyObjectImmediate(old.gameObject);
                swapped++;
            }
            Debug.Log($"[PDF2UnityTransformer] 配置済みの文書 {swapped} 件を {want} 用に切り替えました。");
        }

        public static List<string> Mismatches()
        {
            var want = ActiveTargetFolder;
            var r = new List<string>();
            foreach (var p in FindPlacements())
                if (p.Target != want) r.Add($"{p.Instance.name} は {p.Target} 用のアセットを使っています（{p.AssetPath}）");
            return r;
        }
    }

    /// <summary>Warns (never modifies or fails) when placed documents do not match the build target.</summary>
    sealed class TargetCheckOnBuild : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            foreach (var m in TargetSwitcher.Mismatches())
                Debug.LogWarning($"[PDF2UnityTransformer] {m}。Tools > PDF2UnityTransformer > 配置済み文書をビルドターゲットに切り替え を実行してください。");
        }
    }
}
