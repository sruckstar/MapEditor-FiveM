using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using CitizenFX.Core;

namespace MapEditor.Platform
{
    /// <summary>
    /// Request and answer over FiveM events.
    ///
    /// The server holds the map storage and events are the only way to reach it — they are one-way, have no
    /// return value, and give no ordering guarantee between two different ones. So every request carries an
    /// id, the server quotes it back, and the caller awaits the id rather than the event.
    ///
    /// Anything of size arrives in pieces on <c>mapeditor:cl:blob</c> and is put back together here, so a
    /// caller sees one string. See MapEditorServer for the protocol as a whole.
    ///
    /// Every call answers, including the ones that go nowhere: a request that has not been replied to
    /// within <see cref="TimeoutMs"/> comes back as a failure. That is what a resource whose server half is
    /// missing looks like — an older install, or an owner who copied only the client folder — and the
    /// editor has to carry on with local maps rather than hang on the first menu that asks.
    /// </summary>
    public static class Rpc
    {
        /// <summary>Must match MapEditorServer.ChunkSize.</summary>
        public const int ChunkSize = 32 * 1024;

        /// <summary>
        /// Generous: this covers a multi-megabyte map arriving on a bad connection, and nothing is blocked
        /// waiting for it except the one menu action that asked.
        /// </summary>
        private const int TimeoutMs = 20000;

        /// <summary>
        /// What the answer to a request was. <see cref="Payload"/> is whatever came in on the blob channel,
        /// which is empty for the calls that only succeed or fail.
        /// </summary>
        public sealed class Response
        {
            public Response(bool ok, string message, string payload)
            {
                Ok = ok;
                Message = message ?? "";
                Payload = payload ?? "";
            }

            public readonly bool Ok;

            /// <summary>Why not, when <see cref="Ok"/> is false. Written for the player to read.</summary>
            public readonly string Message;

            public readonly string Payload;

            public Json Json()
            {
                return Platform.Json.TryParse(Payload);
            }
        }

        private sealed class Pending
        {
            public TaskCompletionSource<Response> Completion;
            public StringBuilder Blob;
            public int NextChunk;
        }

        private static readonly Dictionary<int, Pending> Requests = new Dictionary<int, Pending>();

        private static Action<string, object[]> _trigger;
        private static int _nextId;

        /// <summary>
        /// Called once from the client script's constructor. TriggerServerEvent is a member of BaseScript
        /// and cannot be reached from a static class, so the script hands its own over — the same
        /// arrangement <see cref="Frame.Bind"/> uses for Delay.
        /// </summary>
        public static void Bind(EventHandlerDictionary handlers, Action<string, object[]> triggerServerEvent)
        {
            if (handlers == null) throw new ArgumentNullException("handlers");
            if (triggerServerEvent == null) throw new ArgumentNullException("triggerServerEvent");

            _trigger = triggerServerEvent;

            handlers["mapeditor:cl:blob"] += new Action<int, int, int, string>(OnBlob);
            handlers["mapeditor:cl:reply"] += new Action<int, bool, string>(OnReply);
        }

        /// <summary>Whether <see cref="Bind"/> has run. False in the serializer test harness and nowhere else.</summary>
        public static bool Ready
        {
            get { return _trigger != null; }
        }

        /// <summary>
        /// Sends one event and expects nothing back: no request id, no timeout, no waiting. Only
        /// <see cref="Boot"/> uses it, and it uses it from places that must not be able to fail — a
        /// breadcrumb that threw would take down the startup it was sent to explain.
        /// </summary>
        public static void Fire(string verb, params object[] args)
        {
            if (!Ready) return;

            try
            {
                _trigger("mapeditor:sv:" + verb, args);
            }
            catch
            {
                // Deliberately silent: see above.
            }
        }

        /// <summary>
        /// Sends one request and waits for its answer.
        ///
        /// <paramref name="verb"/> is the part after <c>mapeditor:sv:</c>; the request id is prepended to
        /// <paramref name="args"/>, because every handler on the other side takes it first.
        /// </summary>
        public static Task<Response> Call(string verb, params object[] args)
        {
            return Call(TimeoutMs, verb, args);
        }

        /// <summary>
        /// <see cref="Call"/> with a deadline of its own. Only the handshake uses it: that one is waited on
        /// before the editor can be opened, and on a server with no Map Editor on it — an older install, or
        /// an owner who copied only the client folder — the full timeout is dead air the player spends
        /// looking at a resource that has not started.
        /// </summary>
        public static Task<Response> Call(int timeoutMs, string verb, params object[] args)
        {
            if (!Ready) return Failed("The map editor's server component is not running.");

            var id = NextId();
            var pending = Track(id);

            var full = new object[args.Length + 1];
            full[0] = id;
            Array.Copy(args, 0, full, 1, args.Length);

            try
            {
                _trigger("mapeditor:sv:" + verb, full);
            }
            catch (Exception e)
            {
                Requests.Remove(id);
                Log.Error("Rpc.Call(" + verb + ")", e);
                return Failed("The map editor could not reach the server.");
            }

            WatchForTimeout(id, timeoutMs);
            return pending.Completion.Task;
        }

        /// <summary>
        /// Sends a map to the server, in <see cref="ChunkSize"/> pieces on one request id.
        ///
        /// The frame is handed back between pieces: a two-megabyte map is sixty-four events, and firing
        /// them in one go is a visible stutter for no gain — the server assembles them in order either way.
        /// </summary>
        public static async Task<Response> Upload(string verb, string content, params object[] leadingArgs)
        {
            if (!Ready) return new Response(false, "The map editor's server component is not running.", null);
            if (content == null) content = "";

            var id = NextId();
            var pending = Track(id);

            var total = Math.Max(1, (content.Length + ChunkSize - 1) / ChunkSize);
            WatchForTimeout(id, TimeoutMs);

            try
            {
                for (var i = 0; i < total; i++)
                {
                    // The server answers the moment it refuses one — no permission, too large, too often —
                    // and the rest of the map would be shouting at a handler that is no longer listening.
                    if (pending.Completion.Task.IsCompleted) break;

                    var at = i * ChunkSize;
                    var chunk = content.Substring(at, Math.Min(ChunkSize, content.Length - at));

                    var full = new object[leadingArgs.Length + 4];
                    full[0] = id;
                    Array.Copy(leadingArgs, 0, full, 1, leadingArgs.Length);
                    full[leadingArgs.Length + 1] = i;
                    full[leadingArgs.Length + 2] = total;
                    full[leadingArgs.Length + 3] = chunk;

                    _trigger("mapeditor:sv:" + verb, full);

                    if (i + 1 < total) await Frame.Next();
                }
            }
            catch (Exception e)
            {
                Requests.Remove(id);
                Log.Error("Rpc.Upload(" + verb + ")", e);
                return new Response(false, "The map editor could not reach the server.", null);
            }

            return await pending.Completion.Task;
        }

        // --- Incoming --------------------------------------------------------------------------------

        private static void OnBlob(int id, int index, int total, string chunk)
        {
            Pending pending;
            if (!Requests.TryGetValue(id, out pending)) return;

            // Out of order means something other than our server sent it, or a reply already given up on.
            // Either way the assembled document would be wrong, and a wrong map is worse than none.
            if (index != pending.NextChunk)
            {
                Log.Info("Discarding request {0}: chunk {1} arrived where {2} was expected.", id, index, pending.NextChunk);
                Requests.Remove(id);
                pending.Completion.TrySetResult(new Response(false, "The map arrived damaged.", null));
                return;
            }

            if (pending.Blob == null) pending.Blob = new StringBuilder(Math.Min(total, 64) * ChunkSize);
            pending.Blob.Append(chunk);
            pending.NextChunk++;
        }

        private static void OnReply(int id, bool ok, string message)
        {
            Pending pending;
            if (!Requests.TryGetValue(id, out pending)) return;

            Requests.Remove(id);
            pending.Completion.TrySetResult(new Response(ok, message,
                pending.Blob == null ? null : pending.Blob.ToString()));
        }

        // --- Plumbing --------------------------------------------------------------------------------

        private static int NextId()
        {
            // Wraps rather than grows: ids only have to be unique among the handful in flight, and the
            // server quotes back whatever it was given.
            if (_nextId == int.MaxValue) _nextId = 0;
            return ++_nextId;
        }

        private static Pending Track(int id)
        {
            var pending = new Pending { Completion = new TaskCompletionSource<Response>() };
            Requests[id] = pending;
            return pending;
        }

        private static void WatchForTimeout(int id, int milliseconds)
        {
            Log.Guard("Rpc timeout", async () =>
            {
                await Frame.Wait(milliseconds);

                Pending pending;
                if (!Requests.TryGetValue(id, out pending)) return;

                Requests.Remove(id);
                pending.Completion.TrySetResult(new Response(false, "The server did not answer.", null));
            });
        }

        private static Task<Response> Failed(string message)
        {
            var completion = new TaskCompletionSource<Response>();
            completion.SetResult(new Response(false, message, null));
            return completion.Task;
        }
    }
}
