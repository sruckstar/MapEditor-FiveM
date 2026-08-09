// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
// Taken from the .NET Runtime Repository
// https://github.com/dotnet/runtime
// Copyright (c) .NET Foundation and Contributors
// Under the MIT License

namespace MapEditor.Ui // Previously System.ComponentModel
{
    /// <summary>
    /// Represents the method that will handle the event raised when canceling an event.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">A <see cref="CancelEventArgs"/> that contains the event data.</param>
    public delegate void CancelEventHandler(object sender, CancelEventArgs e);
}
