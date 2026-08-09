// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
using System.Drawing;

namespace MapEditor.Ui.Elements
{
    /// <summary>
    /// A 2D item that can be drawn on the screen.
    /// </summary>
    public interface I2Dimensional : IRecalculable, IDrawable
    {
        #region Properties

        /// <summary>
        /// The Position of the drawable.
        /// </summary>
        PointF Position { get; set; }
        /// <summary>
        /// The Size of the drawable.
        /// </summary>
        SizeF Size { get; set; }
        /// <summary>
        /// The Color of the drawable.
        /// </summary>
        Color Color { get; set; }

        #endregion
    }
}
