using System;
using System.Collections.Generic;
using System.Globalization;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using MapEditor.Platform;

namespace MapEditor.Server
{
    /// <summary>One player inside a co-editing session.</summary>
    public sealed class SessionMember
    {
        /// <summary>The player, kept so the session can talk to them. Dropped players leave the list.</summary>
        public Player Player;

        /// <summary>What <see cref="Player.Handle"/> said when they joined; the key everything else uses.</summary>
        public string Handle;

        public string Name;

        /// <summary>
        /// This member's number inside the session. Handed out in order and never reused, because it is the
        /// top half of every object id they mint — see <see cref="Sessions.SlotShift"/>. Reusing one would
        /// let a player who joined after somebody left mint ids that already belong to that person's objects.
        /// </summary>
        public int Slot;

        public int JoinedTicks;
    }

    /// <summary>
    /// One shared draft: the document, who is working on it, and who is holding what.
    ///
    /// The three tables are the map taken apart by object, because that is the unit an edit travels in. Each
    /// holds the same JSON the map file holds — read by the same reader on the other side (see
    /// MapSerializer) — under an id the client minted. The server keeps the tree rather than the text so that
    /// handing the whole document to a joiner is one document rather than a document full of quoted
    /// documents.
    /// </summary>
    public sealed class Session
    {
        public int Id;
        public string Name;

        /// <summary>The slot of whoever is host. The host owns the map's name, publishing, and the reset.</summary>
        public int HostSlot;

        /// <summary>
        /// Bumped whenever the whole document is replaced rather than edited (the host loading another map,
        /// or starting a new one). Members are told to fetch the document again rather than being sent a
        /// delta for every object at once.
        /// </summary>
        public int Epoch;

        public int CreatedTicks;

        public readonly List<SessionMember> Members = new List<SessionMember>();

        public readonly Dictionary<int, Json> Objects = new Dictionary<int, Json>();
        public readonly Dictionary<int, Json> Markers = new Dictionary<int, Json>();
        public readonly Dictionary<int, Json> Removed = new Dictionary<int, Json>();

        /// <summary>The map's name, author and description. The host's to set; see Sessions.ApplyOps.</summary>
        public Json Meta;

        /// <summary>
        /// Which slot is holding each object at this moment, for the objects somebody is holding.
        ///
        /// A hold is not what makes an edit legal — see <see cref="Sessions.ApplyOps"/>, which only refuses an
        /// edit to something <em>somebody else</em> holds. It is a reservation taken for the length of a drag,
        /// so that two people cannot start moving the same crate, and so that everyone else can see the crate
        /// is busy and whose it is.
        /// </summary>
        public readonly Dictionary<int, int> Leases = new Dictionary<int, int>();

        public int NextSlot;

        public SessionMember Host
        {
            get { return FindSlot(HostSlot); }
        }

        public SessionMember FindSlot(int slot)
        {
            foreach (var member in Members)
            {
                if (member.Slot == slot) return member;
            }
            return null;
        }

        public int ObjectCount
        {
            get { return Objects.Count + Removed.Count; }
        }
    }

    /// <summary>
    /// The co-editing sessions this server is holding.
    ///
    /// <b>What a session is.</b> One draft map that several players edit at once. It is not a stored map and
    /// it is not a published one: nothing here touches the disk, and nothing here stands in anybody's world
    /// but the editors'. Saving a session is an ordinary save by whoever presses it (see
    /// <see cref="ServerMaps"/>), and publishing one is an ordinary publish by the host — which ends the
    /// session, because a published map is the server's and no longer a draft.
    ///
    /// <b>Why the server holds the document at all</b>, when every client already has the whole map spawned:
    /// somebody joining halfway has to be given it, and there is no other party that can be asked. A host
    /// that had to serve it would have to be online, willing, and not mid-load. So the server keeps the
    /// object table, applies every edit to it, and hands it out — which also makes it the one place that can
    /// answer "who is holding this crate".
    ///
    /// <b>What the server does not do.</b> It does not interpret an object. It reads the id, the kind of
    /// change and nothing else; the JSON inside is passed through to the other clients and to the joiner as
    /// it arrived. Every rule it enforces is about the shape of the traffic — who may touch what, how many
    /// objects, how large, how often — never about what a prop is. That is deliberate: a server that had an
    /// opinion about a map object would be a second implementation of the map format, and the two would
    /// drift.
    ///
    /// <b>Ids.</b> An object id is minted by the client that creates the object, as
    /// <c>(slot &lt;&lt; 20) | counter</c>. This is the one thing that lets a placement be instant: the
    /// player puts a crate down and it is the crate, with a name everybody will agree on, without a round
    /// trip. The server checks the top half against the sender's slot, so a client cannot mint into someone
    /// else's space, and slots are never reused so a departed member's ids stay theirs.
    /// </summary>
    public static class Sessions
    {
        /// <summary>How many bits of an object id belong to the counter; the rest is the minting slot.</summary>
        public const int SlotShift = 20;

        /// <summary>Ids are positive: 31 usable bits, twenty of counter, eleven of slot.</summary>
        private const int MaxSlot = (1 << (31 - SlotShift)) - 1;

        /// <summary>
        /// The largest object a client may put in one table entry, in characters. A map object is a few
        /// hundred; this is room for a ped with every drawable listed and then some.
        /// </summary>
        private const int MaxObjectChars = 8 * 1024;

        /// <summary>How many changes may travel in one batch. A stacking run is the burst this is sized for.</summary>
        private const int MaxOpsPerBatch = 512;

        private static readonly Dictionary<int, Session> All = new Dictionary<int, Session>();

        /// <summary>Which session a player is in, by player handle. One at a time, by construction.</summary>
        private static readonly Dictionary<string, int> ByPlayer = new Dictionary<string, int>();

        private static int _nextId;

        private static Action<Player, string, object[]> _send;

        /// <summary>
        /// Hands over the way to talk to a client. Called once from the server script's constructor, for the
        /// same reason <see cref="LiveEntities"/> is: TriggerClientEvent belongs to BaseScript and this is
        /// static.
        /// </summary>
        public static void Bind(Action<Player, string, object[]> send)
        {
            if (send == null) throw new ArgumentNullException("send");
            _send = send;
        }

        public static int Count
        {
            get { return All.Count; }
        }

        // --- Lifecycle -------------------------------------------------------------------------------

        public static Session Get(int id)
        {
            Session session;
            return All.TryGetValue(id, out session) ? session : null;
        }

        public static Session Find(Player player)
        {
            if (player == null) return null;

            int id;
            if (!ByPlayer.TryGetValue(player.Handle, out id)) return null;

            Session session;
            return All.TryGetValue(id, out session) ? session : null;
        }

        public static SessionMember MemberOf(Session session, Player player)
        {
            if (session == null || player == null) return null;

            foreach (var member in session.Members)
            {
                if (member.Handle == player.Handle) return member;
            }
            return null;
        }

        /// <summary>
        /// Opens an empty session with this player as its host. The document arrives afterwards, as a push:
        /// the host's editor may be holding a map of some megabytes and that does not fit in the answer to
        /// a request.
        /// </summary>
        public static Session Open(Player player, string name)
        {
            var session = new Session
            {
                Id = ++_nextId,
                Name = name,
                CreatedTicks = Environment.TickCount,
            };

            All[session.Id] = session;

            var host = Add(session, player);
            session.HostSlot = host.Slot;
            return session;
        }

        /// <summary>
        /// Puts a player into a session, or hands back the membership they already have. Rejoining is how
        /// resynchronising works, so it must not be an error.
        /// </summary>
        public static SessionMember Join(Session session, Player player)
        {
            var existing = MemberOf(session, player);
            if (existing != null) return existing;

            return Add(session, player);
        }

        private static SessionMember Add(Session session, Player player)
        {
            var member = new SessionMember
            {
                Player = player,
                Handle = player.Handle,
                Name = player.Name,
                Slot = session.NextSlot++,
                JoinedTicks = Environment.TickCount,
            };

            session.Members.Add(member);
            ByPlayer[player.Handle] = session.Id;
            return member;
        }

        /// <summary>Whether this session has run out of the slots an id can carry.</summary>
        public static bool SlotsExhausted(Session session)
        {
            return session.NextSlot > MaxSlot;
        }

        /// <summary>
        /// Takes a player out of whichever session they are in and tidies up after them: their holds are
        /// given back, and if they were host the session is handed to whoever has been in it longest.
        ///
        /// The session is only forgotten when the last member leaves. A host disconnecting does not end
        /// everybody else's afternoon — the work is on the server and the remaining members carry on with it.
        ///
        /// Returns the session they were in, or null.
        /// </summary>
        public static Session Leave(Player player, out bool closed, out bool hostChanged)
        {
            closed = false;
            hostChanged = false;

            var session = Find(player);
            if (session == null) return null;

            var member = MemberOf(session, player);
            ByPlayer.Remove(player.Handle);

            if (member == null) return session;

            ReleaseAll(session, member.Slot);
            session.Members.Remove(member);

            if (session.Members.Count == 0)
            {
                All.Remove(session.Id);
                closed = true;
                return session;
            }

            if (session.HostSlot != member.Slot) return session;

            // Longest present, not lowest slot: the two differ once anybody has left, and "who has been
            // here longest" is the one a room would agree with.
            var oldest = session.Members[0];
            foreach (var candidate in session.Members)
            {
                if (unchecked(candidate.JoinedTicks - oldest.JoinedTicks) < 0) oldest = candidate;
            }

            session.HostSlot = oldest.Slot;
            hostChanged = true;
            return session;
        }

        /// <summary>
        /// Ends a session outright, whoever is still in it. The members have been told already; this is the
        /// server forgetting it. Their maps are untouched — a session was never where the map lived.
        /// </summary>
        public static void Close(Session session)
        {
            if (session == null) return;

            foreach (var member in session.Members) ByPlayer.Remove(member.Handle);

            session.Members.Clear();
            All.Remove(session.Id);
        }

        /// <summary>
        /// Ends every session there is. For a resource that is stopping: a session exists in this script's
        /// memory and nowhere else, so it cannot outlive it the way a stored map or a server entity does.
        ///
        /// <paramref name="tell"/> is handed the sentence to broadcast rather than a list of players,
        /// because at this point the right audience is everybody: a client that has already lost its own
        /// copy of the script is not going to be found by walking the member lists.
        /// </summary>
        public static void CloseAll(Action<string> tell)
        {
            if (All.Count == 0) return;

            if (tell != null)
                tell("The map editor's server component is restarting, so the session has ended. " +
                     "Your map is still in your editor.");

            All.Clear();
            ByPlayer.Clear();
        }

        /// <summary>Hands the session to another member. The host's own row does this.</summary>
        public static bool HandOver(Session session, int slot)
        {
            if (session.FindSlot(slot) == null) return false;
            session.HostSlot = slot;
            return true;
        }

        // --- The document ----------------------------------------------------------------------------

        /// <summary>
        /// Replaces the whole document. Only the host does this, and only when the map itself has been
        /// swapped — the session opening on the host's current map, or the host loading another one. Every
        /// hold is given back with it, since the objects they were on no longer exist.
        /// </summary>
        public static bool Push(Session session, string document, int maxObjects, out string error)
        {
            error = null;

            var parsed = Json.TryParse(document);
            if (parsed == null || parsed.Kind != JsonKind.Object)
            {
                error = "The session's map could not be read.";
                return false;
            }

            var objects = new Dictionary<int, Json>();
            var markers = new Dictionary<int, Json>();
            var removed = new Dictionary<int, Json>();

            if (!ReadTable(parsed["objects"], objects, out error)) return false;
            if (!ReadTable(parsed["markers"], markers, out error)) return false;
            if (!ReadTable(parsed["removed"], removed, out error)) return false;

            if (objects.Count + removed.Count > maxObjects)
            {
                error = string.Format(CultureInfo.InvariantCulture,
                    "That map has {0} objects; this server allows {1} in a session.",
                    objects.Count + removed.Count, maxObjects);
                return false;
            }

            session.Objects.Clear();
            session.Markers.Clear();
            session.Removed.Clear();
            session.Leases.Clear();

            foreach (var pair in objects) session.Objects[pair.Key] = pair.Value;
            foreach (var pair in markers) session.Markers[pair.Key] = pair.Value;
            foreach (var pair in removed) session.Removed[pair.Key] = pair.Value;

            session.Meta = parsed.Has("meta") ? parsed["meta"] : null;
            session.Epoch++;
            return true;
        }

        private static bool ReadTable(Json array, Dictionary<int, Json> into, out string error)
        {
            error = null;
            if (array.Kind != JsonKind.Array) return true;

            foreach (var item in array.Items)
            {
                var uid = item["u"].AsInt(0);
                var value = item["o"];
                if (uid <= 0 || value.Kind != JsonKind.Object) continue;

                if (value.ToJson().Length > MaxObjectChars)
                {
                    error = "One of the map's objects is larger than this server accepts.";
                    return false;
                }

                into[uid] = value;
            }

            return true;
        }

        /// <summary>The whole session, for somebody who has just joined it or asked to resynchronise.</summary>
        public static string DocumentJson(Session session, SessionMember you)
        {
            var held = Json.Array();
            foreach (var pair in session.Leases)
                held.Add(Json.Object().Set("u", pair.Key).Set("s", pair.Value));

            var document = Json.Object()
                .Set("room", RoomJson(session))
                .Set("you", you.Slot)
                .Set("objects", TableJson(session.Objects))
                .Set("markers", TableJson(session.Markers))
                .Set("removed", TableJson(session.Removed))
                .Set("held", held);

            if (session.Meta != null) document.Set("meta", session.Meta);
            return document.ToJson();
        }

        private static Json TableJson(Dictionary<int, Json> table)
        {
            var array = Json.Array();
            foreach (var pair in table)
                array.Add(Json.Object().Set("u", pair.Key).Set("o", pair.Value));
            return array;
        }

        /// <summary>Who is in the session and who is host. Small enough to send whole whenever it changes.</summary>
        public static Json RoomJson(Session session)
        {
            var members = Json.Array();
            foreach (var member in session.Members)
                members.Add(Json.Object()
                    .Set("slot", member.Slot)
                    .Set("name", member.Name ?? "")
                    .Set("host", member.Slot == session.HostSlot));

            return Json.Object()
                .Set("id", session.Id)
                .Set("name", session.Name ?? "")
                .Set("host", session.HostSlot)
                .Set("epoch", session.Epoch)
                .Set("members", members);
        }

        /// <summary>Every open session, for the join menu. Names and counts only; no map content.</summary>
        public static string ListJson()
        {
            var sessions = Json.Array();
            foreach (var pair in All)
            {
                var session = pair.Value;
                var host = session.Host;

                sessions.Add(Json.Object()
                    .Set("id", session.Id)
                    .Set("name", session.Name ?? "")
                    .Set("host", host == null ? "" : host.Name ?? "")
                    .Set("players", session.Members.Count)
                    .Set("objects", session.ObjectCount));
            }

            return Json.Object().Set("sessions", sessions).ToJson();
        }

        // --- Edits -----------------------------------------------------------------------------------

        /// <summary>
        /// Applies one batch of changes on behalf of <paramref name="member"/>.
        ///
        /// <paramref name="rejected"/> comes back holding the ids this member was not allowed to change,
        /// so the caller can send them what those objects actually are and let the offending client put
        /// its own copy back. That is the whole conflict story: an edit to something somebody else is
        /// holding does not fail loudly, it is simply undone on the screen it came from.
        ///
        /// Returns false only when the batch itself was malformed, which is a client that is not ours.
        /// </summary>
        public static bool ApplyOps(Session session, SessionMember member, string opsJson, int maxObjects,
            out List<int> rejected, out bool changed, out string error)
        {
            rejected = null;
            changed = false;
            error = null;

            var batch = Json.TryParse(opsJson);
            if (batch == null || batch.Kind != JsonKind.Object)
            {
                error = "The batch could not be read.";
                return false;
            }

            // The sender names its own slot so that the batch can be forwarded to everybody else exactly as
            // it arrived, with no rewriting: whoever receives it has to know whose it was to attribute it.
            if (batch["s"].AsInt(-1) != member.Slot)
            {
                error = "The batch was not signed by its sender.";
                return false;
            }

            var ops = batch["ops"];
            if (ops.Kind != JsonKind.Array) return true;
            if (ops.Count > MaxOpsPerBatch)
            {
                error = "Too many changes in one batch.";
                return false;
            }

            foreach (var op in ops.Items)
            {
                var kind = op["k"].AsString("");

                if (kind == "meta")
                {
                    // The map's name is what a save is filed under, so it is the host's alone. Silently
                    // ignored rather than rejected: a member whose editor picked up a name from a load has
                    // nothing to be told off about.
                    if (member.Slot != session.HostSlot) continue;
                    if (op["o"].Kind == JsonKind.Object)
                    {
                        session.Meta = op["o"];
                        changed = true;
                    }
                    continue;
                }

                var uid = op["u"].AsInt(0);
                if (uid <= 0) continue;

                var table = TableFor(session, kind);
                if (table == null) continue;

                // Somebody else has it in their hands. Their copy is the one that counts until they let go.
                int holder;
                if (session.Leases.TryGetValue(uid, out holder) && holder != member.Slot)
                {
                    (rejected ?? (rejected = new List<int>())).Add(uid);
                    continue;
                }

                if (kind.EndsWith("-", StringComparison.Ordinal))
                {
                    if (table.Remove(uid))
                    {
                        session.Leases.Remove(uid);
                        changed = true;
                    }
                    continue;
                }

                var value = op["o"];
                if (value.Kind != JsonKind.Object) continue;

                var isNew = !table.ContainsKey(uid);

                if (isNew)
                {
                    // An id belongs to the slot in its top half. Without this one client could overwrite
                    // another's objects by minting into their space, and a whole session's ids would stop
                    // meaning anything.
                    if ((uid >> SlotShift) != member.Slot)
                    {
                        (rejected ?? (rejected = new List<int>())).Add(uid);
                        continue;
                    }

                    if (session.ObjectCount >= maxObjects && table != session.Markers)
                    {
                        (rejected ?? (rejected = new List<int>())).Add(uid);
                        continue;
                    }
                }

                if (value.ToJson().Length > MaxObjectChars)
                {
                    (rejected ?? (rejected = new List<int>())).Add(uid);
                    continue;
                }

                table[uid] = value;
                changed = true;
            }

            return true;
        }

        /// <summary>
        /// The changes that put an offending client's copy back — one per id it was not allowed to change.
        /// An id that no longer exists at all comes back as a removal, which is the honest answer to
        /// "I moved this": somebody else deleted it while you were holding it.
        /// </summary>
        public static string CorrectionJson(Session session, List<int> uids)
        {
            var ops = Json.Array();

            foreach (var uid in uids)
            {
                var kind = KindOf(session, uid);
                Json value;

                if (kind == null)
                {
                    // Gone from every table. Sent as an object removal; ids are unique across the three, so
                    // whichever table the client has it in, it finds it by id.
                    ops.Add(Json.Object().Set("k", "-").Set("u", uid));
                    continue;
                }

                TableFor(session, kind).TryGetValue(uid, out value);
                ops.Add(Json.Object().Set("k", kind).Set("u", uid).Set("o", value));
            }

            // Signed by nobody and marked as a correction. It is not any member's edit — it is the server
            // saying what is actually there — so the client applies it like any other change and leaves it
            // out of the list of what everyone has been doing.
            return Json.Object().Set("s", -1).Set("c", true).Set("ops", ops).ToJson();
        }

        private static string KindOf(Session session, int uid)
        {
            if (session.Objects.ContainsKey(uid)) return "=";
            if (session.Markers.ContainsKey(uid)) return "m=";
            if (session.Removed.ContainsKey(uid)) return "w=";
            return null;
        }

        /// <summary>
        /// Which of the three tables a change is about. The letter before the sign is the table: nothing for
        /// a map object, <c>m</c> for a marker, <c>w</c> for one of the game's own objects the map hides.
        /// </summary>
        private static Dictionary<int, Json> TableFor(Session session, string kind)
        {
            if (string.IsNullOrEmpty(kind)) return null;

            switch (kind)
            {
                case "+":
                case "=":
                case "-":
                    return session.Objects;
                case "m+":
                case "m=":
                case "m-":
                    return session.Markers;
                case "w+":
                case "w=":
                case "w-":
                    return session.Removed;
                default:
                    return null;
            }
        }

        // --- Holds -----------------------------------------------------------------------------------

        /// <summary>
        /// Reserves every one of <paramref name="uids"/> for <paramref name="slot"/>, or none of them.
        ///
        /// All or nothing because the caller is about to drag a group as one rigid thing: half a group
        /// moving is worse than none of it, and the player can be told which object stopped them.
        /// </summary>
        public static bool Grab(Session session, int slot, List<int> uids, out string heldBy)
        {
            heldBy = null;

            foreach (var uid in uids)
            {
                int holder;
                if (!session.Leases.TryGetValue(uid, out holder) || holder == slot) continue;

                var member = session.FindSlot(holder);
                heldBy = member == null || string.IsNullOrEmpty(member.Name) ? "Someone else" : member.Name;
                return false;
            }

            foreach (var uid in uids) session.Leases[uid] = slot;
            return true;
        }

        public static List<int> Release(Session session, int slot, List<int> uids)
        {
            List<int> released = null;

            foreach (var uid in uids)
            {
                int holder;
                if (!session.Leases.TryGetValue(uid, out holder) || holder != slot) continue;

                session.Leases.Remove(uid);
                (released ?? (released = new List<int>())).Add(uid);
            }

            return released;
        }

        /// <summary>Gives back everything one slot is holding. A member leaving, or a host prising them loose.</summary>
        public static List<int> ReleaseAll(Session session, int slot)
        {
            List<int> released = null;

            foreach (var pair in session.Leases)
            {
                if (pair.Value != slot) continue;
                (released ?? (released = new List<int>())).Add(pair.Key);
            }

            if (released != null)
            {
                foreach (var uid in released) session.Leases.Remove(uid);
            }

            return released;
        }

        // --- Talking to the room ---------------------------------------------------------------------

        /// <summary>Sends one event to every member, optionally leaving out the member it came from.</summary>
        public static void Broadcast(Session session, int exceptSlot, string name, params object[] args)
        {
            if (_send == null) return;

            foreach (var member in session.Members)
            {
                if (member.Slot == exceptSlot) continue;
                if (member.Player == null) continue;

                try
                {
                    _send(member.Player, name, args);
                }
                catch (Exception e)
                {
                    Log.Error("Sessions.Broadcast", e);
                }
            }
        }

        public static void Send(SessionMember member, string name, params object[] args)
        {
            if (_send == null || member == null || member.Player == null) return;

            try
            {
                _send(member.Player, name, args);
            }
            catch (Exception e)
            {
                Log.Error("Sessions.Send", e);
            }
        }

        /// <summary>Tells everyone who is in the session and who is running it. Sent whenever either moves.</summary>
        public static void AnnounceRoom(Session session)
        {
            Broadcast(session, -1, "mapeditor:cl:coroom", RoomJson(session).ToJson());
        }

        /// <summary>How many objects a session may hold. Shares the cap a stored map has; a draft is a map.</summary>
        public static int MaxObjects
        {
            get { return API.GetConvarInt("mapeditor_max_objects", 4000); }
        }

        public static int MaxSessions
        {
            get { return API.GetConvarInt("mapeditor_max_sessions", 8); }
        }

        public static int MaxMembers
        {
            get { return API.GetConvarInt("mapeditor_max_session_players", 8); }
        }
    }
}
