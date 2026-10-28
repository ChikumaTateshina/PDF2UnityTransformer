using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PDF2Unity.Editor
{
    /// <summary>
    /// Machine specific settings (EditorPrefs). The compiler sources ship inside this package
    /// (Compiler~, ignored by the Unity importer); its Python environment lives per user under
    /// %LOCALAPPDATA%/PDF2UnityTransformer and is created by <see cref="PythonEnvironment"/>.
    /// </summary>
    public static class PdfUnitySettings
    {
        const string KeyPython = "PDF2UnityTransformer.PythonExe";
        const string KeyBasePython = "PDF2UnityTransformer.BasePythonExe";
        const string KeyCompiler = "PDF2UnityTransformer.CompilerDir";
        const string KeyTimeout = "PDF2UnityTransformer.TimeoutSec";
        public const string PackageName = "com.chikumatateshina.pdf2unitytransformer";
        public const string ProductName = "PDF2UnityTransformer";
        public const string MenuRoot = "Tools/PDF2UnityTransformer/";
        public const string GeneratedRoot = "Assets/PDF2UnityTransformer/Generated";
        public const string CacheRoot = "Library/PDF2UnityTransformer/Packages";

        public static string PackageRoot
        {
            get
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath($"Packages/{PackageName}");
                return info != null ? info.resolvedPath : Path.GetFullPath($"Packages/{PackageName}");
            }
        }

        public static string BundledCompilerDir => Path.Combine(PackageRoot, "Compiler~");

        public static string EnvironmentDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName, "python-env");

        public static string ManagedPythonExe => Path.Combine(EnvironmentDir,
            Application.platform == RuntimePlatform.WindowsEditor ? "Scripts/python.exe" : "bin/python");

        /// <summary>Interpreter used to run the compiler (the managed venv unless overridden).</summary>
        public static string PythonExe
        {
            get
            {
                var v = EditorPrefs.GetString(KeyPython, "");
                return !string.IsNullOrEmpty(v) ? v : ManagedPythonExe;
            }
            set => EditorPrefs.SetString(KeyPython, value == ManagedPythonExe ? "" : value ?? "");
        }

        /// <summary>System Python used once to create the managed venv (empty = auto-detect).</summary>
        public static string BasePythonExe
        {
            get => EditorPrefs.GetString(KeyBasePython, "");
            set => EditorPrefs.SetString(KeyBasePython, value ?? "");
        }

        public static string CompilerDir
        {
            get
            {
                var v = EditorPrefs.GetString(KeyCompiler, "");
                return !string.IsNullOrEmpty(v) ? v : BundledCompilerDir;
            }
            set => EditorPrefs.SetString(KeyCompiler, value == BundledCompilerDir ? "" : value ?? "");
        }

        public static int TimeoutSec
        {
            get => EditorPrefs.GetInt(KeyTimeout, 1800);
            set => EditorPrefs.SetInt(KeyTimeout, Mathf.Clamp(value, 10, 24 * 3600));
        }
    }
}
