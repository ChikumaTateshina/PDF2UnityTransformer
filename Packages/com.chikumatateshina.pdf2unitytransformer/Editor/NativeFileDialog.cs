using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace PDF2Unity.Editor
{
    /// <summary>
    /// Windows "Open" dialog with multiple selection (EditorUtility.OpenFilePanel selects one file only).
    /// Returns null when the dialog could not be shown, so callers can fall back to the Unity panel.
    /// </summary>
    public static class NativeFileDialog
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        sealed class OpenFileName
        {
            public int lStructSize = Marshal.SizeOf(typeof(OpenFileName));
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            public string lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetOpenFileNameW([In, Out] OpenFileName ofn);

        [DllImport("comdlg32.dll")]
        static extern int CommDlgExtendedError();

        [DllImport("user32.dll")]
        static extern IntPtr GetActiveWindow();

        const int OFN_NOCHANGEDIR = 0x8, OFN_ALLOWMULTISELECT = 0x200, OFN_PATHMUSTEXIST = 0x800,
                  OFN_FILEMUSTEXIST = 0x1000, OFN_EXPLORER = 0x80000;
        const int BufferChars = 1 << 16;

        /// <summary>Selected files (empty if cancelled), or null if the dialog is unavailable.</summary>
        public static List<string> OpenPdfs(string title, string initialDir)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
            var buffer = Marshal.AllocHGlobal(BufferChars * 2);
            try
            {
                Marshal.Copy(new byte[BufferChars * 2], 0, buffer, BufferChars * 2);
                var ofn = new OpenFileName
                {
                    hwndOwner = GetActiveWindow(),
                    lpstrFilter = "PDF ファイル\0*.pdf\0すべてのファイル\0*.*\0\0",
                    nFilterIndex = 1,
                    lpstrFile = buffer,
                    nMaxFile = BufferChars,
                    lpstrInitialDir = initialDir,
                    lpstrTitle = title,
                    Flags = OFN_EXPLORER | OFN_ALLOWMULTISELECT | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
                };
                if (GetOpenFileNameW(ofn)) return Parse(Marshal.PtrToStringUni(buffer, BufferChars));
                return CommDlgExtendedError() == 0 ? new List<string>() : null; // 0: cancelled by the user
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// Splits the dialog's result: one full path, or a folder followed by file names, each NUL-terminated
        /// and the list ended by an empty string.
        /// </summary>
        public static List<string> Parse(string raw)
        {
            var parts = new List<string>();
            foreach (var p in raw.Split('\0'))
            {
                if (p.Length == 0) break;
                parts.Add(p);
            }
            if (parts.Count <= 1) return parts;
            var files = new List<string>();
            for (int k = 1; k < parts.Count; k++) files.Add(Path.Combine(parts[0], parts[k]));
            return files;
        }
    }
}
