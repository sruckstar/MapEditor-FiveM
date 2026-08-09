// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
namespace MapEditor.Ui.Menus
{
    /// <summary>
    /// Defines the behavior of the mouse when a menu is open.
    /// </summary>
    public enum MenuMouseBehavior
    {
        /// <summary>
        /// The mouse will not be usable in the menu.
        /// </summary>
        Disabled,
        /// <summary>
        /// The menu can be used to click the items and navigate to them.
        /// </summary>
        Movement,
        /// <summary>
        /// The wheel can be used to navigate in the menu, click can be used to confirm and right click to return/exit.
        /// </summary>
        Scrolling
    }
}
