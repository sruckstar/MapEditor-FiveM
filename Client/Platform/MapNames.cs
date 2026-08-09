using System;

namespace MapEditor.Platform
{
    /// <summary>
    /// What a map may be called. Shared verbatim by both halves of the resource — the client to say no
    /// before the round trip, the server because it does not believe the client said anything.
    ///
    /// The two barred characters are what the client's own KVP keys spend on themselves — settings,
    /// favorites and the session hand-off still live there, and maps did until they moved onto the server.
    /// Nothing here is trying to make a name safe to use as a file name: the server never uses it as one,
    /// it derives its own file name from scratch (see ServerMaps.MakeFileName), so a name full of slashes
    /// is merely ugly rather than dangerous.
    /// </summary>
    public static class MapNames
    {
        public const int MaxLength = 100;

        public static bool IsValid(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            name = name.Trim();
            if (name.Length == 0 || name.Length > MaxLength) return false;
            if (name.IndexOf(':') >= 0 || name.IndexOf('#') >= 0) return false;

            // Newlines and the rest would come back out in the file picker, in a chat message and in the
            // server log, all of which are lines.
            foreach (var c in name)
            {
                if (char.IsControl(c)) return false;
            }

            return true;
        }
    }
}
