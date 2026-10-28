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
    /// PC and Quest outputs are separate prefabs (SPEC §9). This swaps placed document/page
    /// prefab instances to the variant matching the active build target. It runs only on user
    /// request; the pre-build hook only warns, so the VRChat SDK build flow is not altered.
    /// Documents whose Quest output failed or was never generated are left untouched and
    /// reported as PC-only.
    /// </summary>
    public static class TargetSwitcher
    {
        static readonly Regex Pattern = new Regex(
            "^" + Regex.Escape(PdfUnitySettings.GeneratedRoot) + "/(?<doc>[^/]+)/(?<t>PC|Quest)/(?<rest>Document\\.prefab|Pages/Page_\\d{4}\\.prefab)$");

        public static string ActiveTargetFolder =>
            EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android ? "Quest" : "PC";

        public struct Placement
        {
            public GameObject Instance;
            public string Doc, Target, Rest, AssetPath;
        }

        public static List<Placement> FindPlacements()
        {
            var list = new List<Placement>();
            for (int s = 0; s < EditorSceneManager.sceneCount; s++)
            {
                var scene = EditorSceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    Collect(root.transform, list);
            }
            return list;
        }

        static void Collect(Transform t, List<Placement> list)
        {
            var go = t.gameObject;
            if (PrefabUtility.IsOutermostPrefabInstanceRoot(go))
            {
                var path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                var m = Pattern.Match(path ?? "");
                if (m.Success)
                {
                    list.Add(new Placement { Instance = go, Doc = m.Groups["doc"].Value, Target = m.Groups["t"].Value,
                                             Rest = m.Groups["rest"].Value, AssetPath = path });
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
                var counterpart = $"{PdfUnitySettings.GeneratedRoot}/{p.Doc}/{want}/{p.Rest}";
                var rec = AssetGenerator.Record.Load($"{PdfUnitySettings.GeneratedRoot}/{p.Doc}/{want}");
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(counterpart);
                if (prefab == null || rec.Status == "fail")
                {
                    Debug.LogWarning($"[PDF2UnityTransformer] 「{p.Doc}」には使用できる {want} 用の出力がありません（PC 専用）。" +
                                     "Quest ビルドから外すか、Quest を出力先にして変換し直してください。", p.Instance);
                    continue;
                }
                if (want == "Quest" && !rec.DeviceVerified)
                    Debug.LogWarning($"[PDF2UnityTransformer] 「{p.Doc}」の Quest 出力はまだ実機確認されていません。", p.Instance);
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
