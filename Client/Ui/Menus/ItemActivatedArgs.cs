// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
namespace MapEditor.Ui.Menus
{
    /// <summary>
    /// Represents the arguments of an item activation.
    /// </summary>
    public class ItemActivatedArgs
    {
        #region Properties

        /// <summary>
        /// The item that was just activated.
        /// </summary>
        public NativeItem Item { get; }

        #endregion

        #region Constructors

        internal ItemActivatedArgs(NativeItem item)
        {
            Item = item;
        }

        #endregion
    }
}
