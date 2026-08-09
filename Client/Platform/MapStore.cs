using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using CitizenFX.Core;

namespace MapEditor.Platform
{
    /// <summary>Where a map lives. Both scopes are folders on the server; who can see them is the difference.</summary>
    public enum MapScope
    {
        /// <summary>
        /// This player's own folder on the server, <c>maps/players/&lt;their name&gt;/</c>. Listed only to
        /// them, loadable only by them, and — unless the owner has restricted mapeditor.save — writable by
        /// them. This is what "Save Local" means and where the autosave goes. A personal map is a draft: it
        /// never spawns by itself and nobody else ever sees it.
        /// </summary>
        Personal,

        /// <summary>
        /// The server's shared storage. Everyone connected sees it in the same picker, and while it is
        /// <see cref="MapEntry.Live"/> it is standing in everyone's world. Publishing needs the
        /// mapeditor.publish ACE; taking one back down needs mapeditor.unload.
        /// </summary>
        Shared,
    }

    public class MapEntry
    {
        public MapEntry(MapScope scope, string name)
        {
            Scope = scope;
            Name = name;
        }

        public MapScope Scope { get; private set; }
        public string Name { get; private set; }

        /// <summary>Characters, not bytes — enough to show the player which of two saves is the big one.</summary>
        public int Size { get; set; }

        public DateTime? SavedAt { get; set; }

        /// <summary>Who saved it: the name they were connected under at the time.</summary>
        public string Author { get; set; }

        /// <summary>
        /// Whether this map is standing in every player's world at this moment. Shared maps only —
        /// publishing one sets it, unloading one clears it, and a personal map never carries it.
        ///
        /// It is recorded beside the map rather than inside it, so that a client can tell what to spawn
        /// without fetching and parsing every map in the catalogue. See <see cref="AutoloadedMaps"/>.
        /// </summary>
        public bool Live { get; set; }

        /// <summary>
        /// Enough of the entry to tell one version of a map from the next. What is standing in the world is
        /// compared against the catalogue by this, so that a map republished over the top of itself is
        /// noticed and respawned rather than left as the copy that was there before.
        /// </summary>
        public string Stamp
        {
            get
            {
                return Size.ToString(CultureInfo.InvariantCulture) + "@" +
                       (SavedAt.HasValue ? SavedAt.Value.Ticks.ToString(CultureInfo.InvariantCulture) : "");
            }
        }

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>
    /// Replaces UserMaps and the file picker's Directory.EnumerateFiles walk.
    ///
    /// <b>A FiveM client cannot write a file anywhere.</b> That is the fact everything here follows from.
    /// Maps used to be KVP entries on the player's machine, which is real persistence but not a file: the
    /// player could not open one, copy one, back one up or take one to another server, and nothing outside
    /// the game could see that they existed. So both storages are now the server's, and they differ only in
    /// who may look:
    ///
    /// * <see cref="MapScope.Personal"/> — the player's own folder, made for them the first time they save.
    ///   Nobody else is shown it and nobody else can read it. Saving needs no permission.
    /// * <see cref="MapScope.Shared"/> — the server's own storage, everyone's to load, and needing the
    ///   mapeditor.publish ACE to write to.
    ///
    /// Everything about both is a round trip (see <see cref="Rpc"/>), so the API splits in two:
    ///
    /// * the catalogue is <b>cached</b> and read synchronously, because a menu is redrawn inside a frame
    ///   and cannot await. <see cref="RefreshServer"/> is what fills it, and the menus call it before
    ///   they open;
    /// * content is always <b>awaited</b> — <see cref="ReadAsync"/>, <see cref="WriteAsync"/>,
    ///   <see cref="DeleteAsync"/> — and works for both scopes, so a caller that does not care where a
    ///   map lives does not have to ask.
    ///
    /// The consequence worth stating plainly: on a server whose half of this resource is not running,
    /// there is nowhere to save at all. The editor says so rather than pretending; see
    /// <see cref="ServerAvailable"/>.
    /// </summary>
    public static class MapStore
    {
        /// <summary>
        /// The KVP prefix maps used to be stored under. Only <see cref="LegacyMapNames"/> reads it now: it
        /// exists so that maps saved by an earlier build are moved into the player's folder rather than
        /// stranded where nothing can reach them. See <see cref="MigrateLegacyMaps"/>.
        /// </summary>
        private const string LegacyContentPrefix = "mapeditor:map:";

        private const string LegacyMetaPrefix = "mapeditor:mapmeta:";

        /// <summary>How long the handshake waits before deciding there is no server half. See <see cref="Hello"/>.</summary>
        private const int HelloTimeoutMs = 8000;

        /// <summary>
        /// Long enough that the server's write allowance (one save every few seconds) has refilled between
        /// maps. A migration is a burst of saves that nobody is waiting on, and being throttled into
        /// failing halfway would leave half a player's maps behind.
        /// </summary>
        private const int MigrationGapMs = 6000;

        private static List<MapEntry> _sharedMaps = new List<MapEntry>();
        private static List<MapEntry> _personalMaps = new List<MapEntry>();

        /// <summary>Whether the server half answered <see cref="Hello"/>. Without it there is nowhere to save.</summary>
        public static bool ServerAvailable { get; private set; }

        /// <summary>
        /// Whether this player may open the editor at all, as the server sees it.
        ///
        /// True until told otherwise: a server without the resource's server half — or without the editor
        /// restricted — is the ordinary case, and it must not lock the player out of a client-side tool.
        /// This is a courtesy the client extends, not a wall; see Access on the server side.
        /// </summary>
        public static bool CanUseEditor { get; private set; } = true;

        /// <summary>
        /// Whether this player may keep maps in their own folder. True until told otherwise, for the same
        /// reason as <see cref="CanUseEditor"/>: a server that never answers must not take away the one
        /// place there is to put a map.
        /// </summary>
        public static bool CanSave { get; private set; } = true;

        /// <summary>Whether this player may publish to the shared storage. Only ever granted by ACE.</summary>
        public static bool CanPublish { get; private set; }

        /// <summary>
        /// Whether the server runs OneSync, which this resource requires.
        ///
        /// True until a server that answered says otherwise, so that the one case it is false in is the one
        /// it is meant for: a server with our server half, running without OneSync. It is not a permission
        /// and it is not about this player — a published map's vehicles and its armed peds are created by
        /// the server, and a server that cannot own an entity cannot have them. The editor says so by name
        /// rather than refusing blankly, because nothing on the client's side would explain it.
        /// </summary>
        public static bool ServerHasOneSync { get; private set; } = true;

        /// <summary>
        /// Whether this player may take a published map out of everyone's world. Only ever granted by ACE,
        /// and separate from <see cref="CanPublish"/> because it acts on whichever map is up, not only on
        /// one's own.
        /// </summary>
        public static bool CanUnload { get; private set; }

        /// <summary>
        /// Called when the server says its shared catalogue has changed — a map published, republished,
        /// unloaded or deleted. The editor hangs the reconciliation off this; nothing else does.
        ///
        /// A plain field rather than an event because there is exactly one subscriber and it lives for as
        /// long as the resource does, so there is nothing to unsubscribe and nothing to get a second
        /// handler by accident.
        /// </summary>
        public static Action SharedMapsChanged;

        /// <summary>
        /// Binds the broadcast the server sends when the shared catalogue moves on. Called once, beside
        /// <see cref="Rpc.Bind"/>, and for the same reason: the script owns the handler table.
        /// </summary>
        public static void Bind(EventHandlerDictionary handlers)
        {
            if (handlers == null) throw new ArgumentNullException("handlers");

            handlers["mapeditor:cl:maps"] += new Action(OnSharedMapsChanged);
        }

        private static void OnSharedMapsChanged()
        {
            // Refreshed here rather than in the subscriber: the broadcast carries nothing, so the catalogue
            // has to be fetched once before anybody can act on it.
            Log.Guard("Server maps changed", async () =>
            {
                if (!await RefreshServer()) return;

                var changed = SharedMapsChanged;
                if (changed != null) changed();
            });
        }

        /// <summary>
        /// The largest map the server will take, in characters. Kept so that an oversized map is refused
        /// before it is sent rather than after: the upload is one event per 32 KB, and spending all of them
        /// to be told no at the end is the one failure this side can see coming.
        /// </summary>
        public static int ServerMaxMapSize { get; private set; }

        /// <summary>
        /// The whole conversation with the server, start to finish: who this player is, what the server
        /// will accept, and what it is holding. Started at startup and awaited once the databases are in,
        /// so that a server which never answers costs nothing but its own timeout, spent behind work that
        /// was going to take several seconds anyway.
        /// </summary>
        public static async Task Handshake()
        {
            await Hello();
            await RefreshServer();
        }

        /// <summary>
        /// Asks the server who this player is and what it will accept.
        ///
        /// A server that does not answer is not an error here: the request times out and the editor carries
        /// on as a tool with nowhere to save. Hence the shorter deadline — nothing is coming after it that
        /// is worth waiting the full one for.
        /// </summary>
        private static async Task Hello()
        {
            var response = await Rpc.Call(HelloTimeoutMs, "hello");
            if (!response.Ok)
            {
                Log.Info("No server component answered ({0}). Maps cannot be saved or loaded.", response.Message);
                return;
            }

            var answer = response.Json();
            if (answer == null) return;

            ServerAvailable = true;

            // Defaulted true for a server half older than this field: it had no OneSync requirement, so it
            // must not be reported as failing one.
            ServerHasOneSync = answer["oneSync"].AsBool(true);

            CanUseEditor = answer["canUse"].AsBool(true);

            // Defaulted to what an older server means by its silence: it had no separate permission for
            // either, and let anyone with the editor open keep maps while letting nobody unload.
            CanSave = answer["canSave"].AsBool(true);
            CanPublish = answer["canPublish"].AsBool(false);
            CanUnload = answer["canUnload"].AsBool(false);

            ServerMaxMapSize = answer["maxMapSize"].AsInt(0);

            Log.Info("Server component present. OneSync: {0}. Editor: {1}. Saving: {2}. Publishing: {3}. Unloading: {4}.",
                ServerHasOneSync ? "on" : "OFF", CanUseEditor ? "allowed" : "denied", CanSave ? "allowed" : "denied",
                CanPublish ? "allowed" : "denied", CanUnload ? "allowed" : "denied");

            if (!ServerHasOneSync)
                Log.Info("This server runs without OneSync, so the editor is disabled. The server has to be " +
                         "started with '+set onesync on': without it nothing can own a shared entity, and a " +
                         "published map's vehicles and armed peds would be a private copy per player.");
        }

        /// <summary>
        /// Pulls the catalogue into the cache that <see cref="List"/> reads — both scopes at once, since
        /// the server answers with everything this player may see. Returns false when there was nothing to
        /// ask or the ask failed, in which case the cache is left as it was: a menu showing the previous
        /// list is better than one that empties itself on a dropped packet.
        /// </summary>
        public static async Task<bool> RefreshServer()
        {
            if (!ServerAvailable) return false;

            var response = await Rpc.Call("list");
            if (!response.Ok)
            {
                Log.Info("Could not list the server's maps: {0}", response.Message);
                return false;
            }

            var document = response.Json();
            if (document == null) return false;

            var shared = new List<MapEntry>();
            var personal = new List<MapEntry>();

            foreach (var item in document["maps"].Items)
            {
                var name = item["name"].AsString(null);
                if (string.IsNullOrEmpty(name)) continue;

                var scope = item["scope"].AsString("shared") == "personal" ? MapScope.Personal : MapScope.Shared;

                var entry = new MapEntry(scope, name)
                {
                    Size = item["size"].AsInt(0),
                    Author = item["author"].AsString(null),

                    // "autoload" is what a server of the previous build called this, and on a shared map it
                    // meant the same thing: spawn this for everyone.
                    Live = scope == MapScope.Shared &&
                           item["live"].AsBool(item["autoload"].AsBool(false)),
                };

                DateTime savedAt;
                if (DateTime.TryParse(item["savedAt"].AsString(""), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out savedAt))
                    entry.SavedAt = savedAt;

                (scope == MapScope.Personal ? personal : shared).Add(entry);
            }

            shared.Sort(ByName);
            personal.Sort(ByName);

            _sharedMaps = shared;
            _personalMaps = personal;
            return true;
        }

        private static int ByName(MapEntry a, MapEntry b)
        {
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// What a map may be called. Lives in <see cref="MapNames"/> because the server compiles the same
        /// file and applies the same rule to what arrives from a client.
        /// </summary>
        public static bool IsValidName(string name)
        {
            return MapNames.IsValid(name);
        }

        /// <summary>
        /// The maps in one scope, out of the cache <see cref="RefreshServer"/> filled — synchronous,
        /// because the menus that call this are redrawn inside a frame.
        /// </summary>
        public static List<MapEntry> List(MapScope scope)
        {
            return new List<MapEntry>(scope == MapScope.Personal ? _personalMaps : _sharedMaps);
        }

        /// <summary>The map's content, or null when it is gone or the server would not part with it.</summary>
        public static async Task<string> ReadAsync(MapEntry entry)
        {
            if (entry == null) return null;

            var response = await Rpc.Call("load", ScopeName(entry.Scope), entry.Name);
            if (response.Ok) return response.Payload;

            Log.Info("Could not read the map '{0}': {1}", entry.Name, response.Message);
            return null;
        }

        /// <summary>
        /// Writes the map into whichever scope was asked for. Returns null when it went in, and the reason
        /// it did not otherwise — the server refuses for its own reasons (permission, size, object count,
        /// how often), and the player is owed the one it gave rather than a generic failure.
        ///
        /// Writing to <see cref="MapScope.Shared"/> is publishing, and publishing puts the map into every
        /// player's world at once, this one's included. There is no flag for that here because there is no
        /// choice about it; see MapEditorServer.
        /// </summary>
        public static async Task<string> WriteAsync(MapScope scope, string name, string content)
        {
            if (!IsValidName(name)) return "A map name cannot be empty or contain ':' or '#'.";
            if (!ServerAvailable) return NoServerMessage;

            if (ServerMaxMapSize > 0 && content.Length > ServerMaxMapSize)
                return string.Format(CultureInfo.InvariantCulture,
                    "That map is {0:N0} characters; this server accepts {1:N0}.", content.Length, ServerMaxMapSize);

            var response = await Rpc.Upload("save", content, ScopeName(scope), name.Trim());
            if (!response.Ok) return response.Message;

            // The catalogue has one more map, or one with a new size, and the picker redraws from the cache.
            // Everyone else finds out from the server's broadcast; this side does not wait for its own copy.
            await RefreshServer();
            return null;
        }

        /// <summary>
        /// Takes a published map out of the world — everyone's world, since that is the only place it is —
        /// and leaves it in storage. Returns null on success, or why not.
        ///
        /// This is the step that makes a published map editable again: while it is up, the editor will not
        /// open it. See <see cref="MapEntry.Live"/>.
        /// </summary>
        public static async Task<string> UnloadAsync(MapEntry entry)
        {
            if (entry == null) return null;
            if (entry.Scope != MapScope.Shared) return "Only maps published on the server can be unloaded.";
            if (!ServerAvailable) return NoServerMessage;

            var response = await Rpc.Call("unload", entry.Name);
            if (!response.Ok) return response.Message;

            await RefreshServer();
            return null;
        }

        /// <summary>
        /// Removes the map from whichever scope it is in. Returns null on success, or why not.
        /// </summary>
        public static async Task<string> DeleteAsync(MapEntry entry)
        {
            if (entry == null) return null;
            if (!ServerAvailable) return NoServerMessage;

            var response = await Rpc.Call("delete", ScopeName(entry.Scope), entry.Name);
            if (!response.Ok) return response.Message;

            await RefreshServer();
            return null;
        }

        /// <summary>What the editor tells the player when there is nothing on the other end.</summary>
        public static string NoServerMessage
        {
            get
            {
                return "This server is not running Map Editor's server component, so there is nowhere to " +
                       "keep maps. Ask the server owner to install the whole resource.";
            }
        }

        private static string ScopeName(MapScope scope)
        {
            return scope == MapScope.Personal ? "personal" : "shared";
        }

        // --- Maps left behind in KVP ------------------------------------------------------------------

        /// <summary>The names of maps an earlier build wrote into KVP, and which nothing else can reach now.</summary>
        private static List<string> LegacyMapNames()
        {
            var names = new List<string>();
            foreach (var key in Kvp.Keys(LegacyContentPrefix))
                names.Add(key.Substring(LegacyContentPrefix.Length));
            return names;
        }

        /// <summary>
        /// Moves the maps an earlier build kept in KVP into the player's folder on the server.
        ///
        /// Without this they would simply disappear: the picker no longer looks at KVP, and there is no
        /// other way to open one. Deliberately unhurried — one map, a pause, the next — because the server
        /// throttles writes and a migration is a burst of them that nobody is waiting for. A map that is
        /// refused is left in KVP and tried again next time; only a map that is known to have landed is
        /// dropped from it.
        ///
        /// Returns how many were moved.
        /// </summary>
        public static async Task<int> MigrateLegacyMaps()
        {
            if (!ServerAvailable) return 0;

            var names = LegacyMapNames();
            if (names.Count == 0) return 0;

            Log.Info("Moving {0} map(s) out of the old local storage into your folder on the server.", names.Count);

            var moved = 0;
            for (var i = 0; i < names.Count; i++)
            {
                var name = names[i];
                var content = Kvp.GetString(LegacyContentPrefix + name);
                if (string.IsNullOrEmpty(content))
                {
                    ForgetLegacyMap(name);
                    continue;
                }

                // The old metadata entry's autoload flag is not carried over: personal maps are drafts and
                // nothing spawns them by itself, so reviving it would put a map in front of a player with
                // no row anywhere to stop it.
                var error = await WriteAsync(MapScope.Personal, name, content);
                if (error != null)
                {
                    Log.Info("'{0}' could not be moved and was left where it is: {1}", name, error);
                }
                else
                {
                    ForgetLegacyMap(name);
                    moved++;
                }

                if (i + 1 < names.Count) await Frame.Wait(MigrationGapMs);
            }

            Log.Info("Moved {0} of {1} map(s).", moved, names.Count);
            return moved;
        }

        private static void ForgetLegacyMap(string name)
        {
            Kvp.Delete(LegacyContentPrefix + name);
            Kvp.Delete(LegacyMetaPrefix + name);
        }
    }
}
