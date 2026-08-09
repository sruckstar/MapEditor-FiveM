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
    /// <b>Co-editing has a protocol of its own</b>, on top of the same request/answer plumbing. A session is
    /// a draft map several players are building at once; it lives in <see cref="Sessions"/>, never touches
    /// the disk, and ends when the last person leaves. Everything below the first four is fire-and-forget:
    /// an edit that went missing is put right by the next pass of the sender's own change detector, and an
    /// answer would only add a round trip to something that happens sixty times a second.
    ///
    /// <code>
    /// client -> mapeditor:sv:cohost (req, name)               open a session on the map I am holding
    /// client -> mapeditor:sv:colist (req)                     what sessions are open
    /// client -> mapeditor:sv:cojoin (req, id)                 join, or resynchronise; answer is the document
    /// client -> mapeditor:sv:copush (req, index, total, chunk) host: this is the map now
    /// client -> mapeditor:sv:coleave (req)
    /// client -> mapeditor:sv:cohand (req, slot)               host: you run it now
    /// client -> mapeditor:sv:cofree (req, slot)               host: let go of what they are holding
    /// client -> mapeditor:sv:cograb (req, uids)               reserve these while I drag them
    /// client -> mapeditor:sv:coops  (ops)                     a batch of committed changes
    /// client -> mapeditor:sv:codrop (uids)                    let go
    /// client -> mapeditor:sv:codrag (drag)                    where the things I am holding are, right now
    /// client -> mapeditor:sv:cohere (where)                   where I am, three times a second
    ///
    /// server -> mapeditor:cl:coroom (room)                    who is in the session, and who runs it
    /// server -> mapeditor:cl:coops  (ops)                     somebody else's committed changes
    /// server -> mapeditor:cl:coheld (slot, uids, held)        somebody took or gave up a reservation
    /// server -> mapeditor:cl:codrag (slot, drag)              somebody else's drag, in flight
    /// server -> mapeditor:cl:cohere (slot, where)             somebody else's camera
    /// server -> mapeditor:cl:coreset ()                       the whole map changed; fetch it again
    /// server -> mapeditor:cl:cobye  (why)                     you are out of the session
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
        /// Batches of committed edits, and the reservations that go with them. Generous, because this is the
        /// shape ordinary work makes: dropping a hundred props out of the stacking tool is one batch, and a
        /// player dragging something sends one every few frames. Nothing here touches the disk.
        /// </summary>
        private readonly RateLimiter _edits = new RateLimiter(60, 30);

        /// <summary>
        /// Where everybody is and what they are dragging. Refused silently rather than answered: these are
        /// the two messages it costs nothing to miss, since the next one is a fraction of a second behind.
        /// </summary>
        private readonly RateLimiter _presence = new RateLimiter(30, 15);

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

            // Co-editing. See the protocol table in the class remarks.
            EventHandlers["mapeditor:sv:cohost"] += new Action<Player, int, string>(OnCoHost);
            EventHandlers["mapeditor:sv:colist"] += new Action<Player, int>(OnCoList);
            EventHandlers["mapeditor:sv:cojoin"] += new Action<Player, int, int>(OnCoJoin);
            EventHandlers["mapeditor:sv:copush"] += new Action<Player, int, int, int, string>(OnCoPush);
            EventHandlers["mapeditor:sv:coleave"] += new Action<Player, int>(OnCoLeave);
            EventHandlers["mapeditor:sv:coend"] += new Action<Player, int, string>(OnCoEnd);
            EventHandlers["mapeditor:sv:cohand"] += new Action<Player, int, int>(OnCoHand);
            EventHandlers["mapeditor:sv:cofree"] += new Action<Player, int, int>(OnCoFree);
            EventHandlers["mapeditor:sv:cograb"] += new Action<Player, int, string>(OnCoGrab);
            EventHandlers["mapeditor:sv:coops"] += new Action<Player, string>(OnCoOps);
            EventHandlers["mapeditor:sv:codrop"] += new Action<Player, string>(OnCoDrop);
            EventHandlers["mapeditor:sv:codrag"] += new Action<Player, string>(OnCoDrag);
            EventHandlers["mapeditor:sv:cohere"] += new Action<Player, string>(OnCoHere);

            EventHandlers["playerDropped"] += new Action<Player, string>(OnPlayerDropped);
            EventHandlers["onResourceStop"] += new Action<string>(OnResourceStop);

            // Before anyone tries to save, not during their save: a folder the server cannot write to is
            // the owner's problem to fix, and they read the console at startup, not the player's screen.
            ServerMaps.EnsureStorage();

            LiveEntities.Bind(() => Players, (player, name, args) => TriggerClientEvent(player, name, args));
            Sessions.Bind((player, name, args) => TriggerClientEvent(player, name, args));

            Log.Info("Server component ready. {0} shared map(s) in storage, {1} of them standing; the editor is {2}. " +
                     "Co-editing: {3}, up to {4} session(s) of {5} player(s).",
                ServerMaps.Count, ServerMaps.LiveCount,
                Access.RestrictUse ? "restricted to " + Access.UsePermission : "open to everyone",
                Access.RestrictCollab ? "restricted to " + Access.CollabPermission : "open to everyone",
                Sessions.MaxSessions, Sessions.MaxMembers);

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
                .Set("canCollab", Access.CanCollaborate(player))
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

        // --- Co-editing ------------------------------------------------------------------------------
        //
        // A session is a draft map with several people in it. Nothing here writes a file, spawns anything or
        // changes what any other player on the server sees: the objects a session holds exist only in the
        // editors of the people in it, which is why joining one needs no more permission than opening the
        // editor does. Saving and publishing a session's map are the ordinary save and publish, done by one
        // of its members, and go through the handlers above with the permissions they have always had.

        /// <summary>
        /// Opens a session with this player as its host. It starts empty and the host pushes the map it is
        /// to be about immediately afterwards (<see cref="OnCoPush"/>) — a map is megabytes and does not fit
        /// in the answer to a request.
        /// </summary>
        private void OnCoHost([FromSource] Player player, int request, string name)
        {
            if (!_writes.Allow(player.Handle))
            {
                Reply(player, request, false, "Too many requests. Try again in a moment.");
                return;
            }

            if (!Access.CanCollaborate(player))
            {
                Reply(player, request, false, "You do not have permission to edit maps with other players here.");
                return;
            }

            // Already in one: taken as leaving it. Not refused, because the case this actually happens in is
            // a client that restarted — its editor knows nothing of the session it was in, while this side
            // still has it in the room and still has its objects reserved to it. A player can only be in one
            // session, so the request says which one plainly enough.
            LeaveSession(player, null);

            if (Sessions.Count >= Sessions.MaxSessions)
            {
                Reply(player, request, false, "This server is already running its maximum of " +
                                              Sessions.MaxSessions.ToString(CultureInfo.InvariantCulture) +
                                              " sessions.");
                return;
            }

            var trimmed = string.IsNullOrWhiteSpace(name) ? player.Name + "'s map" : name.Trim();
            if (trimmed.Length > MapNames.MaxLength) trimmed = trimmed.Substring(0, MapNames.MaxLength);

            var session = Sessions.Open(player, trimmed);
            var member = Sessions.MemberOf(session, player);

            Log.Info("{0} opened session {1} ('{2}').", player.Name, session.Id, Describe(session.Name));

            SendBlob(player, request, Sessions.DocumentJson(session, member));
            Reply(player, request, true, "");
        }

        /// <summary>What is open. Names and headcounts only — no map content travels here.</summary>
        private void OnCoList([FromSource] Player player, int request)
        {
            if (!_reads.Allow(player.Handle))
            {
                Reply(player, request, false, "Too many requests. Try again in a moment.");
                return;
            }

            SendBlob(player, request, Sessions.ListJson());
            Reply(player, request, true, "");
        }

        /// <summary>
        /// Joins a session, or hands an existing member the document again.
        ///
        /// The second is not a special case, it is the resynchronise button: a client that thinks it has
        /// drifted asks for the whole thing and rebuilds from it, and so does one that was told the map
        /// underneath it has been replaced. Both are the same request, which is why there is only one.
        /// </summary>
        private void OnCoJoin([FromSource] Player player, int request, int id)
        {
            if (!_reads.Allow(player.Handle))
            {
                Reply(player, request, false, "Too many requests. Try again in a moment.");
                return;
            }

            if (!Access.CanCollaborate(player))
            {
                Reply(player, request, false, "You do not have permission to edit maps with other players here.");
                return;
            }

            var session = Sessions.Get(id);
            if (session == null)
            {
                Reply(player, request, false, "That session has ended.");
                return;
            }

            var existing = Sessions.MemberOf(session, player);
            if (existing == null)
            {
                // In another one: taken as leaving it, for the reason given in OnCoHost.
                LeaveSession(player, null);

                if (session.Members.Count >= Sessions.MaxMembers)
                {
                    Reply(player, request, false, "That session already has this server's maximum of " +
                                                  Sessions.MaxMembers.ToString(CultureInfo.InvariantCulture) +
                                                  " players in it.");
                    return;
                }

                if (Sessions.SlotsExhausted(session))
                {
                    // Only reachable after two thousand comings and goings in one session. Said plainly
                    // rather than by handing out a slot that would make two players' object ids collide.
                    Reply(player, request, false, "That session has been running too long to take anyone new. " +
                                                  "Somebody in it should save the map and start another.");
                    return;
                }
            }

            var member = Sessions.Join(session, player);

            SendBlob(player, request, Sessions.DocumentJson(session, member));
            Reply(player, request, true, "");

            if (existing != null) return;

            Log.Info("{0} joined session {1} ('{2}'); {3} in it now.", player.Name, session.Id,
                Describe(session.Name), session.Members.Count);

            Sessions.AnnounceRoom(session);
        }

        /// <summary>
        /// The host saying what the session's map now is, in <see cref="ChunkSize"/> pieces on one request.
        ///
        /// Sent once when the session opens, and again whenever the host replaces the map outright — loading
        /// another one, or starting a new one. Everyone else is told to fetch it again rather than being sent
        /// a change per object: two thousand additions in one batch is the one case the delta channel is the
        /// wrong shape for, and re-fetching is a path that already exists and is already exercised.
        /// </summary>
        private void OnCoPush([FromSource] Player player, int request, int index, int total, string chunk)
        {
            var key = player.Handle + ":push:" + request.ToString(CultureInfo.InvariantCulture);

            var session = Sessions.Find(player);
            var member = Sessions.MemberOf(session, player);

            if (index == 0)
            {
                DropStaleUploads();

                if (session == null || member == null)
                {
                    Reply(player, request, false, "You are not in a session.");
                    return;
                }

                if (member.Slot != session.HostSlot)
                {
                    Reply(player, request, false, "Only the session's host can replace its map.");
                    return;
                }

                if (!_writes.Allow(player.Handle))
                {
                    Reply(player, request, false, "Too many requests. Try again in a moment.");
                    return;
                }

                if (total < 1 || (long)total * ChunkSize > MaxMapSize + ChunkSize)
                {
                    Reply(player, request, false, "That map is larger than this server accepts.");
                    return;
                }

                _uploads[key] = new Upload { Total = total, Ticks = Environment.TickCount };
            }

            Upload upload;
            if (!_uploads.TryGetValue(key, out upload)) return;

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

            // Re-checked at the end as well as at the start: an upload spans frames and the host can have
            // left, or handed the session over, in between.
            if (session == null || member == null || member.Slot != session.HostSlot)
            {
                Reply(player, request, false, "You are no longer the session's host.");
                return;
            }

            string error;
            if (!Sessions.Push(session, upload.Content.ToString(), Sessions.MaxObjects, out error))
            {
                Reply(player, request, false, error);
                return;
            }

            Reply(player, request, true, "");

            // Not to the host: their editor is already holding exactly what they just sent.
            Sessions.Broadcast(session, member.Slot, "mapeditor:cl:coreset");
        }

        private void OnCoLeave([FromSource] Player player, int request)
        {
            var left = LeaveSession(player, null);
            Reply(player, request, left, left ? "" : "You are not in a session.");
        }

        /// <summary>
        /// Takes a player out of their session and tells everyone left what changed. <paramref name="why"/>
        /// is what the leaver is told, or null when they asked to leave and know perfectly well why.
        /// </summary>
        private bool LeaveSession(Player player, string why)
        {
            bool closed, hostChanged;
            var session = Sessions.Leave(player, out closed, out hostChanged);
            if (session == null) return false;

            if (why != null) TriggerClientEvent(player, "mapeditor:cl:cobye", why);

            if (closed)
            {
                Log.Info("Session {0} ('{1}') ended; the last player left it.", session.Id, Describe(session.Name));
                return true;
            }

            // Whatever they were holding is everyone's again, and the room has one fewer name — and possibly
            // a different host. One announcement covers all three: the room message carries the whole list.
            Sessions.AnnounceRoom(session);

            if (hostChanged)
            {
                var host = session.Host;
                Log.Info("Session {0} ('{1}') is now {2}'s.", session.Id, Describe(session.Name),
                    host == null ? "nobody" : host.Name);
            }

            return true;
        }

        /// <summary>
        /// The host closing the session for everybody.
        ///
        /// It exists because publishing needs it. A published map goes into every player's world and out of
        /// its author's editor, and a session whose map has just been published is several people editing a
        /// second copy of something that is already standing — so the host publishing ends the session, and
        /// says so before it does. The row that ends one for its own sake is the same request.
        ///
        /// Nobody loses anything: every member's editor is still holding the map, and any of them can save
        /// their copy. Only the shared thread between them is cut.
        /// </summary>
        private void OnCoEnd([FromSource] Player player, int request, string why)
        {
            Session session;
            SessionMember member;
            if (!RequireHost(player, request, out session, out member)) return;

            var reason = string.IsNullOrEmpty(why)
                ? (player.Name ?? "The host") + " ended the session. The map is still in your editor."
                : Describe(why, 200);

            Reply(player, request, true, "");

            // Everybody, the host included: their client tears down the same state either way, and the host
            // asked for it so the message is not news to them.
            Sessions.Broadcast(session, -1, "mapeditor:cl:cobye", reason);
            Sessions.Close(session);

            Log.Info("{0} ended session {1} ('{2}').", player.Name, session.Id, Describe(session.Name));
        }

        /// <summary>The host handing the session to somebody else. It is the only power in a session.</summary>
        private void OnCoHand([FromSource] Player player, int request, int slot)
        {
            Session session;
            SessionMember member;
            if (!RequireHost(player, request, out session, out member)) return;

            if (!Sessions.HandOver(session, slot))
            {
                Reply(player, request, false, "That player is not in this session any more.");
                return;
            }

            Reply(player, request, true, "");
            Sessions.AnnounceRoom(session);

            var host = session.Host;
            Log.Info("Session {0} ('{1}') handed to {2}.", session.Id, Describe(session.Name),
                host == null ? "nobody" : host.Name);
        }

        /// <summary>
        /// The host prising loose whatever one member is holding.
        ///
        /// A reservation is held until it is given back, with no timer on it, because every timer that could
        /// be put on one is wrong: a player configuring a prop in the properties menu sends nothing for
        /// minutes and is not idle. So the case a timer would have covered — somebody who walked away
        /// holding half the map — is a row on the host's menu instead, which is also the only person who can
        /// judge it.
        /// </summary>
        private void OnCoFree([FromSource] Player player, int request, int slot)
        {
            Session session;
            SessionMember member;
            if (!RequireHost(player, request, out session, out member)) return;

            var released = Sessions.ReleaseAll(session, slot);
            Reply(player, request, true, "");

            if (released == null) return;

            AnnounceHolds(session, slot, released, false);
        }

        private bool RequireHost(Player player, int request, out Session session, out SessionMember member)
        {
            session = Sessions.Find(player);
            member = Sessions.MemberOf(session, player);

            if (session == null || member == null)
            {
                Reply(player, request, false, "You are not in a session.");
                return false;
            }

            if (member.Slot != session.HostSlot)
            {
                Reply(player, request, false, "Only the session's host can do that.");
                return false;
            }

            if (!_writes.Allow(player.Handle))
            {
                Reply(player, request, false, "Too many requests. Try again in a moment.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Reserving objects for the length of a drag. All of them or none: the caller is about to move a
        /// group as one rigid thing, and half of it moving is worse than none of it.
        /// </summary>
        private void OnCoGrab([FromSource] Player player, int request, string uidsJson)
        {
            var session = Sessions.Find(player);
            var member = Sessions.MemberOf(session, player);

            if (session == null || member == null)
            {
                Reply(player, request, false, "You are not in a session.");
                return;
            }

            if (!_edits.Allow(player.Handle))
            {
                Reply(player, request, false, "Too many requests. Try again in a moment.");
                return;
            }

            var uids = ReadUids(uidsJson);
            if (uids.Count == 0)
            {
                Reply(player, request, true, "");
                return;
            }

            string heldBy;
            if (!Sessions.Grab(session, member.Slot, uids, out heldBy))
            {
                Reply(player, request, false, Describe(heldBy) + " is holding that.");
                return;
            }

            Reply(player, request, true, "");
            AnnounceHolds(session, member.Slot, uids, true);
        }

        private void OnCoDrop([FromSource] Player player, string uidsJson)
        {
            var session = Sessions.Find(player);
            var member = Sessions.MemberOf(session, player);
            if (session == null || member == null) return;
            if (!_edits.Allow(player.Handle)) return;

            var released = Sessions.Release(session, member.Slot, ReadUids(uidsJson));
            if (released == null) return;

            AnnounceHolds(session, member.Slot, released, false);
        }

        private static void AnnounceHolds(Session session, int slot, List<int> uids, bool held)
        {
            var array = Json.Array();
            foreach (var uid in uids) array.Add(Json.Of(uid));

            // Sent to the holder as well: they asked optimistically and started dragging before the answer
            // came back, so this is the message that confirms it — and the one another client's refusal
            // arrives on when two people reached for the same crate in the same frame.
            Sessions.Broadcast(session, -1, "mapeditor:cl:coheld", slot, array.ToJson(), held);
        }

        private static List<int> ReadUids(string json)
        {
            var uids = new List<int>();

            var array = Json.TryParse(json);
            if (array == null || array.Kind != JsonKind.Array) return uids;

            foreach (var item in array.Items)
            {
                var uid = item.AsInt(0);
                if (uid > 0) uids.Add(uid);
            }

            return uids;
        }

        /// <summary>
        /// A batch of changes somebody has finished making. Applied to the session's document, then passed
        /// to everyone else exactly as it arrived.
        ///
        /// Anything the sender was not allowed to change comes back to them alone, as the change that puts
        /// it back — see <see cref="Sessions.CorrectionJson"/>. That is the whole of conflict resolution
        /// here, and it is deliberately quiet: the object the player was moving slides back to where it is,
        /// and the name of whoever is holding it is already floating above it.
        /// </summary>
        private void OnCoOps([FromSource] Player player, string opsJson)
        {
            var session = Sessions.Find(player);
            var member = Sessions.MemberOf(session, player);
            if (session == null || member == null) return;

            if (!_edits.Allow(player.Handle)) return;

            if (string.IsNullOrEmpty(opsJson) || opsJson.Length > MaxMapSize) return;

            List<int> rejected;
            bool changed;
            string error;

            try
            {
                if (!Sessions.ApplyOps(session, member, opsJson, Sessions.MaxObjects, out rejected, out changed, out error))
                {
                    Log.Info("Dropped a batch from {0} in session {1}: {2}", Describe(player.Name), session.Id, error);
                    return;
                }
            }
            catch (Exception e)
            {
                Log.Error("Sessions.ApplyOps", e);
                return;
            }

            if (changed) Sessions.Broadcast(session, member.Slot, "mapeditor:cl:coops", opsJson);

            if (rejected != null)
                Sessions.Send(member, "mapeditor:cl:coops", Sessions.CorrectionJson(session, rejected));
        }

        /// <summary>
        /// Where the things one player is dragging are at this instant. Relayed and not stored: the document
        /// learns about the move when the drag ends, which is the moment the position stops being a guess.
        /// Somebody who joins mid-drag sees the object where it was picked up and it snaps into place a
        /// second later, which is the right trade for not writing to the document sixty times a second.
        /// </summary>
        private void OnCoDrag([FromSource] Player player, string dragJson)
        {
            var session = Sessions.Find(player);
            var member = Sessions.MemberOf(session, player);
            if (session == null || member == null) return;

            if (!_presence.Allow(player.Handle)) return;
            if (string.IsNullOrEmpty(dragJson) || dragJson.Length > ChunkSize) return;

            Sessions.Broadcast(session, member.Slot, "mapeditor:cl:codrag", member.Slot, dragJson);
        }

        /// <summary>
        /// Where one player's camera is. The reason it exists: in the editor a player is frozen, invisible
        /// and eight metres behind a camera nobody else can see, so without this there is no way at all to
        /// tell where the person you are building with is standing.
        /// </summary>
        private void OnCoHere([FromSource] Player player, string whereJson)
        {
            var session = Sessions.Find(player);
            var member = Sessions.MemberOf(session, player);
            if (session == null || member == null) return;

            if (!_presence.Allow(player.Handle)) return;
            if (string.IsNullOrEmpty(whereJson) || whereJson.Length > 512) return;

            Sessions.Broadcast(session, member.Slot, "mapeditor:cl:cohere", member.Slot, whereJson);
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

            // A session lives in this script and nowhere else, so it goes down with it. Every participant
            // still has the map standing in their own editor and can save it; what ends is the thread
            // between them. Said rather than left to be discovered, because it is the one piece of state
            // here that a resource restart really does destroy.
            try
            {
                Sessions.CloseAll(cause => TriggerClientEvent("mapeditor:cl:cobye", cause));
            }
            catch (Exception e)
            {
                Log.Error("Sessions.CloseAll", e);
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
            // Before the buckets, so that whatever they were holding in a session is given back to the
            // people still in it rather than left reserved to somebody who has gone.
            try
            {
                LeaveSession(player, null);
            }
            catch (Exception e)
            {
                Log.Error("OnPlayerDropped (session)", e);
            }

            _reads.Forget(player.Handle);
            _writes.Forget(player.Handle);
            _edits.Forget(player.Handle);
            _presence.Forget(player.Handle);
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
