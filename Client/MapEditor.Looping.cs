using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using CitizenFX.Core;
using MapEditor.Platform;
using MapEditor.Ui.Menus;
using Control = CitizenFX.Core.Control;
// MapEditor has a Quaternion of its own, so the game's one has to be named apart from it in here.
using MathQuaternion = CitizenFX.Core.Quaternion;

namespace MapEditor
{
    public partial class MapEditor
    {
        /// <summary>
        /// Where the loop's center sits relative to the base object, which is also what decides the axis
        /// the copies revolve about: above or below it they turn about the object's own X axis and stand
        /// the loop up on its side, to its left or right they turn about its Z axis and lay the loop flat.
        /// Either way the first copy leaves along the object's own forward axis.
        /// </summary>
        private enum LoopCenter
        {
            Top,
            Bottom,
            Left,
            Right,
        }

        private enum LoopDirection
        {
            Forward,
            Backward,
        }

        /// <summary>
        /// Objects in one loop, the base object included. The prop limit is the real ceiling; this keeps
        /// a leaned-on scroll key, or a big radius on auto, from spawning hundreds of props at once.
        /// </summary>
        private const int MaxLoopObjects = 400;

        private const int MaxLoopCount = 20;

        /// <summary>
        /// Above this many copies the preview boxes cost more than they are worth: each one is twelve
        /// world-space lines, every frame. The copies are real props, so the loop is visible regardless.
        /// </summary>
        private const int MaxLoopPreviewBoxes = 48;

        private const float LoopRadiusStep = 0.1f;
        private const float LoopRadiusMin = 0.1f;
        private const float LoopRadiusMax = 500f;

        private const float LoopOffsetStep = 0.1f;
        private const float LoopOffsetLimit = 500f;

        private const float LoopRotationStep = 0.1f;
        private const float LoopRotationLimit = 360f;

        /// <summary>Segments the guide circle is drawn with, per loop.</summary>
        private const int LoopGuideSegments = 48;
        private const int MaxLoopGuideSegments = 240;

        private static readonly Color LoopBaseColor = Color.FromArgb(200, 200, 200, 10);
        private static readonly Color LoopCopyColor = Color.FromArgb(200, 20, 150, 240);
        private static readonly Color LoopGuideColor = Color.FromArgb(160, 240, 160, 20);

        private NativeMenu _loopingMenu;

        /// <summary>Kept so the auto-calculated count can be written back into the row it is shown in.</summary>
        private NativeDynamicItem<int> _loopObjectsItem;

        /// <summary>
        /// The copies riding the loop, ordered outwards from the base. They are real map entities from the
        /// moment they spawn — saving the loop just stops tracking them, aborting deletes them.
        /// </summary>
        private readonly List<Entity> _loopCopies = new List<Entity>();

        /// <summary>The object the loop is generated from. Non-null exactly while the tool is running.</summary>
        private Entity _loopingBase;

        private int _loopCount;
        private float _loopRadius;
        private float _loopOffset;

        /// <summary>
        /// Objects in the loop, counting the base object as the first of them. Read off the copies rather
        /// than counted alongside them: a second number for the same thing is a number that can be wrong,
        /// and this one is read by the layout on every frame.
        /// </summary>
        private int LoopObjects
        {
            get { return _loopCopies.Count + 1; }
        }

        private bool _loopAutoObjects;
        private LoopCenter _loopCenter;
        private LoopDirection _loopDirection;

        /// <summary>Degrees each copy is turned about its own axes on top of the one before it.</summary>
        private Vector3 _loopRotationOffset;

        private void BuildLoopingMenu()
        {
            _loopingMenu = new NativeMenu(Translation.Translate("Looping Generator"),
                "~b~" + Translation.Translate("Create loops the old way"));
            _loopingMenu.Buttons.Visible = false;
            _loopingMenu.Closed += OnLoopingMenuClosed;
            _menuPool.Add(_loopingMenu);
        }

        /// <summary>
        /// Backing out of the menu leaves the tool, and the copies were never saved, so they go with it.
        /// Saving hides the menu through <see cref="SetMenuVisible"/>, which this handler ignores.
        /// </summary>
        private void OnLoopingMenuClosed(object sender, EventArgs e)
        {
            if (_programmaticMenuChange) return;
            EndLooping(false);
        }

        private void RedrawLoopingMenu(bool refreshIndex)
        {
            int index = _loopingMenu.SelectedIndex;
            _loopingMenu.Clear();

            var loops = new NativeDynamicItem<int>(Translation.Translate("Loops:"),
                Translation.Translate("How many full turns the chain of objects makes."), _loopCount);
            loops.ItemChanged += (sender, e) =>
            {
                int step = IsMultiplierDown() ? ScrollMultiplier : 1;
                _loopCount = Clamp(e.Object + (e.Direction == Direction.Left ? -step : step), 1, MaxLoopCount);
                // On auto this spawns, and spawning waits for a model: it finishes on a later frame. See Run.
                Run("Loop count", RelayoutLoop);
                e.Object = _loopCount;
            };
            _loopingMenu.Add(loops);

            var radius = new NativeDynamicItem<float>(Translation.Translate("Radius:"),
                Translation.Translate("How far the loop reaches from its center, in game units."), _loopRadius);
            radius.ItemChanged += (sender, e) =>
            {
                float step = LoopRadiusStep * (IsMultiplierDown() ? ScrollMultiplier : 1);
                _loopRadius = Clamp((float)Math.Round(e.Object + (e.Direction == Direction.Left ? -step : step), 2),
                    LoopRadiusMin, LoopRadiusMax);
                Run("Loop radius", RelayoutLoop);
                e.Object = _loopRadius;
            };
            _loopingMenu.Add(radius);

            var offset = new NativeDynamicItem<float>(Translation.Translate("Offset:"),
                Translation.Translate("How far the loop travels sideways over one full turn. Leave it at 0 for a closed ring, raise it to draw out a corkscrew."),
                _loopOffset);
            offset.ItemChanged += (sender, e) =>
            {
                float step = LoopOffsetStep * (IsMultiplierDown() ? ScrollMultiplier : 1);
                _loopOffset = Clamp((float)Math.Round(e.Object + (e.Direction == Direction.Left ? -step : step), 2),
                    -LoopOffsetLimit, LoopOffsetLimit);
                ApplyLoopLayout();
                e.Object = _loopOffset;
            };
            _loopingMenu.Add(offset);

            _loopObjectsItem = new NativeDynamicItem<int>(Translation.Translate("Objects:"),
                Translation.Translate("How many objects the loop is built out of, the selected one included."),
                LoopObjects)
            {
                Enabled = !_loopAutoObjects,
            };
            _loopObjectsItem.ItemChanged += (sender, e) =>
            {
                // The count is not the player's to set while it is being calculated for them.
                if (_loopAutoObjects)
                {
                    e.Object = LoopObjects;
                    return;
                }

                // Counted off the loop that is standing, never off what the row is showing: the two can be
                // a frame apart. See RefreshStackCounts for the whole of it — this row had the same fault.
                int step = IsMultiplierDown() ? ScrollMultiplier : 1;
                int wanted = LoopObjects + (e.Direction == Direction.Left ? -step : step);

                // What was asked for, so the row moves under the player's hand; whether it was got is
                // settled a frame later by RefreshLoopObjects.
                e.Object = Clamp(wanted, 1, MaxLoopObjects);
                Run("Loop objects", () => SetLoopObjects(wanted));
            };
            _loopingMenu.Add(_loopObjectsItem);

            var auto = new NativeCheckboxItem(Translation.Translate("Auto calculate objects"),
                Translation.Translate("Fill the loop with as many objects as it takes for them to meet end to end."),
                _loopAutoObjects);
            auto.CheckboxChanged += (sender, e) =>
            {
                _loopAutoObjects = auto.Checked;
                _loopObjectsItem.Enabled = !_loopAutoObjects;
                Run("Loop auto", RelayoutLoop);
            };
            _loopingMenu.Add(auto);

            var centers = new[]
            {
                Translation.Translate("Top"),
                Translation.Translate("Bottom"),
                Translation.Translate("Left"),
                Translation.Translate("Right"),
            };
            var center = new NativeListItem<string>(Translation.Translate("Looping Center:"), centers)
            {
                Description = Translation.Translate("Which side of the object the loop's center sits on. Above or below it the loop stands upright; to its left or right the loop lies flat."),
                SelectedIndex = ClampIndex((int)_loopCenter, centers.Length),
            };
            center.ItemChanged += (sender, e) =>
            {
                _loopCenter = (LoopCenter)e.Index;
                ApplyLoopLayout();
            };
            _loopingMenu.Add(center);

            var directions = new[]
            {
                Translation.Translate("Forward"),
                Translation.Translate("Backward"),
            };
            var direction = new NativeListItem<string>(Translation.Translate("Looping Direction:"), directions)
            {
                Description = Translation.Translate("Change the direction in which the looping is generated."),
                SelectedIndex = ClampIndex((int)_loopDirection, directions.Length),
            };
            direction.ItemChanged += (sender, e) =>
            {
                _loopDirection = (LoopDirection)e.Index;
                ApplyLoopLayout();
            };
            _loopingMenu.Add(direction);

            _loopingMenu.Add(new NativeItem(Translation.Translate("Continuous rotation offsets:")) { Enabled = false });

            AddLoopRotationRow("x", () => _loopRotationOffset.X, v => _loopRotationOffset.X = v);
            AddLoopRotationRow("y", () => _loopRotationOffset.Y, v => _loopRotationOffset.Y = v);
            AddLoopRotationRow("z", () => _loopRotationOffset.Z, v => _loopRotationOffset.Z = v);

            var save = new NativeItem(Translation.Translate("Save"),
                Translation.Translate("Keep the generated loop and leave the tool."));
            save.Activated += (sender, item) => EndLooping(true);
            _loopingMenu.Add(save);

            if (_loopingMenu.Items.Count == 0) return;
            _loopingMenu.SelectedIndex = refreshIndex ? 0 : ClampIndex(index, _loopingMenu.Items.Count);
        }

        /// <summary>
        /// One axis of the rotation the copies pick up as the loop goes round. The offsets are per copy and
        /// they add up, so a degree or two here twists the whole loop rather than tilting it once.
        /// </summary>
        private void AddLoopRotationRow(string label, Func<float> get, Action<float> set)
        {
            var item = new NativeDynamicItem<float>(label,
                string.Format(CultureInfo.InvariantCulture,
                    Translation.Translate("Degrees added about the object's own {0} axis with every object placed."),
                    label.ToUpper()),
                get());
            item.ItemChanged += (sender, e) =>
            {
                float step = LoopRotationStep * (IsMultiplierDown() ? ScrollMultiplier : 1);
                float value = Clamp((float)Math.Round(e.Object + (e.Direction == Direction.Left ? -step : step), 2),
                    -LoopRotationLimit, LoopRotationLimit);

                set(value);
                ApplyLoopLayout();
                e.Object = value;
            };
            _loopingMenu.Add(item);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            return value > max ? max : value;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min) return min;
            return value > max ? max : value;
        }

        /// <summary>
        /// Opens the tool on <paramref name="ent"/>. The object itself is left where it is: it is the first
        /// object of the loop and the anchor the rest are measured from, so the tool takes over the controls
        /// that would move it.
        /// </summary>
        private void BeginLooping(Entity ent)
        {
            if (ent == null || !ent.Exists()) return;

            _loopingBase = ent;
            _selectedProp = ent;

            ResetLoopSettings();

            RedrawLoopingMenu(true);
            CloseAllMenus();
            SetMenuVisible(_loopingMenu, true);
        }

        /// <summary>
        /// Back to an empty loop around the base object. The copies are gone by the time this is called.
        /// </summary>
        private void ResetLoopSettings()
        {
            _loopCopies.Clear();

            _loopCount = 1;
            _loopRadius = 7.5f;
            _loopOffset = 0f;
            _loopAutoObjects = false;
            _loopCenter = LoopCenter.Top;
            _loopDirection = LoopDirection.Forward;
            _loopRotationOffset = Vector3.Zero;
        }

        /// <summary>
        /// Leaves the tool, either keeping the copies (Save) or throwing them away (backing out).
        /// </summary>
        private void EndLooping(bool keepCopies)
        {
            if (_loopingBase == null) return;

            int generated = _loopCopies.Count;

            if (!keepCopies)
            {
                foreach (Entity copy in _loopCopies)
                    DeleteEditorEntity(copy);
            }

            _loopCopies.Clear();

            var baseEnt = _loopingBase;
            _loopingBase = null;
            SetMenuVisible(_loopingMenu, false);

            if (!keepCopies)
            {
                // Back to the properties menu the tool was opened from, with the object still selected.
                if (baseEnt != null && baseEnt.Exists())
                {
                    _selectedProp = baseEnt;
                    RedrawObjectInfoMenu(baseEnt, true);
                    SetMenuVisible(_objectInfoMenu, true);
                    return;
                }

                _selectedProp = null;
                _mainCamera?.StopPointing();
                return;
            }

            _selectedProp = null;
            _mainCamera?.StopPointing();
            _changesMade++;
            Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + string.Format(CultureInfo.InvariantCulture,
                Translation.Translate("Generated a looping out of {0} object(s)."), generated));
        }

        /// <summary>
        /// Throws away the copies made so far without leaving the tool, so the player can start the loop
        /// over from the base object.
        /// </summary>
        private void AbortLooping()
        {
            if (_loopingBase == null) return;

            foreach (Entity copy in _loopCopies)
                DeleteEditorEntity(copy);

            ResetLoopSettings();
            RedrawLoopingMenu(false);
            _changesMade++;
        }

        /// <summary>
        /// Puts the loop back together after a change to its shape. On auto the shape is what decides how
        /// many objects it takes to fill, so that is recounted first.
        /// </summary>
        private async Task RelayoutLoop()
        {
            if (_loopAutoObjects)
            {
                // The row is written by RefreshLoopObjects, not here. See RefreshStackCounts.
                await SetLoopObjects(AutoLoopObjectCount());
                return;
            }

            ApplyLoopLayout();
        }

        /// <summary>
        /// Grows or shrinks the loop to <paramref name="objects"/> objects, the base object counted as the
        /// first of them. Copies are only ever added and removed at the far end, so the ones already placed
        /// keep their entities; where they sit is settled afterwards, by the layout.
        /// </summary>
        private async Task SetLoopObjects(int objects)
        {
            if (_loopingBase == null || !_loopingBase.Exists()) return;

            int wanted = Clamp(objects, 1, MaxLoopObjects) - 1;

            while (_loopCopies.Count > wanted)
            {
                int last = _loopCopies.Count - 1;
                DeleteEditorEntity(_loopCopies[last]);
                _loopCopies.RemoveAt(last);
            }

            while (_loopCopies.Count < wanted)
            {
                // The base object can go while a long loop is being built, a frame at a time.
                if (_loopingBase == null || !_loopingBase.Exists()) break;

                var copy = await CopyEntity(_loopingBase);
                // The prop limit, or a model that would not spawn. PropStreamer has already said so.
                if (copy == null) break;
                _loopCopies.Add(copy);
            }

            ApplyLoopLayout();
            _changesMade++;
        }

        /// <summary>
        /// Puts the object count row back in step with the loop that is actually standing, on every frame.
        /// The reason it is done from here rather than where the count changes is on
        /// <see cref="RefreshStackCounts"/>.
        /// </summary>
        private void RefreshLoopObjects()
        {
            if (_loopObjectsItem == null || _loopObjectsItem.SelectedItem == LoopObjects) return;

            _loopObjectsItem.SelectedItem = LoopObjects;
        }

        /// <summary>
        /// The objects it takes to line the loop end to end: the arc they have to cover, over the length of
        /// one of them along the way it travels. That way is the object's own Y axis whichever side the
        /// center is on, which is why only the model's Y size is asked for.
        /// </summary>
        private int AutoLoopObjectCount()
        {
            if (_loopingBase == null || !_loopingBase.Exists()) return 1;

            Vector3 min, max;
            LuaBridge.ModelDimensions(_loopingBase.Model.Hash, out min, out max);
            float length = Math.Abs(max.Y - min.Y);

            // A model with no length of its own would ask for an unbounded number of copies.
            if (length < 0.01f) length = 0.01f;

            float arc = 2f * (float)Math.PI * _loopRadius * _loopCount;
            return Clamp((int)Math.Round(arc / length), 1, MaxLoopObjects);
        }

        /// <summary>
        /// The pivot the loop turns around, the axis it turns about, and the axis a non-zero offset draws it
        /// out along. All three come off the base object's own axes, so a loop built on a banked object comes
        /// out banked with it.
        /// </summary>
        private void LoopGeometry(out Vector3 pivot, out Vector3 spinAxis, out Vector3 coilAxis)
        {
            var origin = _loopingBase.Position;
            var right = Unit(GetEntityOffset(_loopingBase, new Vector3(1f, 0f, 0f)) - origin);
            var up = Unit(GetEntityOffset(_loopingBase, new Vector3(0f, 0f, 1f)) - origin);

            // The axes are signed so that a positive turn always carries the first copy forwards, out of the
            // object's face, whichever side of it the center is on.
            switch (_loopCenter)
            {
                case LoopCenter.Top:
                    pivot = origin + (up * _loopRadius);
                    spinAxis = right;
                    break;
                case LoopCenter.Bottom:
                    pivot = origin - (up * _loopRadius);
                    spinAxis = -right;
                    break;
                case LoopCenter.Left:
                    pivot = origin - (right * _loopRadius);
                    spinAxis = up;
                    break;
                default:
                    pivot = origin + (right * _loopRadius);
                    spinAxis = -up;
                    break;
            }

            // Unsigned, so that a positive offset draws the loop out one predictable way no matter which way
            // it turns: sideways for an upright loop, upwards for a flat one.
            coilAxis = _loopCenter == LoopCenter.Top || _loopCenter == LoopCenter.Bottom ? right : up;
        }

        /// <summary>
        /// Puts every copy where its place in the loop says it goes. Cheap enough to run on every scroll of
        /// a row, which is what keeps the preview live.
        /// </summary>
        private void ApplyLoopLayout()
        {
            if (_loopingBase == null || !_loopingBase.Exists() || _loopCopies.Count == 0) return;

            Vector3 pivot, spinAxis, coilAxis;
            LoopGeometry(out pivot, out spinAxis, out coilAxis);

            var baseRotation = _loopingBase.Quaternion;
            var arm = _loopingBase.Position - pivot;

            for (int i = 0; i < _loopCopies.Count; i++)
            {
                var copy = _loopCopies[i];
                if (copy == null || !copy.Exists()) continue;

                // The base object holds the first place in the loop, so the copies start at the second.
                float progress = LoopProgress(i + 1);
                var spin = AxisAngle(spinAxis, LoopSweep() * progress);

                SetEntityPosition(copy, pivot + Rotate(spin, arm) + (coilAxis * (_loopOffset * _loopCount * progress)));

                // The copy is carried round by the same turn that moved it, which is what keeps it lying
                // along the loop instead of facing the way the base object faces.
                Quaternion.SetEntityQuaternion(copy, Multiply(Multiply(spin, baseRotation), LoopTwist(i + 1)));
            }
        }

        /// <summary>Degrees the whole loop covers, negative when it is generated backwards.</summary>
        private float LoopSweep()
        {
            return 360f * _loopCount * (_loopDirection == LoopDirection.Forward ? 1f : -1f);
        }

        /// <summary>
        /// How far round the loop the object in place <paramref name="n"/> sits, as a fraction of the whole.
        /// The last object stops one place short of the first, which is what lets a closed ring meet itself.
        /// </summary>
        private float LoopProgress(int n)
        {
            return n / (float)Math.Max(LoopObjects, 1);
        }

        /// <summary>
        /// The turn the object in place <paramref name="n"/> has picked up from the rotation offsets, about
        /// its own axes: X first, then Y, then Z.
        /// </summary>
        private MathQuaternion LoopTwist(int n)
        {
            if (_loopRotationOffset == Vector3.Zero) return MathQuaternion.Identity;

            var twist = _loopRotationOffset * n;
            return Multiply(
                Multiply(AxisAngle(new Vector3(0f, 0f, 1f), twist.Z), AxisAngle(new Vector3(0f, 1f, 0f), twist.Y)),
                AxisAngle(new Vector3(1f, 0f, 0f), twist.X));
        }

        private static Vector3 Unit(Vector3 v)
        {
            v.Normalize();
            return v;
        }

        private static MathQuaternion AxisAngle(Vector3 axis, float degrees)
        {
            float half = degrees.ToRadians() * 0.5f;
            float sin = (float)Math.Sin(half);
            return new MathQuaternion(axis.X * sin, axis.Y * sin, axis.Z * sin, (float)Math.Cos(half));
        }

        /// <summary>Hamilton product: the turn <paramref name="b"/> makes first, then the one <paramref name="a"/> makes.</summary>
        private static MathQuaternion Multiply(MathQuaternion a, MathQuaternion b)
        {
            return new MathQuaternion(
                (a.W * b.X) + (a.X * b.W) + (a.Y * b.Z) - (a.Z * b.Y),
                (a.W * b.Y) - (a.X * b.Z) + (a.Y * b.W) + (a.Z * b.X),
                (a.W * b.Z) + (a.X * b.Y) - (a.Y * b.X) + (a.Z * b.W),
                (a.W * b.W) - (a.X * b.X) - (a.Y * b.Y) - (a.Z * b.Z));
        }

        private static Vector3 Rotate(MathQuaternion q, Vector3 v)
        {
            var axis = new Vector3(q.X, q.Y, q.Z);
            var t = VectorExtensions.CrossWith(axis, v) * 2f;
            return v + (t * q.W) + VectorExtensions.CrossWith(axis, t);
        }

        /// <summary>
        /// The path the copies ride, drawn whether or not any have been placed yet, so that the radius and
        /// the center can be dialled in on an empty loop.
        /// </summary>
        private void DrawLoopGuide()
        {
            Vector3 pivot, spinAxis, coilAxis;
            LoopGeometry(out pivot, out spinAxis, out coilAxis);

            var origin = _loopingBase.Position;
            var arm = origin - pivot;
            float sweep = LoopSweep();

            int segments = Math.Min(LoopGuideSegments * _loopCount, MaxLoopGuideSegments);
            var previous = origin;

            for (int i = 1; i <= segments; i++)
            {
                float progress = i / (float)segments;
                var point = pivot + Rotate(AxisAngle(spinAxis, sweep * progress), arm)
                            + (coilAxis * (_loopOffset * _loopCount * progress));

                World.DrawLine(previous, point, LoopGuideColor);
                previous = point;
            }
        }

        private void ProcessLooping()
        {
            if (_loopingBase == null || !_loopingBase.Exists())
            {
                EndLooping(false);
                return;
            }

            RefreshLoopObjects();
            DrawEntityBox(_loopingBase, LoopBaseColor);
            DrawLoopGuide();

            if (_loopCopies.Count <= MaxLoopPreviewBoxes)
            {
                foreach (Entity copy in _loopCopies)
                {
                    if (copy == null || !copy.Exists()) continue;
                    DrawEntityBox(copy, LoopCopyColor);
                }
            }

            if (Game.IsControlJustPressed(0, Control.CreatorDelete))
                AbortLooping();

            DrawButtons(_loopingButtons);
        }
    }
}
