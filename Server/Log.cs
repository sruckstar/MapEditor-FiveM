using System;
using CitizenFX.Core;

namespace MapEditor.Server
{
    /// <summary>
    /// The server console. The client's own Log writes to F8 with the same prefix, so a line's side is
    /// told apart by where it turned up rather than by what it says.
    /// </summary>
    public static class Log
    {
        private const string Prefix = "[MapEditor] ";

        public static void Info(string message)
        {
            Debug.WriteLine(Prefix + message);
        }

        public static void Info(string format, params object[] args)
        {
            Debug.WriteLine(Prefix + string.Format(format, args));
        }

        public static void Error(string context, Exception ex)
        {
            Debug.WriteLine(Prefix + "ERROR in " + context + ": " + ex);
        }
    }
}
