// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
namespace MapEditor.Ui
{
    /// <summary>
    /// Interface for items that can be processed in an Object Pool.
    /// </summary>
    public interface IProcessable
    {
        #region Properties

        /// <summary>
        /// If this processable item is visible on the screen.
        /// </summary>
        bool Visible { get; set; }

        #endregion

        #region Functions

        /// <summary>
        /// Processes the object.
        /// </summary>
        void Process();

        #endregion
    }
}
