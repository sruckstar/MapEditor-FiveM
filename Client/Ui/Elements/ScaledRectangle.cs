// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
using CitizenFX.Core.Native;
using System.Drawing;

namespace MapEditor.Ui.Elements
{
    /// <summary>
    /// A 2D rectangle.
    /// </summary>
    public class ScaledRectangle : BaseElement
    {
        #region Constructor

        /// <summary>
        /// Creates a new <see cref="ScaledRectangle"/> with the specified Position and Size.
        /// </summary>
        /// <param name="pos">The position of the Rectangle.</param>
        /// <param name="size">The size of the Rectangle.</param>
        public ScaledRectangle(PointF pos, SizeF size) : base(pos, size)
        {
        }

        #endregion

        #region Functions

        /// <summary>
        /// Draws the rectangle on the screen.
        /// </summary>
        public override void Draw()
        {
            if (Size == SizeF.Empty)
            {
                return;
            }
            API.DrawRect(relativePosition.X, relativePosition.Y, relativeSize.Width, relativeSize.Height, Color.R, Color.G, Color.B, Color.A);
        }
        /// <summary>
        /// Recalculates the position based on the size.
        /// </summary>
        public override void Recalculate()
        {
            base.Recalculate();
            relativePosition.X += relativeSize.Width * 0.5f;
            relativePosition.Y += relativeSize.Height * 0.5f;
        }

        #endregion
    }
}
