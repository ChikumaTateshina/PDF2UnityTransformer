using System.Collections.Generic;

namespace PDF2Unity.Core.Graphics
{
    public enum PathOp : byte { Move, Line, Cubic, Close }

    /// <summary>Path command. Move/Line use (X3, Y3) as the end point; Cubic uses all three points.</summary>
    public struct PathCmd
    {
        public PathOp Op;
        public double X1, Y1, X2, Y2, X3, Y3;

        public static PathCmd Move(double x, double y) => new PathCmd { Op = PathOp.Move, X3 = x, Y3 = y };
        public static PathCmd Line(double x, double y) => new PathCmd { Op = PathOp.Line, X3 = x, Y3 = y };
        public static PathCmd Close() => new PathCmd { Op = PathOp.Close };
        public static PathCmd Cubic(double x1, double y1, double x2, double y2, double x3, double y3) =>
            new PathCmd { Op = PathOp.Cubic, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, X3 = x3, Y3 = y3 };

        public PathCmd Transform(Matrix m)
        {
            var r = this;
            m.Apply(X1, Y1, out r.X1, out r.Y1);
            m.Apply(X2, Y2, out r.X2, out r.Y2);
            m.Apply(X3, Y3, out r.X3, out r.Y3);
            return r;
        }
    }

    /// <summary>Collects outline commands (used by font interpreters), applying a scale/offset.</summary>
    public sealed class PathSink
    {
        public readonly List<PathCmd> Cmds = new List<PathCmd>();
        readonly double sx, sy, tx, ty;
        double cx, cy, startX, startY;
        bool open;

        public PathSink(double sx, double sy, double tx = 0, double ty = 0)
        {
            this.sx = sx; this.sy = sy; this.tx = tx; this.ty = ty;
        }

        public double CurrentX => cx;
        public double CurrentY => cy;

        public void MoveTo(double x, double y)
        {
            if (open) ClosePath();
            Cmds.Add(PathCmd.Move(x * sx + tx, y * sy + ty));
            cx = startX = x;
            cy = startY = y;
            open = true;
        }

        public void LineTo(double x, double y)
        {
            if (!open) MoveTo(cx, cy);
            Cmds.Add(PathCmd.Line(x * sx + tx, y * sy + ty));
            cx = x;
            cy = y;
        }

        public void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
        {
            if (!open) MoveTo(cx, cy);
            Cmds.Add(PathCmd.Cubic(x1 * sx + tx, y1 * sy + ty, x2 * sx + tx, y2 * sy + ty, x3 * sx + tx, y3 * sy + ty));
            cx = x3;
            cy = y3;
        }

        public void QuadTo(double qx, double qy, double x, double y)
        {
            CurveTo(cx + 2.0 / 3 * (qx - cx), cy + 2.0 / 3 * (qy - cy), x + 2.0 / 3 * (qx - x), y + 2.0 / 3 * (qy - y), x, y);
        }

        public void ClosePath()
        {
            if (!open) return;
            Cmds.Add(PathCmd.Close());
            open = false;
            cx = startX;
            cy = startY;
        }

        public void Finish() => ClosePath();
    }
}
