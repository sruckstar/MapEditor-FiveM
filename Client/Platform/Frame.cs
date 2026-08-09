using System;
using System.Threading.Tasks;

namespace MapEditor.Platform
{
    /// <summary>
    /// Replaces ScriptHookVDotNet's Script.Wait / Script.Yield.
    ///
    /// SHVDN runs each script on its own fiber, so yielding a frame is a synchronous call that can be made
    /// from any depth of the call stack. FiveM has no fibers: the only way to give the frame back is to
    /// await, which is why every method that used to call Script.Yield is now async in this port.
    ///
    /// BaseScript.Delay is not accessible from outside a BaseScript, so the script class hands its own
    /// bound delegate over in <see cref="Bind"/> before anything else runs.
    /// </summary>
    public static class Frame
    {
        private static Func<int, Task> _delay;

        /// <summary>Called once by the client script's constructor.</summary>
        public static void Bind(Func<int, Task> delay)
        {
            if (delay == null) throw new ArgumentNullException("delay");
            _delay = delay;
        }

        /// <summary>Gives the frame back and resumes on the next tick. The direct analogue of Script.Yield().</summary>
        public static Task Next()
        {
            return _delay(0);
        }

        /// <summary>The analogue of Script.Wait(ms).</summary>
        public static Task Wait(int milliseconds)
        {
            return _delay(milliseconds);
        }

        /// <summary>
        /// Yields only once <paramref name="budgetMs"/> of wall clock has been spent since the last yield.
        /// Used by the chunked loaders (ObjectList.ini, category files, map loading): yielding on every
        /// iteration would stretch a one-frame job over thousands of frames, while never yielding trips
        /// FiveM's "script took too long" warning.
        /// </summary>
        public sealed class Budget
        {
            private readonly int _budgetMs;
            private int _startTicks;

            public Budget(int budgetMs)
            {
                _budgetMs = budgetMs;
                _startTicks = Environment.TickCount;
            }

            public bool Expired
            {
                get { return unchecked(Environment.TickCount - _startTicks) >= _budgetMs; }
            }

            public async Task YieldIfExpired()
            {
                if (!Expired) return;
                await Frame.Next();
                _startTicks = Environment.TickCount;
            }
        }
    }
}
