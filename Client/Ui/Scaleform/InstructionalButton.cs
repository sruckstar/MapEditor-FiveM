// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
using CitizenFX.Core;
using CitizenFX.Core.Native;
using System;

namespace MapEditor.Ui.Scaleform
{
    /// <summary>
    /// An individual instructional button.
    /// </summary>
    public struct InstructionalButton
    {
        #region Fields

        private Control control;
        private string raw;
        private string description;

        #endregion

        #region Properties

        /// <summary>
        /// The description of this button.
        /// </summary>
        public string Description
        {
            get => description;
            set => description = value ?? throw new ArgumentNullException(nameof(value));
        }
        /// <summary>
        /// The Control used by this button.
        /// </summary>
        public Control Control
        {
            get => control;
            set
            {
                control = value;
                raw = API.GetControlInstructionalButton(2, (int)value, 1);
            }
        }
        /// <summary>
        /// The Raw Control sent to the Scaleform.
        /// </summary>
        public string Raw
        {
            get => raw;
            set
            {
                raw = value ?? throw new ArgumentNullException(nameof(value));
                control = (Control)(-1);
            }
        }

        #endregion

        #region Constructors

        /// <summary>
        /// Creates an instructional button for a Control.
        /// </summary>
        /// <param name="description">The text for the description.</param>
        /// <param name="control">The control to use.</param>
        public InstructionalButton(string description, Control control)
        {
            this.description = description ?? throw new ArgumentNullException(nameof(description));
            this.control = control;
            raw = API.GetControlInstructionalButton(2, (int)control, 1);
        }
        /// <summary>
        /// Creates an instructional button for a raw control.
        /// </summary>
        /// <param name="description">The text for the description.</param>
        /// <param name="raw">The raw value of the control.</param>
        public InstructionalButton(string description, string raw)
        {
            this.description = description ?? throw new ArgumentNullException(nameof(description));
            control = (Control)(-1);
            this.raw = raw ?? throw new ArgumentNullException(nameof(raw));
        }

        #endregion
    }
}
