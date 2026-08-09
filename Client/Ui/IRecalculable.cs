// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
namespace MapEditor.Ui
{
    /// <summary>
    /// Interface for classes that have values that need to be recalculated on resolution changes.
    /// </summary>
    public interface IRecalculable
    {
        #region Functions

        /// <summary>
        /// Recalculates the values.
        /// </summary>
        void Recalculate();

        #endregion
    }
}
