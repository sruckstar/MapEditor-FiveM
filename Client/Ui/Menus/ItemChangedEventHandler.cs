// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
namespace MapEditor.Ui.Menus
{
    /// <summary>
    /// Represents the method that is called when the selected item is changed on a List Item.
    /// </summary>
    /// <typeparam name="T">The type of item that was changed.</typeparam>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">A <see cref="ItemChangedEventArgs{T}"/> with the information of the selected item.</param>
    public delegate void ItemChangedEventHandler<T>(object sender, ItemChangedEventArgs<T> e);
}
