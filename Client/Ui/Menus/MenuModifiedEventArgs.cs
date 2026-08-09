// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
namespace MapEditor.Ui.Menus
{
    /// <summary>
    /// Represents the different
    /// </summary>
    public class MenuModifiedEventArgs
    {
        #region Properties

        /// <summary>
        /// The item that was modified.
        /// </summary>
        public NativeItem Item { get; }
        /// <summary>
        /// The operation that was performed in the item.
        /// </summary>
        public ItemOperation Operation { get; }

        #endregion

        #region Constructors

        /// <summary>
        /// Creates a new <see cref="MenuModifiedEventArgs"/>.
        /// </summary>
        /// <param name="item">The item that was modified.</param>
        /// <param name="operation">The operation that was performed in the item.</param>
        public MenuModifiedEventArgs(NativeItem item, ItemOperation operation)
        {
            Item = item;
            Operation = operation;
        }

        #endregion
    }
}
