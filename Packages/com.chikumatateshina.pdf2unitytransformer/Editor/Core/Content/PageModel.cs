using System.Collections.Generic;
using PDF2Unity.Core.Fonts;
using PDF2Unity.Core.Graphics;
using PDF2Unity.Core.Pdf;

namespace PDF2Unity.Core.Content
{
    public enum OpKind { Path, Glyphs, Image, Shading, Unsupported }

    public sealed class Paint
    {
        public double R, G, B, Alpha = 1;
        public ShadingResource Shading; // shading pattern
        public Matrix PatternMatrix;
        public bool IsShading => Shading != null;
    }

    public sealed class StrokeStyle
    {
        public double Width = 1, MiterLimit = 10, DashPhase;
        public int Cap, Join;
        public double[] Dash = new double[0];
    }

    /// <summary>An image drawn by an operation; decoded by PDFium through a synthetic page.</summary>
    public sealed class ImageRef
    {
        public string Key;            // stable identity (object ref or content hash) + stencil colour
        public PdfRef XObject;        // indirect image XObject (preferred)
        public byte[] InlineImage;    // "BI ... ID ... EI" bytes for inline images
        public PdfDict Resources;     // resources in scope (for named colour spaces of inline images)
        public int Width, Height;
        public bool Stencil;
        public double R, G, B;        // fill colour of stencil masks
        public bool Interpolate;
    }

    public sealed class Glyph
    {
        public string Key;
        public Matrix M; // text space (size 1) -> user space
    }

    public sealed class DrawOp
    {
        public int Id;
        public OpKind Kind;
        public Matrix Ctm;
        public int Clip;
        public TruncPoint Point;  // where this op sits in the (nested) content streams
        public long TruncateAt => Point?.Root.End ?? 0;
        public string Source;

        public List<PathCmd> Path;
        public string FillRule;   // "nonzero" | "evenodd" | null
        public Paint Fill, Stroke;
        public StrokeStyle Style;

        public PdfFont Font;
        public List<Glyph> Glyphs;
        public int TextMode;

        public ImageRef Image;
        public double Alpha = 1;

        public ShadingResource Shading;
        public Matrix ShadingMatrix;

        public string Reason, Detail;
        public Rect BBox;         // page space (unsupported ops)
    }

    /// <summary>
    /// Position of an operation in the chain page content -> form XObject -> ... Each level records the
    /// byte range of the instruction being executed there (the Do that invokes the next level, or the
    /// painting operator itself at the deepest level). Used to render "content up to this operation".
    /// </summary>
    public sealed class TruncPoint
    {
        public TruncPoint Parent;
        public PdfStream Form;      // null for the page level
        public byte[] Content;      // decoded content of this level
        public PdfDict Resources;   // resources in scope at this level
        public int Start, End;      // byte range of the instruction at this level
        public string DoName;       // XObject name invoking the child level (set on parents)

        public TruncPoint Root { get { var p = this; while (p.Parent != null) p = p.Parent; return p; } }

        public TruncPoint WithDo(string name) => new TruncPoint
        {
            Parent = Parent, Form = Form, Content = Content, Resources = Resources, Start = Start, End = End, DoName = name,
        };
    }

    public sealed class ClipNode
    {
        public int Parent = -1;
        public List<PathCmd> Path;
        public Matrix Ctm;
        public string Rule;
    }

    public sealed class PageModel
    {
        public int Index;
        public PdfDict PageDict;
        public Rect MediaBox, CropBox;
        public int Rotation;
        public double UserUnit = 1;
        public double DisplayW, DisplayH;
        public Matrix PageToDisplay;
        public byte[] Content = new byte[0];
        public readonly List<DrawOp> Ops = new List<DrawOp>();
        public readonly List<ClipNode> Clips = new List<ClipNode>();
        public int Annotations;
    }

    public sealed class ShadingResource
    {
        public int Type;               // 2 axial, 3 radial
        public double[] Coords, Domain;
        public bool Extend0, Extend1;
        public float[] Ramp;           // 256 x rgb over the domain
        public double[] Background;    // rgb or null
        public Rect? BBox;
    }
}
