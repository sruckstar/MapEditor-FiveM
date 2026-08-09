using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using CitizenFX.Core;

namespace MapEditor.Platform
{
    /// <summary>
    /// The natives this resource calls from Lua instead of from C#, and the reason it has to.
    ///
    /// Both of them hand their answer back through a Vector3 pointer, and on the FiveM Enhanced client
    /// that is a shape the C# runtime cannot push. <c>CitizenFX.Base.NativeApi.PushArg</c> — the one place every
    /// native argument goes through — accepts strings, byte arrays, ints, uints, floats and bools, and
    /// throws <c>Exception("Unsupported type " + name)</c> for anything else. The v1 API assemblies
    /// Enhanced ships in <c>coreclr/fallback</c> hand it a Vector3 for every out-parameter of that type,
    /// so the runtime's own generated wrapper throws before the native is reached:
    ///
    /// <code>
    /// System.Exception: Unsupported type Vector3
    ///   at CitizenFX.Base.NativeApi.PushArg[T](T arg)
    ///   at CitizenFX.Core.Native.API.GetShapeTestResult(Int32, Boolean&amp;, Vector3&amp;, Vector3&amp;, Int32&amp;)
    ///   at CitizenFX.Core.World.Raycast(Vector3, Vector3, IntersectOptions, Entity)
    /// </code>
    ///
    /// Nothing on our side can be written differently to avoid it, and no other C# route reaches those
    /// natives: results of pointer arguments are read back through <c>NativeApi.GetRes*</c>, which
    /// belongs to the runtime and is not reachable from a script, so <c>Function.Call</c> cannot collect
    /// them either — it only ever hands back result 0, the return value. <c>OutputArgument</c>, which is
    /// how the SP build and every FiveM sample write this, is absent from the Enhanced assemblies
    /// entirely. Lua's native invoker has handled pointer arguments since the beginning, so that is where
    /// these two calls now live: <c>resource/client/bridge.lua</c>.
    ///
    /// The same code runs on the legacy client, where the C# path works. One path rather than two on
    /// purpose: a difference that only shows up on one of the two clients is a difference that gets found
    /// by a player, and the cost here is a handful of microseconds on calls that are made once per frame
    /// at most.
    ///
    /// <c>tools/sandbox-audit</c> now reads <c>PushArg</c>'s IL and reports any call that would land in
    /// the same trap, so the next one of these is found before it ships rather than from a screenshot.
    /// </summary>
    public static class LuaBridge
    {
        /// <summary>Must match the event bridge.lua listens on.</summary>
        private const string BindEvent = "mapeditor:internal:bindNatives";

        /// <summary>Where the other half lives inside the resource, as fxmanifest.lua names it.</summary>
        private const string BridgeFile = "client/bridge.lua";

        /// <summary>The shape test status that means the probe has an answer. 0 is failed, 1 is pending.</summary>
        private const int ShapeTestReady = 2;

        /// <summary>
        /// How many times binding is attempted before it is written off. There is nothing to wait for —
        /// both scripts are loaded by the time anything asks for a raycast — so more than a couple of
        /// tries would only mean an event per frame forever on an install that is missing bridge.lua.
        /// </summary>
        private const int MaxBindAttempts = 3;

        private static CallbackDelegate _probe;
        private static CallbackDelegate _modelDimensions;
        private static int _bindAttempts;
        private static bool _saidAnswerWasWrong;

        /// <summary>
        /// What the setter was handed on the last attempt, kept only to be named in the failure message.
        /// "The event reached bridge.lua and it called back with the wrong thing" and "nothing was
        /// listening" are the same silence from here, and they are fixed in different places.
        /// </summary>
        private static bool _setterRan;
        private static string _whatArrived;

        /// <summary>What a shape test came back with.</summary>
        public struct ProbeResult
        {
            /// <summary>
            /// Whether the probe answered at all. False means no answer rather than nothing in the way,
            /// and the difference matters to the caller that drops the player onto the ground: "nothing
            /// below" would leave them hanging in the air.
            /// </summary>
            public bool Answered;

            public bool DidHit;

            /// <summary>Where the ray met the world. Only meaningful when <see cref="DidHit"/>.</summary>
            public Vector3 Position;

            /// <summary>The entity the ray met, or 0 for the map itself.</summary>
            public int EntityHandle;
        }

        private struct Box
        {
            public Vector3 Min;
            public Vector3 Max;
        }

        /// <summary>Model bounding boxes by model hash. See <see cref="ModelDimensions"/>.</summary>
        private static readonly Dictionary<int, Box> Boxes = new Dictionary<int, Box>();

        /// <summary>
        /// Fires a ray from <paramref name="from"/> to <paramref name="to"/> and reports what it met.
        ///
        /// A probe that could not be made at all comes back as <see cref="ProbeResult"/>'s default, which
        /// reads as "no answer, nothing hit" — every caller already has to handle that case, because a
        /// ray into open sky is the ordinary version of it.
        /// </summary>
        public static ProbeResult Probe(Vector3 from, Vector3 to, IntersectOptions flags, Entity toIgnore)
        {
            var result = new ProbeResult();
            if (!Bind()) return result;

            // Entity.Equals starts with a reference check, so == null is safe on a CitizenFX entity —
            // unlike Model, where it is not.
            var ignore = toIgnore == null ? 0 : toIgnore.Handle;

            float[] fields;
            if (!Answer(_probe, "probe", 6, out fields,
                    from.X, from.Y, from.Z, to.X, to.Y, to.Z, (int)flags, ignore))
                return result;

            result.Answered = (int)fields[0] == ShapeTestReady;
            result.DidHit = fields[1] != 0f;
            result.Position = new Vector3(fields[2], fields[3], fields[4]);
            result.EntityHandle = (int)fields[5];
            return result;
        }

        /// <summary>
        /// A model's bounding box, in model space. False when the box could not be had.
        ///
        /// Cached, and cached for good: the box is a property of the model file and cannot change, while
        /// the call behind it is the expensive one in this file — the five places that ask for it ask on
        /// every frame, for the entity under the crosshair, for each entity being stacked, for each label
        /// drawn over a world object.
        ///
        /// Only real answers are kept. The native answers with an empty box for a model the game has not
        /// loaded, and remembering that would leave the model sizeless for the rest of the session.
        /// </summary>
        public static bool ModelDimensions(int modelHash, out Vector3 min, out Vector3 max)
        {
            Box box;
            if (Boxes.TryGetValue(modelHash, out box))
            {
                min = box.Min;
                max = box.Max;
                return true;
            }

            min = Vector3.Zero;
            max = Vector3.Zero;

            if (!Bind()) return false;

            float[] fields;
            if (!Answer(_modelDimensions, "modelDimensions", 6, out fields, modelHash)) return false;

            min = new Vector3(fields[0], fields[1], fields[2]);
            max = new Vector3(fields[3], fields[4], fields[5]);

            if (min == max) return false;

            Boxes[modelHash] = new Box { Min = min, Max = max };
            return true;
        }

        // --- Plumbing ---------------------------------------------------------------------------------

        /// <summary>
        /// Gets hold of the two Lua functions, once.
        ///
        /// The callback has already run by the time TriggerEvent returns: a local event is dispatched
        /// straight through to its handlers rather than queued, which is the same thing CitizenFX's own
        /// <c>Exports</c> relies on to hand back a return value at all.
        /// </summary>
        private static bool Bind()
        {
            if (Bound()) return true;
            if (_bindAttempts >= MaxBindAttempts) return false;

            _bindAttempts++;

            try
            {
                BaseScript.TriggerEvent(BindEvent, new Action<object, object>((probe, dimensions) =>
                {
                    _setterRan = true;
                    _whatArrived = Name(probe) + " and " + Name(dimensions);

                    _probe = probe as CallbackDelegate;
                    _modelDimensions = dimensions as CallbackDelegate;
                }));
            }
            catch (Exception e)
            {
                Log.Error("LuaBridge.Bind", e);
            }

            if (Bound()) return true;

            // Part of a binding is no binding: whichever half arrived is dropped so the next attempt starts
            // from nothing rather than a state no caller checks for.
            _probe = null;
            _modelDimensions = null;

            if (_bindAttempts >= MaxBindAttempts) SayItFailed();

            return false;
        }

        /// <summary>Both or neither: a caller that got half the bridge would fail somewhere else.</summary>
        private static bool Bound()
        {
            return _probe != null && _modelDimensions != null;
        }

        /// <summary>
        /// Says which half of the handshake broke, because the two are fixed in different places and from
        /// here they look the same. bridge.lua prints a line of its own when it loads, and whether that line
        /// is in the console is the whole diagnosis: no line means the file is not being loaded — it is
        /// missing from the deployed resource, or its fxmanifest lists it under files{} instead of
        /// client_scripts{}, or it failed to parse and said so at resource start.
        /// </summary>
        private static void SayItFailed()
        {
            if (_setterRan)
            {
                Log.Info("client/bridge.lua answered {0} with {1} rather than with two functions. " +
                         "The Lua and the .NET halves of the resource are not from the same build.",
                    BindEvent, _whatArrived);
            }
            else
            {
                Log.Info("Nothing answered {0}. Placement, stacking and bounding boxes need " +
                         "client/bridge.lua; look for its \"bridge.lua loaded\" line at resource start — " +
                         "without it the file is not being loaded, and the deployed fxmanifest.lua has to " +
                         "list it under client_scripts{{}}, ahead of the .net.dll.",
                    BindEvent);

                // Reading the file back says whether it is in the resource this client downloaded, which is
                // the fork in the diagnosis: absent means the deployment is short a file, present means it
                // arrived and did not run, with the Lua parse error at resource start saying why.
                var text = ResourceFiles.ReadOptionalText(BridgeFile);
                Log.Info(text == null
                        ? "  {0} does not read back from this client's copy of the resource."
                        : "  {0} reads back from this client's copy of the resource, {1} bytes of it.",
                    BridgeFile, text == null ? 0 : text.Length);
            }

            Boot.Alarm("LuaBridge: no usable answer to " + BindEvent);
        }

        /// <summary>What something is, for a message about it not being what was expected.</summary>
        private static string Name(object value)
        {
            return value == null ? "nothing" : value.GetType().Name;
        }

        /// <summary>
        /// Calls one of the Lua functions and takes the numbers out of what it said. False on anything
        /// unexpected, so that a caller reads a broken answer as no answer.
        /// </summary>
        private static bool Answer(CallbackDelegate fn, string what, int count, out float[] values,
            params object[] args)
        {
            values = null;

            object returned;
            try
            {
                returned = fn(args);
            }
            catch (Exception e)
            {
                Log.Error("LuaBridge." + what, e);
                return false;
            }

            var text = AsString(returned);
            if (text == null) return Wrong(what, "no string came back");

            var parts = text.Split(' ');
            if (parts.Length != count) return Wrong(what, "\"" + text + "\" is not " + count + " numbers");

            var parsed = new float[count];
            for (var i = 0; i < count; i++)
            {
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]))
                    return Wrong(what, "\"" + text + "\" has a field that is not a number");
            }

            values = parsed;
            return true;
        }

        /// <summary>
        /// The string the Lua side returned. Whether a function reference's return value arrives on its
        /// own or wrapped in the list of everything the function returned is a detail of the runtime's
        /// MessagePack layer rather than of anything documented, so both are read.
        /// </summary>
        private static string AsString(object returned)
        {
            var text = returned as string;
            if (text != null) return text;

            var list = returned as IList;
            if (list != null && list.Count > 0) return list[0] as string;

            return null;
        }

        /// <summary>
        /// Said once and then never again: this can only be a mismatch between this file and bridge.lua,
        /// it will be true on every frame after the first, and a line per frame is by itself enough to
        /// bring a client to a halt.
        /// </summary>
        private static bool Wrong(string what, string why)
        {
            if (!_saidAnswerWasWrong)
            {
                _saidAnswerWasWrong = true;
                Log.Info("client/bridge.lua answered {0} with something unreadable: {1}. " +
                         "The Lua and the .NET halves of the resource are not from the same build.", what, why);
            }

            return false;
        }
    }
}
