// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
namespace MapEditor.Ui.Menus
{
    /// <summary>
    /// Represents the method that is called when the items on a menu are changed (added or removed).
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">An <see cref="MenuModifiedEventArgs"/> with the menu operation.</param>
    public delegate void MenuModifiedEventHandler(object sender, MenuModifiedEventArgs e);
}
