// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
using CitizenFX.Core;
using CitizenFX.Core.Native;
using System.Drawing;

namespace MapEditor.Ui.Tools
{
    /// <summary>
    /// The screen of the game being rendered.
    /// </summary>
    public static class GameScreen
    {
        #region Properties

        /// <summary>
        /// Gets the actual Screen resolution the game is being rendered at.
        /// </summary>
        /// <remarks>
        /// LemonUI reads CitizenFX's Screen.Resolution here. That property builds a System.Drawing.Size, which
        /// the Enhanced client's sandbox refuses — and it refuses it inside the runtime's own legacy shim, so
        /// there is nothing to catch and nothing to work around. The native behind the property is called
        /// directly instead; it answers with two ints and touches no forbidden type.
        /// </remarks>
        public static SizeF AbsoluteResolution
        {
            get
            {
                int width = 0, height = 0;
                API.GetActiveScreenResolution(ref width, ref height);
                return new SizeF(width, height);
            }
        }
        /// <summary>
        /// The Aspect Ratio of the screen.
        /// </summary>
        public static float AspectRatio
        {
            get
            {
                return API.GetAspectRatio(false);
            }
        }
        /// <summary>
        /// The location of the cursor on screen between 0 and 1.
        /// </summary>
        public static PointF Cursor
        {
            get
            {
                float cursorX = API.GetControlNormal(0, (int)Control.CursorX);
                float cursorY = API.GetControlNormal(0, (int)Control.CursorY);
                return new PointF(cursorX.ToXScaled(), cursorY.ToYScaled());
            }
        }

        #endregion

        #region Functions

        /// <summary>
        /// Checks if the cursor is inside of the scaled area.
        /// </summary>
        /// <param name="pos">The scaled position.</param>
        /// <param name="size">The scaled size of the area.</param>
        /// <returns><see langword="true"/> if the cursor is in the specified bounds, <see langword="false"/> otherwise.</returns>
        public static bool IsCursorInArea(PointF pos, SizeF size) => IsCursorInArea(pos.X, pos.Y, size.Width, size.Height);
        /// <summary>
        /// Checks if the cursor is inside of the scaled area.
        /// </summary>
        /// <param name="x">The scaled X position.</param>
        /// <param name="y">The scaled Y position.</param>
        /// <param name="width">The scaled width of the area.</param>
        /// <param name="height">The scaled height of the area.</param>
        /// <returns><see langword="true"/> if the cursor is in the specified bounds, <see langword="false"/> otherwise.</returns>
        public static bool IsCursorInArea(float x, float y, float width, float height)
        {
            PointF cursorPosition = Cursor;

            bool isX = cursorPosition.X >= x && cursorPosition.X <= x + width;
            bool isY = cursorPosition.Y > y && cursorPosition.Y < y + height;
            return isX && isY;
        }
        /// <summary>
        /// Shows the cursor during the current game frame.
        /// </summary>
        public static void ShowCursorThisFrame()
        {
            API.SetMouseCursorActiveThisFrame();
        }

        #endregion
    }
}
