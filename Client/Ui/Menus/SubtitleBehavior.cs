// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
using System;

namespace MapEditor.Ui.Menus
{
    /// <summary>
    /// The behavior of the <see cref="NativeMenu"/>'s subtitle.
    /// </summary>
    [Obsolete("Please use HeaderBehavior instead", true)]
    public enum SubtitleBehavior
    {
        /// <summary>
        /// The subtitle will always be shown.
        /// </summary>
        AlwaysShow = 0,
        /// <summary>
        /// The subtitle will always be shown, except when is empty.
        /// </summary>
        ShowIfRequired = 1,
        /// <summary>
        /// The subtitle will never be shown.
        /// </summary>
        AlwaysHide = 2
    }
}
