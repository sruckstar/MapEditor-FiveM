using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using CitizenFX.Core;
using MapEditor.Platform;
using MapEditor.Ui.Menus;
using Control = CitizenFX.Core.Control;

namespace MapEditor
{
    public partial class MapEditor
    {
        /// <summary>
        /// The six directions a stack can grow in. They are the base object's own local axes, not the
        /// world's: a plank laid at an angle stacks along its own length, which is what makes the copies
        /// line up without any further nudging.
        /// </summary>
        private enum StackAxis
        {
            XPlus,
            XMinus,
            YPlus,
            YMinus,
            ZPlus,
            ZMinus,
        }

        private const int StackAxisCount = 6;

        /// <summary>
        /// Copies allowed in one direction. The prop limit is the real ceiling; this only keeps a leaned-on
        /// scroll key from spawning hundreds of props before the player can let go.
        /// </summary>
        private const int MaxStackPerAxis = 100;

        /// <summary>
        /// Above this many copies the preview boxes cost more than they are worth: each one is twelve
        /// world-space lines, every frame.
        /// </summary>
        private const int MaxStackPreviewBoxes = 48;

        private const float StackPaddingStep = 0.05f;
        private const float StackPaddingLimit = 100f;

        /// <summary>Scrolling with this held moves ten steps at a time. Shared with the looping tool.</summary>
        private const int ScrollMultiplier = 10;

        private static readonly Color StackBaseColor = Color.FromArgb(200, 200, 200, 10);
        private static readonly Color StackCopyColor = Color.FromArgb(200, 20, 150, 240);

        private NativeMenu _stackingMenu;

        /// <summary>
        /// The count rows, kept so that what they show can be put back in step with what is standing on
        /// every frame. See <see cref="RefreshStackCounts"/> for why they cannot keep themselves.
        /// </summary>
        private readonly NativeDynamicItem<int>[] _stackCountItems = new NativeDynamicItem<int>[StackAxisCount];

        private readonly float[] _stackPaddings = new float[StackAxisCount];

        /// <summary>
        /// The copies made in each direction, ordered outwards from the base. They are real map entities
        /// from the moment they spawn — saving the stack just stops tracking them, aborting deletes them.
        /// </summary>
        private readonly List<Entity>[] _stackCopies =
        {
            new List<Entity>(),
            new List<Entity>(),
            new List<Entity>(),
            new List<Entity>(),
            new List<Entity>(),
            new List<Entity>(),
        };

        /// <summary>The object the stack grows from. Non-null exactly while the tool is running.</summary>
        private Entity _stackingBase;

        private void BuildStackingMenu()
        {
            _stackingMenu = new NativeMenu(Translation.Translate("Stacking Tool"),
                "~b~" + Translation.Translate("Stacking objects made easy"));
            _stackingMenu.Buttons.Visible = false;
            _stackingMenu.Closed += OnStackingMenuClosed;
            _menuPool.Add(_stackingMenu);
        }

        /// <summary>
        /// Backing out of the menu leaves the tool, and the copies were never saved, so they go with it.
        /// Saving hides the menu through <see cref="SetMenuVisible"/>, which this handler ignores.
        /// </summary>
        private void OnStackingMenuClosed(object sender, EventArgs e)
        {
            if (_programmaticMenuChange) return;
            EndStacking(false);
        }

        private void RedrawStackingMenu(bool refreshIndex)
        {
            int index = _stackingMenu.SelectedIndex;
            _stackingMenu.Clear();

            for (int i = 0; i < StackAxisCount; i++)
                AddStackAxisRows((StackAxis)i);

            var save = new NativeItem(Translation.Translate("Save"),
                Translation.Translate("Keep the stacked copies and leave the tool."));
            save.Activated += (sender, item) => EndStacking(true);
            _stackingMenu.Add(save);

            if (_stackingMenu.Items.Count == 0) return;
            _stackingMenu.SelectedIndex = refreshIndex ? 0 : ClampIndex(index, _stackingMenu.Items.Count);
        }

        /// <summary>
        /// The two rows one direction is configured with: how many copies, and how much room to leave
        /// between them.
        /// </summary>
        private void AddStackAxisRows(StackAxis axis)
        {
            int i = (int)axis;
            string label = StackAxisLabel(axis);

            var count = new NativeDynamicItem<int>(
                string.Format(CultureInfo.InvariantCulture, "{0} {1}:", Translation.Translate("Stack"), label),
                string.Format(CultureInfo.InvariantCulture,
                    Translation.Translate("How many copies of the object to stack along {0}."), label),
                _stackCopies[i].Count);
            count.ItemChanged += (sender, e) =>
            {
                // Counted off what is standing, never off what the row shows: the two can be a frame apart,
                // and a step from the shown number would ask for a stack that is already there.
                int step = IsMultiplierDown() ? ScrollMultiplier : 1;
                int wanted = _stackCopies[i].Count + (e.Direction == Direction.Left ? -step : step);

                // What was asked for, so the row moves under the player's hand; whether it was got is settled
                // a frame later by RefreshStackCounts.
                e.Object = Clamp(wanted, 0, MaxStackPerAxis);
                Run("Stack count", () => SetStackCount(axis, wanted));
            };
            _stackingMenu.Add(count);
            _stackCountItems[i] = count;

            var padding = new NativeDynamicItem<float>(
                string.Format(CultureInfo.InvariantCulture, "{0} {1} {2}:", Translation.Translate("Stack"), label,
                    Translation.Translate("padding")),
                string.Format(CultureInfo.InvariantCulture,
                    Translation.Translate("The gap left between the copies stacked along {0}. Negative values overlap them."), label),
                _stackPaddings[i]);
            padding.ItemChanged += (sender, e) =>
            {
                float step = StackPaddingStep * (IsMultiplierDown() ? ScrollMultiplier : 1);
                float value = (float)Math.Round(e.Object + (e.Direction == Direction.Left ? -step : step), 2);
                if (value < -StackPaddingLimit) value = -StackPaddingLimit;
                if (value > StackPaddingLimit) value = StackPaddingLimit;

                _stackPaddings[i] = value;
                ApplyStackLayout();
                e.Object = value;
            };
            _stackingMenu.Add(padding);
        }

        private static string StackAxisLabel(StackAxis axis)
        {
            switch (axis)
            {
                case StackAxis.XPlus: return "X+";
                case StackAxis.XMinus: return "X-";
                case StackAxis.YPlus: return "Y+";
                case StackAxis.YMinus: return "Y-";
                case StackAxis.ZPlus: return "Z+";
                default: return "Z-";
            }
        }

        /// <summary>The direction the copies march off in, in the base object's local space.</summary>
        private static Vector3 StackAxisDirection(StackAxis axis)
        {
            switch (axis)
            {
                case StackAxis.XPlus: return new Vector3(1f, 0f, 0f);
                case StackAxis.XMinus: return new Vector3(-1f, 0f, 0f);
                case StackAxis.YPlus: return new Vector3(0f, 1f, 0f);
                case StackAxis.YMinus: return new Vector3(0f, -1f, 0f);
                case StackAxis.ZPlus: return new Vector3(0f, 0f, 1f);
                default: return new Vector3(0f, 0f, -1f);
            }
        }

        /// <summary>How much of the model's own size one step along the axis has to clear.</summary>
        private static float StackAxisSize(Vector3 modelSize, StackAxis axis)
        {
            switch (axis)
            {
                case StackAxis.XPlus:
                case StackAxis.XMinus:
                    return modelSize.X;
                case StackAxis.YPlus:
                case StackAxis.YMinus:
                    return modelSize.Y;
                default:
                    return modelSize.Z;
            }
        }

        private static Vector3 StackModelSize(Entity ent)
        {
            Vector3 min, max;
            LuaBridge.ModelDimensions(ent.Model.Hash, out min, out max);
            var size = max - min;
            return new Vector3(Math.Abs(size.X), Math.Abs(size.Y), Math.Abs(size.Z));
        }

        private static bool IsMultiplierDown()
        {
            return Game.IsControlPressed(0, Control.Sprint);
        }

        /// <summary>
        /// Opens the tool on <paramref name="ent"/>. The object itself is left where it is: it is the
        /// anchor every copy is measured from, so the tool takes over the controls that would move it.
        /// </summary>
        private void BeginStacking(Entity ent)
        {
            if (ent == null || !ent.Exists()) return;

            _stackingBase = ent;
            _selectedProp = ent;

            for (int i = 0; i < StackAxisCount; i++)
            {
                _stackPaddings[i] = 0f;
                _stackCopies[i].Clear();
            }

            RedrawStackingMenu(true);
            CloseAllMenus();
            SetMenuVisible(_stackingMenu, true);
        }

        /// <summary>
        /// Leaves the tool, either keeping the copies (Save) or throwing them away (backing out).
        /// </summary>
        private void EndStacking(bool keepCopies)
        {
            if (_stackingBase == null) return;

            int stacked = 0;
            for (int i = 0; i < StackAxisCount; i++)
            {
                stacked += _stackCopies[i].Count;

                if (!keepCopies)
                {
                    foreach (Entity copy in _stackCopies[i])
                        DeleteEditorEntity(copy);
                }

                _stackCopies[i].Clear();
            }

            var baseEnt = _stackingBase;
            _stackingBase = null;
            SetMenuVisible(_stackingMenu, false);

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
                Translation.Translate("Stacked {0} object(s)."), stacked));
        }

        /// <summary>
        /// Throws away the copies made so far without leaving the tool, so the player can start the stack
        /// over from the base object.
        /// </summary>
        private void AbortStacking()
        {
            if (_stackingBase == null) return;

            for (int i = 0; i < StackAxisCount; i++)
            {
                foreach (Entity copy in _stackCopies[i])
                    DeleteEditorEntity(copy);

                _stackCopies[i].Clear();
            }

            RedrawStackingMenu(false);
            _changesMade++;
        }

        /// <summary>
        /// Grows or shrinks one direction of the stack to <paramref name="count"/> copies. Copies are only
        /// ever added and removed at the outer end, so the ones already placed keep their entities.
        /// </summary>
        private async Task SetStackCount(StackAxis axis, int count)
        {
            if (_stackingBase == null || !_stackingBase.Exists()) return;

            count = Clamp(count, 0, MaxStackPerAxis);

            int i = (int)axis;
            var copies = _stackCopies[i];

            while (copies.Count > count)
            {
                int last = copies.Count - 1;
                DeleteEditorEntity(copies[last]);
                copies.RemoveAt(last);
            }

            while (copies.Count < count)
            {
                // The base object can go while a long stack is being built, a frame at a time.
                if (_stackingBase == null || !_stackingBase.Exists()) break;

                var copy = await CopyEntity(_stackingBase);
                // The prop limit, or a model that would not spawn. PropStreamer has already said so.
                if (copy == null) break;
                copies.Add(copy);
            }

            ApplyStackLayout();
            _changesMade++;
        }

        /// <summary>
        /// Puts the count rows back in step with the copies that are actually standing.
        ///
        /// Done here, every frame, rather than at the end of the spawn that changed them, because the menu
        /// writes the row itself after the handler returns — <c>NativeDynamicItem.GoRight</c> assigns
        /// <c>SelectedItem</c> from the event's value once the handler is done — so anything the handler
        /// leaves behind is overwritten by the number that was current before it ran. A copy of a model
        /// that is already loaded spawns without giving the frame back, and for those the handler is
        /// exactly where the new count lands, which is why the rows used to trail the stack by one.
        /// </summary>
        private void RefreshStackCounts()
        {
            for (int i = 0; i < StackAxisCount; i++)
            {
                var item = _stackCountItems[i];
                if (item == null || item.SelectedItem == _stackCopies[i].Count) continue;

                item.SelectedItem = _stackCopies[i].Count;
            }
        }

        /// <summary>
        /// Puts every copy where its direction, its place in the line and the current padding say it goes.
        /// Cheap enough to run on every scroll of a padding row, which is what keeps the preview live.
        /// </summary>
        private void ApplyStackLayout()
        {
            if (_stackingBase == null || !_stackingBase.Exists()) return;

            var modelSize = StackModelSize(_stackingBase);

            for (int i = 0; i < StackAxisCount; i++)
            {
                var axis = (StackAxis)i;
                var copies = _stackCopies[i];
                if (copies.Count == 0) continue;

                var direction = StackAxisDirection(axis);
                float step = StackAxisSize(modelSize, axis) + _stackPaddings[i];

                for (int n = 0; n < copies.Count; n++)
                {
                    var copy = copies[n];
                    if (copy == null || !copy.Exists()) continue;

                    // Measured off the base entity, so the offset is rotated into the base's own axes.
                    SetEntityPosition(copy, GetEntityOffset(_stackingBase, direction * (step * (n + 1))));
                }
            }
        }

        private void ProcessStacking()
        {
            if (_stackingBase == null || !_stackingBase.Exists())
            {
                EndStacking(false);
                return;
            }

            RefreshStackCounts();
            DrawEntityBox(_stackingBase, StackBaseColor);

            int total = 0;
            for (int i = 0; i < StackAxisCount; i++)
                total += _stackCopies[i].Count;

            if (total <= MaxStackPreviewBoxes)
            {
                for (int i = 0; i < StackAxisCount; i++)
                {
                    foreach (Entity copy in _stackCopies[i])
                    {
                        if (copy == null || !copy.Exists()) continue;
                        DrawEntityBox(copy, StackCopyColor);
                    }
                }
            }

            if (Game.IsControlJustPressed(0, Control.CreatorDelete))
                AbortStacking();

            DrawButtons(_stackingButtons);
        }
    }
}
