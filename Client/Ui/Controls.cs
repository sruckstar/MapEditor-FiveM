// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
using CitizenFX.Core;
using CitizenFX.Core.Native;
using System.Collections.Generic;
using MapEditor.Ui.Elements;

namespace MapEditor.Ui
{
    /// <summary>
    /// Tools for dealing with controls.
    /// </summary>
    internal static class Controls
    {
        #region Properties

        /// <summary>
        /// Gets if the player used a controller for the last input.
        /// </summary>
        public static bool IsUsingController
        {
            get
            {
                return !API.IsInputDisabled(2);
            }
        }

        #endregion

        #region Functions

        /// <summary>
        /// Checks if a control was pressed during the last frame.
        /// </summary>
        /// <param name="control">The control to check.</param>
        /// <returns>true if the control was pressed, false otherwise.</returns>
        public static bool IsJustPressed(Control control)
        {
            return API.IsDisabledControlJustPressed(0, (int)control);
        }
        /// <summary>
        /// Checks if a control is currently pressed.
        /// </summary>
        /// <param name="control">The control to check.</param>
        /// <returns>true if the control is pressed, false otherwise.</returns>
        public static bool IsPressed(Control control)
        {
            return API.IsDisabledControlPressed(0, (int)control);
        }
        /// <summary>
        /// Disables all of the controls during the next frame.
        /// </summary>
        public static void DisableAll(int inputGroup = 0)
        {
            API.DisableAllControlActions(inputGroup);
        }
        /// <summary>
        /// Enables a control during the next frame.
        /// </summary>
        /// <param name="control">The control to enable.</param>
        public static void EnableThisFrame(Control control)
        {
            API.EnableControlAction(0, (int)control, true);
        }
        /// <summary>
        /// Enables a specific set of controls during the next frame.
        /// </summary>
        /// <param name="controls">The controls to enable.</param>
        public static void EnableThisFrame(IEnumerable<Control> controls)
        {
            foreach (Control control in controls)
            {
                EnableThisFrame(control);
            }
        }
        /// <summary>
        /// Disables a control during the next frame.
        /// </summary>
        /// <param name="control">The control to disable.</param>
        public static void DisableThisFrame(Control control)
        {
            API.DisableControlAction(0, (int)control, true);
        }

        #endregion
    }
}
