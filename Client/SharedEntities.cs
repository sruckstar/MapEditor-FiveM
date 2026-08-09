using System;
using System.Collections.Generic;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using MapEditor.Platform;

namespace MapEditor
{
    /// <summary>
    /// The client half of the server's entities: finishing off what the server created but could not
    /// configure.
    ///
    /// A published map's cars, armed peds and walking peds are one entity for the whole session, created by
    /// the server (see Server/LiveEntities.cs) rather than by every client for itself. The server can place
    /// such an entity and freeze it, and there its natives run out: a task, a relationship group, a livery,
    /// a siren, a piece of clothing and a quaternion rotation are all client-side and always have been. So
    /// for each entity it makes, the server asks whichever client currently owns it to apply the rest, and
    /// hands over the object exactly as the map file holds it.
    ///
    /// <b>Why the answer takes a few frames.</b> The request arrives with a network id, and a network id is
    /// not an entity until the entity has actually streamed in on this machine — which is usually a frame or
    /// two after the server created it, and occasionally never, if the player turned round and drove off.
    /// And a task only takes on the machine that has control, which may already have moved on. So each
    /// request waits here, is retried for a while, and is dropped if it never becomes answerable; the server
    /// asks again, of whoever owns the thing by then.
    ///
    /// Nothing here spawns or deletes anything. Existence is the server's decision and only the server's —
    /// which is the whole point of moving these objects there.
    /// </summary>
    public static class SharedEntities
    {
        /// <summary>One entity the server is waiting to hear about.</summary>
        private sealed class Pending
        {
            public int NetId;
            public MapObject Object;

            /// <summary>Game-timer reading when the request arrived. See <see cref="GiveUpMs"/>.</summary>
            public int Since;

            /// <summary>Whether control has already been asked for once, so it is asked for once.</summary>
            public bool Requested;

            /// <summary>
            /// Whether the configuration has been applied. It is applied once and only once — half of it is
            /// tasks, and starting a ped's scenario again every frame is not a retry, it is a ped that never
            /// gets past the first frame of it. What is chased instead is the one part that can be seen to
            /// have failed; see <see cref="Tick"/>.
            /// </summary>
            public bool Applied;

            /// <summary>Game-timer reading of the last livery attempt, so it is chased at a sane rate.</summary>
            public int Tried;
        }

        private static readonly List<Pending> Waiting = new List<Pending>();

        /// <summary>
        /// How long one request is kept before it is dropped. Long enough to cover a model streaming in and
        /// ownership settling, short enough that a player who drove away leaves nothing behind. The server
        /// re-asks on its own schedule, so dropping one here costs a few seconds and not the configuration.
        /// </summary>
        private const int GiveUpMs = 15000;

        /// <summary>How often a livery that has not gone on is asked for again. See <see cref="Tick"/>.</summary>
        private const int LiveryAgainMs = 250;

        /// <summary>
        /// Binds the server's request. Called once from the client script's constructor, beside the others,
        /// because the handler table belongs to the script.
        /// </summary>
        public static void Bind(EventHandlerDictionary handlers)
        {
            if (handlers == null) throw new ArgumentNullException("handlers");

            handlers["mapeditor:cl:configure"] += new Action<int, string>(OnConfigure);
        }

        private static void OnConfigure(int netId, string document)
        {
            if (netId == 0) return;

            var o = MapSerializer.ObjectFromJsonText(document);
            if (o == null)
            {
                Log.Info("The server asked for entity {0} to be configured, but its object could not be read.", netId);
                return;
            }

            // A repeated ask replaces the old one rather than queueing behind it: the server re-asks because
            // it has heard nothing, and the newest description is the one to act on.
            for (var i = Waiting.Count - 1; i >= 0; i--)
            {
                if (Waiting[i].NetId == netId) Waiting.RemoveAt(i);
            }

            Waiting.Add(new Pending
            {
                NetId = netId,
                Object = o,
                Since = Clock.Milliseconds,
            });
        }

        /// <summary>
        /// Carries each waiting entity one step further towards what the server asked for. Called every
        /// frame; it costs nothing at all in the ordinary case, because the list is empty except in the
        /// seconds after a player walks up to part of a published map.
        ///
        /// An entry leaves this list when the work is done and answered for, or when it ages out. The
        /// configuration itself is applied once — see <see cref="Pending.Applied"/> — and the only thing
        /// after that is the livery, which is chased because it can be seen not to have taken.
        /// </summary>
        public static void Tick()
        {
            if (Waiting.Count == 0) return;

            for (var i = Waiting.Count - 1; i >= 0; i--)
            {
                var pending = Waiting[i];

                if (Clock.Since(pending.Since) > GiveUpMs)
                {
                    // Not an error: the entity never came into scope here, or somebody else took control.
                    // The server is still holding it and will ask again, of whoever owns it then.
                    Waiting.RemoveAt(i);
                    continue;
                }

                var handle = API.NetworkGetEntityFromNetworkId(pending.NetId);
                if (handle == 0 || !API.DoesEntityExist(handle)) continue;

                if (!API.NetworkHasControlOfEntity(handle))
                {
                    // Asked for once. Control is granted asynchronously and asking every frame is how a
                    // script ends up fighting the engine for something it is about to be given anyway.
                    if (!pending.Requested)
                    {
                        pending.Requested = true;
                        API.NetworkRequestControlOfEntity(handle);
                    }
                    continue;
                }

                if (!pending.Applied)
                {
                    // Marked before it is attempted: a configuration that threw will not come right by being
                    // run four hundred more times, and the exception is in the console where it can be read.
                    pending.Applied = true;

                    try
                    {
                        AutoloadedMaps.Configure(Entity.FromHandle(handle), pending.Object);
                    }
                    catch (Exception e)
                    {
                        Log.Error("SharedEntities.Configure", e);
                    }
                }

                // The livery is the one piece of this that can be seen to have failed, and it fails at
                // exactly the moment this runs: on a vehicle that has this instant arrived over the network
                // and is not yet in a state to take it. So it is asked for again until the vehicle admits to
                // wearing it, or until the request ages out above with everything else. At a rate rather
                // than every frame — a mod kit goes on with it, and a vehicle that is going to accept it
                // does so long before a quarter of a second is up.
                if (Clock.Since(pending.Tried) < LiveryAgainMs) continue;
                pending.Tried = Clock.Milliseconds;

                if (!AutoloadedMaps.ApplyLivery(handle, pending.Object)) continue;

                Waiting.RemoveAt(i);
                Rpc.Fire("configured", pending.NetId);
            }
        }

        /// <summary>Drops everything outstanding. For a resource that is stopping.</summary>
        public static void Clear()
        {
            Waiting.Clear();
        }
    }
}
