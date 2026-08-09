// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
namespace MapEditor.Ui.Menus
{
    /// <summary>
    /// The Style of title for the Color Panel.
    /// </summary>
    public enum ColorTitleStyle
    {
        /// <summary>
        /// Does not shows any Title.
        /// The count will still be shown if <see cref="NativeColorPanel.ShowCount"/> is set to <see langword="true"/>.
        /// </summary>
        None = -1,
        /// <summary>
        /// Shows a Simple Title for all of the Colors.
        /// </summary>
        Simple = 0,
        /// <summary>
        /// Shows the Color Name as the Title.
        /// </summary>
        ColorName = 1
    }
}
