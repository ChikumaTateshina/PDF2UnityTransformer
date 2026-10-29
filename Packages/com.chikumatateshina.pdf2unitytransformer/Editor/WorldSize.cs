using System;
using System.IO;
using UnityEditor;

namespace PDF2Unity.Editor
{
    /// <summary>
    /// Measures what a generated prefab adds to the world download: builds it into an LZMA asset bundle for the
    /// target platform (as a world upload does) and returns the bundle size.
    /// </summary>
    public static class WorldSize
    {
        public static BuildTarget TargetFor(string target) => target == "quest" ? BuildTarget.Android : BuildTarget.StandaloneWindows64;

        public static bool Supported(string target)
        {
            var bt = TargetFor(target);
            return BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(bt), bt);
        }

        /// <summary>Bundle size in bytes of <paramref name="prefabPath"/> and everything it references.</summary>
        public static long Measure(string prefabPath, string target)
        {
            var dir = Path.Combine(Path.GetTempPath(), "pdf2unity-size-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var build = new[] { new AssetBundleBuild { assetBundleName = "document", assetNames = new[] { prefabPath } } };
                if (BuildPipeline.BuildAssetBundles(dir, build, BuildAssetBundleOptions.None, TargetFor(target)) == null)
                    throw new InvalidOperationException("アセットバンドルを作れませんでした。Console を確認してください。");
                return new FileInfo(Path.Combine(dir, "document")).Length;
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }
    }
}
