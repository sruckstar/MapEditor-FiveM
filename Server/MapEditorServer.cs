using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using MapEditor.Platform;

namespace MapEditor.Server
{
    /// <summary>
    /// The server half of the editor.
    ///
    /// Every map lives here, in one of the two scopes <see cref="ServerMaps"/> keeps: the player's own
    /// folder, which only they see, and the shared one, which everyone sees and which needs an ACE to
    /// write to. The client keeps no maps of its own any more — a FiveM client cannot write a file, and
    /// the KVP entries that stood in for one were invisible to their owner, tied to this one server, and
    /// impossible to hand to anybody.
    ///
    /// <b>The client is not trusted.</b> Everything below re-checks what it was told: the permission
    /// (<see cref="Access"/>), the rate (<see cref="RateLimiter"/>), the size, the format and the object
    /// count (<see cref="ServerMaps.Validate"/>). The client's own copy of "may I publish" only decides
    /// whether the menu row is greyed out — it decides nothing here. Which maps are whose is decided the
    /// same way: the owner comes from <see cref="ServerMaps.OwnerKey"/> for the player the event actually
    /// came from, never from the event.
    ///
    /// <b>Publishing is applying.</b> A map saved to the shared storage is marked
    /// <see cref="ServerMapEntry.Live"/> and every connected client is told to go and spawn it, the author
    /// included; <c>unload</c> clears the flag and every client takes it back out again. There is no
    /// separate "autoload" step, because there was never a case for a published map that nobody could see:
    /// the two states of a shared map are up and down, and the catalogue records which.
    ///
    /// <b>The protocol.</b> Every request carries an id the client made up; every answer quotes it back.
    /// Anything of size travels as <see cref="ChunkSize"/> pieces on <c>mapeditor:cl:blob</c> — a map is
    /// hundreds of kilobytes and a single net event is not the place for it — and the terminating
    /// <c>mapeditor:cl:reply</c> says whether it was any good. <c>scope</c> is "personal" or "shared".
    ///
    /// <code>
    /// client -> mapeditor:sv:hello  (req)
    /// client -> mapeditor:sv:list   (req)
    /// client -> mapeditor:sv:load   (req, scope, name)
    /// client -> mapeditor:sv:save   (req, scope, name, index, total, chunk)   one event per chunk
    /// client -> mapeditor:sv:delete (req, scope, name)
    /// client -> mapeditor:sv:unload (req, name)
    /// client -> mapeditor:sv:configured (netId)   this entity is now what the map says it is
    ///
    /// server -> mapeditor:cl:blob   (req, index, total, chunk)    zero or more, then:
    /// server -> mapeditor:cl:reply  (req, ok, message)
    /// server -> mapeditor:cl:maps   ()   broadcast: the shared catalogue changed, come and look
    /// server -> mapeditor:cl:configure (netId, object)   to the owner of a server-created entity
    /// </code>
    ///
    /// <b>OneSync is required.</b> Not as a preference: a published map's cars, armed peds and walking peds
    /// are created by the server (see <see cref="LiveEntities"/>) because a local copy of one of those is a
    /// different thing for every player who has it. Without OneSync the server cannot own an entity at all,
    /// so it says so at startup, refuses to publish, and tells the client the editor is unavailable — which
    /// is the one refusal a player could not otherwise account for from their own side.
    ///
    /// The broadcast carries nothing but the fact that something changed: every client already knows how to
    /// fetch the catalogue and reconcile what it has standing against it, and pushing a megabyte of map to
    /// everybody down the same pipe the answer to their next request has to travel is a worse way to spend
    /// the same bytes. A client that joins afterwards does the same reconciliation once, at startup — which
    /// is after it has waited for the player to be in the world, so it is the <c>playerSpawned</c> moment
    /// the plan asks for.
    /// </summary>
    public class MapEditorServer : BaseScript
    {
        /// <summary>
        /// Must match Rpc.ChunkSize on the client. Well under any net event limit, and small enough that a
        /// map arriving in pieces does not sit in one frame's worth of work.
        /// </summary>
        public const int ChunkSize = 32 * 1024;

        /// <summary>How long a half-arrived upload is kept before its pieces are thrown away.</summary>
        private const int UploadTimeoutMs = 30000;

        /// <summary>
        /// Reads are cheap and the file picker makes a few of them at once; the burst is what a client
        /// legitimately does on startup, listing and then pulling every autoloaded map.
        /// </summary>
        private readonly RateLimiter _reads = new RateLimiter(24, 2);

        /// <summary>
        /// Writes touch the disk. One every five seconds sustained, four back to back — enough for someone
        /// building, nowhere near enough to be worth abusing. Counted per request, not per chunk.
        /// </summary>
        private readonly RateLimiter _writes = new RateLimiter(4, 0.2);

        /// <summary>A map on its way in, in pieces. Keyed by player and request.</summary>
        private sealed class Upload
        {
            public MapScope Scope;
            public string Name;
            public int Total;
            public int Next;
            public int Ticks;
            public readonly StringBuilder Content = new StringBuilder();
        }

        private readonly Dictionary<string, Upload> _uploads = new Dictionary<string, Upload>();

        /// <summary>
        /// How many startup lines one client may print here before it is ignored. Startup sends about a
        /// dozen; the rest of the allowance is for the ones that carry an error message.
        /// </summary>
        private const int MaxTraceLines = 40;

        /// <summary>Lines printed per player, so a client cannot fill the owner's console. See <see cref="OnTrace"/>.</summary>
        private readonly Dictionary<string, int> _traces = new Dictionary<string, int>();

        public MapEditorServer()
        {
            EventHandlers["mapeditor:sv:hello"] += new Action<Player, int>(OnHello);
            EventHandlers["mapeditor:sv:list"] += new Action<Player, int>(OnList);
            EventHandlers["mapeditor:sv:load"] += new Action<Player, int, string, string>(OnLoad);
            EventHandlers["mapeditor:sv:save"] += new Action<Player, int, string, string, int, int, string>(OnSave);
            EventHandlers["mapeditor:sv:delete"] += new Action<Player, int, string, string>(OnDelete);
            EventHandlers["mapeditor:sv:unload"] += new Action<Player, int, string>(OnUnload);
            EventHandlers["mapeditor:sv:trace"] += new Action<Player, string>(OnTrace);
            EventHandlers["mapeditor:sv:configured"] += new Action<Player, int>(OnConfigured);
            EventHandlers["playerDropped"] += new Action<Player, string>(OnPlayerDropped);
            EventHandlers["onResourceStop"] += new Action<string>(OnResourceStop);

            // Before anyone tries to save, not during their save: a folder the server cannot write to is
            // the owner's problem to fix, and they read the console at startup, not the player's screen.
            ServerMaps.EnsureStorage();

            LiveEntities.Bind(() => Players, (player, name, args) => TriggerClientEvent(player, name, args));

            Log.Info("Server component ready. {0} shared map(s) in storage, {1} of them standing; the editor is {2}.",
                ServerMaps.Count, ServerMaps.LiveCount,
                Access.RestrictUse ? "restricted to " + Access.UsePermission : "open to everyone");

            if (LiveEntities.OneSyncEnabled)
            {
                LiveEntities.Reconcile();
                Log.Info("OneSync is {0}. {1} object(s) of the standing maps belong to the server; up to {2} of " +
                         "them exist at a time, the ones players are near.",
                    LiveEntities.OneSyncMode, LiveEntities.Total,
                    API.GetConvarInt("mapeditor_max_shared_entities", 200));

                Tick += OnServerTick;
            }
            else
            {
                // Said this loudly and once, because everything downstream of it is a refusal the player
                // sees with no way to work out why from their own side.
                Log.Info("ONESYNC IS OFF, SO THE MAP EDITOR IS DISABLED. Start the server with " +
                         "'+set onesync on'. Without it the server cannot own entities, and a published map's " +
                         "vehicles, armed peds and walking peds would be a private copy per player rather than " +
                         "one thing everybody shares. See DEPLOY.md.");
            }
        }

        /// <summary>
        /// The proximity pass that decides which of the published maps' shared objects exist right now. See
        /// <see cref="LiveEntities.Update"/>; only hooked up when there is OneSync to do it with.
        ///
        /// A second apart because that is the resolution the question has: this is players walking or
        /// driving towards things tens of metres away, not anything that happens within a frame.
        /// </summary>
        private async Task OnServerTick()
        {
            await Delay(SharedPassMs);

            try
            {
                LiveEntities.Update();
            }
            catch (Exception e)
            {
                Log.Error("LiveEntities.Update", e);
            }
        }

        /// <summary>How often the shared objects are compared against where the players are.</summary>
        private const int SharedPassMs = 1000;

        // --- Handlers --------------------------------------------------------------------------------

        /// <summary>
        /// What this player is allowed to do, and what the server will accept. Asked once at startup: the
        /// client greys out the rows it cannot use rather than offering them and failing afterwards.
        /// </summary>
        private void OnHello([FromSource] Player player, int request)
        {
            if (!_reads.Allow(player.Handle))
            {
                // Answered rather than dropped, so the client fails at once instead of sitting out the
                // whole timeout. It only reaches here by asking far more often than a startup does.
                Reply(player, request, false, "Too many requests. Try again in a moment.");
                return;
            }

            // OneSync is not a permission but lands in the same answer, because the client needs it for the
            // same decision: without it the server cannot own an entity, so a published map's cars and armed
            // peds would be a private copy per player. This line is what lets the editor say why it refuses.
            var oneSync = LiveEntities.OneSyncEnabled;

            var answer = Json.Object()
                .Set("oneSync", oneSync)
                .Set("canUse", oneSync && Access.CanUse(player))
                .Set("canSave", Access.CanSave(player))
                .Set("canPublish", oneSync && Access.CanPublish(player))
                .Set("canUnload", Access.CanUnload(player))
                .Set("maxMapSize", MaxMapSize)
                .Set("maxObjects", MaxObjects);

            SendBlob(player, request, answer.ToJson());
            Reply(player, request, true, "");
        }

        /// <summary>
        /// The catalogue: every shared map, plus this player's own, and nothing of anybody else's. Open to
        /// everyone with or without <see cref="Access.UsePermission"/>: a player who may not open the
        /// editor still has to be told which maps to spawn as scenery.
        /// </summary>
        private void OnList([FromSource] Player player, int request)
        {
            if (!_reads.Allow(player.Handle))
            {
                Reply(player, request, false, "Too many requests. Try again in a moment.");
                return;
            }

            SendBlob(player, request, ServerMaps.ListJson(ServerMaps.OwnerKey(player)));
            Reply(player, request, true, "");
        }

        private void OnLoad([FromSource] Player player, int request, string scopeName, string name)
        {
            if (!_reads.Allow(player.Handle))
            {
                Reply(player, request, false, "Too many requests. Try again in a moment.");
                return;
            }

            MapScope scope;
            if (!TryReadScope(scopeName, out scope))
            {
                Reply(player, request, false, "The client asked for a storage this server does not have.");
                return;
            }

            string content;
            try
            {
                // The owner is this player, always: a personal map is found by the key of whoever the event
                // came from, so asking for someone else's by name simply finds nothing.
                content = ServerMaps.Read(scope, ServerMaps.OwnerKey(player), name);
            }
            catch (Exception e)
            {
                Log.Error("OnLoad", e);
                Reply(player, request, false, "The server could not read that map.");
                return;
            }

            if (content == null)
            {
                Reply(player, request, false, "There is no map called '" + Describe(name) + "' on this server.");
                return;
            }

            SendBlob(player, request, content);
            Reply(player, request, true, "");
        }

        /// <summary>
        /// One chunk of an upload. The map is only looked at once the last piece is in — a document in
        /// halves cannot be parsed, and refusing it early would mean judging it on its first 32 KB.
        ///
        /// The two scopes are refused for different reasons. A shared map is everyone's — it goes up in
        /// every player's world the moment it lands — so it needs <see cref="Access.PublishPermission"/> and
        /// counts against the server's own total. A personal one is a draft nobody else ever sees, so it
        /// needs only <see cref="Access.SavePermission"/>, which is open unless the owner has said otherwise.
        /// </summary>
        private void OnSave([FromSource] Player player, int request, string scopeName, string name, int index, int total, string chunk)
        {
            var key = player.Handle + ":" + request.ToString(CultureInfo.InvariantCulture);

            // Only the opening chunk spends an allowance, so that a large map is not throttled into
            // failing halfway through by its own size.
            if (index == 0)
            {
                DropStaleUploads();

                MapScope scope;
                if (!TryReadScope(scopeName, out scope))
                {
                    Reply(player, request, false, "The client asked for a storage this server does not have.");
                    return;
                }

                // Checked before the permission, because it is not about this player: nobody may publish on
                // a server that cannot own the entities a published map needs.
                if (scope == MapScope.Shared && !LiveEntities.OneSyncEnabled)
                {
                    Reply(player, request, false, "This server runs without OneSync, so it cannot put a map " +
                                                  "into everyone's world. The server console says what to change.");
                    return;
                }

                if (scope == MapScope.Shared && !Access.CanPublish(player))
                {
                    Reply(player, request, false, "You do not have permission to save maps on this server.");
                    return;
                }

                if (scope == MapScope.Personal && !Access.CanSave(player))
                {
                    Reply(player, request, false, "You do not have permission to keep maps on this server.");
                    return;
                }

                if (scope == MapScope.Personal && ServerMaps.OwnerKey(player) == null)
                {
                    // No licence, no name, nothing to file the folder under. Only reachable on a server
                    // handing out no identifiers at all; better said plainly than stored somewhere it can
                    // never be found again.
                    Reply(player, request, false, "This server cannot tell who you are, so it has nowhere private to put your map.");
                    return;
                }

                if (!_writes.Allow(player.Handle))
                {
                    Reply(player, request, false, "Too many saves. Try again in a moment.");
                    return;
                }

                if (!MapNames.IsValid(name))
                {
                    Reply(player, request, false, "That map name cannot be used.");
                    return;
                }

                if (total < 1 || (long)total * ChunkSize > MaxMapSize + ChunkSize)
                {
                    Reply(player, request, false, "That map is larger than this server accepts.");
                    return;
                }

                // Trimmed before the lookup, not only before storing: without this a stray space would make
                // an overwrite look like a new map and count against the cap.
                var trimmed = name.Trim();

                var owner = ServerMaps.OwnerKey(player);
                if (ServerMaps.Find(scope, owner, trimmed) == null)
                {
                    if (scope == MapScope.Shared && ServerMaps.Count >= MaxMaps)
                    {
                        Reply(player, request, false, "This server is already holding its maximum of " +
                                                      MaxMaps.ToString(CultureInfo.InvariantCulture) + " shared maps.");
                        return;
                    }

                    if (scope == MapScope.Personal && ServerMaps.CountFor(owner) >= MaxPersonalMaps)
                    {
                        Reply(player, request, false, "You already have this server's maximum of " +
                                                      MaxPersonalMaps.ToString(CultureInfo.InvariantCulture) +
                                                      " saved maps. Delete one to save another.");
                        return;
                    }
                }

                _uploads[key] = new Upload
                {
                    Scope = scope,
                    Name = trimmed,
                    Total = total,
                    Ticks = Environment.TickCount,
                };
            }

            Upload upload;
            if (!_uploads.TryGetValue(key, out upload))
            {
                // Either the opening chunk was refused above — in which case the client has already been
                // told why — or this is a stray chunk of an upload that timed out.
                return;
            }

            // Chunks arrive in the order they were sent; anything else is a client that is not ours.
            if (index != upload.Next || total != upload.Total)
            {
                _uploads.Remove(key);
                Reply(player, request, false, "The upload arrived out of order.");
                return;
            }

            upload.Next++;
            upload.Ticks = Environment.TickCount;
            if (chunk != null) upload.Content.Append(chunk);

            if (upload.Content.Length > MaxMapSize)
            {
                _uploads.Remove(key);
                Reply(player, request, false, "That map is larger than this server accepts.");
                return;
            }

            if (upload.Next < upload.Total) return;

            _uploads.Remove(key);
            var content = upload.Content.ToString();

            string error;
            if (!ServerMaps.Validate(content, MaxObjects, out error))
            {
                Reply(player, request, false, error);
                return;
            }

            try
            {
                var entry = ServerMaps.Write(upload.Scope, ServerMaps.OwnerKey(player), player.Name,
                    upload.Name, content, player.Name);

                Log.Info("{0} saved '{1}' to {2} storage ({3} characters{4}).", player.Name, entry.Name,
                    upload.Scope == MapScope.Shared ? "shared" : "their own", entry.Size,
                    entry.Live ? "; it is now standing for everyone" : "");

                // Answered before the broadcast, so the author's own client finishes its save — and clears
                // the map out of its editor — before it is told to go and spawn the published copy.
                Reply(player, request, true, "");

                if (upload.Scope == MapScope.Shared) AnnounceSharedMaps();
            }
            catch (Exception e)
            {
                Log.Error("OnSave", e);
                Reply(player, request, false, e.Message);
            }
        }

        private void OnDelete([FromSource] Player player, int request, string scopeName, string name)
        {
            MapScope scope;
            if (!TryReadScope(scopeName, out scope))
            {
                Reply(player, request, false, "The client asked for a storage this server does not have.");
                return;
            }

            if (scope == MapScope.Shared && !Access.CanPublish(player))
            {
                Reply(player, request, false, "You do not have permission to delete maps on this server.");
                return;
            }

            if (!_writes.Allow(player.Handle))
            {
                Reply(player, request, false, "Too many requests. Try again in a moment.");
                return;
            }

            // A map standing in everyone's world is not deleted out from under them: the file would go while
            // the props stayed, with nothing left to take them away. Unloading first is a separate
            // permission on purpose.
            if (scope == MapScope.Shared)
            {
                var standing = ServerMaps.Find(MapScope.Shared, null, name);
                if (standing != null && standing.Live)
                {
                    Reply(player, request, false,
                        "'" + Describe(name) + "' is loaded on the server. Unload it before deleting it.");
                    return;
                }
            }

            try
            {
                // Someone else's personal map is not found rather than refused: it was never listed to this
                // player, so there is nothing for the answer to confirm the existence of.
                if (!ServerMaps.Delete(scope, ServerMaps.OwnerKey(player), name))
                {
                    Reply(player, request, false, "There is no map called '" + Describe(name) + "' on this server.");
                    return;
                }
            }
            catch (Exception e)
            {
                Log.Error("OnDelete", e);
                Reply(player, request, false, "The server could not delete that map.");
                return;
            }

            Log.Info("{0} deleted '{1}'.", player.Name, Describe(name));
            Reply(player, request, true, "");

            // Only the shared catalogue is everyone's business; a personal map leaving nobody else's list
            // is nobody else's news.
            if (scope == MapScope.Shared) AnnounceSharedMaps();
        }

        /// <summary>
        /// Takes a published map back out of the world — out of everyone's world, which is the only way it
        /// is in one — and leaves the file in storage.
        ///
        /// This is the step that makes a published map editable again. While it is up, the client refuses to
        /// open it in the editor: two people editing what everyone else is standing in the middle of is a
        /// question this port does not answer (§10 of the plan puts collaborative editing out of scope), and
        /// the author's own copy would be the one that quietly disagreed with the server's.
        ///
        /// <see cref="Access.UnloadPermission"/> rather than publish: this row acts on whichever map is up,
        /// including one somebody else put there.
        /// </summary>
        private void OnUnload([FromSource] Player player, int request, string name)
        {
            if (!Access.CanUnload(player))
            {
                Reply(player, request, false, "You do not have permission to unload the server's maps.");
                return;
            }

            if (!_writes.Allow(player.Handle))
            {
                Reply(player, request, false, "Too many requests. Try again in a moment.");
                return;
            }

            ServerMapEntry entry;
            try
            {
                entry = ServerMaps.SetLive(name, false);
            }
            catch (Exception e)
            {
                Log.Error("OnUnload", e);
                Reply(player, request, false, "The server could not unload that map.");
                return;
            }

            if (entry == null)
            {
                Reply(player, request, false, "There is no map called '" + Describe(name) + "' on this server.");
                return;
            }

            Log.Info("{0} unloaded '{1}'; it is no longer standing for anyone.", player.Name, entry.Name);
            Reply(player, request, true, "");
            AnnounceSharedMaps();
        }

        /// <summary>
        /// Tells every client that the shared catalogue has moved on, so that each of them fetches it and
        /// reconciles what it has standing against what should be.
        ///
        /// Nothing is carried: see the class remarks. It also means this one line covers every kind of
        /// change — a map published, republished over the top, unloaded, deleted — without any of them
        /// needing a message of its own, and a client that missed one is put right by the next.
        /// </summary>
        private void AnnounceSharedMaps()
        {
            // The server's half of the same reconciliation, first: the clients are about to spawn everything
            // that is theirs, and what is not must already be on its way in — or gone — when they look.
            try
            {
                LiveEntities.Reconcile();
            }
            catch (Exception e)
            {
                Log.Error("LiveEntities.Reconcile", e);
            }

            TriggerClientEvent("mapeditor:cl:maps");
        }

        /// <summary>
        /// A client reporting that it has finished configuring one of the server's entities — the tasks,
        /// relationship groups, liveries and the rest that have no server-side natives. See
        /// <see cref="LiveEntities"/>.
        /// </summary>
        private void OnConfigured([FromSource] Player player, int netId)
        {
            LiveEntities.Configured(netId);
        }

        /// <summary>
        /// Server entities outlive the script that made them, so a resource restart would otherwise leave a
        /// world full of props with no record anywhere of what they belong to. The map files are untouched:
        /// the instance that replaces this one reads the catalogue and puts the same objects back.
        /// </summary>
        private void OnResourceStop(string resourceName)
        {
            if (resourceName != API.GetCurrentResourceName()) return;

            try
            {
                LiveEntities.UnloadAll();
            }
            catch (Exception e)
            {
                Log.Error("LiveEntities.UnloadAll", e);
            }
        }

        /// <summary>
        /// Which storage the client meant. Unknown text is not quietly treated as one of them: a client
        /// that has been changed, or one from a newer build, must be told no rather than have its map filed
        /// somewhere it did not ask for.
        /// </summary>
        private static bool TryReadScope(string text, out MapScope scope)
        {
            scope = MapScope.Personal;
            if (string.IsNullOrEmpty(text)) return false;

            if (string.Equals(text, "personal", StringComparison.OrdinalIgnoreCase)) return true;

            if (string.Equals(text, "shared", StringComparison.OrdinalIgnoreCase))
            {
                scope = MapScope.Shared;
                return true;
            }

            return false;
        }

        /// <summary>
        /// A line from a client's startup, printed here because a client that hangs cannot print anything
        /// itself: F8 needs frames, and the Enhanced client keeps no log file. See Client/Platform/Boot.cs.
        ///
        /// Trusted with nothing — it is a string, it goes to the console, and it is counted. The count is
        /// what keeps a client that has been made to shout out of the owner's console: startup is a dozen
        /// lines, and everything past <see cref="MaxTraceLines"/> is dropped until the player reconnects.
        /// </summary>
        private void OnTrace([FromSource] Player player, string step)
        {
            int seen;
            _traces.TryGetValue(player.Handle, out seen);
            if (seen > MaxTraceLines) return;

            _traces[player.Handle] = seen + 1;

            if (seen == MaxTraceLines)
            {
                Log.Info("boot [{0}]: further lines dropped ({1} already).", Describe(player.Name), MaxTraceLines);
                return;
            }

            Log.Info("boot [{0}]: {1}", Describe(player.Name), Describe(step, 400));
        }

        private void OnPlayerDropped([FromSource] Player player, string reason)
        {
            _reads.Forget(player.Handle);
            _writes.Forget(player.Handle);
            _traces.Remove(player.Handle);

            var prefix = player.Handle + ":";
            var stale = new List<string>();
            foreach (var pair in _uploads)
            {
                if (pair.Key.StartsWith(prefix, StringComparison.Ordinal)) stale.Add(pair.Key);
            }
            foreach (var key in stale) _uploads.Remove(key);
        }

        // --- Plumbing --------------------------------------------------------------------------------

        private void Reply(Player player, int request, bool ok, string message)
        {
            TriggerClientEvent(player, "mapeditor:cl:reply", request, ok, message ?? "");
        }

        private void SendBlob(Player player, int request, string content)
        {
            if (string.IsNullOrEmpty(content)) return;

            var total = (content.Length + ChunkSize - 1) / ChunkSize;
            for (var i = 0; i < total; i++)
            {
                var at = i * ChunkSize;
                var length = Math.Min(ChunkSize, content.Length - at);
                TriggerClientEvent(player, "mapeditor:cl:blob", request, i, total, content.Substring(at, length));
            }
        }

        /// <summary>
        /// A client that starts a save and disconnects, or simply stops sending, leaves its pieces behind.
        /// Swept on the next save rather than on a timer: nothing else is holding this table open.
        /// </summary>
        private void DropStaleUploads()
        {
            if (_uploads.Count == 0) return;

            var now = Environment.TickCount;
            var stale = new List<string>();
            foreach (var pair in _uploads)
            {
                if (unchecked(now - pair.Value.Ticks) > UploadTimeoutMs) stale.Add(pair.Key);
            }
            foreach (var key in stale) _uploads.Remove(key);
        }

        /// <summary>A client-supplied name on its way into a message or a log line.</summary>
        private static string Describe(string name)
        {
            return Describe(name, 64);
        }

        /// <summary>
        /// <see cref="Describe(string)"/> with room for something longer than a name — a startup line
        /// carrying an exception message, which is worth more whole than short.
        /// </summary>
        private static string Describe(string name, int max)
        {
            if (string.IsNullOrEmpty(name)) return "";
            name = name.Replace("\n", " ").Replace("\r", " ");
            return name.Length > max ? name.Substring(0, max) + "..." : name;
        }

        // --- Limits ----------------------------------------------------------------------------------

        /// <summary>Characters, matching how the rest of the editor measures a map.</summary>
        private static int MaxMapSize
        {
            get { return API.GetConvarInt("mapeditor_max_map_size", 4 * 1024 * 1024); }
        }

        private static int MaxObjects
        {
            get { return API.GetConvarInt("mapeditor_max_objects", 4000); }
        }

        /// <summary>Shared maps only. A player's own folder has its own cap.</summary>
        private static int MaxMaps
        {
            get { return API.GetConvarInt("mapeditor_max_maps", 200); }
        }

        /// <summary>
        /// How many maps one player may keep in their own folder. This is where the autosave lands and
        /// where every ordinary save goes, so it is the cap a builder actually meets — generous, and there
        /// to stop one player filling the owner's disk rather than to ration anybody.
        /// </summary>
        private static int MaxPersonalMaps
        {
            get { return API.GetConvarInt("mapeditor_max_personal_maps", 60); }
        }
    }
}
