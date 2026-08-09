// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
using CitizenFX.Core.Native;
using CitizenFX.Core.UI;
using System;
using System.Drawing;

namespace MapEditor.Ui.Tools
{
    /// <summary>
    /// Tools for changing, resetting and retrieving the Safe Zone of the game.
    /// </summary>
    public static class SafeZone
    {
        #region Properties

        /// <summary>
        /// The size of the safe zone.
        /// </summary>
        /// <remarks>
        /// This property should not be used to manually calculate the safe zone. Use <see cref="GetPositionAt(System.Drawing.PointF,MapEditor.Ui.GFXAlignment,MapEditor.Ui.GFXAlignment)"/> to get the safe zone size.
        /// </remarks>
        public static float Size
        {
            get
            {
                return API.GetSafeZoneSize();
            }
        }
        /// <summary>
        /// The top left corner after the safe zone.
        /// </summary>
        public static PointF TopLeft => GetPositionAt(PointF.Empty, GFXAlignment.Left, GFXAlignment.Top);
        /// <summary>
        /// The top right corner after the safe zone.
        /// </summary>
        public static PointF TopRight => GetPositionAt(PointF.Empty, GFXAlignment.Right, GFXAlignment.Top);
        /// <summary>
        /// The bottom left corner after the safe zone.
        /// </summary>
        public static PointF BottomLeft => GetPositionAt(PointF.Empty, GFXAlignment.Left, GFXAlignment.Bottom);
        /// <summary>
        /// The bottom right corner after the safe zone.
        /// </summary>
        public static PointF BottomRight => GetPositionAt(PointF.Empty, GFXAlignment.Right, GFXAlignment.Bottom);

        #endregion

        #region Tools

        private static GFXAlignment AlignmentToGFXAlignment(Alignment alignment)
        {
            switch (alignment)
            {
                case Alignment.Left:
                    return GFXAlignment.Left;
                case Alignment.Right:
                    return GFXAlignment.Right;
                case Alignment.Center:
                    return GFXAlignment.Center;
                default:
                    throw new ArgumentException("Alignment is not one of the allowed values (Left, Right, Center).", nameof(alignment));
            }
        }

        #endregion

        #region Functions

        /// <summary>
        /// Converts the specified position into one that is aware of the safe zone.
        /// </summary>
        /// <param name="og">The original 1080p based position.</param>
        /// <returns>A new 1080p based position that is aware of the the Alignment.</returns>
        public static PointF GetSafePosition(PointF og) => GetSafePosition(og.X, og.Y);
        /// <summary>
        /// Converts the specified position into one that is aware of <see cref="SetAlignment(GFXAlignment, GFXAlignment)"/>.
        /// </summary>
        /// <param name="x">The 1080p based X position.</param>
        /// <param name="y">The 1080p based Y position.</param>
        /// <returns>A new 1080p based position that is aware of the the Alignment.</returns>
        public static PointF GetSafePosition(float x, float y)
        {
            float relativeX = x.ToXRelative();
            float relativeY = y.ToYRelative();

            float realX = 0, realY = 0;
            API.GetScriptGfxPosition(relativeX, relativeY, ref realX, ref realY);

            return new PointF(realX.ToXScaled(), realY.ToYScaled());
        }
        /// <summary>
        /// Sets the alignment for the safe zone.
        /// </summary>
        /// <param name="horizontal">The Horizontal alignment of the items.</param>
        /// <param name="vertical">The vertical alignment of the items.</param>
        public static void SetAlignment(Alignment horizontal, GFXAlignment vertical) => SetAlignment(AlignmentToGFXAlignment(horizontal), vertical);
        /// <summary>
        /// Sets the alignment for the safe zone.
        /// </summary>
        /// <param name="horizontal">The Horizontal alignment of the items.</param>
        /// <param name="vertical">The vertical alignment of the items.</param>
        public static void SetAlignment(GFXAlignment horizontal, GFXAlignment vertical)
        {
            API.SetScriptGfxAlign((int)horizontal, (int)vertical);
            API.SetScriptGfxAlignParams(0, 0, 0, 0);
        }
        /// <summary>
        /// Resets the alignment of the safe zone.
        /// </summary>
        public static void ResetAlignment()
        {
            API.ResetScriptGfxAlign();
        }
        /// <summary>
        /// Gets the specified position with the specified safe zone alignment.
        /// </summary>
        /// <param name="position">The position to get.</param>
        /// <param name="horizontal">The horizontal alignment.</param>
        /// <param name="vertical">The vertical alignment.</param>
        /// <returns>The  safe zone alignment.</returns>
        public static PointF GetPositionAt(PointF position, Alignment horizontal, GFXAlignment vertical) => GetPositionAt(position, AlignmentToGFXAlignment(horizontal), vertical);
        /// <summary>
        /// Gets the specified position with the specified safe zone alignment.
        /// </summary>
        /// <param name="position">The position to get.</param>
        /// <param name="horizontal">The horizontal alignment.</param>
        /// <param name="vertical">The vertical alignment.</param>
        /// <returns>The scaled safe zone alignment.</returns>
        public static PointF GetPositionAt(PointF position, GFXAlignment horizontal, GFXAlignment vertical)
        {
            SetAlignment(horizontal, vertical);
            PointF pos = GetSafePosition(position);
            ResetAlignment();
            return pos;
        }

        #endregion
    }
}
