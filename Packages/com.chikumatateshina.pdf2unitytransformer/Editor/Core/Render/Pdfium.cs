using System;
using System.Runtime.InteropServices;

namespace PDF2Unity.Core.Render
{
    /// <summary>
    /// Minimal P/Invoke surface of PDFium (BSD-3-Clause, bundled as an Editor-only plugin).
    /// PDFium is not thread-safe: every call goes through <see cref="Gate"/>.
    /// </summary>
    public static class Pdfium
    {
        const string Dll = "pdfium";
        public static readonly object Gate = new object();
        static bool initialised;

        [StructLayout(LayoutKind.Sequential)]
        public struct FS_MATRIX { public float a, b, c, d, e, f; }

        [StructLayout(LayoutKind.Sequential)]
        public struct FS_RECTF { public float left, top, right, bottom; }

        [DllImport(Dll)] static extern void FPDF_InitLibrary();
        [DllImport(Dll)] public static extern IntPtr FPDF_LoadMemDocument64(IntPtr data, UIntPtr size, [MarshalAs(UnmanagedType.LPStr)] string password);
        [DllImport(Dll)] public static extern void FPDF_CloseDocument(IntPtr doc);
        [DllImport(Dll)] public static extern int FPDF_GetPageCount(IntPtr doc);
        [DllImport(Dll)] public static extern uint FPDF_GetLastError();
        [DllImport(Dll)] public static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
        [DllImport(Dll)] public static extern void FPDF_ClosePage(IntPtr page);
        [DllImport(Dll)] public static extern float FPDF_GetPageWidthF(IntPtr page);
        [DllImport(Dll)] public static extern float FPDF_GetPageHeightF(IntPtr page);
        [DllImport(Dll)] public static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
        [DllImport(Dll)] public static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
        [DllImport(Dll)] public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
        [DllImport(Dll)] public static extern int FPDFBitmap_GetStride(IntPtr bitmap);
        [DllImport(Dll)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);
        [DllImport(Dll)] public static extern void FPDF_RenderPageBitmapWithMatrix(IntPtr bitmap, IntPtr page, ref FS_MATRIX matrix, ref FS_RECTF clip, int flags);

        public const int FlagReverseByteOrder = 0x10; // output RGBA instead of BGRA

        public static void Init()
        {
            lock (Gate)
            {
                if (initialised) return;
                FPDF_InitLibrary();
                initialised = true;
            }
        }
    }

    /// <summary>RGBA8 image, rows top to bottom.</summary>
    public sealed class RgbaImage
    {
        public readonly int Width, Height;
        public readonly byte[] Pixels;

        public RgbaImage(int w, int h, byte[] px = null)
        {
            Width = w;
            Height = h;
            Pixels = px ?? new byte[w * h * 4];
        }
    }

    /// <summary>A PDF document loaded into PDFium from memory.</summary>
    public sealed class PdfiumDocument : IDisposable
    {
        IntPtr doc;
        IntPtr mem;

        public PdfiumDocument(byte[] bytes)
        {
            Pdfium.Init();
            lock (Pdfium.Gate)
            {
                mem = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, mem, bytes.Length);
                doc = Pdfium.FPDF_LoadMemDocument64(mem, (UIntPtr)(ulong)bytes.Length, null);
                if (doc == IntPtr.Zero)
                {
                    var err = Pdfium.FPDF_GetLastError();
                    Marshal.FreeHGlobal(mem);
                    mem = IntPtr.Zero;
                    throw new CompileException(err == 4 ? "input.encrypted" : "render.load", $"PDFium could not load the document (error {err})");
                }
            }
        }

        public int PageCount { get { lock (Pdfium.Gate) return Pdfium.FPDF_GetPageCount(doc); } }

        public (float w, float h) PageSize(int index)
        {
            lock (Pdfium.Gate)
            {
                var p = Pdfium.FPDF_LoadPage(doc, index);
                if (p == IntPtr.Zero) return (0, 0);
                var r = (Pdfium.FPDF_GetPageWidthF(p), Pdfium.FPDF_GetPageHeightF(p));
                Pdfium.FPDF_ClosePage(p);
                return r;
            }
        }

        /// <summary>
        /// Renders the display-space rectangle (points, origin top-left of the visible page) of a page.
        /// Transparent background when <paramref name="transparent"/>, otherwise white.
        /// </summary>
        public RgbaImage Render(int index, double x0, double y0, double x1, double y1, int width, int height, bool transparent)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            double sx = width / Math.Max(1e-9, x1 - x0), sy = height / Math.Max(1e-9, y1 - y0);
            lock (Pdfium.Gate)
            {
                var page = Pdfium.FPDF_LoadPage(doc, index);
                if (page == IntPtr.Zero) throw new CompileException("render.page", $"PDFium could not load page {index + 1}");
                var bmp = Pdfium.FPDFBitmap_Create(width, height, 1);
                if (bmp == IntPtr.Zero)
                {
                    Pdfium.FPDF_ClosePage(page);
                    throw new CompileException("render.memory", $"cannot allocate a {width}x{height} bitmap");
                }
                try
                {
                    Pdfium.FPDFBitmap_FillRect(bmp, 0, 0, width, height, transparent ? 0x00000000u : 0xFFFFFFFFu);
                    var m = new Pdfium.FS_MATRIX { a = (float)sx, b = 0, c = 0, d = (float)sy, e = (float)(-x0 * sx), f = (float)(-y0 * sy) };
                    var clip = new Pdfium.FS_RECTF { left = 0, top = 0, right = width, bottom = height };
                    Pdfium.FPDF_RenderPageBitmapWithMatrix(bmp, page, ref m, ref clip, Pdfium.FlagReverseByteOrder);
                    var buf = Pdfium.FPDFBitmap_GetBuffer(bmp);
                    int stride = Pdfium.FPDFBitmap_GetStride(bmp);
                    var img = new RgbaImage(width, height);
                    for (int y = 0; y < height; y++)
                        Marshal.Copy(buf + y * stride, img.Pixels, y * width * 4, width * 4);
                    return img;
                }
                finally
                {
                    Pdfium.FPDFBitmap_Destroy(bmp);
                    Pdfium.FPDF_ClosePage(page);
                }
            }
        }

        public void Dispose()
        {
            lock (Pdfium.Gate)
            {
                if (doc != IntPtr.Zero) Pdfium.FPDF_CloseDocument(doc);
                doc = IntPtr.Zero;
                if (mem != IntPtr.Zero) Marshal.FreeHGlobal(mem);
                mem = IntPtr.Zero;
            }
        }
    }
}
