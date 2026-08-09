using System.Collections.Generic;
using System.Threading.Tasks;
using CitizenFX.Core;
using MapEditor.Ui.Scaleform;
using MapEditor.Platform;

namespace MapEditor
{
    /// <summary>
    /// What came back from asking for a model: whether the game has it in memory, and the model itself when
    /// it does.
    ///
    /// The obvious shape is a Model that is null when the load failed, which is what the SP build returned.
    /// It cannot be used here. CitizenFX's Model is a class with a user-defined ==, and that operator
    /// dereferences its left operand, so <c>model == null</c> — the very check a caller writes to test for
    /// failure — throws a NullReferenceException instead of answering, in either operand order.
    /// <see cref="SmartStreaming.TryRequestModel"/> answers this with the Try shape; an async method cannot
    /// have an out parameter, so this is the same answer in a shape a Task can carry.
    /// </summary>
    public struct ModelRequest
    {
        /// <summary>Whether the game has the model in memory. Nothing else here means anything unless it does.</summary>
        public readonly bool Loaded;

        /// <summary>
        /// The loaded model. Null when <see cref="Loaded"/> is false, and a null Model is not something to
        /// compare against null — read this only after checking the flag.
        /// </summary>
        public readonly Model Model;

        private ModelRequest(bool loaded, Model model)
        {
            Loaded = loaded;
            Model = model;
        }

        /// <summary>The game does not have the model, and waiting longer would not change that.</summary>
        public static readonly ModelRequest Failed = new ModelRequest(false, null);

        public static ModelRequest Of(Model model)
        {
            return new ModelRequest(true, model);
        }
    }

    public static class ObjectPreview
    {
        private static InstructionalButtons _loadingButtons;

        /// <summary>
        /// How many frames the streamer is given before the wait is called off. The SP build's number, kept:
        /// this is a wait a player asked for and is sitting through, not a per-frame budget.
        /// </summary>
        private const int MaxFrames = 200;

        /// <summary>
        /// Hashes a failure has already been reported for. Scrolling the object list re-previews the row
        /// under the cursor, so without this a single flagged row parked under the cursor would fill the
        /// console, and a model that is not registered here goes on not being registered here for as long
        /// as the player stands there. One line per model per session is enough to tell which models are
        /// affected and why.
        /// </summary>
        private static readonly HashSet<int> Reported = new HashSet<int>();

        /// <summary>
        /// Says once, per model, why a model could not be had.
        ///
        /// There are three ways out of <see cref="LoadObject"/> without a model and they mean quite different
        /// things — a name that hashed to nothing, a model the game has never heard of, and a model the
        /// streamer simply did not get to in time — but only the middle one blacklists the hash, and none of
        /// them left a trace before this. A row reading "~r~Invalid" is the same row either way, so without
        /// this there is no way to tell a genuinely absent model from a blacklist inherited from an earlier
        /// run that "Reset Invalid Objects" would clear.
        /// </summary>
        private static void ReportOnce(int hash, string format, params object[] args)
        {
            if (!Reported.Add(hash)) return;
            Log.Info(format, args);
        }

        /// <summary>
        /// Waits for a model, showing "Loading Model" while it does.
        ///
        /// Where the SP build called Script.Yield on its own fiber and froze the whole script until the model
        /// arrived, this gives the frame back: the rest of the editor keeps running, and more than one load
        /// can be in flight at once. The shared scaleform survives that — several loads just draw it several
        /// times on the same frame.
        /// </summary>
        public static async Task<ModelRequest> LoadObject(int hash)
        {
            if (hash == 0)
            {
                ReportOnce(0, "Asked for model hash 0 — an empty entry in a map, or a list line whose name " +
                              "hashed to nothing. Not blacklisted: there is no model here to blacklist.");
                return ModelRequest.Failed;
            }

            var m = new Model(hash);

            // Not "this model does not exist" but "no archetype is registered for it here". An interior or
            // DLC .#typ is only loaded while its map data is streamed in, so the same model answers yes
            // inside the building it was made for. Nothing is remembered — the question is asked again.
            if (!ObjectDatabase.IsAvailableHere(hash))
            {
                ReportOnce(hash, "Model {0} is not registered on this client where the player is standing: " +
                                 "IS_MODEL_VALID and IS_MODEL_IN_CDIMAGE both say no. Usually an interior or " +
                                 "DLC archetype — try it near the interior it belongs to, or have the server " +
                                 "request its .ytyp (see DLC_ITYP_REQUEST in the readme).",
                    ObjectDatabase.Describe(hash));
                return ModelRequest.Failed;
            }

            if (_loadingButtons == null)
            {
                // Scaleforms are Visible = false by default and Draw() is a no-op until it's set.
                _loadingButtons = new InstructionalButtons(
                    new InstructionalButton(Translation.Translate("Loading Model"), "b_50"))
                {
                    Visible = true,
                };
                _loadingButtons.Update();
            }

            int counter = 0;
            while (!m.IsLoaded && counter < MaxFrames)
            {
                m.Request();
                await Frame.Next();
                counter++;
                _loadingButtons.Draw();
            }

            if (!m.IsLoaded)
            {
                // The streamer was busy or the model is genuinely unloadable. Either way this is not proof
                // that the model is bad, so report the failure without blacklisting it.
                ReportOnce(hash, "Model {0} is known to this client (valid={1}, inCdImage={2}) but did not " +
                                 "stream in within {3} frames. Not blacklisted — try it again.",
                    ObjectDatabase.Describe(hash), m.IsValid, m.IsInCdImage, MaxFrames);
                m.MarkAsNoLongerNeeded();
                return ModelRequest.Failed;
            }

            // None of the reasons above is permanent — the first least of all, being a statement about where
            // the player stood — so a stale "already said that" would hide the change.
            Reported.Remove(hash);
            return ModelRequest.Of(m);
        }
    }
}
