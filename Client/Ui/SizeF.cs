using System.Globalization;

namespace MapEditor.Ui
{
    /// <summary>
    /// A width and a height, in the 1080-tall space every element in <see cref="MapEditor.Ui"/> is laid out in.
    ///
    /// This is the one type in the UI layer that is not simply LemonUI's, and the reason the layer exists at
    /// all. LemonUI uses <c>System.Drawing.SizeF</c>, and the CAS sandbox of the FiveM Enhanced client allows
    /// exactly five types out of <c>System.Drawing</c>: Color, ColorConverter, Point, PointF and
    /// PointConverter. <c>Size</c> and <c>SizeF</c> are not among them, so the first call into LemonUI threw
    /// a <c>SecurityException</c> before a single frame was drawn.
    ///
    /// Deliberately named <c>SizeF</c> and laid out like the original: every file of the UI layer sits under
    /// <see cref="MapEditor.Ui"/>, so this type is found before the <c>using System.Drawing</c> at the top of
    /// the file, and the ported sources needed no edit. Outside the layer the two are ambiguous — a compiler
    /// error rather than a silent choice — so the editor's own files pick one with a using alias.
    /// </summary>
    public struct SizeF
    {
        public static readonly SizeF Empty = new SizeF(0f, 0f);

        public float Width { get; set; }
        public float Height { get; set; }

        public SizeF(float width, float height)
        {
            Width = width;
            Height = height;
        }

        public static bool operator ==(SizeF left, SizeF right)
        {
            return left.Width == right.Width && left.Height == right.Height;
        }

        public static bool operator !=(SizeF left, SizeF right)
        {
            return !(left == right);
        }

        public override bool Equals(object obj)
        {
            return obj is SizeF other && this == other;
        }

        public override int GetHashCode()
        {
            return Width.GetHashCode() ^ (Height.GetHashCode() << 16);
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{{Width={0}, Height={1}}}", Width, Height);
        }
    }
}
