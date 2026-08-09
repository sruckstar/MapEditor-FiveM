using System.Threading.Tasks;
using CitizenFX.Core;
using CitizenFX.Core.Native;

namespace MapEditor.Platform
{
    /// <summary>
    /// Breadcrumbs from the client's startup, printed on the <b>server</b> console.
    ///
    /// Why anywhere but the client console: a client that hangs takes F8 with it — the console needs
    /// frames to draw, and a script that stops handing the frame back means there are none. The FiveM
    /// Enhanced client writes no log file either (checked: nothing under either client's data directory),
    /// so a frozen Enhanced client leaves nothing at all behind. The server is the one party still running,
    /// and its console is written to disk by whoever runs it.
    ///
    /// <see cref="Step"/> hands the frame back after sending. That is the whole trick: a net event queued
    /// during a frame leaves at the end of that frame, so a step that never returns would take its own
    /// breadcrumb down with it. Yielding first means the last line the server printed is the step
    /// <i>before</i> the one that hung — which is exactly how the hanging step gets named.
    ///
    /// On by default, because the port is still being brought up and a hang costs a whole session
    /// otherwise. <c>setr mapeditor_trace 0</c> in server.cfg turns it off; it is a dozen tiny events per
    /// join, all of them before the editor exists.
    /// </summary>
    public static class Boot
    {
        private const string Convar = "mapeditor_trace";

        /// <summary>
        /// Set once startup is over. Everything here is about the window between the resource starting and
        /// the editor existing; after that the client console is reachable again and this would only be
        /// noise on somebody else's server.
        /// </summary>
        private static bool _finished;

        private static bool Enabled
        {
            // Read every time rather than cached: convars replicated with setr can arrive after the
            // resource has started, and the whole point is to be on for the run that goes wrong.
            get { return !_finished && API.GetConvarInt(Convar, 1) != 0; }
        }

        /// <summary>
        /// Names the step that is about to run, and hands the frame back so the line is on its way before
        /// that step gets the chance to hang.
        /// </summary>
        public static async Task Step(string step)
        {
            if (!Enabled) return;

            Send(step);
            await Frame.Next();
        }

        /// <summary>
        /// <see cref="Step"/> from somewhere that cannot yield — a constructor, a tick. The line still
        /// leaves at the end of the current frame, so it survives anything that hangs in a later one but
        /// not something that hangs in this one.
        /// </summary>
        public static void Note(string step)
        {
            if (!Enabled) return;

            Send(step);
        }

        /// <summary>Startup is over: stop tracing. Says so once, so the server console shows the end.</summary>
        public static void Finished(string summary)
        {
            if (Enabled) Send("done — " + summary);
            _finished = true;
        }

        /// <summary>
        /// Something went wrong after startup and the player may have no console to read it in. Says it
        /// anyway, whether or not startup is over; the server counts these like any other line, so a fault
        /// that repeats every frame cannot turn into a flood.
        /// </summary>
        public static void Alarm(string what)
        {
            if (API.GetConvarInt(Convar, 1) == 0) return;

            Send(what);
        }

        private static void Send(string step)
        {
            // Also to the client console, for the legacy client where there is a log file to read it in.
            Debug.WriteLine("[MapEditor] boot: " + step);

            if (Rpc.Ready) Rpc.Fire("trace", step);
        }
    }
}
