// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
using System;

namespace MapEditor.Ui.Scaleform
{
    /// <summary>
    /// Scaleforms are 2D Adobe Flash-like objects.
    /// </summary>
    public interface IScaleform : IDrawable, IProcessable, IDisposable
    {
        #region Properties

        /// <summary>
        /// Draws the Scaleform in full screen.
        /// </summary>
        void DrawFullScreen();

        #endregion
    }
}
