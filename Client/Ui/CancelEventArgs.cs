// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
// Taken from the .NET Runtime Repository
// https://github.com/dotnet/runtime
// Copyright (c) .NET Foundation and Contributors
// Under the MIT License

using System;

namespace MapEditor.Ui // Previously System.ComponentModel
{
    /// <summary>
    /// EventArgs used to describe a cancel event.
    /// </summary>
    public class CancelEventArgs : EventArgs
    {
        #region Properties

        /// <summary>
        /// Gets or sets a value indicating whether we should cancel the operation or not
        /// </summary>
        public bool Cancel { get; set; }

        #endregion

        #region Constructors

        /// <summary>
        /// Default constructor
        /// </summary>
        public CancelEventArgs()
        {
        }
        /// <summary>
        /// Helper constructor
        /// </summary>
        /// <param name="cancel"></param>
        public CancelEventArgs(bool cancel) => Cancel = cancel;

        #endregion
    }
}
