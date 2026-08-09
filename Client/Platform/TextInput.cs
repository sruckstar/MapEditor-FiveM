using System;
using System.Threading.Tasks;
using CitizenFX.Core;
using CitizenFX.Core.Native;

namespace MapEditor.Platform
{
    /// <summary>
    /// The on-screen keyboard, driven by hand rather than through CitizenFX's <c>Game.GetUserInput</c>.
    ///
    /// CitizenFX's version is three lines — DISPLAY_ONSCREEN_KEYBOARD, then UPDATE_ONSCREEN_KEYBOARD every
    /// frame until it stops returning 0 — but it passes <c>null</c> for the four description strings the
    /// native takes, and it reports nothing at all when the box fails to come up. Both matter here: the
    /// Lua idiom every FiveM resource uses passes empty strings, and a box that does not appear is exactly
    /// the failure this had to be able to see. So the same three lines live here, with "" instead of null
    /// and with the state the game reports written to F8.
    ///
    /// Ctrl+V pasting, which the SP build added on top via GetAsyncKeyState + WinForms Clipboard, is gone:
    /// FiveM allows neither P/Invoke nor System.Windows.Forms. Getting it back means an NUI text field.
    /// </summary>
    public static class TextInput
    {
        /// <summary>The stock text label the SP build's box was titled with. "Enter a value".</summary>
        private const string TitleLabel = "FMMC_KEY_TIP8";

        /// <summary>
        /// UPDATE_ONSCREEN_KEYBOARD's answers. 0 means the player is still typing; the box is only on
        /// screen while it says that.
        /// </summary>
        private const int Pending = 0;
        private const int Success = 1;

        /// <summary>
        /// Frames before an open keyboard is given up on. The box has no timeout of its own, so this only
        /// fires when the game has stopped answering at all — which is the failure worth a log line.
        /// </summary>
        private const int StuckFrames = 60 * 60;

        private static int _openCount;

        /// <summary>
        /// Whether the player is typing right now. The SP build never needed to ask: its keyboard blocked
        /// the script fiber, so no frame of the editor ran while the box was up. Here the frames keep
        /// coming, and a frame of the editor reads the very controls the player is typing on. Callers that
        /// run every frame stand down while this is set.
        /// </summary>
        public static bool IsOpen
        {
            get { return _openCount > 0; }
        }

        /// <summary>Written to F8 by every open, so a box that never appears leaves a trace.</summary>
        public static bool Verbose = true;

        /// <summary>Returns null when the player cancelled.</summary>
        public static async Task<string> Get(string defaultText, int maxLength)
        {
            // Two keyboards at once is one keyboard: the game has a single box, and the second call is
            // dropped on the floor while the first is up. Better to say so than to hang on it.
            if (IsOpen)
            {
                Log.Info("Keyboard: refused, one is already open.");
                return null;
            }

            _openCount++;
            try
            {
                return await Run(defaultText ?? string.Empty, maxLength);
            }
            finally
            {
                _openCount--;
            }
        }

        public static Task<string> Get(int maxLength)
        {
            return Get(string.Empty, maxLength);
        }

        private static async Task<string> Run(string defaultText, int maxLength)
        {
            // The game keeps the last box's result and state around, so asking once before opening clears
            // out an abandoned box — otherwise the loop below reads that stale answer on its first frame
            // and closes a box the player never saw.
            int stale = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            if (Verbose && stale != Pending)
                Log.Info("Keyboard: state before opening was {0}.", stale);

            // p0 is 1 for the plain single-line box, the four "" are the unused description lines under the
            // field, and maxLength + 1 leaves room for the terminator.
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD,
                1, TitleLabel, "", defaultText, "", "", "", maxLength + 1);

            if (Verbose)
                Log.Info("Keyboard: opened (default \"{0}\", max {1}).", defaultText, maxLength);

            int status = Pending;
            int frames = 0;

            // The box is drawn by the game as a side effect of being asked about, so this has to run every
            // frame for as long as the player is typing: stop calling it and the box goes off the screen.
            while (true)
            {
                // Otherwise the keys being typed are still live controls — letters that move the camera,
                // Enter that spawns a prop. The editor's tick stands down on IsOpen, but every other
                // resource on the server is still reading them.
                Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, 0);

                status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
                if (status != Pending) break;

                frames++;
                if (frames == 1 && Verbose)
                    Log.Info("Keyboard: box is up, waiting for the player.");

                if (frames > StuckFrames)
                {
                    Log.Info("Keyboard: gave up after {0} frames with no answer.", frames);
                    return null;
                }

                await Frame.Next();
            }

            if (status != Success)
            {
                if (Verbose)
                    Log.Info("Keyboard: closed without a result (state {0}, {1} frames).", status, frames);
                return null;
            }

            // The typed wrapper, not Function.Call<string>: the generic Call cannot return a string on
            // Enhanced.
            string result = API.GetOnscreenKeyboardResult();
            if (Verbose)
                Log.Info("Keyboard: got \"{0}\" after {1} frames.", result, frames);

            return result;
        }
    }
}
