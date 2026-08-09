using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using MapEditor.Platform;

namespace MapEditor
{
    public partial class MapEditor
    {
        /// <summary>
        /// How far from the camera an object is still named. The game's own props are dense enough in a city
        /// street that a wider net would bury the screen in text before it named anything the player could
        /// still aim at.
        /// </summary>
        private const float WorldObjectNameRadius = 40f;

        /// <summary>The nearest this many objects are named, so a crowded street costs a bounded number of draws.</summary>
        private const int MaxWorldObjectNames = 60;

        /// <summary>
        /// The pool sweep is the expensive half of naming, and objects the player has not moved towards yet do
        /// not change: the labels are found on this cadence and only re-drawn every frame.
        /// </summary>
        private const int WorldObjectScanInterval = 500;

        private const float WorldObjectNameScale = 0.36f;
        private const float MinWorldObjectNameScale = 0.18f;

        /// <summary>Matches the blue crosshair, which already means "the game's object, not yours".</summary>
        private static readonly Color WorldObjectNameColor = Color.FromArgb(220, 255, 255, 255);
        private static readonly Color AimedWorldObjectNameColor = Color.FromArgb(255, 100, 180, 255);
        private static readonly Color UnlistedWorldObjectNameColor = Color.FromArgb(170, 160, 160, 160);

        /// <summary>
        /// One named object standing in the world. The name and the height are resolved once by the sweep that
        /// found it: neither can change while the object is what it is, and both cost more than the draw does.
        /// </summary>
        private class WorldObjectLabel
        {
            public Entity Entity;
            public ObjectTypes Type;

            /// <summary>The model name, or null for a model no object list holds — see <see cref="Text"/>.</summary>
            public string Model;

            /// <summary>What is drawn: the model name, or the bare hash of a model that has no name to draw.</summary>
            public string Text;

            /// <summary>Lifts the name off the object's origin to about the top of it.</summary>
            public float Height;
        }

        private readonly List<WorldObjectLabel> _worldObjectLabels = new List<WorldObjectLabel>();
        /// <summary>Game-timer reading. Far enough back that the first frame scans; see <see cref="Clock"/>.</summary>
        private int _lastWorldObjectScan = -WorldObjectScanInterval;

        /// <summary>
        /// Names the game's own objects where they stand, so that the two things the editor can do with one —
        /// copy it, star its model — can be aimed at by name rather than by guesswork. The objects the editor
        /// placed are left out: those the player already knows by name, and they are listed in their own menu.
        /// </summary>
        private void DrawWorldObjectNames(Entity hitEnt)
        {
            if (Clock.Elapsed(_lastWorldObjectScan, WorldObjectScanInterval))
            {
                ScanWorldObjects();
                _lastWorldObjectScan = Clock.Milliseconds;
            }

            var camPos = _mainCamera.Position;
            var camDir = VectorExtensions.RotationToDirection(_mainCamera.Rotation);

            // The labels are held nearest first and drawn back to front: text carries no depth, so where two of
            // them overlap the one left readable is whichever was drawn last, and that should be the nearer.
            for (int i = _worldObjectLabels.Count - 1; i >= 0; i--)
            {
                var label = _worldObjectLabels[i];
                var ent = label.Entity;
                // An object can be streamed out, or deleted by the player, between one sweep and the next.
                if (ent == null || !ent.Exists()) continue;

                var pos = ent.Position + new Vector3(0f, 0f, label.Height);
                var toLabel = pos - camPos;
                float distance = toLabel.Length();
                if (distance > WorldObjectNameRadius) continue;

                // SET_DRAW_ORIGIN projects a point behind the camera back onto the screen as though it were in
                // front of it, so anything behind the camera has to be dropped before it is handed over.
                if (Vector3.Dot(camDir, Vector3.Normalize(toLabel)) <= 0f || !ent.IsOnScreen) continue;

                bool aimed = hitEnt != null && hitEnt.Handle == ent.Handle;

                // The text has one size on screen no matter how far away the object is, so the falloff that
                // perspective would have given it has to be applied by hand.
                float scale = Math.Max(MinWorldObjectNameScale,
                    WorldObjectNameScale * (1f - (distance / WorldObjectNameRadius)));

                var color = WorldObjectNameColor;
                if (aimed)
                    color = AimedWorldObjectNameColor;
                else if (label.Model == null)
                    color = UnlistedWorldObjectNameColor;

                DrawText3D(pos, label.Text, color, aimed ? scale * 1.3f : scale);

                // The name says what is under the crosshair; the box says which one of them it is.
                if (aimed)
                    DrawEntityBox(ent, AimedWorldObjectNameColor);
            }
        }

        /// <summary>
        /// Collects the game's objects standing around the camera, nearest first. Everything the editor itself
        /// spawned is dropped: it is drawn from <see cref="PropStreamer"/>'s own handles, and naming it would
        /// only crowd out the objects the player cannot otherwise identify.
        /// </summary>
        private void ScanWorldObjects()
        {
            _worldObjectLabels.Clear();

            var camPos = _mainCamera.Position;
            var scripted = new HashSet<int>(PropStreamer.GetAllHandles());
            int player = Game.Player.Character.Handle;

            // Distance decides which objects are named at all, so it is read here, once, rather than out of
            // each object again for every comparison the sort makes.
            var candidates = new List<Tuple<float, Entity, ObjectTypes>>();

            // SHVDN 3 had World.GetNearby*, which is a whole-pool sweep with a distance test on the end.
            // CitizenFX only offers the sweep, so the distance test is done here.
            float radiusSquared = WorldObjectNameRadius * WorldObjectNameRadius;

            void Collect(IEnumerable<Entity> entities, ObjectTypes type)
            {
                foreach (var ent in entities)
                {
                    if (ent == null || !ent.Exists()) continue;
                    if (ent.Handle == player || scripted.Contains(ent.Handle)) continue;

                    var distanceSquared = (ent.Position - camPos).LengthSquared();
                    if (distanceSquared > radiusSquared) continue;

                    candidates.Add(Tuple.Create(distanceSquared, ent, type));
                }
            }

            Collect(World.GetAllProps(), ObjectTypes.Prop);
            Collect(World.GetAllVehicles(), ObjectTypes.Vehicle);
            Collect(World.GetAllPeds(), ObjectTypes.Ped);

            // Naming an object means measuring its model and searching a list of 25,000 names backwards for its
            // hash, so it is only worth doing for the objects that will actually carry a label.
            foreach (var candidate in candidates.OrderBy(c => c.Item1).Take(MaxWorldObjectNames))
            {
                var ent = candidate.Item2;
                var type = candidate.Item3;

                int hash = ent.Model.Hash;
                var model = ObjectDatabase.NameFor(type, hash);
                Vector3 min, max;
                LuaBridge.ModelDimensions(hash, out min, out max);

                _worldObjectLabels.Add(new WorldObjectLabel
                {
                    Entity = ent,
                    Type = type,
                    Model = model,
                    // A model no list holds can still be copied — that goes by hash — so it is worth naming by
                    // the only name it has, rather than leaving a gap where an object plainly stands.
                    Text = model ?? "0x" + hash.ToString("X8"),
                    Height = (max.Z - min.Z) * 0.5f,
                });
            }
        }

        /// <summary>
        /// Draws text at a point in the world. SET_DRAW_ORIGIN moves the screen's origin onto that point, so the
        /// text is then laid out at 0,0 — that is, on the object itself.
        /// </summary>
        private static void DrawText3D(Vector3 position, string text, Color color, float scale)
        {
            Function.Call(Hash.SET_DRAW_ORIGIN, position.X, position.Y, position.Z, 0);

            Function.Call(Hash.SET_TEXT_FONT, 4);
            Function.Call(Hash.SET_TEXT_SCALE, scale, scale);
            Function.Call(Hash.SET_TEXT_COLOUR, color.R, color.G, color.B, color.A);
            Function.Call(Hash.SET_TEXT_CENTRE, true);
            // Model names are read against whatever the object is standing in front of, which is as often a
            // white wall as a dark one.
            Function.Call(Hash.SET_TEXT_OUTLINE);

            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, 0f, 0f);

            Function.Call(Hash.CLEAR_DRAW_ORIGIN);
        }

        /// <summary>
        /// Stars the model of whatever is under the crosshair, so that an object found in the world is one step
        /// from the list the player builds it from later, without having to search for a name they can only see
        /// on screen. Unstars it if it is already starred, the same as the star key does in the object list.
        /// </summary>
        private void FavoriteAimedEntity(Entity hitEnt)
        {
            ObjectTypes type;
            if (!TryGetObjectType(hitEnt, out type)) return;

            var model = ObjectDatabase.NameFor(type, hitEnt.Model.Hash);
            if (model == null)
            {
                // A favorite is stored by name and placed by looking the name back up, so a model that is in no
                // list has nothing to store. It can still be copied, which goes by hash.
                Compat.Notify("~b~~h~Map Editor~h~~w~~n~" +
                    Translation.Translate("This model is not in the object list and cannot be favorited."));
                return;
            }

            ToggleFavoriteModel(type, model);
        }

        /// <summary>The list a world entity would be placed from, or false for anything else (a pickup, a projectile).</summary>
        private static bool TryGetObjectType(Entity ent, out ObjectTypes type)
        {
            type = ObjectTypes.Prop;
            if (ent == null || !ent.Exists()) return false;

            if (IsProp(ent)) { type = ObjectTypes.Prop; return true; }
            if (IsVehicle(ent)) { type = ObjectTypes.Vehicle; return true; }
            if (IsPed(ent)) { type = ObjectTypes.Ped; return true; }

            return false;
        }
    }
}
