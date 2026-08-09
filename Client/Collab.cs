using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CitizenFX.Core;
using MapEditor.Platform;

namespace MapEditor
{
    /// <summary>One of the other people in the session, as far as this client can see them.</summary>
    public sealed class CollabPeer
    {
        public int Slot;
        public string Name;
        public bool IsHost;

        /// <summary>Where their camera is, or <see cref="Vector3.Zero"/> until they have said.</summary>
        public Vector3 Position;

        public float Heading;

        /// <summary>Whether they are in the freecam. Somebody on the ground is in the session but not building.</summary>
        public bool InEditor;

        /// <summary>Game-timer reading of their last position. See <see cref="Clock"/>.</summary>
        public int Seen;

        public bool HasPosition
        {
            get { return Seen != 0; }
        }
    }

    /// <summary>One open session, as the join menu sees it.</summary>
    public sealed class CollabSession
    {
        public int Id;
        public string Name;
        public string Host;
        public int Players;
        public int Objects;
    }

    /// <summary>The answer to opening or joining: the whole document, or why not.</summary>
    public sealed class CollabDocument
    {
        public string Error;
        public Json Body;
    }

    /// <summary>
    /// The client's end of a co-editing session: who is in it, who is holding what, and the wire.
    ///
    /// Nothing here touches the world. It keeps the state a session has and moves messages, and the editor
    /// — see MapEditor.Collab.cs — is what turns that into props standing in front of people. The split is
    /// the same one <see cref="MapStore"/> makes and for the same reason: everything on this side is a round
    /// trip or an event, and a menu redrawn inside a frame cannot await one.
    ///
    /// <b>What a session is.</b> One draft map, several editors. Every participant has the whole map spawned
    /// locally, exactly as they would if they had loaded it alone — nothing in a session is a network entity
    /// and nothing is replicated by the engine. What travels is the <em>document</em>, in pieces: an object
    /// added, an object changed, an object taken away. Everyone computes the same world from the same
    /// description, which is the same argument the published maps run on (see
    /// <see cref="Platform.SharedObjects"/>), applied to a map that is still being written.
    ///
    /// <b>Identity.</b> Every object in a session has an id, minted by whoever created it as
    /// <c>(slot &lt;&lt; 20) | counter</c>. Minting locally is what makes placing a prop instant: it is put
    /// down, it has a name everyone will agree on, and no round trip happened. The server checks the top
    /// half against the sender, so no client can mint into another's space.
    ///
    /// <b>Conflict.</b> Reserving an object — see <see cref="Grab"/> — is what stops two people dragging the
    /// same crate, and is taken the instant one is picked up. It is not what makes an edit legal: the server
    /// refuses an edit to something <em>somebody else</em> is holding, and sends the offender the object as
    /// it actually is, so their copy slides back. That means every path that changes a map object is covered,
    /// including the ones nobody remembered to instrument.
    /// </summary>
    public static class Collab
    {
        /// <summary>Must match Sessions.SlotShift on the server: the low bits of an id are the counter.</summary>
        private const int SlotShift = 20;

        private const int MaxCounter = (1 << SlotShift) - 1;

        /// <summary>How long since a participant last said where they are before their tag stops being drawn.</summary>
        public const int PresenceStaleMs = 6000;

        // --- State -----------------------------------------------------------------------------------

        /// <summary>Whether this server has co-editing at all, and whether this player may use it.</summary>
        public static bool Available { get; private set; }

        public static bool Active { get; private set; }

        public static int SessionId { get; private set; }

        public static string SessionName { get; private set; }

        public static int MySlot { get; private set; }

        public static bool IsHost { get; private set; }

        /// <summary>Everyone else in the session, by slot. This player is not in it.</summary>
        public static readonly Dictionary<int, CollabPeer> Peers = new Dictionary<int, CollabPeer>();

        /// <summary>Which slot is holding each reserved object, this player's own holds included.</summary>
        public static readonly Dictionary<int, int> Held = new Dictionary<int, int>();

        /// <summary>What <see cref="Refresh"/> last found. The join menu is redrawn from this.</summary>
        public static readonly List<CollabSession> OpenSessions = new List<CollabSession>();

        /// <summary>
        /// Batches of somebody else's changes, waiting for the editor to put them into the world. A queue
        /// rather than an event, because applying one spawns models and therefore spans frames.
        /// </summary>
        public static readonly Queue<Json> Inbox = new Queue<Json>();

        /// <summary>
        /// The latest drag from each participant, by slot. Only the latest matters — this is where something
        /// is right now, and a message that has been overtaken describes where it was.
        /// </summary>
        public static readonly Dictionary<int, Json> Drags = new Dictionary<int, Json>();

        /// <summary>Set when the map underneath the session has been replaced and has to be fetched again.</summary>
        public static bool ResetPending;

        /// <summary>Why the session ended, for the editor to say once. Null when it has been said.</summary>
        public static string EndedReason;

        /// <summary>Bumped whenever the participant list or the host changes, so menus know to redraw.</summary>
        public static int RoomVersion { get; private set; }

        private static int _counter;

        // --- Binding ---------------------------------------------------------------------------------

        /// <summary>
        /// Binds the session's inbound events. Called once from the client script's constructor, beside
        /// <see cref="Rpc.Bind"/>: the handler table belongs to the script, and a message that arrives
        /// before the editor exists must not be lost.
        /// </summary>
        public static void Bind(EventHandlerDictionary handlers)
        {
            if (handlers == null) throw new ArgumentNullException("handlers");

            handlers["mapeditor:cl:coroom"] += new Action<string>(OnRoom);
            handlers["mapeditor:cl:coops"] += new Action<string>(OnOps);
            handlers["mapeditor:cl:coheld"] += new Action<int, string, bool>(OnHeld);
            handlers["mapeditor:cl:codrag"] += new Action<int, string>(OnDrag);
            handlers["mapeditor:cl:cohere"] += new Action<int, string>(OnHere);
            handlers["mapeditor:cl:coreset"] += new Action(OnReset);
            handlers["mapeditor:cl:cobye"] += new Action<string>(OnBye);
        }

        /// <summary>Told by the handshake whether the server has a session half and whether this player may use it.</summary>
        public static void SetAvailable(bool available)
        {
            Available = available;
        }

        // --- Ids -------------------------------------------------------------------------------------

        /// <summary>
        /// The next id for an object this client is about to create. Zero when there is nothing to mint
        /// into — no session, or a counter that has run out, which takes a million objects from one player
        /// in one session and is a session that ought to have been saved long ago.
        /// </summary>
        public static int NextUid()
        {
            if (!Active || _counter >= MaxCounter) return 0;
            return (MySlot << SlotShift) | (++_counter);
        }

        /// <summary>Who is holding this object, or -1. This player's own slot counts as holding it.</summary>
        public static int HolderOf(int uid)
        {
            int slot;
            return uid != 0 && Held.TryGetValue(uid, out slot) ? slot : -1;
        }

        /// <summary>Whether somebody other than this player has reserved the object.</summary>
        public static bool HeldByOther(int uid)
        {
            var holder = HolderOf(uid);
            return holder != -1 && holder != MySlot;
        }

        /// <summary>The name of whoever is holding it, for a message that has to say who.</summary>
        public static string HolderName(int uid)
        {
            var holder = HolderOf(uid);
            if (holder == -1 || holder == MySlot) return null;

            CollabPeer peer;
            return Peers.TryGetValue(holder, out peer) ? peer.Name : "Someone else";
        }

        // --- Opening, joining, leaving ---------------------------------------------------------------

        /// <summary>
        /// Opens a session and becomes its host. It starts empty; the caller pushes the map it is about
        /// straight afterwards with <see cref="Push"/>.
        /// </summary>
        public static async Task<CollabDocument> Open(string name)
        {
            Inbox.Clear();
            var response = await Rpc.Call("cohost", name ?? "");
            return Enter(response);
        }

        /// <summary>
        /// Joins a session, or — for a client that is already in one — asks for the whole document again.
        /// The two are the same request on purpose: resynchronising is the path a joiner already exercises
        /// every time, rather than a rescue route that is only ever run when something has gone wrong.
        /// </summary>
        public static async Task<CollabDocument> Join(int id)
        {
            // Emptied before the request goes out, never after the answer comes back. The server builds the
            // document at the moment it is asked, so a change that arrives afterwards belongs on top of it
            // and has to survive; one that arrived before it is in the document already. Clearing on the
            // way back would drop exactly the messages that matter.
            Inbox.Clear();
            Drags.Clear();

            var response = await Rpc.Call("cojoin", id);
            return Enter(response);
        }

        private static CollabDocument Enter(Rpc.Response response)
        {
            if (!response.Ok) return new CollabDocument { Error = response.Message };

            var body = response.Json();
            if (body == null) return new CollabDocument { Error = "The session's map could not be read." };

            var room = body["room"];
            if (room.Kind != JsonKind.Object)
                return new CollabDocument { Error = "The session's map could not be read." };

            // Everything about the previous session goes, including the counter: ids are minted per session
            // and a counter carried over would keep a slot's ids unique for no reason and confuse nothing
            // but a reader of the log. Not the inbox, though — see Join.
            Peers.Clear();
            Held.Clear();
            ResetPending = false;
            IsHost = false;

            MySlot = body["you"].AsInt(0);
            _counter = 0;
            Active = true;

            ReadRoom(room);

            foreach (var item in body["held"].Items)
            {
                var uid = item["u"].AsInt(0);
                if (uid > 0) Held[uid] = item["s"].AsInt(-1);
            }

            return new CollabDocument { Body = body };
        }

        /// <summary>
        /// Hands the server the whole map the session is about. Only the host may, and only when the map
        /// has been replaced rather than edited — the session opening, or the host loading another map.
        /// Everyone else is told to fetch it again; see <see cref="ResetPending"/>.
        /// </summary>
        public static async Task<string> Push(string document)
        {
            if (!Active) return "You are not in a session.";

            var response = await Rpc.Upload("copush", document);
            return response.Ok ? null : response.Message;
        }

        /// <summary>
        /// Closes the session for everybody. The host's row, and what publishing does before it hands the
        /// map to the server: a published map stands in every player's world, and a session still editing a
        /// second copy of it is two answers to the same question.
        /// </summary>
        public static async Task<string> End(string why)
        {
            if (!Active) return null;

            var response = await Rpc.Call("coend", why ?? "");
            if (!response.Ok) return response.Message;

            // The server's own goodbye reaches this client too and does exactly this; done here as well so
            // that whatever the caller goes on to do — publishing, usually — is already out of the session.
            Clear();
            Active = false;
            EndedReason = null;
            return null;
        }

        /// <summary>
        /// Says goodbye without waiting to be heard. For the resource stopping, where there is no next frame
        /// to receive an answer in and the ordinary path would hang on its own timeout.
        /// </summary>
        public static void LeaveQuietly()
        {
            if (!Active) return;

            // Request zero: the handler answers it and nothing is listening, which is exactly the point.
            Rpc.Fire("coleave", 0);
            Clear();
            Active = false;
        }

        public static async Task<string> Leave()
        {
            if (!Active) return null;

            var response = await Rpc.Call("coleave");
            Clear();
            Active = false;
            return response.Ok ? null : response.Message;
        }

        /// <summary>Hands the session to another participant.</summary>
        public static async Task<string> HandOver(int slot)
        {
            var response = await Rpc.Call("cohand", slot);
            return response.Ok ? null : response.Message;
        }

        /// <summary>Prises loose everything one participant is holding. The host's row; see the server.</summary>
        public static async Task<string> Free(int slot)
        {
            var response = await Rpc.Call("cofree", slot);
            return response.Ok ? null : response.Message;
        }

        /// <summary>Fills <see cref="Open"/> with the sessions running on this server.</summary>
        public static async Task<string> Refresh()
        {
            if (!Available) return "This server does not run co-editing.";

            var response = await Rpc.Call("colist");
            if (!response.Ok) return response.Message;

            var body = response.Json();
            if (body == null) return "The server's answer could not be read.";

            OpenSessions.Clear();
            foreach (var item in body["sessions"].Items)
            {
                OpenSessions.Add(new CollabSession
                {
                    Id = item["id"].AsInt(0),
                    Name = item["name"].AsString(""),
                    Host = item["host"].AsString(""),
                    Players = item["players"].AsInt(0),
                    Objects = item["objects"].AsInt(0),
                });
            }

            return null;
        }

        // --- Reservations ----------------------------------------------------------------------------

        /// <summary>
        /// Asks for every one of these objects, or none of them. Returns null when they are this player's,
        /// or the reason they are not.
        ///
        /// The caller does not wait for this before starting to drag: the reservation is what stops the
        /// second person, and the first one should not pay a round trip for being first. If the answer is
        /// no, whatever was picked up is put down again and the server's own copy slides it back.
        /// </summary>
        public static async Task<string> Grab(List<int> uids)
        {
            if (!Active || uids == null || uids.Count == 0) return null;

            var response = await Rpc.Call("cograb", UidsJson(uids));
            if (response.Ok)
            {
                // Recorded here as well as from the broadcast, so that the very next frame already knows
                // these are ours and does not ask again.
                foreach (var uid in uids) Held[uid] = MySlot;
                return null;
            }

            return response.Message;
        }

        public static void Drop(List<int> uids)
        {
            if (!Active || uids == null || uids.Count == 0) return;

            foreach (var uid in uids)
            {
                int slot;
                if (Held.TryGetValue(uid, out slot) && slot == MySlot) Held.Remove(uid);
            }

            Rpc.Fire("codrop", UidsJson(uids));
        }

        private static string UidsJson(List<int> uids)
        {
            var array = Json.Array();
            foreach (var uid in uids) array.Add(Json.Of(uid));
            return array.ToJson();
        }

        // --- Sending -------------------------------------------------------------------------------

        /// <summary>
        /// Sends a batch of changes. Fire-and-forget: a batch that goes missing is put right by the next
        /// pass of the sender's own change detector, which compares the whole map against what the session
        /// was last told, so nothing depends on any one message arriving.
        /// </summary>
        public static void SendOps(Json ops)
        {
            if (!Active || ops == null || ops.Count == 0) return;

            // Signed, so the server can pass the batch on to everybody else exactly as it arrived and each
            // of them knows whose it was without the server rewriting anything.
            Rpc.Fire("coops", Json.Object().Set("s", MySlot).Set("ops", ops).ToJson());
        }

        public static void SendDrag(Json drag)
        {
            if (!Active || drag == null || drag.Count == 0) return;
            Rpc.Fire("codrag", drag.ToJson());
        }

        public static void SendHere(Vector3 position, float heading, bool inEditor)
        {
            if (!Active) return;

            Rpc.Fire("cohere", Json.Object()
                .Set("x", position.X)
                .Set("y", position.Y)
                .Set("z", position.Z)
                .Set("h", heading)
                .Set("e", inEditor)
                .ToJson());
        }

        // --- Incoming --------------------------------------------------------------------------------

        private static void OnRoom(string json)
        {
            var room = Json.TryParse(json);
            if (room == null || room.Kind != JsonKind.Object) return;
            if (!Active || room["id"].AsInt(0) != SessionId) return;

            ReadRoom(room);
        }

        private static void ReadRoom(Json room)
        {
            SessionId = room["id"].AsInt(0);
            SessionName = room["name"].AsString("");

            var hostSlot = room["host"].AsInt(-1);
            IsHost = hostSlot == MySlot;

            // Rebuilt rather than merged: the list is the whole truth about who is here, and a member who
            // left has to disappear from it. Positions are carried over — presence arrives on its own
            // channel three times a second, and a tag that blinked out on every join would be worse than
            // one that is a third of a second stale.
            var previous = new Dictionary<int, CollabPeer>(Peers);
            Peers.Clear();

            foreach (var item in room["members"].Items)
            {
                var slot = item["slot"].AsInt(-1);
                if (slot < 0 || slot == MySlot) continue;

                CollabPeer peer;
                if (!previous.TryGetValue(slot, out peer)) peer = new CollabPeer { Slot = slot };

                peer.Name = item["name"].AsString("");
                peer.IsHost = slot == hostSlot;
                Peers[slot] = peer;
            }

            // Anything held by somebody who is no longer here is nobody's. The server has already let those
            // go; this is the same conclusion reached locally, so that a menu drawn before the next message
            // does not show a name that has left.
            List<int> orphaned = null;
            foreach (var pair in Held)
            {
                if (pair.Value == MySlot || Peers.ContainsKey(pair.Value)) continue;
                (orphaned ?? (orphaned = new List<int>())).Add(pair.Key);
            }

            if (orphaned != null)
            {
                foreach (var uid in orphaned) Held.Remove(uid);
            }

            RoomVersion++;
        }

        private static void OnOps(string json)
        {
            if (!Active) return;

            var batch = Json.TryParse(json);
            if (batch == null || batch.Kind != JsonKind.Object) return;

            Inbox.Enqueue(batch);
        }

        private static void OnHeld(int slot, string uidsJson, bool held)
        {
            if (!Active) return;

            var array = Json.TryParse(uidsJson);
            if (array == null || array.Kind != JsonKind.Array) return;

            foreach (var item in array.Items)
            {
                var uid = item.AsInt(0);
                if (uid <= 0) continue;

                if (held) Held[uid] = slot;
                else if (HolderOf(uid) == slot) Held.Remove(uid);
            }
        }

        private static void OnDrag(int slot, string json)
        {
            if (!Active || slot == MySlot) return;

            var drag = Json.TryParse(json);
            if (drag == null || drag.Kind != JsonKind.Object) return;

            Drags[slot] = drag;
        }

        private static void OnHere(int slot, string json)
        {
            if (!Active || slot == MySlot) return;

            var where = Json.TryParse(json);
            if (where == null || where.Kind != JsonKind.Object) return;

            CollabPeer peer;
            if (!Peers.TryGetValue(slot, out peer))
            {
                // A participant whose room message has not arrived yet. Kept anyway: the position is worth
                // more than the moment it was learnt, and the name lands a fraction of a second later.
                peer = new CollabPeer { Slot = slot, Name = "" };
                Peers[slot] = peer;
            }

            peer.Position = new Vector3(where["x"].AsFloat(0f), where["y"].AsFloat(0f), where["z"].AsFloat(0f));
            peer.Heading = where["h"].AsFloat(0f);
            peer.InEditor = where["e"].AsBool(false);
            peer.Seen = Clock.Milliseconds;
        }

        private static void OnReset()
        {
            if (!Active) return;
            ResetPending = true;
        }

        private static void OnBye(string why)
        {
            if (!Active) return;

            EndedReason = string.IsNullOrEmpty(why) ? "The session has ended." : why;
            Clear();
            Active = false;
        }

        /// <summary>
        /// Forgets everything about the session that was. Called when one ends and when another begins;
        /// the editor's own copy of the map is not touched, because it is the player's work either way.
        /// </summary>
        public static void Clear()
        {
            Peers.Clear();
            Held.Clear();
            Drags.Clear();
            Inbox.Clear();
            ResetPending = false;
            IsHost = false;
            SessionId = 0;
            SessionName = null;
        }
    }
}
