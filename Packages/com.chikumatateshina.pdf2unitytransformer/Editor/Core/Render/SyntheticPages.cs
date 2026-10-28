using System;
using System.Collections.Generic;
using PDF2Unity.Core.Pdf;

namespace PDF2Unity.Core.Render
{
    /// <summary>
    /// Builds an in-memory incremental update of the source PDF whose page tree contains only
    /// synthetic pages, then loads it into PDFium once. Synthetic pages reuse the original
    /// resources by reference, so PDFium renders them with the original fonts, images, colour
    /// management and transparency:
    ///   * truncated pages: the original page with its content (and nested form XObjects) cut at
    ///     a given operator, used to precomposite an effect without the content painted after it,
    ///   * image pages: a single image XObject / inline image drawn into a w x h page
    ///     (full image decoding incl. JPX/JBIG2/CCITT, ICC, SMask, Decode).
    /// </summary>
    public sealed class SyntheticPages
    {
        readonly PdfFile file;
        int next;
        readonly int pagesNum;
        readonly List<(int num, Dictionary<string, object> dict, byte[] data)> streams = new List<(int, Dictionary<string, object>, byte[])>();
        readonly List<(int num, Dictionary<string, object> dict)> pages = new List<(int, Dictionary<string, object>)>();

        public SyntheticPages(PdfFile file)
        {
            this.file = file;
            next = Math.Max(file.MaxObjectNumber, (int)file.Trailer.GetNumber("Size", 0)) + 1;
            pagesNum = next++;
        }

        public int Count => pages.Count;

        /// <summary>Adds a stream object (unfiltered data) and returns its reference.</summary>
        public PdfRef AddStream(Dictionary<string, object> dict, byte[] data)
        {
            int n = next++;
            streams.Add((n, dict, data));
            return new PdfRef(n, 0);
        }

        /// <summary>Copy of <paramref name="page"/> (inherited attributes resolved) with new content / resources.</summary>
        public int AddPageCopy(PdfDict page, byte[] content, object resources = null)
        {
            var d = new Dictionary<string, object>();
            foreach (var kv in page.Items)
            {
                if (kv.Key == "Parent" || kv.Key == "Contents" || kv.Key == "Annots" || kv.Key == "B" ||
                    kv.Key == "Thumb" || kv.Key == "StructParents" || kv.Key == "AA") continue;
                d[kv.Key] = kv.Value;
            }
            foreach (var key in new[] { "Resources", "MediaBox", "CropBox", "Rotate" })
                if (!d.ContainsKey(key))
                {
                    var v = InheritedRaw(page, key);
                    if (v != null) d[key] = v;
                }
            if (resources != null) d["Resources"] = resources;
            d["Type"] = new PdfName("Page");
            return AddPage(d, content);
        }

        /// <summary>Page of size w x h (points = pixels at 72 dpi) drawing content with the given resources.</summary>
        public int AddPage(double w, double h, object resources, byte[] content)
        {
            var d = new Dictionary<string, object>
            {
                ["Type"] = new PdfName("Page"),
                ["MediaBox"] = new List<object> { 0.0, 0.0, w, h },
            };
            if (resources != null) d["Resources"] = resources;
            return AddPage(d, content);
        }

        int AddPage(Dictionary<string, object> d, byte[] content)
        {
            d["Contents"] = AddStream(null, content);
            d["Parent"] = new PdfRef(pagesNum, 0);
            int n = next++;
            pages.Add((n, d));
            return pages.Count - 1;
        }

        static object InheritedRaw(PdfDict page, string key)
        {
            var n = page;
            for (int i = 0; n != null && i < 64; i++)
            {
                if (n.Has(key)) return n.Raw(key);
                n = n.GetDict("Parent");
            }
            return null;
        }

        public byte[] BuildBytes()
        {
            var w = new PdfWriter();
            w.Raw("\n");
            foreach (var (num, dict, data) in streams)
            {
                var d = new PdfDict();
                if (dict != null) foreach (var kv in dict) d.Set(kv.Key, kv.Value);
                w.WriteStream(num, d, data);
            }
            var kids = new List<object>();
            foreach (var (num, dict) in pages)
            {
                w.WriteObject(num, dict);
                kids.Add(new PdfRef(num, 0));
            }
            w.WriteObject(pagesNum, new Dictionary<string, object>
            {
                ["Type"] = new PdfName("Pages"),
                ["Kids"] = kids,
                ["Count"] = (double)kids.Count,
            });
            var catalog = new Dictionary<string, object>(file.Root.Items) { ["Pages"] = new PdfRef(pagesNum, 0) };
            catalog.Remove("OpenAction");
            catalog.Remove("AcroForm");
            catalog.Remove("Names");
            catalog.Remove("AA");
            int last = next;
            int catNum = file.Trailer.Raw("Root") is PdfRef rr ? rr.Num : last++;
            w.WriteObject(catNum, catalog);
            var trailer = new Dictionary<string, object> { ["Root"] = new PdfRef(catNum, 0) };
            if (file.Trailer.Raw("ID") != null) trailer["ID"] = file.Trailer.Raw("ID");
            bool prev = file.StartXref > 0 && !file.Reconstructed;
            if (prev) trailer["Prev"] = (double)file.StartXref;
            long baseOffset = file.Data.Length;
            bool xrefStream = prev && file.StartXref < file.Data.Length && file.Data[file.StartXref] >= '0' && file.Data[file.StartXref] <= '9';
            if (xrefStream) w.FinishWithXrefStream(trailer, baseOffset, last + 1, last);
            else w.Finish(trailer, baseOffset, last);
            var upd = w.ToArray();
            var all = new byte[file.Data.Length + upd.Length];
            Buffer.BlockCopy(file.Data, 0, all, 0, file.Data.Length);
            Buffer.BlockCopy(upd, 0, all, file.Data.Length, upd.Length);
            return all;
        }

        public PdfiumDocument Load() => new PdfiumDocument(BuildBytes());
    }
}
