// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
namespace MapEditor.Ui
{
    /// <summary>
    /// Represents an item that can be drawn.
    /// </summary>
    public interface IDrawable
    {
        #region Functions

        /// <summary>
        /// Draws the item on the screen.
        /// </summary>
        void Draw();

        #endregion
    }
}
