using System;
using System.Globalization;
using CitizenFX.Core;
using CitizenFX.Core.Native;

namespace MapEditor.Platform
{
    /// <summary>
    /// Time, without <see cref="DateTime"/>'s clock.
    ///
    /// On the Enhanced client `DateTime.Now` and `DateTime.UtcNow` throw before they return a value:
    ///
    /// <code>
    /// System.TypeInitializationException: The type initializer for 'LeapSecondCache' threw an exception.
    ///  ---> System.NotImplementedException: This method is not available in FiveM C# runtime.
    ///    at Interop.NtDll.NtQuerySystemInformation(...)
    ///    at System.DateTime.GetSystemSupportsLeapSeconds()
    /// </code>
    ///
    /// The property asks the OS once, from a static constructor, whether the system clock accounts for
    /// leap seconds — and that ask goes through ntdll, which the runtime stubs out. Because it happens in
    /// a type initializer, it fails once and stays failed: every later read of either property throws the
    /// same exception again. Everything else about the type is fine — arithmetic, parsing, formatting,
    /// <c>new DateTime(...)</c> — it is only "what time is it" that is gone. Legacy is unaffected, but
    /// this file is what both clients use, so nothing here is conditional.
    ///
    /// Three replacements, because the editor asks three different questions:
    ///
    /// * <see cref="Milliseconds"/> — "how long since X", for autosave intervals, scan throttles and
    ///   timeouts. The game's own timer, which is what the rest of the editor already measures frames
    ///   with, and it is monotonic: no clock changes, no time zones, nothing to parse.
    /// * <see cref="SharedSeconds"/> — "what time is it <i>for everybody</i>", for the one thing that has
    ///   to look identical on two machines with nothing sent between them. A different native and a
    ///   different kind of answer; see its own remarks for why the obvious one is wrong.
    /// * <see cref="UtcNow"/> — "what is today's date", needed only for the timestamp shown beside a
    ///   saved map. That one really does want a wall clock, and the game has natives for it.
    ///
    /// <see cref="Frame"/> keeps its own <c>Environment.TickCount</c> and does not come through here: it
    /// is measuring the milliseconds inside a single frame, and a native call per check would be part of
    /// what it is trying to measure. That one is safe — the runtime's stubs are a list of Win32 entry
    /// points, GetTickCount64 is not on it, and NtQuerySystemInformation is.
    /// </summary>
    public static class Clock
    {
        /// <summary>
        /// Milliseconds since the game started. Monotonic within a session and shared by every script in
        /// the process, so a value stored by one run of the resource still means something to the next.
        ///
        /// Wraps after about 24.8 days of the game being open; <see cref="Since"/> is what handles that.
        /// </summary>
        public static int Milliseconds
        {
            get { return Game.GameTime; }
        }

        /// <summary>
        /// How long ago <paramref name="stamp"/> was taken, in milliseconds. Unchecked subtraction, so the
        /// answer stays right across the timer's wrap — which is also why intervals are compared through
        /// this rather than by subtracting two <see cref="Milliseconds"/> readings by hand.
        /// </summary>
        public static int Since(int stamp)
        {
            return unchecked(Game.GameTime - stamp);
        }

        /// <summary>Whether at least <paramref name="milliseconds"/> have passed since <paramref name="stamp"/>.</summary>
        public static bool Elapsed(int stamp, int milliseconds)
        {
            return Since(stamp) >= milliseconds;
        }

        /// <summary>
        /// The clock every client of a server reads the same, in seconds. Anything that has to <i>look</i>
        /// the same on two machines without a byte being sent has to be a function of this and of nothing
        /// else.
        ///
        /// Not <see cref="Milliseconds"/>, which is where this started and what is worth saying plainly:
        /// GET_GAME_TIMER counts from the moment each player launched their own game, so it is a different
        /// number on every machine in the session. Two people in front of the same blinking laser each saw
        /// it blink to their own beat. GET_NETWORK_TIME is the session's clock, held in step by the server,
        /// and it is what makes a published or co-edited laser possible without sending anything.
        ///
        /// Two things about the number itself, both of which decide how it has to be read:
        ///
        /// * It is an unsigned 32-bit count of milliseconds arriving through an <c>int</c>, so half its
        ///   range comes back negative. It is read back as unsigned here and the same way in the exported
        ///   map's Lua, so that the two implementations of the laser cannot disagree about what time it is.
        /// * <b>Seconds of it do not fit in a float.</b> The count runs to about 4.3 billion milliseconds,
        ///   and at 4.3 million seconds a float's own step is a quarter of a second — a blink that stutters
        ///   and a wave that moves in jerks. Hence double, and hence double all the way down to the sine.
        ///
        /// Before the session's clock has started there is nothing shared to read and the game's timer
        /// stands in: a laser blinking to itself beats a laser stuck on.
        /// </summary>
        public static double SharedSeconds
        {
            get
            {
                var ms = API.HasNetworkTimeStarted()
                    ? unchecked((uint) API.GetNetworkTime())
                    : unchecked((uint) Game.GameTime);

                return ms / 1000.0;
            }
        }

        /// <summary>
        /// The current UTC date and time, or null when the game will not say — GET_UTC_TIME is a game
        /// native rather than a FiveM one, and this is the only caller that would have to guess if a
        /// build ever stopped answering. Every use of it is a timestamp shown to the player, so null is
        /// allowed to mean "then it is not shown".
        /// </summary>
        public static DateTime? UtcNow()
        {
            int year = 0, month = 0, day = 0, hour = 0, minute = 0, second = 0;
            API.GetUtcTime(ref year, ref month, ref day, ref hour, ref minute, ref second);
            return Compose(year, month, day, hour, minute, second, DateTimeKind.Utc);
        }

        /// <summary>
        /// <see cref="UtcNow"/> as an ISO 8601 round-trip string, the form the metadata is stored in and
        /// the server sends its own timestamps in. Null when the clock is unavailable.
        /// </summary>
        public static string Stamp()
        {
            var now = UtcNow();
            return now.HasValue ? now.Value.ToString("o", CultureInfo.InvariantCulture) : null;
        }

        /// <summary>
        /// A UTC time in the player's own time zone, for display.
        ///
        /// Not <see cref="DateTime.ToLocalTime"/>: that goes through <c>TimeZoneInfo.Local</c>, which asks
        /// the OS through the same interop layer the runtime stubs out, and there is no reason for this
        /// file to depend on the BCL's clock at all when the game answers both questions. The offset is
        /// measured instead, by asking for both clocks and taking the difference; zones are whole minutes,
        /// so the seconds between the two native calls round away.
        /// </summary>
        public static DateTime ToLocal(DateTime utc)
        {
            if (utc.Kind == DateTimeKind.Local) return utc;

            int year = 0, month = 0, day = 0, hour = 0, minute = 0, second = 0;
            API.GetLocalTime(ref year, ref month, ref day, ref hour, ref minute, ref second);
            var local = Compose(year, month, day, hour, minute, second, DateTimeKind.Local);

            var now = UtcNow();
            if (!local.HasValue || !now.HasValue) return utc;

            var offset = TimeSpan.FromMinutes(Math.Round((local.Value - now.Value).TotalMinutes));
            return DateTime.SpecifyKind(utc + offset, DateTimeKind.Local);
        }

        /// <summary>
        /// Builds the DateTime, or null if the numbers are not a date. A native that is not implemented
        /// leaves the outputs at zero rather than failing, so the check is the only way to tell a missing
        /// clock from a working one.
        /// </summary>
        private static DateTime? Compose(int year, int month, int day, int hour, int minute, int second, DateTimeKind kind)
        {
            if (year < 1 || year > 9999 || month < 1 || month > 12 || day < 1 || day > 31 ||
                hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 59)
                return null;

            if (day > DateTime.DaysInMonth(year, month)) return null;

            return new DateTime(year, month, day, hour, minute, second, kind);
        }
    }
}
