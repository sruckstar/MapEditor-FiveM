// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
namespace MapEditor.Ui.Menus
{
    /// <summary>
    /// The visibility setting for the Item Count of the Menu.
    /// </summary>
    public enum CountVisibility
    {
        /// <summary>
        /// The Item Count is never shown.
        /// </summary>
        Never = -1,
        /// <summary>
        /// The Item Count is shown when is not possible to show all of the items in the screen.
        /// </summary>
        Auto = 0,
        /// <summary>
        /// The Item Count is always shown.
        /// </summary>
        Always = 1
    }
}
