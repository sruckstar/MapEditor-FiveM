using System;
using System.Collections.Generic;
using System.Globalization;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using MapEditor.Platform;

namespace MapEditor.Server
{
    /// <summary>
    /// The objects of the published maps that the server owns, rather than every client owning a copy of
    /// its own. This is level 2 of the three the port status document lays out, and it is the reason OneSync
    /// is now required to run this resource at all.
    ///
    /// <b>Why any of it exists.</b> A frozen prop needs no synchronising: its state is a function of the map
    /// file, and every client computing the same function of the same file already agrees with every other
    /// one, exactly, for free and without a limit. What cannot work that way is anything the game or a
    /// player touches after it is spawned — a ped that walks, a ped that shoots, a car somebody gets into.
    /// Those, spawned locally, give each player their own private copy: their own gunman to kill, their own
    /// NPC ten metres from everyone else's, and a friend sitting in mid-air where their car is not.
    /// <see cref="SharedObjects"/> is the one rule that decides which is which, and the client half of this
    /// (AutoloadedMaps) skips exactly what this class takes.
    ///
    /// <b>Why the server creates them rather than electing a client to.</b> A server entity is one entity
    /// for the whole session: ownership migrates in the engine, nobody spawns a second copy, and it outlives
    /// whoever happened to be standing near it when it appeared. Electing an owner would work — and would
    /// even make configuring simpler, since the elected client already has the code — but it loses state at
    /// every handover: the owner leaves, the entity goes with them, the next client makes a new one, and the
    /// walking ped is back at its starting mark with the damage and the passengers gone.
    ///
    /// <b>Existence, not a flag.</b> "Synchronise it when two players are near" is the wrong shape for the
    /// question, and the right one is here: a shared object exists as a server entity while somebody is near
    /// it and does not exist at all otherwise. That is what keeps a hard cap on live entities from being a
    /// cap on how much a map may contain — <see cref="MaxEntities"/> bounds how many stand at once, not how
    /// many are written down. Local scenery is not in this pool at all, and the client's own streaming keeps
    /// dealing with it separately and on its own terms.
    ///
    /// <b>The half the server cannot do.</b> The server can create an entity and place it; it cannot make it
    /// into what the map says it is. Tasks, relationship groups, ped props, liveries, sirens and quaternion
    /// rotation have no server-side natives — they are the client's. So every entity created here is
    /// followed by one request to the client that owns it (<c>mapeditor:cl:configure</c>) carrying the
    /// object as the map holds it; that client applies the rest once and says so. Unanswered requests are
    /// retried, because the owner may have left between the creation and the message.
    ///
    /// A vehicle's paint is the exception at both ends: the server can set it (<see cref="Paint"/>) and read
    /// it back (<see cref="LooksRight"/>), so it is applied here and checked here rather than taken on
    /// trust. What can be verified is verified — a request that was lost, or answered while the entity was
    /// still streaming in, is otherwise indistinguishable from one that worked.
    /// </summary>
    public static class LiveEntities
    {
        /// <summary>
        /// One object of a live map that the server owns, and the entity standing for it at the moment —
        /// which is nothing at all while no player is near enough to be shown it.
        /// </summary>
        private sealed class Shared
        {
            /// <summary>The object as the map holds it, forwarded verbatim to whoever configures it.</summary>
            public string Document;

            public string Type;
            public int Hash;
            public float X, Y, Z;
            public float Pitch, Roll, Yaw;

            /// <summary>Frozen unless the map says the object moves. A door is never frozen; see the client.</summary>
            public bool Frozen;

            /// <summary>
            /// A vehicle's paint and its livery, or -1 for "the map does not say". The colours are the one
            /// piece of configuration the server can both apply and read back (see <see cref="Paint"/> and
            /// <see cref="LooksRight"/>); the livery is kept only so the complaint can quote it.
            /// </summary>
            public int PrimaryColor = -1, SecondaryColor = -1, Livery = -1;

            /// <summary>0 while it is not in the world.</summary>
            public int Handle;

            /// <summary>
            /// Whether the entity has actually appeared and had the server's half of the setup applied.
            /// False for the moments between asking for one and getting it; see <see cref="Place"/>.
            /// </summary>
            public bool Placed;

            /// <summary>
            /// Game-time reading of when the entity was asked for. Only until it appears — after that
            /// <see cref="Placed"/> is what says the handle means something.
            /// </summary>
            public int AskedForAt;

            public int NetId;

            /// <summary>Whether the owning client has said it applied what the server could not.</summary>
            public bool Configured;

            /// <summary>Game-time reading of the last configure request, so an unanswered one is retried.</summary>
            public int AskedAt;

            public int Asks;

            /// <summary>
            /// How many times the paint has been applied. Counted separately from <see cref="Asks"/>, which
            /// only counts requests that actually went out: painting needs no owner of ours to be found,
            /// and would otherwise be the one thing here without a bound on it.
            /// </summary>
            public int Paints;

            /// <summary>Whether the console has already been told this one never came right. Said once.</summary>
            public bool Complained;
        }

        /// <summary>What one published map put into the server's world.</summary>
        private sealed class LiveMap
        {
            public LiveMap(string key, string stamp)
            {
                Key = key;
                Stamp = stamp;
            }

            /// <summary>The name the map is filed under, which is what the catalogue is matched on.</summary>
            public string Key;

            /// <summary>
            /// Size and save time of the entry this was read from. A map republished over the top of itself
            /// keeps its name and changes this, and that is the only way <see cref="Reconcile"/> can tell
            /// that what is standing is no longer what the file says.
            /// </summary>
            public string Stamp;

            public readonly List<Shared> Objects = new List<Shared>();
        }

        private static readonly List<LiveMap> Maps = new List<LiveMap>();

        /// <summary>Sends one client an event. Bound from the script, which is the only thing that can.</summary>
        private static Action<Player, string, object[]> _send;

        /// <summary>The connected players, asked afresh every pass rather than cached.</summary>
        private static Func<PlayerList> _players;

        /// <summary>
        /// Whether the server is running OneSync at all. Read once, at startup, because the convar is
        /// read-only and set on the command line: it cannot change while the server is up.
        /// </summary>
        private static bool _oneSync;

        /// <summary>Whether the cap has already been complained about, so it is said once and not per pass.</summary>
        private static bool _capReported;

        public static void Bind(Func<PlayerList> players, Action<Player, string, object[]> send)
        {
            if (players == null) throw new ArgumentNullException("players");
            if (send == null) throw new ArgumentNullException("send");

            _players = players;
            _send = send;
            _oneSync = OneSyncEnabled;
        }

        /// <summary>
        /// Whether this server can own entities at all. <c>onesync</c> is a read-only convar set with
        /// <c>+set onesync on</c> on the command line, and its three values are <c>off</c>, <c>legacy</c>
        /// and <c>on</c>. Anything that is not "off" will do: server-created entities exist in legacy
        /// OneSync too, and infinity is only the recommended one.
        /// </summary>
        public static bool OneSyncEnabled
        {
            get
            {
                var mode = (API.GetConvar("onesync", "off") ?? "").Trim();
                return mode.Length > 0
                       && !string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase)
                       && mode != "0";
            }
        }

        /// <summary>What the startup line and the refusal message call the mode this server is in.</summary>
        public static string OneSyncMode
        {
            get
            {
                var mode = (API.GetConvar("onesync", "off") ?? "").Trim();
                return mode.Length == 0 ? "off" : mode;
            }
        }

        /// <summary>Entities standing right now, for the log line and for the cap.</summary>
        public static int Count
        {
            get
            {
                var count = 0;
                foreach (var map in Maps)
                {
                    foreach (var o in map.Objects)
                    {
                        if (o.Handle != 0) count++;
                    }
                }
                return count;
            }
        }

        /// <summary>Objects the published maps have handed to the server, standing or not.</summary>
        public static int Total
        {
            get
            {
                var total = 0;
                foreach (var map in Maps) total += map.Objects.Count;
                return total;
            }
        }

        /// <summary>
        /// Brings the set of live maps into line with the catalogue: reads in what has just been published,
        /// drops what has been unloaded or deleted, and re-reads what was published over the top of itself.
        ///
        /// Deliberately the same shape as the client's own reconciliation, and for the same reason: state is
        /// compared whole rather than applied as a delta, so a step that was missed is put right by the next
        /// one instead of leaving the two sides quietly disagreeing forever.
        /// </summary>
        public static void Reconcile()
        {
            if (!_oneSync) return;

            var live = new List<ServerMapEntry>();
            foreach (var entry in ServerMaps.All)
            {
                if (entry.Live) live.Add(entry);
            }

            for (var i = Maps.Count - 1; i >= 0; i--)
            {
                var standing = Maps[i];

                var stillThere = false;
                foreach (var entry in live)
                {
                    if (!string.Equals(entry.Name, standing.Key, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Stamp(entry) != standing.Stamp) continue;
                    stillThere = true;
                    break;
                }

                if (stillThere) continue;

                Despawn(standing);
                Maps.RemoveAt(i);
            }

            foreach (var entry in live)
            {
                var known = false;
                foreach (var map in Maps)
                {
                    if (!string.Equals(map.Key, entry.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    known = true;
                    break;
                }

                if (known) continue;

                var loaded = Read(entry);
                if (loaded == null) continue;

                Maps.Add(loaded);

                if (loaded.Objects.Count > 0)
                    Log.Info("'{0}': {1} object(s) belong to the server.", entry.Name, loaded.Objects.Count);
            }
        }

        /// <summary>
        /// Reads one published map and keeps the objects the rule hands to the server. The rest of the
        /// document is the client's business and is not even looked at here.
        /// </summary>
        private static LiveMap Read(ServerMapEntry entry)
        {
            var content = ServerMaps.Read(MapScope.Shared, null, entry.Name);
            if (content == null) return null;

            var document = Json.TryParse(content);
            if (document == null || document.Kind != JsonKind.Object)
            {
                Log.Info("Published map '{0}' could not be read; none of it will be owned by the server.", entry.Name);
                return null;
            }

            var map = new LiveMap(entry.Name, Stamp(entry));

            foreach (var item in document["objects"].Items)
            {
                if (item == null || item.Kind != JsonKind.Object) continue;

                var type = item["type"].AsString("Prop");
                bool? shared = item.Has("shared") ? item["shared"].AsBool(false) : (bool?) null;

                var needsServer = SharedObjects.NeedsServer(type, shared,
                    item["dynamic"].AsBool(false), item["door"].AsBool(false),
                    item["action"].AsString(null), item["weapon"].AsString(null),
                    item["relationship"].AsString(null));

                if (!needsServer) continue;

                var position = item["position"];
                var rotation = item["rotation"];

                var o = new Shared
                {
                    Document = item.ToJson(),
                    Type = type,
                    Hash = item["hash"].AsInt(0),
                    X = position[0].AsFloat(0f),
                    Y = position[1].AsFloat(0f),
                    Z = position[2].AsFloat(0f),
                    Pitch = rotation[0].AsFloat(0f),
                    Roll = rotation[1].AsFloat(0f),
                    Yaw = rotation[2].AsFloat(0f),
                    Frozen = !item["dynamic"].AsBool(false) && !item["door"].AsBool(false),
                };

                // Read out of the document as well as forwarded inside it: the server acts on these two
                // itself, where everything else in there is only ever passed on to a client.
                if (string.Equals(type, "Vehicle", StringComparison.OrdinalIgnoreCase))
                {
                    o.PrimaryColor = item["primaryColor"].AsInt(-1);
                    o.SecondaryColor = item["secondaryColor"].AsInt(-1);
                    o.Livery = item["livery"].AsInt(-1);
                }

                map.Objects.Add(o);
            }

            return map;
        }

        /// <summary>
        /// One pass over everything the published maps handed the server: create what somebody has walked up
        /// to, delete what everybody has walked away from, and chase the configuration of anything that has
        /// not confirmed it.
        ///
        /// Run on a timer rather than every frame — this is proximity, measured in tens of metres, against
        /// players who move at walking or driving speed.
        /// </summary>
        public static void Update()
        {
            if (!_oneSync || Maps.Count == 0) return;

            var positions = PlayerPositions();
            var near = NearRadius;
            var far = near + KeepMargin;
            var standing = Count;
            var cap = MaxEntities;

            foreach (var map in Maps)
            {
                foreach (var o in map.Objects)
                {
                    if (o.Handle != 0 && !API.DoesEntityExist(o.Handle) && Gone(o))
                    {
                        // Deleted by a client that had control, or lost with the session it lived in. The
                        // record goes back to "not standing" and is made again when somebody is near.
                        o.Handle = 0;
                        o.NetId = 0;
                        o.Placed = false;
                        o.Configured = false;
                        standing--;
                    }

                    var wanted = o.Handle == 0
                        ? IsWithin(positions, o, near)
                        : IsWithin(positions, o, far);

                    if (wanted && o.Handle == 0)
                    {
                        if (standing >= cap)
                        {
                            if (!_capReported)
                            {
                                _capReported = true;
                                Log.Info("The live entity cap ({0}) has been reached; further shared objects " +
                                         "will not appear until players move away from the ones standing. " +
                                         "Raise mapeditor_max_shared_entities if this is not what you want.", cap);
                            }
                            continue;
                        }

                        if (Spawn(o)) standing++;
                        continue;
                    }

                    if (!wanted && o.Handle != 0)
                    {
                        Delete(o);
                        standing--;
                        continue;
                    }

                    // Still on its way: there is a handle here and nothing in the world yet, and everything
                    // below would be done to nothing at all.
                    if (!Place(o)) continue;

                    // Compared rather than believed. A client's "done" is the only word there is about a
                    // task or a piece of clothing, but a vehicle's paint and livery can be read back — and
                    // an answer that arrived while the entity was still streaming in looks exactly like one
                    // that worked. Same shape as Reconcile, and for the same reason: what was missed is put
                    // right by the next pass instead of being wrong for the rest of the session.
                    if (o.Configured && LooksRight(o)) continue;

                    AskOwnerToConfigure(o);
                    Complain(o);
                }
            }
        }

        /// <summary>
        /// Whether an entity that does not exist is one that has been and gone, rather than one that has not
        /// arrived yet.
        ///
        /// The creation natives are RPCs — the server registers the entity and a client in range makes it —
        /// so for the first moments there is a handle here and nothing in the world. Without this the pass
        /// that runs in that window reads "deleted", forgets the entity and asks for another one, and the
        /// first is left standing where nothing will ever configure it or take it away again:
        /// <see cref="KeepEntity"/> means the server will not clean it up either.
        /// </summary>
        private static bool Gone(Shared o)
        {
            return o.Placed || unchecked(Environment.TickCount - o.AskedForAt) > CreatingMs;
        }

        /// <summary>
        /// Asks for one shared object as a server entity. Returns whether there is now something to wait
        /// for; making it into what the map says is <see cref="Place"/>'s job.
        ///
        /// The creation natives for entities are RPCs: the server picks a client in range and has it do the
        /// creating, so a failure here usually means nobody suitable was in scope after all, and the next
        /// pass tries again. That is also why proximity is checked first rather than creating everything up
        /// front — outside a player's focus zone there is nobody to run them.
        /// </summary>
        private static bool Spawn(Shared o)
        {
            int handle;

            switch ((o.Type ?? "").ToLowerInvariant())
            {
                case "vehicle":
                    handle = API.CreateVehicle((uint) o.Hash, o.X, o.Y, o.Z, o.Yaw, true, true);
                    break;

                case "ped":
                    // Peds stand on their feet where props hang from their centre, and the editor stores the
                    // centre. The same metre the client's own spawn takes off.
                    handle = API.CreatePed(PedType, (uint) o.Hash, o.X, o.Y, o.Z - 1f, o.Yaw, true, true);
                    break;

                default:
                    handle = API.CreateObjectNoOffset((uint) o.Hash, o.X, o.Y, o.Z, true, true, false);
                    break;
            }

            if (handle == 0) return false;

            o.Handle = handle;
            o.AskedForAt = Environment.TickCount;
            o.Placed = false;
            o.NetId = 0;
            o.Configured = false;
            o.Asks = 0;
            o.Paints = 0;
            o.Complained = false;

            // Nothing else here. Everything the server does to the entity is done in Place, once there is
            // an entity to do it to — which is usually this same call and sometimes a later pass.
            Place(o);
            return true;
        }

        /// <summary>
        /// Does everything to the entity the server has a native for, the first time it is really there.
        /// Returns whether it now stands.
        ///
        /// Separate from <see cref="Spawn"/> because a handle from an RPC creation native is a promise
        /// rather than an entity: the server has registered it and a client in range is being told to make
        /// it. Every native called on it before that happens is called on nothing, silently, and that is
        /// enough on its own to leave a published car wearing whatever colours the game picked for it: the
        /// network id read in that window is zero, so the owner is never asked to finish the job, and the
        /// id was read once and never again.
        /// </summary>
        private static bool Place(Shared o)
        {
            if (o.Handle == 0) return false;
            if (o.Placed) return true;
            if (!API.DoesEntityExist(o.Handle)) return false;

            o.Placed = true;

            // The server's own relevancy sweep would take these away the moment it decided they were not
            // interesting; their lifetime is this class's to decide and nobody else's.
            API.SetEntityOrphanMode(o.Handle, KeepEntity);

            if (!string.Equals(o.Type, "Ped", StringComparison.OrdinalIgnoreCase))
                API.SetEntityRotation(o.Handle, o.Pitch, o.Roll, o.Yaw, RotationOrder, true);

            API.FreezeEntityPosition(o.Handle, o.Frozen);

            o.NetId = API.NetworkGetNetworkIdFromEntity(o.Handle);

            AskOwnerToConfigure(o);
            return true;
        }

        /// <summary>
        /// The vehicle's paint, which is the one thing on this list the server can apply itself:
        /// SET_VEHICLE_COLOURS is among the natives a server may run on an entity's owner, where
        /// SET_VEHICLE_LIVERY and SET_VEHICLE_SIREN are not.
        ///
        /// Worth taking here rather than leaving to the client's half. Paint is what a car is recognised by
        /// from across the street, and the map already holds it, so it has no business depending on a
        /// request being answered.
        /// </summary>
        private static void Paint(Shared o)
        {
            if (o.Handle == 0 || o.Paints >= MaxAsks) return;
            if (o.PrimaryColor < 0 || o.SecondaryColor < 0) return;
            if (!string.Equals(o.Type, "Vehicle", StringComparison.OrdinalIgnoreCase)) return;

            o.Paints++;
            API.SetVehicleColours(o.Handle, o.PrimaryColor, o.SecondaryColor);
        }

        /// <summary>
        /// Whether what stands there is what the map says, as far as the server can see — which is a
        /// vehicle's colours, and only those. Everything else (tasks, clothing, sirens, quaternion
        /// rotation) has no server-side getter and the client's answer is the only word there is about it.
        ///
        /// <b>Not the livery, though the server has GET_VEHICLE_LIVERY.</b> That native reads the vehicle's
        /// own livery slot, and every model added since the game shipped keeps its liveries as mods in slot
        /// 48 instead, where the server has no getter at all: it would answer -1 about a car that is
        /// visibly wearing one and send the owner five requests to put on what it already has. The client
        /// checks its own work there — <c>AutoloadedMaps.ApplyLivery</c> only says done once the vehicle
        /// admits to wearing it — so the answer is worth more here than a second opinion that cannot see.
        /// </summary>
        private static bool LooksRight(Shared o)
        {
            if (o.Handle == 0) return false;
            if (!string.Equals(o.Type, "Vehicle", StringComparison.OrdinalIgnoreCase)) return true;
            if (o.PrimaryColor < 0 || o.SecondaryColor < 0) return true;

            int primary = 0, secondary = 0;
            API.GetVehicleColours(o.Handle, ref primary, ref secondary);
            return primary == o.PrimaryColor && secondary == o.SecondaryColor;
        }

        private static void Delete(Shared o)
        {
            // Asked for whether or not it exists here: one that was asked for and has not appeared yet is
            // exactly the one that would be left behind, and DELETE_ENTITY on a handle with nothing under
            // it does nothing.
            if (o.Handle != 0) API.DeleteEntity(o.Handle);

            o.Handle = 0;
            o.NetId = 0;
            o.Placed = false;
            o.Configured = false;
            o.Asks = 0;
            o.Paints = 0;
            o.Complained = false;
        }

        /// <summary>
        /// Asks a client to apply everything the server has no native for.
        ///
        /// Asked of one client rather than broadcast because half of it is tasks, and a task only takes on
        /// the machine that has control. Retried because ownership migrates and because the client that was
        /// asked may have left in between; given up on after <see cref="MaxAsks"/>, at which point the
        /// entity stands there as a plain unconfigured model, which is worse than intended and much better
        /// than not standing there at all.
        /// </summary>
        private static void AskOwnerToConfigure(Shared o)
        {
            if (o.Handle == 0 || o.NetId == 0) return;
            if (o.Asks >= MaxAsks) return;

            var now = Environment.TickCount;
            if (o.Asks > 0 && unchecked(now - o.AskedAt) < AskAgainMs) return;

            // The half the server can do goes with every ask, and not once at creation: the moment a
            // request is needed is the moment to stop assuming the last one landed. Bounded on its own
            // count, so a colour the game refuses is asked for a few times and then let be.
            Paint(o);

            var target = Owner(o) ?? Nearest(o);
            if (target == null) return;

            o.Asks++;
            o.AskedAt = now;
            _send(target, "mapeditor:cl:configure", new object[] { o.NetId, o.Document });
        }

        /// <summary>The player who owns the entity, or null if no client does.</summary>
        private static Player Owner(Shared o)
        {
            var owner = API.NetworkGetEntityOwner(o.Handle);
            if (owner <= 0) return null;

            // PlayerList's int indexer only wraps the id in a Player and never comes back empty, so a player
            // who has left looks exactly like one who is here. The name is absent for one that is gone.
            return string.IsNullOrEmpty(API.GetPlayerName(owner.ToString(CultureInfo.InvariantCulture)))
                ? null
                : _players()[owner];
        }

        /// <summary>
        /// The player standing closest to the object, or null if nobody has a ped yet.
        ///
        /// Asked when the entity has no client owner — which is a real state and not an error: an entity the
        /// server made and froze, that no player has so much as leaned on, can sit there belonging to
        /// nobody. Waiting for an owner that has no reason to appear leaves the entity unconfigured for the
        /// rest of the session, and there is nothing in the console to say so.
        ///
        /// Asking a client that does not own it is safe because the client half never assumed it did: it
        /// waits for the entity to arrive, asks for control, and applies nothing until it has it. See
        /// <see cref="SharedEntities"/> on the client. Nearest rather than any, because control is granted
        /// to somebody in range and asking a player on the other side of the map is asking for silence.
        /// </summary>
        private static Player Nearest(Shared o)
        {
            Player nearest = null;
            var nearestSquare = float.MaxValue;

            foreach (var player in _players())
            {
                var ped = API.GetPlayerPed(player.Handle);
                if (ped == 0) continue;

                var position = API.GetEntityCoords(ped);
                var dx = position.X - o.X;
                var dy = position.Y - o.Y;
                var dz = position.Z - o.Z;
                var square = dx * dx + dy * dy + dz * dz;

                if (square >= nearestSquare) continue;

                nearestSquare = square;
                nearest = player;
            }

            return nearest;
        }

        /// <summary>
        /// Says once, per object, that it has been asked for as many times as it is going to be and is still
        /// not what the map says.
        ///
        /// Worth a line in the console because there is nowhere else for this to show up. The client writes
        /// no log the owner can read, and the failure looks from the world like a map that was saved wrong —
        /// so the one thing the server can do is print what it sees against what the document asked for, and
        /// whether the client ever answered at all. The two are different faults with different fixes.
        /// </summary>
        private static void Complain(Shared o)
        {
            if (o.Complained || o.Asks < MaxAsks) return;
            o.Complained = true;

            if (!string.Equals(o.Type, "Vehicle", StringComparison.OrdinalIgnoreCase))
            {
                Log.Info("A shared {0} at ({1:0.#}, {2:0.#}, {3:0.#}) was asked to be configured {4} times " +
                         "and never answered; it stands as a plain model.",
                    o.Type, o.X, o.Y, o.Z, o.Asks);
                return;
            }

            int primary = 0, secondary = 0;
            API.GetVehicleColours(o.Handle, ref primary, ref secondary);

            Log.Info("A shared vehicle at ({0:0.#}, {1:0.#}, {2:0.#}) is not what the map says after {3} " +
                     "request(s). It wears colours {4}/{5}; the map asks for {6}/{7}. The client that was " +
                     "asked {8}. The livery, which the map puts at {9}, is not something this side can see; " +
                     "an unanswered request is the only sign there is that it did not go on.",
                o.X, o.Y, o.Z, o.Asks, primary, secondary, o.PrimaryColor, o.SecondaryColor,
                o.Configured ? "answered, so it applied this and the game did not keep it" : "never answered",
                o.Livery);
        }

        /// <summary>
        /// A client saying it has applied the configuration for that entity. Trusted with nothing more than
        /// "stop asking": the worst a false one can do is leave one entity unconfigured, which is what not
        /// answering does anyway.
        /// </summary>
        public static void Configured(int netId)
        {
            foreach (var map in Maps)
            {
                foreach (var o in map.Objects)
                {
                    if (o.NetId != netId || o.Handle == 0) continue;
                    o.Configured = true;
                    return;
                }
            }
        }

        /// <summary>Takes everything one map put into the world back out of it.</summary>
        private static void Despawn(LiveMap map)
        {
            foreach (var o in map.Objects) Delete(o);
            map.Objects.Clear();
        }

        /// <summary>
        /// Everything the server is holding goes. For a resource that is stopping: these entities are
        /// server-side and outlive the script, so the instance that replaces this one would find a world
        /// full of props it has no record of.
        /// </summary>
        public static void UnloadAll()
        {
            foreach (var map in Maps) Despawn(map);
            Maps.Clear();
        }

        /// <summary>
        /// Where every player's ped is. Players who have not spawned yet have no ped and are left out
        /// rather than counted as being at the origin, which is a real place on this map.
        /// </summary>
        private static List<Vector3> PlayerPositions()
        {
            var positions = new List<Vector3>();

            foreach (var player in _players())
            {
                var ped = API.GetPlayerPed(player.Handle);
                if (ped == 0) continue;

                positions.Add(API.GetEntityCoords(ped));
            }

            return positions;
        }

        private static bool IsWithin(List<Vector3> positions, Shared o, float radius)
        {
            var square = radius * radius;

            foreach (var position in positions)
            {
                var dx = position.X - o.X;
                var dy = position.Y - o.Y;
                var dz = position.Z - o.Z;

                if (dx * dx + dy * dy + dz * dz <= square) return true;
            }

            return false;
        }

        /// <summary>
        /// What identifies the version of a published map. Same pair the client compares on, and for the
        /// same reason: a map republished over the top of itself is one catalogue entry with new content.
        /// </summary>
        private static string Stamp(ServerMapEntry entry)
        {
            return entry.Size.ToString(CultureInfo.InvariantCulture) + "@" + (entry.SavedAt ?? "");
        }

        // --- Constants and limits ------------------------------------------------------------------------

        /// <summary>Mission ped, the same type the client's own spawn asks for.</summary>
        private const int PedType = 26;

        /// <summary>SET_ENTITY_ORPHAN_MODE's KeepEntity. See the native's own documentation.</summary>
        private const int KeepEntity = 2;

        /// <summary>The rotation order SET_ENTITY_ROTATION is given everywhere else in this port.</summary>
        private const int RotationOrder = 2;

        /// <summary>
        /// How much further than <see cref="NearRadius"/> a player may walk before what they walked up to is
        /// taken away again. Without the margin an object on the boundary would be created and deleted on
        /// alternate passes for as long as somebody stood there.
        /// </summary>
        private const float KeepMargin = 60f;

        /// <summary>Milliseconds before an unanswered configure request is sent again.</summary>
        private const int AskAgainMs = 4000;

        /// <summary>
        /// How long an entity that has been asked for may take to appear before the record gives up on it
        /// and asks for another. Generous on purpose: creating one means a round trip to a client, and
        /// asking twice for the same object leaves the first copy standing for good. See <see cref="Gone"/>.
        /// </summary>
        private const int CreatingMs = 15000;

        /// <summary>How many times one entity's configuration is chased before it is left as it is.</summary>
        private const int MaxAsks = 5;

        /// <summary>
        /// How near a player has to be for a shared object to exist. Comfortably inside OneSync's own focus
        /// zone (424 units), because the creation natives are RPCs run by a client in range: an object asked
        /// for outside every player's zone has nobody to create it and would fail on every pass.
        /// </summary>
        private static float NearRadius
        {
            get
            {
                var radius = API.GetConvarInt("mapeditor_shared_radius", 250);
                if (radius < 25) radius = 25;
                if (radius > 400) radius = 400;
                return radius;
            }
        }

        /// <summary>
        /// How many shared entities may stand at once. This is a cap on the world, not on the maps: an
        /// object nobody is near does not exist, so a server can hold far more shared objects than this and
        /// only pay for the ones somebody is looking at.
        /// </summary>
        private static int MaxEntities
        {
            get { return API.GetConvarInt("mapeditor_max_shared_entities", 200); }
        }
    }
}
