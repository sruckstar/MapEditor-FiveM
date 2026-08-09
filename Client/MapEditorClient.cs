using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using MapEditor.Platform;

namespace MapEditor
{
    /// <summary>
    /// What FiveM loads. The SP build had none of this: <c>MapEditor</c> was itself the SHVDN
    /// <c>Script</c>, it did all its loading in its constructor, and it took its hotkey from a
    /// <c>KeyDown</c> event. None of the three survives the port, so the editor is now a plain class and
    /// this is the shell around it.
    ///
    /// * <b>Loading cannot happen in a constructor.</b> ObjectList.ini is 880 KB and the category list
    ///   another 650 KB, and both are parsed a few milliseconds at a time with the frame handed back in
    ///   between (see <see cref="Frame.Budget"/>) so that FiveM does not log the resource as stalling the
    ///   client. Yielding is only possible from an async method, so startup is <see cref="Initialize"/>
    ///   and the editor does not exist until it has finished.
    /// * <b>There are no key events.</b> The editor is opened by a command, and the command is bound to a
    ///   key with RegisterKeyMapping — which means the player rebinds it in GTA's own settings menu
    ///   rather than in ours, and the ActivationKey setting is gone.
    /// * <b>A resource can be stopped.</b> Everything the editor spawned is game-side and outlives the
    ///   script, so onResourceStop does what SHVDN's Aborted did.
    /// </summary>
    public class MapEditorClient : BaseScript
    {
        /// <summary>The command RegisterKeyMapping binds. Also typeable in the client console.</summary>
        private const string ToggleCommand = "mapeditor";

        /// <summary>Only the default: the player rebinds it in Settings → Key Bindings → FiveM.</summary>
        private const string DefaultKey = "F7";

        /// <summary>
        /// Opens the game's text box and nothing else. Typed in the client console (F8) to tell a broken
        /// keyboard apart from a broken editor: this path touches neither the editor nor the freecam, so a
        /// box that fails to appear here is failing outside anything this resource does per frame.
        /// </summary>
        private const string KeyboardTestCommand = "mapeditor_kbd";

        /// <summary>
        /// Lists, in the client console, the prop models the game has no archetype for where the player is
        /// standing. Takes an optional substring to narrow the list down.
        ///
        /// This is the list a server owner needs and cannot otherwise get: those models are not broken and
        /// there is nothing this resource can do to load them, but a resource that requests the .ytyp they
        /// are defined in (DLC_ITYP_REQUEST, see the readme) makes them available everywhere. Run it where
        /// the map is being built.
        /// </summary>
        private const string UnavailableCommand = "mapeditor_unavailable";

        /// <summary>
        /// How many names the command prints before it stops and reports a count instead. The console
        /// scrolls, and ten thousand lines of it are no more useful than one.
        /// </summary>
        private const int MaxListedModels = 200;

        private const string ObjectListFile = "data/ObjectList.ini";

        /// <summary>
        /// With this set (<c>setr mapeditor_manual_start 1</c>), nothing of the startup below runs until the
        /// player asks for the editor. It exists for the case this whole diagnostic layer exists for: a
        /// client that dies while starting up cannot be joined to at all, and this puts the moment it dies
        /// under the player's finger instead of on the join.
        /// </summary>
        private const string ManualStartConvar = "mapeditor_manual_start";

        /// <summary>
        /// How many frames in a row the editor may throw before it is switched off. An editor that throws
        /// every frame does not just fail: the log line, the stack and the exception itself cost more than a
        /// frame is worth, and the game slows to the point where the console cannot be opened to read any of
        /// it. Three, because one bad frame is a glitch and three is a state.
        /// </summary>
        private const int MaxTickFailures = 3;

        private MapEditor _editor;

        /// <summary>
        /// Set the moment startup begins, not when it ends: <see cref="Initialize"/> is a tick handler and
        /// would otherwise be entered again on every frame while the first run is still awaiting a chunk.
        /// </summary>
        private bool _starting;

        /// <summary>Set by the toggle command. Only read when <see cref="ManualStartConvar"/> is on.</summary>
        private bool _startRequested;

        /// <summary>Frames the editor has thrown on in a row. See <see cref="MaxTickFailures"/>.</summary>
        private int _tickFailures;

        /// <summary>Whether the editor has ever completed a frame. Reported once; see <see cref="OnTick"/>.</summary>
        private bool _firstTickDone;

        public MapEditorClient()
        {
            // Before anything else: every chunked loader below yields through this, and a null delegate
            // would take the resource down on the first await rather than at a readable place.
            Frame.Bind(Delay);

            // Same arrangement, and for the same reason: TriggerServerEvent is a member of BaseScript, and
            // the store that needs it is static.
            Rpc.Bind(EventHandlers, (eventName, args) => TriggerServerEvent(eventName, args));

            // The broadcast the server sends when a map is published or unloaded, bound at the earliest
            // possible moment. Anything published before the editor exists is picked up by its first frame,
            // which reconciles from scratch.
            MapStore.Bind(EventHandlers);

            // The server asking this client to finish off an entity it created. Bound this early because the
            // request can arrive before the editor exists, and a handler that is not there loses it.
            SharedEntities.Bind(EventHandlers);

            EventHandlers["onResourceStop"] += new Action<string>(OnResourceStop);

            API.RegisterCommand(ToggleCommand, new Action<int, List<object>, string>(OnToggleCommand), false);
            API.RegisterKeyMapping(ToggleCommand, "Map Editor", "keyboard", DefaultKey);

            API.RegisterCommand(KeyboardTestCommand, new Action<int, List<object>, string>(OnKeyboardTestCommand), false);
            API.RegisterCommand(UnavailableCommand, new Action<int, List<object>, string>(OnUnavailableCommand), false);

            ModManager.Register(EventHandlers);

            Tick += Initialize;
        }

        /// <summary>
        /// Reads everything the editor browses before building it. Runs once, over as many frames as it
        /// takes, and hands the tick over to the editor at the end.
        /// </summary>
        private async Task Initialize()
        {
            if (_starting) return;
            _starting = true;

            try
            {
                // A client resource starts before the player is in the world, and everything below asks the
                // game about the player — their relationship group, their language.
                await Boot.Step("waiting for the player to be in the world");
                while (!API.NetworkIsPlayerActive(API.PlayerId()))
                    await Delay(100);

                // The escape hatch for a client that dies during startup: with the convar set, none of the
                // below runs until the player asks for the editor. Off by default.
                if (API.GetConvarInt(ManualStartConvar, 0) != 0)
                {
                    await Boot.Step("holding: " + ManualStartConvar + " is set, waiting for the " + ToggleCommand + " command");
                    while (!_startRequested)
                        await Delay(250);
                }

                // Started rather than awaited: it answers even when nothing is listening — a server running
                // only the client half — but it answers by timing out, and that wait belongs behind the
                // database loading below. Awaited before the editor exists, since the first thing the editor
                // does is spawn the autoloaded maps, and the server's are among them.
                await Boot.Step("handshake");
                var handshake = MapStore.Handshake();

                await Boot.Step("settings");
                var settings = Settings.Load();

                await Boot.Step("translations");
                Translation.Load(settings.Translation);

                await Boot.Step("relationship groups");
                ObjectDatabase.SetupRelationships();
                ObjectDatabase.FlagUnavailable = settings.OmitInvalidObjects;
                ObjectDatabase.DropLegacyBlacklist();

                // The prop list ships as a file; vehicles and peds are rebuilt from the game's own hash
                // enums, so the resource ships no list for either.
                await Boot.Step("object list");
                ObjectDatabase.MainDb = await ObjectDatabase.LoadFromResource(ObjectListFile);

                await Boot.Step("vehicle and ped enums");
                ObjectDatabase.LoadEnumDatabases();

                // Must follow the three databases: the categories are a view over them, and a category
                // file can fold a hand-added model name back into one.
                await Boot.Step("categories");
                await ObjectCategories.LoadAll();

                SmartStreaming.Range = settings.StreamingRange;

                await Boot.Step("waiting for the server's answer");
                await handshake;

                // Maps an earlier build wrote into KVP, back when the client kept its own. Nothing reads
                // there now, so they are moved to the player's folder on the server. Started, never awaited:
                // it paces itself around the server's write allowance.
                Log.Guard("Moving maps out of local storage", () => MapStore.MigrateLegacyMaps());

                // The heaviest single step: every menu and five instructional-button scaleforms in one
                // frame. It gets a breadcrumb of its own because a constructor cannot hand the frame back,
                // so a hang inside it would otherwise be reported as a hang in whatever ran before.
                await Boot.Step("building the editor");
                _editor = new MapEditor(settings);

                // The last thing startup says for itself. "done" comes from the other end of the first editor
                // frame (see OnTick), the one piece of startup this method cannot watch: nothing sent from
                // inside a hanging frame ever leaves it.
                await Boot.Step("first frame");
                Tick -= Initialize;
                Tick += OnTick;
            }
            catch (Exception e)
            {
                // Without this the exception is swallowed by the tick and the resource simply never opens,
                // with nothing anywhere to say why.
                Log.Error("MapEditorClient.Initialize", e);

                // Both of the places a player can be told when their console is not reachable: their own
                // screen, and the server's console.
                Boot.Note("FAILED: " + e.GetType().Name + ": " + e.Message);
                Compat.Notify("~r~~h~Map Editor~h~~w~~n~" + e.GetType().Name + ": " + e.Message);

                Tick -= Initialize;
            }
        }

        /// <summary>
        /// The editor's frame, with a fuse on it.
        ///
        /// Catching and carrying on is right for one bad frame and wrong for a fault that repeats: printing
        /// a stack trace every frame is itself enough to bring the client to a halt, and a client at a halt
        /// cannot open the console the stack traces are being printed to. So after
        /// <see cref="MaxTickFailures"/> frames in a row the editor is taken off the tick and the reason is
        /// put where it can still be read — the player's screen, and the server console.
        /// </summary>
        private async Task OnTick()
        {
            try
            {
                await _editor.OnTick();
                _tickFailures = 0;

                if (!_firstTickDone)
                {
                    _firstTickDone = true;

                    // Reached only once the editor's first frame has run end to end, which is why this is
                    // here rather than at the end of Initialize.
                    Log.Info("Ready. {0} props, {1} vehicles, {2} peds.",
                        ObjectDatabase.MainDb.Count, ObjectDatabase.VehicleDb.Count, ObjectDatabase.PedDb.Count);
                    Boot.Finished(string.Format("first editor frame ran; {0} props, {1} vehicles, {2} peds",
                        ObjectDatabase.MainDb.Count, ObjectDatabase.VehicleDb.Count, ObjectDatabase.PedDb.Count));
                }
            }
            catch (Exception e)
            {
                Log.Error("MapEditorClient.OnTick", e);

                if (++_tickFailures < MaxTickFailures) return;

                Tick -= OnTick;

                // The editor is gone but what it held is not: a frozen invisible player looking through a
                // camera nothing drives, made permanent by the tick going away. Guarded on its own, since
                // an editor in a state it throws out of is exactly the case this runs in.
                try
                {
                    _editor.HandBackToPlayer();
                }
                catch (Exception cleanup)
                {
                    Log.Error("MapEditorClient.OnTick cleanup", cleanup);
                }

                var what = e.GetType().Name + ": " + e.Message;
                Log.Info("The editor threw on {0} frames in a row and has been switched off. " +
                         "Restart the resource to try again.", MaxTickFailures);
                Boot.Alarm("editor switched off after " + MaxTickFailures + " failed frames — " + what);
                Compat.Notify("~r~~h~Map Editor~h~~w~~n~" + what);
            }
        }

        /// <summary>
        /// Replaces the SP build's OnKeyDown. The command carries the arguments FiveM hands every command
        /// and uses none of them: on a client, source is always 0 and the editor takes no parameters.
        /// </summary>
        private void OnToggleCommand(int source, List<object> args, string raw)
        {
            if (_editor == null)
            {
                // Also the trigger for a held startup: with ManualStartConvar set, this is what starts it.
                // Harmless otherwise — nothing reads the flag when startup is already running.
                _startRequested = true;

                Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate("Still loading the object list. One moment."));
                return;
            }

            _editor.ToggleMenu();
        }

        private void OnKeyboardTestCommand(int source, List<object> args, string raw)
        {
            Log.Guard("Keyboard test", async () =>
            {
                string typed = await TextInput.Get("test", 30);
                Log.Info("Keyboard test: {0}", typed == null ? "<cancelled or never opened>" : "\"" + typed + "\"");
            });
        }

        /// <summary>
        /// Walks the whole prop list against the game, from wherever the player is, and says what it does
        /// not have. See <see cref="UnavailableCommand"/> for why anyone would want that.
        /// </summary>
        private void OnUnavailableCommand(int source, List<object> args, string raw)
        {
            if (ObjectDatabase.MainDb == null)
            {
                Log.Info("The object list is still loading.");
                return;
            }

            string filter = args != null && args.Count > 0 && args[0] != null
                ? args[0].ToString().ToLowerInvariant()
                : null;

            Log.Guard("Availability scan", async () =>
            {
                // Asked of the game directly rather than through the per-location cache: this walks 25,000
                // models over several frames, and a player who drifts meanwhile would get half an answer
                // from each place with nothing marking which.
                var missing = new List<string>();
                int checkedCount = 0;
                var budget = new Frame.Budget(4);

                // Copied first: the scan spans frames, and the category loader folds hand-added names into
                // this dictionary while it runs — enumerating it directly would throw if the two overlap.
                foreach (var pair in new List<KeyValuePair<string, int>>(ObjectDatabase.MainDb))
                {
                    await budget.YieldIfExpired();

                    if (filter != null && pair.Key.ToLowerInvariant().IndexOf(filter, StringComparison.Ordinal) < 0)
                        continue;

                    checkedCount++;
                    if (!ObjectDatabase.IsRegisteredNow(pair.Value)) missing.Add(pair.Key);
                }

                var position = Game.Player.Character.Position;
                Log.Info("Availability at {0:0}, {1:0}, {2:0}: {3} of {4} prop models{5} are not registered on " +
                         "this client. They are not broken — an interior or DLC archetype only exists while " +
                         "the game has that area's map data loaded. To have them everywhere, a resource has " +
                         "to request their .ytyp (DLC_ITYP_REQUEST).",
                    position.X, position.Y, position.Z, missing.Count, checkedCount,
                    filter == null ? "" : " matching \"" + filter + "\"");

                for (int i = 0; i < missing.Count && i < MaxListedModels; i++)
                    Log.Info("  {0}", missing[i]);

                if (missing.Count > MaxListedModels)
                    Log.Info("  ... and {0} more. Pass a substring to narrow this down: {1} m25_",
                        missing.Count - MaxListedModels, UnavailableCommand);
            });
        }

        /// <summary>
        /// A resource restart tears the script down and builds it again from nothing, while everything it
        /// spawned stays exactly where it was — with no script left that knows those entities exist. So the
        /// map goes out of the world here and into KVP, for the instance that replaces this one to pick up.
        /// See <see cref="SessionRestore"/>.
        /// </summary>
        private void OnResourceStop(string resourceName)
        {
            if (resourceName != API.GetCurrentResourceName()) return;
            if (_editor == null) return;

            try
            {
                _editor.OnAborted();
            }
            catch (Exception e)
            {
                Log.Error("MapEditorClient.OnResourceStop", e);
            }
        }
    }
}
