using System;
using System.Collections.Generic;
using CitizenFX.Core;

namespace MapEditor.Platform
{
    /// <summary>
    /// The SP build wrote a log file next to the script. A FiveM client cannot write to disk, so
    /// everything goes to the client console (F8) instead, prefixed so it can be filtered.
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

        /// <summary>
        /// How long the same failure stays quiet after it has been reported once. Everything the editor
        /// does that can throw is either per frame or per keypress, so a fault that repeats does not repeat
        /// twice — it repeats sixty times a second, and a stack trace per frame is by itself enough to bring
        /// a client down to a standstill.
        /// </summary>
        private const int RepeatQuietMs = 10000;

        /// <summary>How many distinct failures are remembered before the table is thrown away and rebuilt.</summary>
        private const int MaxRemembered = 64;

        private sealed class Repeat
        {
            public int LastPrinted;
            public int Suppressed;
        }

        private static readonly Dictionary<string, Repeat> Repeats = new Dictionary<string, Repeat>();

        public static void Error(string context, Exception ex)
        {
            var key = context + "|" + ex.GetType().FullName + "|" + ex.Message;

            Repeat repeat;
            if (Repeats.TryGetValue(key, out repeat))
            {
                if (!Clock.Elapsed(repeat.LastPrinted, RepeatQuietMs))
                {
                    repeat.Suppressed++;
                    return;
                }
            }
            else
            {
                // Not a cache, so nothing is lost by dropping it: the next occurrence of anything in it
                // prints in full and starts counting again.
                if (Repeats.Count >= MaxRemembered) Repeats.Clear();
                Repeats[key] = repeat = new Repeat();
            }

            var again = repeat.Suppressed > 0
                ? " (and " + repeat.Suppressed + " more like it since the last line)"
                : "";
            repeat.Suppressed = 0;
            repeat.LastPrinted = Clock.Milliseconds;

            Debug.WriteLine(Prefix + "ERROR in " + context + again + ": " + ex);
        }

        /// <summary>
        /// Menu item handlers return void, so the bodies that had to become async are `async void`.
        /// An exception escaping one of those is unobservable and kills the resource without a usable
        /// stack, so every such body wraps itself in this.
        /// </summary>
        public static async void Guard(string context, System.Func<System.Threading.Tasks.Task> body)
        {
            try
            {
                await body();
            }
            catch (Exception ex)
            {
                Error(context, ex);
            }
        }
    }
}
