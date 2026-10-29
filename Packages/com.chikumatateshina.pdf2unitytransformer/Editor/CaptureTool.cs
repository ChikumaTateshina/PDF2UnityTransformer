using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PDF2Unity.Editor
{
    /// <summary>
    /// Offscreen capture of a generated page prefab (SPEC §10.2 step 2). The camera looks at the
    /// page front from -Z with an orthographic frustum matching the page, so the PNG can be
    /// compared with the PDFium reference (tools/quality/compare.py --unity).
    /// </summary>
    public static class CaptureTool
    {
        public static void CapturePage(GameObject pagePrefab, Vector2 unitySize, int width, int height, string outPng)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var go = (GameObject)PrefabUtility.InstantiatePrefab(pagePrefab, scene);
            go.transform.position = Vector3.zero;
            go.transform.rotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            var camGo = new GameObject("PDF2UnityCaptureCamera");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(camGo, scene);
            var cam = camGo.AddComponent<Camera>();
            cam.scene = scene;
            cam.orthographic = true;
            cam.orthographicSize = unitySize.y / 2f;
            cam.aspect = unitySize.x / unitySize.y;
            cam.transform.position = new Vector3(0, 0, -10);
            cam.transform.rotation = Quaternion.identity;   // looks along +Z at the page front
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 100f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.white;
            cam.allowMSAA = true;

            // Supersampled render (MSAA is not reliably applied to offscreen cameras in batch mode).
            int ss = Mathf.Clamp(SystemInfo.maxTextureSize / Mathf.Max(width, height), 1, 4);
            var desc = new RenderTextureDescriptor(width * ss, height * ss, RenderTextureFormat.ARGB32, 24)
            {
                sRGB = QualitySettings.activeColorSpace == ColorSpace.Linear,
            };
            var rt = new RenderTexture(desc);
            var prev = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var big = new Texture2D(width * ss, height * ss, TextureFormat.RGBA32, false, false);
                big.ReadPixels(new Rect(0, 0, width * ss, height * ss), 0, 0);
                var src = big.GetPixels32();
                var dst = new Color32[width * height];
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int dy = 0; dy < ss; dy++)
                    for (int dx = 0; dx < ss; dx++)
                    {
                        var c = src[(y * ss + dy) * width * ss + x * ss + dx];
                        r += c.r; g += c.g; b += c.b;
                    }
                    int n = ss * ss;
                    dst[y * width + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
                }
                var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
                tex.SetPixels32(dst);
                tex.Apply();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPng)));
                File.WriteAllBytes(outPng, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                Object.DestroyImmediate(big);
            }
            finally
            {
                RenderTexture.active = prev;
                cam.targetTexture = null;
                rt.Release();
                Object.DestroyImmediate(rt);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
    }
}
