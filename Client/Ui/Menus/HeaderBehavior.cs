// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
namespace MapEditor.Ui.Menus
{
    /// <summary>
    /// The behavior of the <see cref="NativeMenu"/>'s header.
    /// </summary>
    public enum HeaderBehavior
    {
        /// <summary>
        /// The header will always be shown.
        /// </summary>
        AlwaysShow = 0,
        /// <summary>
        /// The header will always be shown, except when is empty.
        /// </summary>
        ShowIfRequired = 1,
        /// <summary>
        /// The header will never be shown.
        /// </summary>
        AlwaysHide = 2
    }
}
