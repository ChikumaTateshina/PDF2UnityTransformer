using System.IO;
using UnityEditor;
using UnityEngine;

namespace PDF2Unity.Editor
{
    /// <summary>Project paths and per-machine settings of the editor tooling.</summary>
    public static class PdfUnitySettings
    {
        const string KeyTimeout = "PDF2UnityTransformer.TimeoutSec";
        public const string PackageName = "com.chikumatateshina.pdf2unitytransformer";
        public const string ProductName = "PDF2UnityTransformer";
        public const string MenuRoot = "Tools/PDF2UnityTransformer/";
        public const string DefaultOutputFolder = "Assets/PDF2UnityTransformer/Generated";
        public const string CacheRoot = "Library/PDF2UnityTransformer/Packages";
        public const string ToolCache = "Library/PDF2UnityTransformer/Cache";

        public static int TimeoutSec
        {
            get => EditorPrefs.GetInt(KeyTimeout, 1800);
            set => EditorPrefs.SetInt(KeyTimeout, Mathf.Clamp(value, 10, 24 * 3600));
        }

        /// <summary>True when <paramref name="folder"/> is "Assets" or a folder below it.</summary>
        public static bool IsAssetsFolder(string folder)
        {
            var f = (folder ?? "").Replace('\\', '/').TrimEnd('/');
            return f == "Assets" || f.StartsWith("Assets/");
        }

        /// <summary>Converts an absolute path inside the project to "Assets/..." (null when outside).</summary>
        public static string ToProjectRelative(string absolute)
        {
            if (string.IsNullOrEmpty(absolute)) return null;
            var full = Path.GetFullPath(absolute).Replace('\\', '/');
            var assets = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
            if (full == assets) return "Assets";
            if (full.StartsWith(assets + "/")) return "Assets" + full.Substring(assets.Length);
            return null;
        }
    }
}
