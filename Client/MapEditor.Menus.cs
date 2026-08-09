using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using MapEditor.Ui.Menus;
using MapEditor.Platform;

namespace MapEditor
{
    public partial class MapEditor
    {
        /// <summary>
        /// Badges replacing NativeUI's BadgeStyle.Franklin / BadgeStyle.Alert.
        /// </summary>
        private static readonly BadgeSet FolderBadge = new BadgeSet("commonmenu", "shop_franklin_icon_a", "shop_franklin_icon_b");
        private static readonly BadgeSet AlertBadge = new BadgeSet("commonmenu", "mp_alerttriangle", "mp_alerttriangle");

        /// <summary>Marks a starred model, wherever it is listed. See <see cref="ToggleFavorite"/>.</summary>
        private static readonly BadgeSet StarBadge = new BadgeSet("commonmenu", "shop_new_star", "shop_new_star");

        private enum EntityMenuKind
        {
            Entity,
            World,
            Marker,
            Laser,

            /// <summary>
            /// An entity of the map that is not in the world at the moment, because the player is nowhere near
            /// it. Its row is the same row it had while it was standing, pointed at the record smart streaming
            /// is holding it in rather than at a handle the game has taken back.
            /// </summary>
            Streamed,
        }

        /// <summary>
        /// Identifies what a "Current Entities" row points at, in the row's Tag rather than its Description,
        /// which the menu renders to the player.
        /// </summary>
        private class EntityMenuTag
        {
            public EntityMenuKind Kind;
            public int Id;          // entity handle, marker id or laser id
            public string WorldId;  // MapObject.Id, for props removed from the world
        }

        /// <summary>A menu's worth of rows, each paired with the model hash its availability is read from.</summary>
        private class RowGroup
        {
            public readonly List<NativeItem> Items = new List<NativeItem>();
            public readonly List<int> Hashes = new List<int>();
        }

        /// <summary>The rows of one category, grouped the way the DLC filter reads them.</summary>
        private class CategoryRows
        {
            /// <summary>
            /// Rows per DLC name, always including <see cref="Dlc.AllName"/>, which holds the whole category.
            /// A filter change is then a lookup, not a rebuild.
            /// </summary>
            public readonly Dictionary<string, RowGroup> ByDlc = new Dictionary<string, RowGroup>();

            /// <summary>The filter's values in release order, or empty for a list that offers no filter.</summary>
            public readonly List<string> Dlcs = new List<string>();
        }

        /// <summary>Every row built so far, per object type, keyed by model name. See <see cref="RowFor"/>.</summary>
        private readonly Dictionary<ObjectTypes, Dictionary<string, NativeItem>> _rowsByModel =
            new Dictionary<ObjectTypes, Dictionary<string, NativeItem>>();

        /// <summary>The prepared rows of every category opened so far. See <see cref="RowsFor"/>.</summary>
        private readonly Dictionary<ObjectCategory, CategoryRows> _rowsByCategory =
            new Dictionary<ObjectCategory, CategoryRows>();

        /// <summary>
        /// The models whose rows are still to be built ahead of the player, and how far along that is. Null
        /// once there is nothing left to build. See <see cref="WarmObjectRows"/>.
        /// </summary>
        private List<string> _rowWarmupQueue;
        private int _rowWarmupIndex;
        private bool _rowWarmupStarted;

        /// <summary>
        /// How many rows one tick may build ahead of the player. Building all 25,000 prop rows at once would
        /// only move the stall, so it is spread over frames: the list is ready within a couple of seconds of
        /// opening the menu and no single frame is late.
        ///
        /// A count rather than a time budget because FiveM's Mono refuses to load Stopwatch, and what is left
        /// (see <see cref="Platform.Frame.Budget"/>) is coarser than the slice being measured.
        /// </summary>
        private const int RowWarmupRowsPerTick = 400;

        /// <summary>
        /// Starts building the prop rows in the background, on the first sign the player is heading for the
        /// object list. Does nothing once they are built, so it is safe to call on every menu opening.
        /// </summary>
        private void BeginObjectRowWarmup()
        {
            if (_rowWarmupStarted) return;
            _rowWarmupStarted = true;

            // A snapshot, so that a model folded into the database later cannot invalidate the walk. Rows the
            // player got to first are already in _rowsByModel and are skipped when the queue reaches them.
            _rowWarmupQueue = new List<string>(ObjectDatabase.MainDb.Keys);
            _rowWarmupIndex = 0;
        }

        /// <summary>
        /// Builds the next slice of the prop rows. Whatever is left when the player reaches a category is
        /// built on the spot by <see cref="RowsFor"/>, so this only ever has to be fast, never complete.
        /// </summary>
        private void WarmObjectRows()
        {
            if (_rowWarmupQueue == null) return;

            int end = Math.Min(_rowWarmupIndex + RowWarmupRowsPerTick, _rowWarmupQueue.Count);
            while (_rowWarmupIndex < end)
                RowFor(ObjectTypes.Prop, _rowWarmupQueue[_rowWarmupIndex++]);

            if (_rowWarmupIndex < _rowWarmupQueue.Count) return;

            _rowWarmupQueue = null;
        }

        private void BuildSettingsMenu()
        {
            var possibleLangauges = new List<string> { "Auto" };
            possibleLangauges.AddRange(Translation.Translations.Select(t => t.Language).ToList());

            var language = new NativeListItem<string>(Translation.Translate("Language"), possibleLangauges.ToArray())
            {
                SelectedIndex = Math.Max(0, possibleLangauges.IndexOf(_settings.Translation)),
            };
            language.ItemChanged += (sender, e) =>
            {
                var newLanguage = e.Object;
                Translation.SetLanguage(newLanguage);
                _settings.Translation = newLanguage;
                SaveSettings();
                if (newLanguage == "Auto")
                {
                    language.Description = "Use your game's language settings.";
                    return;
                }
                var descFile = Translation.Translations.FirstOrDefault(t => t.Language == newLanguage);
                if (descFile == null) return;
                language.Description = "~h~" + Translation.Translate("Translator") + ":~h~ " + descFile.Translator;
            };

            var crosshairNames = Enum.GetNames(typeof(CrosshairType));
            var checkem = new NativeListItem<string>(Translation.Translate("Marker"), crosshairNames)
            {
                SelectedIndex = Math.Max(0, crosshairNames.ToList().FindIndex(x => x == _settings.CrosshairType.ToString())),
            };
            checkem.ItemChanged += (sender, e) =>
            {
                CrosshairType outHash;
                Enum.TryParse(e.Object, out outHash);
                _settings.CrosshairType = outHash;
                SaveSettings();
            };

            var disableText = Translation.Translate("Disable");
            var autosaveList = new List<string> { disableText };
            for (int i = 5; i <= 60; i += 5)
                autosaveList.Add(i.ToString(CultureInfo.InvariantCulture));

            int aIndex = autosaveList.IndexOf(_settings.AutosaveInterval.ToString(CultureInfo.InvariantCulture));
            if (aIndex == -1) aIndex = 0;

            var autosaveItem = new NativeListItem<string>(Translation.Translate("Autosave Interval"),
                Translation.Translate("Interval in minutes between automatic autosaves."), autosaveList.ToArray())
            {
                SelectedIndex = aIndex,
            };
            autosaveItem.ItemChanged += (sender, e) =>
            {
                _settings.AutosaveInterval = e.Object == disableText ? -1 : Convert.ToInt32(e.Object, CultureInfo.InvariantCulture);
                SaveSettings();
            };

            var defaultText = Translation.Translate("Default");
            var possibleDrawDistances = new List<string> { defaultText, "50", "75" };
            for (int i = 100; i <= 3000; i += 100)
                possibleDrawDistances.Add(i.ToString(CultureInfo.InvariantCulture));

            int dIndex = possibleDrawDistances.IndexOf(_settings.DrawDistance.ToString(CultureInfo.InvariantCulture));
            if (dIndex == -1) dIndex = 0;

            var drawDistanceItem = new NativeListItem<string>(Translation.Translate("Draw Distance"),
                Translation.Translate("Draw distance for props, vehicles and peds. Reload the map for changes to take effect."),
                possibleDrawDistances.ToArray())
            {
                SelectedIndex = dIndex,
            };
            drawDistanceItem.ItemChanged += (sender, e) =>
            {
                _settings.DrawDistance = e.Object == defaultText ? -1 : Convert.ToInt32(e.Object, CultureInfo.InvariantCulture);
                SaveSettings();
            };

            var streamingRanges = new List<string> { disableText };
            for (int i = 100; i <= 1000; i += 100)
                streamingRanges.Add(i.ToString(CultureInfo.InvariantCulture));
            streamingRanges.Add("1500");
            streamingRanges.Add("2000");
            streamingRanges.Add("3000");

            int sIndex = streamingRanges.IndexOf(_settings.StreamingRange.ToString(CultureInfo.InvariantCulture));
            if (sIndex == -1) sIndex = streamingRanges.IndexOf(SmartStreaming.DefaultRange.ToString(CultureInfo.InvariantCulture));

            var streamingItem = new NativeListItem<string>(Translation.Translate("Streaming Range"),
                Translation.Translate("How far away a placed prop, vehicle or ped is unloaded from the world, in metres." +
                                      " It comes back when you return. Applies to the map you are building and to autoloaded maps."),
                streamingRanges.ToArray())
            {
                SelectedIndex = ClampIndex(sIndex, streamingRanges.Count),
            };
            streamingItem.ItemChanged += (sender, e) =>
            {
                _settings.StreamingRange = e.Object == disableText
                    ? SmartStreaming.Off
                    : Convert.ToInt32(e.Object, CultureInfo.InvariantCulture);
                SmartStreaming.Range = _settings.StreamingRange;
                SaveSettings();
            };

            var senslist = Enumerable.Range(1, 59).ToArray();

            var gamboy = new NativeListItem<int>(Translation.Translate("Mouse Camera Sensitivity"), senslist)
            {
                SelectedIndex = ClampIndex(_settings.CameraSensivity - 1, senslist.Length),
            };
            gamboy.ItemChanged += (sender, e) =>
            {
                _settings.CameraSensivity = e.Object;
                SaveSettings();
            };

            var gampadSens = new NativeListItem<int>(Translation.Translate("Gamepad Camera Sensitivity"), senslist)
            {
                SelectedIndex = ClampIndex(_settings.GamepadCameraSensitivity - 1, senslist.Length),
            };
            gampadSens.ItemChanged += (sender, e) =>
            {
                _settings.GamepadCameraSensitivity = e.Object;
                SaveSettings();
            };

            var keymovesens = new NativeListItem<int>(Translation.Translate("Keyboard Movement Sensitivity"), senslist)
            {
                SelectedIndex = ClampIndex(_settings.KeyboardMovementSensitivity - 1, senslist.Length),
            };
            keymovesens.ItemChanged += (sender, e) =>
            {
                _settings.KeyboardMovementSensitivity = e.Object;
                SaveSettings();
            };

            var gammovesens = new NativeListItem<int>(Translation.Translate("Gamepad Movement Sensitivity"), senslist)
            {
                SelectedIndex = ClampIndex(_settings.GamepadMovementSensitivity - 1, senslist.Length),
            };
            gammovesens.ItemChanged += (sender, e) =>
            {
                _settings.GamepadMovementSensitivity = e.Object;
                SaveSettings();
            };

            var butts = new NativeCheckboxItem(Translation.Translate("Instructional Buttons"), _settings.InstructionalButtons);
            butts.CheckboxChanged += (sender, e) =>
            {
                _settings.InstructionalButtons = butts.Checked;
                SaveSettings();
            };

            var gamepadItem = new NativeCheckboxItem(Translation.Translate("Enable Gamepad Shortcut"), _settings.Gamepad);
            gamepadItem.CheckboxChanged += (sender, e) =>
            {
                _settings.Gamepad = gamepadItem.Checked;
                SaveSettings();
            };

            var counterItem = new NativeCheckboxItem(Translation.Translate("Entity Counter"), _settings.PropCounterDisplay);
            counterItem.CheckboxChanged += (sender, e) =>
            {
                _settings.PropCounterDisplay = counterItem.Checked;
                SaveSettings();
            };

            var snapper = new NativeCheckboxItem(Translation.Translate("Follow Object With Camera"), _settings.SnapCameraToSelectedObject);
            snapper.CheckboxChanged += (sender, e) =>
            {
                _settings.SnapCameraToSelectedObject = snapper.Checked;
                SaveSettings();
            };

            var boundItem = new NativeCheckboxItem(Translation.Translate("Bounding Box"), _settings.BoundingBox.GetValueOrDefault(false));
            boundItem.CheckboxChanged += (sender, e) =>
            {
                _settings.BoundingBox = boundItem.Checked;
                SaveSettings();
            };

            var worldNamesItem = new NativeCheckboxItem(Translation.Translate("World Object Names"), Translation.Translate(
                "Shows the model name of the game's own objects around you, so you can copy or favorite them by sight."),
                _settings.WorldObjectNames);
            worldNamesItem.CheckboxChanged += (sender, e) =>
            {
                _settings.WorldObjectNames = worldNamesItem.Checked;
                SaveSettings();
            };

            // No "Networked Entities" setting: the editor's entities are always local, and a finished map
            // reaches other players by being published rather than replicated out of somebody's editor.
            // See PropStreamer.Local.

            var objectValidationItem = new NativeCheckboxItem(Translation.Translate("Mark Unavailable Objects"),
                Translation.Translate("Marks the objects the game has no model for where you are standing. " +
                                      "Turning this off hides the mark; it does not make those objects load."),
                _settings.OmitInvalidObjects);
            objectValidationItem.CheckboxChanged += (sender, e) =>
            {
                _settings.OmitInvalidObjects = objectValidationItem.Checked;
                ObjectDatabase.FlagUnavailable = objectValidationItem.Checked;
                SaveSettings();
                RedrawObjectsMenu(_currentCategory, _currentObjectType);
            };

            var resetInvalid = new NativeItem(Translation.Translate("Re-check Objects Here"), Translation.Translate(
                "Asks the game about every object again, from where you are standing now. Worth doing after " +
                "walking into an interior: its props are only registered while you are near it."));
            resetInvalid.Activated += (men, item) =>
            {
                ObjectDatabase.ForgetAvailability();
                RedrawObjectsMenu(_currentCategory, _currentObjectType);
            };

            // Once it put the group back on peds that had turned on the player mid-build; nothing in the
            // editor wears a group any more (see PropStreamer.HoldStill), so what it resets now is the
            // setting itself — the row that used to undo the damage undoes the cause.
            var resetGrps = new NativeItem(Translation.Translate("Reset Active Relationship Groups"),
                Translation.Translate("Set every ped in this map back to Companion, so that none of them " +
                                      "fights anybody once the map is published."));
            resetGrps.Activated += (men, item) =>
            {
                PropStreamer.Peds.ForEach(handle => PropStreamer.ActiveRelationships[handle] = "Companion");
                foreach (var streamed in PropStreamer.StreamedOut)
                {
                    if (streamed.Object.Type == ObjectTypes.Ped) streamed.Object.Relationship = "Companion";
                }
                _changesMade++;
            };

            _settingsMenu.Add(language);
            _settingsMenu.Add(gamepadItem);
            _settingsMenu.Add(drawDistanceItem);
            _settingsMenu.Add(streamingItem);
            _settingsMenu.Add(autosaveItem);
            _settingsMenu.Add(checkem);
            _settingsMenu.Add(boundItem);
            _settingsMenu.Add(gamboy);
            _settingsMenu.Add(gampadSens);
            _settingsMenu.Add(keymovesens);
            _settingsMenu.Add(gammovesens);
            _settingsMenu.Add(butts);
            _settingsMenu.Add(counterItem);
            _settingsMenu.Add(snapper);
            _settingsMenu.Add(worldNamesItem);
            _settingsMenu.Add(objectValidationItem);
            _settingsMenu.Add(resetInvalid);
            _settingsMenu.Add(resetGrps);
            _settingsMenu.SelectedIndex = 0;
            _settingsMenu.Buttons.Visible = false;
        }

        private static int ClampIndex(int index, int count)
        {
            if (index < 0) return 0;
            if (index >= count) return count - 1;
            return index;
        }

        // --- The preview prop -------------------------------------------------------------------------
        //
        // Loading a model is asynchronous and the player scrolls faster than the streamer answers, so the
        // selection only records what is wanted and one loader runs behind it, re-reading that field each
        // time it finishes. Scrolling through fifty rows starts one load, not fifty.

        /// <summary>The model the cursor is on right now, which is not necessarily the one being loaded.</summary>
        private int _previewWantedHash;

        /// <summary>The row that hash came from, so an unloadable model is flagged on the right row.</summary>
        private NativeItem _previewWantedRow;

        private bool _previewLoading;

        private void OnIndexChange(object sender, SelectedEventArgs e)
        {
            var menu = (NativeMenu) sender;
            OnIndexChange(menu, e);
        }

        private void OnIndexChange(NativeMenu sender, SelectedEventArgs e)
        {
            // Rebuilding a menu moves its selection, which raises this event. Without this guard that would
            // spawn a preview prop while the player is somewhere else entirely, e.g. in the settings menu.
            if (!_isChoosingObject) return;

            int index = e.Index;
            if (index < 0 || index >= sender.Items.Count) return;

            // The DLC filter shares the object list, and there is no model behind it to preview.
            if (ReferenceEquals(sender.Items[index], _dlcFilterItem)) return;

            int requestedHash;
            var title = sender.Items[index].Title;
            switch (_currentObjectType)
            {
                case ObjectTypes.Prop:
                    if (!ObjectDatabase.MainDb.TryGetValue(title, out requestedHash)) return;
                    break;
                case ObjectTypes.Vehicle:
                    if (!ObjectDatabase.VehicleDb.TryGetValue(title, out requestedHash)) return;
                    break;
                case ObjectTypes.Ped:
                    if (!ObjectDatabase.PedDb.TryGetValue(title, out requestedHash)) return;
                    break;
                default:
                    return;
            }

            if (_previewProp != null && _previewProp.Model.Hash == requestedHash) return;

            _previewWantedHash = requestedHash;
            _previewWantedRow = sender.Items[index];

            // The loader already running will pick this up when its current model lands.
            if (_previewLoading) return;

            _previewLoading = true;
            Log.Guard("Preview model", LoadPreviewProp);
        }

        /// <summary>
        /// Keeps the preview spot showing whatever the cursor is on, one model at a time, until the two
        /// agree. A blacklisted model is re-checked rather than skipped: rejecting a missing model costs two
        /// native calls, and skipping it here is what would make a single bad check permanent.
        /// </summary>
        private async Task LoadPreviewProp()
        {
            try
            {
                while (true)
                {
                    int hash = _previewWantedHash;
                    var row = _previewWantedRow;

                    if (!_isChoosingObject) return;
                    if (_previewProp != null && _previewProp.Model.Hash == hash) return;

                    var request = await ObjectPreview.LoadObject(hash);

                    // The player scrolled on while the streamer was working. Start again on what they are
                    // looking at now, and let the model they have left behind go.
                    if (hash != _previewWantedHash)
                    {
                        if (request.Loaded) request.Model.MarkAsNoLongerNeeded();
                        continue;
                    }

                    if (!_isChoosingObject)
                    {
                        if (request.Loaded) request.Model.MarkAsNoLongerNeeded();
                        return;
                    }

                    if (!request.Loaded)
                    {
                        FlagFailedRow(row, hash);
                        return;
                    }

                    // The model is here, whatever the row said a moment ago.
                    if (row != null)
                    {
                        row.AltTitle = string.Empty;
                        row.Description = string.Empty;
                    }

                    // Local, not networked: this is a mannequin standing in a field nobody else is in, and
                    // every scroll of the list would otherwise push an entity through the network.
                    Entity spawned = null;
                    switch (_currentObjectType)
                    {
                        case ObjectTypes.Prop:
                            spawned = Compat.PropFrom(Natives.CreateObjectNoOffset(hash, _objectPreviewPos, false, false));
                            break;
                        case ObjectTypes.Vehicle:
                            spawned = Compat.VehicleFrom(Natives.CreateVehicle(hash, _objectPreviewPos, 0f, false));
                            break;
                        case ObjectTypes.Ped:
                            spawned = Compat.PedFrom(Natives.CreatePed(hash, _objectPreviewPos, 0f, false));
                            break;
                    }

                    request.Model.MarkAsNoLongerNeeded();

                    if (spawned != null)
                    {
                        // Nothing else owns a local entity, so without this the population cleanup is free to
                        // take the preview away while the player is looking at it.
                        spawned.IsPersistent = true;
                        spawned.IsPositionFrozen = true;
                        spawned.Rotation = new Vector3(0, 0, 180f);
                        if (spawned.Model.IsPed)
                            spawned.Heading = 180f;
                    }

                    // One last look: the cursor can have moved while the entity was being built.
                    if (hash != _previewWantedHash || !_isChoosingObject)
                    {
                        spawned?.Delete();
                        if (!_isChoosingObject) return;
                        continue;
                    }

                    _previewProp?.Delete();
                    _previewProp = spawned;
                    return;
                }
            }
            finally
            {
                _previewLoading = false;
            }
        }

        private void OnObjectSelect(object sender, ItemActivatedArgs e)
        {
            // Activating the DLC filter must not try to place it as if it were a model.
            if (ReferenceEquals(e.Item, _dlcFilterItem)) return;

            var title = e.Item.Title;
            int objectHash;

            switch (_currentObjectType)
            {
                case ObjectTypes.Prop:
                    if (!ObjectDatabase.MainDb.TryGetValue(title, out objectHash)) return;
                    break;
                case ObjectTypes.Vehicle:
                    if (!ObjectDatabase.VehicleDb.TryGetValue(title, out objectHash)) return;
                    break;
                case ObjectTypes.Ped:
                    if (!ObjectDatabase.PedDb.TryGetValue(title, out objectHash)) return;
                    break;
                default:
                    return;
            }

            if (PropStreamer.EntityCount == 0)
                _lastAutosave = Clock.Milliseconds;

            _quitWithSearchVisible = _searchMenu.Visible;
            var type = _currentObjectType;

            // The picker comes down before the model has been waited for: the menu closing is the answer to
            // the player's request, and the prop lands under the crosshair a moment later.
            _isChoosingObject = false;
            SetMenuVisible(_objectsMenu, false);
            SetMenuVisible(_searchMenu, false);
            _previewProp?.Delete();
            _previewProp = null;

            Run("Place object", async () =>
            {
                var request = await ObjectPreview.LoadObject(objectHash);
                if (!request.Loaded) return;

                var at = VectorExtensions.RaycastEverything(new Vector2(0f, 0f));
                Entity placed = null;

                switch (type)
                {
                    case ObjectTypes.Prop:
                        placed = await PropStreamer.CreateProp(request.Model, at, new Vector3(0, 0, 0), false,
                            force: true, drawDistance: _settings.DrawDistance);
                        break;
                    case ObjectTypes.Vehicle:
                        placed = await PropStreamer.CreateVehicle(request.Model, at, 0f, true,
                            drawDistance: _settings.DrawDistance);
                        break;
                    case ObjectTypes.Ped:
                        placed = await PropStreamer.CreatePed(request.Model, at, 0f, true,
                            drawDistance: _settings.DrawDistance);
                        if (placed != null)
                        {
                            PropStreamer.ActiveScenarios[placed.Handle] = "None";
                            PropStreamer.ActiveRelationships[placed.Handle] = DefaultRelationship.ToString();
                            PropStreamer.ActiveWeapons[placed.Handle] = WeaponHash.Unarmed;
                        }
                        break;
                }

                if (placed == null) return;

                AddItemToEntityMenu(placed);
                _snappedProp = placed;
                _changesMade++;
            });
        }

        /// <summary>
        /// Lists the categories available for <paramref name="type"/>. Picking one fills
        /// <see cref="_objectsMenu"/> via <see cref="OnCategorySelect"/>.
        /// </summary>
        private void RedrawCategoriesMenu(ObjectTypes type = ObjectTypes.Prop)
        {
            var items = ObjectCategories.For(type)
                .Select(category => new NativeItem(category.Name)
                {
                    AltTitle = category.Objects.Count.ToString(CultureInfo.InvariantCulture),
                })
                .ToList();

            _categoriesMenu.Name = "~b~" + Translation.Translate("PLACE") + " " + type.ToString().ToUpper();
            SetMenuItems(_categoriesMenu, null, items, 0);
        }

        private void OnCategorySelect(object sender, ItemActivatedArgs e)
        {
            var categories = ObjectCategories.For(_currentObjectType);
            int index = _categoriesMenu.Items.IndexOf(e.Item);
            if (index < 0 || index >= categories.Count) return;

            var category = categories[index];

            // Favorites is the one category that can be empty, and an empty menu draws as a bare header
            // with nothing to move onto and no row to back out of.
            if (category.Objects.Count == 0)
            {
                Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate(
                    "No favorites yet. Highlight an object while browsing and press the favorite button to add it."));
                return;
            }

            _currentCategory = category;
            RedrawObjectsMenu(_currentCategory, _currentObjectType);
            _objectsMenu.Name = "~b~" + _currentCategory.Name.ToUpper();

            SetMenuVisible(_categoriesMenu, false);
            SetMenuVisible(_objectsMenu, true);
            OnIndexChange(_objectsMenu, new SelectedEventArgs(_objectsMenu.SelectedIndex, 0));
        }

        /// <summary>
        /// Hands a menu its rows in one pass, laying it out exactly once.
        /// </summary>
        /// <remarks>
        /// <see cref="NativeMenu.Add(NativeItem)"/> lays the whole menu out again on every call, measuring
        /// each on-screen row's text through game natives. Filling a 25,000-row prop list one row at a time
        /// therefore costs 25,000 layouts and stalls the game for seconds. NativeMenu exposes the backing
        /// list, so the rows go in without a layout each and the <see cref="NativeMenu.SelectedIndex"/>
        /// setter at the end lays the menu out once.
        /// </remarks>
        private static void SetMenuItems(NativeMenu menu, NativeItem header, List<NativeItem> items, int selectedIndex)
        {
            menu.Clear();

            if (header != null)
                menu.Items.Add(header);
            if (items != null)
                menu.Items.AddRange(items);

            if (menu.Items.Count == 0) return;
            menu.SelectedIndex = ClampIndex(selectedIndex, menu.Items.Count);
        }

        /// <summary>
        /// The menu row standing for one model, built once and shared by every category that lists it —
        /// <see cref="_objectsMenu"/> only shows one category at a time. Worth keeping because
        /// <see cref="NativeItem"/>'s constructor resolves the screen size through the game, so 25,000 props
        /// would otherwise mean 25,000 trips into it.
        /// </summary>
        private NativeItem RowFor(ObjectTypes type, string model)
        {
            Dictionary<string, NativeItem> rows;
            if (!_rowsByModel.TryGetValue(type, out rows))
                _rowsByModel[type] = rows = new Dictionary<string, NativeItem>();

            NativeItem row;
            if (!rows.TryGetValue(model, out row))
            {
                rows[model] = row = new NativeItem(model);
                // Only has to be read here: the row is shared by every list the model appears in, so
                // ToggleFavorite re-badging the one instance keeps all of them in step from then on.
                if (Favorites.IsFavorite(type, model))
                    row.LeftBadgeSet = StarBadge;
            }
            return row;
        }

        /// <summary>
        /// Stars or unstars the highlighted model, from the object list or the search results alike — the
        /// point of the list being to catch the model the player just went looking for.
        /// </summary>
        private void ToggleFavorite()
        {
            var menu = _searchMenu.Visible ? _searchMenu : (_objectsMenu.Visible ? _objectsMenu : null);
            if (menu == null) return;

            int index = menu.SelectedIndex;
            if (index < 0 || index >= menu.Items.Count) return;

            var item = menu.Items[index];
            // The DLC filter shares the object list, and there is no model behind it to star.
            if (ReferenceEquals(item, _dlcFilterItem)) return;

            bool? result = ToggleFavoriteModel(_currentObjectType, item.Title);
            if (result == null) return;

            bool starred = result.Value;
            var favorites = Favorites.CategoryFor(_currentObjectType);

            // Unstarring from inside the favorites list has to take the row out from under the player.
            if (starred || menu != _objectsMenu || !ReferenceEquals(_currentCategory, favorites)) return;

            if (favorites.Objects.Count == 0)
            {
                // Nothing left to browse, and an empty menu offers no way back: step up a level instead.
                _previewProp?.Delete();
                _previewProp = null;
                _currentCategory = null;
                SetMenuVisible(_objectsMenu, false);
                SetMenuVisible(_categoriesMenu, true);
                return;
            }

            // Holding the index rather than the row lands the cursor on whatever moved up into its place.
            RedrawObjectsMenu(favorites, _currentObjectType, index);
            OnIndexChange(_objectsMenu, new SelectedEventArgs(_objectsMenu.SelectedIndex, 0));
        }

        /// <summary>
        /// Stars or unstars a model and brings everything that shows it back in step, whether the player got to
        /// it through the object list or by aiming at one standing in the world. Returns whether the model ended
        /// up starred, or null when no object list holds it and there is nothing to star.
        /// </summary>
        private bool? ToggleFavoriteModel(ObjectTypes type, string model)
        {
            var db = ObjectDatabase.DbFor(type);
            if (db == null || !db.ContainsKey(model)) return null;

            bool starred = Favorites.Toggle(type, model);

            // Every list the model appears in hands out the one row (see RowFor), so re-badging that instance
            // badges it everywhere at once. There is no row yet if the player has never browsed to the model.
            Dictionary<string, NativeItem> rows;
            NativeItem row;
            if (_rowsByModel.TryGetValue(type, out rows) && rows.TryGetValue(model, out row))
                row.LeftBadgeSet = starred ? StarBadge : null;

            var favorites = Favorites.CategoryFor(type);

            // The category just gained or lost a model, so the rows built for it no longer describe it.
            _rowsByCategory.Remove(favorites);
            RefreshCategoryCount(favorites);

            Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + model + "~n~" +
                Translation.Translate(starred ? "Added to Favorites" : "Removed from Favorites"));

            return starred;
        }

        /// <summary>
        /// Re-reads a category's object count onto its row in <see cref="_categoriesMenu"/>. Favorites is
        /// the only category that changes while the menus are up, and the player can return to that menu
        /// without it being rebuilt.
        /// </summary>
        private void RefreshCategoryCount(ObjectCategory category)
        {
            // The rows were built straight off this list, so they line up index for index.
            int index = ObjectCategories.For(_currentObjectType).IndexOf(category);
            if (index < 0 || index >= _categoriesMenu.Items.Count) return;

            _categoriesMenu.Items[index].AltTitle = category.Objects.Count.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The description a flagged row carries, so that the label is not the only thing the player has to
        /// go on. Kept next to the label it belongs with rather than inlined twice.
        /// </summary>
        private const string UnavailableHint =
            "The game has no archetype for this model where you are. Interior and DLC props are only " +
            "registered near the interior they belong to — go there, and this row stops saying it.";

        /// <summary>
        /// Asks the game whether it currently knows a prop's model, and labels the row with the answer.
        ///
        /// The answer is about the place as much as the model — an archetype defined in an interior .#typ is
        /// only registered while that interior's map data is streamed in — so it cannot be baked into the row
        /// at build time and it cannot be remembered between sessions. It is re-asked, cheaply and from the
        /// cache, every time the list is laid out; see <see cref="ObjectDatabase.IsAvailableHere"/>.
        ///
        /// Only written when it actually changes: the setter lays the row out, which costs a text
        /// measurement, and the overwhelming majority of rows are not flagged and never change.
        /// </summary>
        private static void RefreshAvailabilityFlag(NativeItem row, int hash)
        {
            bool unavailable = ObjectDatabase.FlagUnavailable && !ObjectDatabase.IsAvailableHere(hash);
            var flag = unavailable ? "~o~" + Translation.Translate("N/A here") : string.Empty;

            if (row.AltTitle == flag) return;

            row.AltTitle = flag;
            row.Description = unavailable ? Translation.Translate(UnavailableHint) : string.Empty;
        }

        /// <summary>
        /// Labels the row of a model that would not load. Which label it gets is the difference between "not
        /// here" and "not now": a model the game has no archetype for at this spot is a place problem and the
        /// player can walk out of it, while one that is registered and still did not arrive in two hundred
        /// frames is a busy streamer and the player can simply ask again.
        /// </summary>
        private static void FlagFailedRow(NativeItem row, int hash)
        {
            if (row == null) return;

            if (!ObjectDatabase.IsAvailableHere(hash))
            {
                RefreshAvailabilityFlag(row, hash);
                return;
            }

            row.AltTitle = "~r~" + Translation.Translate("Timed out");
            row.Description = Translation.Translate("The streamer did not deliver this model in time. Try it again.");
        }

        /// <summary>
        /// The rows of <paramref name="category"/>, grouped the way the DLC filter reads them, built on the
        /// first visit and kept. Only prop model names carry a pack prefix; the vehicle and ped lists are
        /// keyed by the game's own enum names ("Deveste", "Hooker01SFY"), which say nothing about the DLC
        /// that shipped them, so they are left ungrouped and get no filter.
        /// </summary>
        private CategoryRows RowsFor(ObjectCategory category, ObjectTypes type)
        {
            CategoryRows cached;
            if (_rowsByCategory.TryGetValue(category, out cached)) return cached;

            var rows = new CategoryRows();
            var all = new RowGroup();
            rows.ByDlc[Dlc.AllName] = all;

            foreach (var pair in category.Objects)
            {
                var row = RowFor(type, pair.Key);
                all.Items.Add(row);
                all.Hashes.Add(pair.Value);

                if (type != ObjectTypes.Prop) continue;

                var dlc = Dlc.NameFor(pair.Key);
                RowGroup group;
                if (!rows.ByDlc.TryGetValue(dlc, out group))
                    rows.ByDlc[dlc] = group = new RowGroup();
                group.Items.Add(row);
                group.Hashes.Add(pair.Value);
            }

            if (type == ObjectTypes.Prop)
            {
                var present = Dlc.Present(category.Objects.Keys);
                // A single value is always "All DLC" on its own: nothing to filter by.
                if (present.Count > 1)
                    rows.Dlcs.AddRange(present);
            }

            _rowsByCategory[category] = rows;
            return rows;
        }

        /// <summary>
        /// Builds the object list for a category, headed by the DLC filter. <paramref name="selectedIndex"/>
        /// overrides where the cursor lands, for a rebuild that has to leave it where the player put it.
        /// </summary>
        private void RedrawObjectsMenu(ObjectCategory category, ObjectTypes type = ObjectTypes.Prop, int selectedIndex = -1)
        {
            _dlcFilterItem = null;

            if (category == null)
            {
                _objectsMenu.Clear();
                return;
            }

            var rows = RowsFor(category, type);

            if (rows.Dlcs.Count > 0)
            {
                if (!rows.Dlcs.Contains(_dlcFilter))
                    _dlcFilter = Dlc.AllName;

                _dlcFilterItem = new NativeListItem<string>(Translation.Translate("DLC"), rows.Dlcs.ToArray())
                {
                    SelectedIndex = rows.Dlcs.IndexOf(_dlcFilter),
                };
                _dlcFilterItem.ItemChanged += (sender, e) =>
                {
                    _dlcFilter = e.Object;
                    FillObjectsMenu(rows, type, false);
                };
            }

            FillObjectsMenu(rows, type, true, selectedIndex);
        }

        /// <summary>
        /// Lists the rows the DLC filter lets through, under the filter row itself. The filter item is
        /// re-added rather than rebuilt: this also runs from that item's own ItemChanged, so the instance
        /// the menu is part-way through handling has to survive the refill.
        /// </summary>
        private void FillObjectsMenu(CategoryRows rows, ObjectTypes type, bool selectFirstObject, int selectedIndex = -1)
        {
            // Picking the filter's group is the whole cost of a filter change now: the rows behind it were
            // built and sorted into it when the category was first opened.
            RowGroup group;
            if (_dlcFilterItem == null || !rows.ByDlc.TryGetValue(_dlcFilter, out group))
                group = rows.ByDlc[Dlc.AllName];

            // Only props are checked against the game, so only they can be flagged.
            if (type == ObjectTypes.Prop)
            {
                for (int i = 0; i < group.Items.Count; i++)
                    RefreshAvailabilityFlag(group.Items[i], group.Hashes[i]);
            }

            if (_dlcFilterItem != null)
            {
                _dlcFilterItem.Description = string.Format(CultureInfo.InvariantCulture, "{0} ({1})",
                    Translation.Translate("Show only the objects one DLC added."), group.Items.Count);
            }

            // Opening a category lands on its first model, so that it previews something right away; a
            // change of filter keeps the cursor where the player left it, on the filter row.
            var selected = selectedIndex >= 0
                ? selectedIndex
                : selectFirstObject && _dlcFilterItem != null && group.Items.Count > 0 ? 1 : 0;
            SetMenuItems(_objectsMenu, _dlcFilterItem, group.Items, selected);
        }

        private bool ApplySearchQuery(string searchQuery, string modelName)
        {
            var q = searchQuery.ToLower();
            if (q.Contains(" or "))
            {
                var queries = Regex.Split(q, "\\s+or\\s+");
                return queries.Aggregate(false, (current, query) => current || ApplySearchQuery(query, modelName));
            }

            if (q.Contains(" and "))
            {
                var queries = Regex.Split(q, "\\s+and\\s+");
                return queries.Aggregate(true, (current, query) => current && ApplySearchQuery(query, modelName));
            }

            return modelName.ToLower().Contains(q);
        }

        /// <summary>
        /// Lists every model matching the query. A loose query matches thousands of them, so this fills the
        /// menu the same way a category does: shared rows, laid out in one pass. See <see cref="SetMenuItems"/>.
        /// </summary>
        private void RedrawSearchMenu(string searchQuery, ObjectTypes type = ObjectTypes.Prop)
        {
            var results = new List<NativeItem>();

            switch (type)
            {
                case ObjectTypes.Prop:
                    foreach (var u in ObjectDatabase.MainDb.Where(pair => ApplySearchQuery(searchQuery, pair.Key)))
                    {
                        var row = RowFor(type, u.Key);
                        RefreshAvailabilityFlag(row, u.Value);
                        results.Add(row);
                    }
                    break;
                case ObjectTypes.Vehicle:
                    foreach (var u in ObjectDatabase.VehicleDb.Where(pair => ApplySearchQuery(searchQuery, pair.Key)))
                        results.Add(RowFor(type, u.Key));
                    break;
                case ObjectTypes.Ped:
                    foreach (var u in ObjectDatabase.PedDb.Where(pair => ApplySearchQuery(searchQuery, pair.Key)))
                        results.Add(RowFor(type, u.Key));
                    break;
            }

            SetMenuItems(_searchMenu, null, results, 0);
        }

        private string GetSafeShortString(string input, int limit)
        {
            if (input == null) return null;
            if (input.Length > limit)
                return input.Substring(0, limit) + "...";
            return input;
        }

        /// <summary>
        /// The saved maps: this player's own and the server's shared ones, in one flat list.
        ///
        /// The SP build walked the file system from here — folders, a ".." row, and a peek inside every .xml
        /// to work out which trainer had written it. None of that exists here: the client cannot see a
        /// folder at all, only the catalogue the server hands it, and a map has no extension to read a
        /// format off. So the list is flat, and the format is worked out from the document itself when the
        /// map is actually opened — see <see cref="MapSerializer.Detect"/>.
        /// </summary>
        private void RedrawFilepickerMenu()
        {
            _filepicker.Clear();

            var maps = AllSavedMaps();
            _filepicker.Name = "~b~" + Translation.Translate("PICK MAP");

            if (maps.Count == 0)
            {
                _filepicker.Add(new NativeItem(Translation.Translate("No saved maps yet."),
                    Translation.Translate("Maps you save are kept in your own folder on this server."))
                {
                    Enabled = false,
                    LeftBadgeSet = AlertBadge,
                });
                return;
            }

            foreach (var map in maps)
            {
                var entry = map;

                // A published map already stands in everyone's world, so opening it would build a second copy
                // on top of the one the player sees. The row stays shut until somebody unloads it.
                var live = entry.Scope == MapScope.Shared && entry.Live;

                var item = new NativeItem(MapRowTitle(entry), DescribeMap(entry))
                {
                    LeftBadgeSet = live ? AlertBadge : FolderBadge,
                    Enabled = !live,
                };

                item.Activated += (sender, selectedItem) =>
                {
                    SetMenuVisible(_filepicker, false);
                    Run("Load map", () => LoadMap(entry));
                };

                _filepicker.Add(item);
            }

            RedrawDeleteMapMenu();
            var deleteItem = _filepicker.AddSubMenu(_deleteMapMenu);
            deleteItem.Title = "~r~" + Translation.Translate("Delete a Map");
            deleteItem.Description = Translation.Translate("Maps live in this server's storage, not in a folder, so this is the only way to remove one.");

            _filepicker.SelectedIndex = 0;
        }

        /// <summary>
        /// Every map the player can open, from both places one can live: the server's shared storage and
        /// their own folder on it. The shared ones go first — on a build server they are what everyone is
        /// actually looking for.
        /// </summary>
        private static List<MapEntry> AllSavedMaps()
        {
            var maps = MapStore.List(MapScope.Shared);
            maps.AddRange(MapStore.List(MapScope.Personal));
            return maps;
        }

        /// <summary>
        /// Which storage a row is in. The single-player build had folders to say this with; here the two
        /// scopes sit in one flat list, and without the marker a map published by someone else is
        /// indistinguishable from one the player saved themselves.
        /// </summary>
        private string MapRowTitle(MapEntry entry)
        {
            var name = GetSafeShortString(entry.Name, 40);
            return entry.Scope == MapScope.Shared ? "~c~[" + Translation.Translate("SERVER") + "]~s~ " + name : name;
        }

        /// <summary>What a saved map's row says about it. There is no metadata to read without opening the
        /// map itself, and opening every one of them to fill a menu is not worth the milliseconds.</summary>
        private string DescribeMap(MapEntry entry)
        {
            var description = string.Format(CultureInfo.InvariantCulture, "~h~{0}:~h~ {1:N0}",
                Translation.Translate("Size"), entry.Size);

            if (entry.SavedAt.HasValue)
                description += "\n~h~" + Translation.Translate("Saved") + ":~h~ " +
                    Clock.ToLocal(entry.SavedAt.Value).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            if (!string.IsNullOrEmpty(entry.Author))
                description += "\n~h~" + Translation.Translate("Published by") + ":~h~ " + GetSafeShortString(entry.Author, 32);

            if (entry.Live)
                description += "\n" + Translation.Translate(
                    "Loaded on the server: standing in everyone's world. Unload it to edit it.");

            return description;
        }

        private void RedrawDeleteMapMenu()
        {
            _deleteMapMenu.Clear();

            foreach (var map in AllSavedMaps())
            {
                var entry = map;

                // Removing a server map takes the same permission as publishing one; the server checks again,
                // and the greyed-out row only says why beforehand. A standing map cannot be deleted at all —
                // the file would go while the props stayed — so unloading comes first.
                var live = entry.Scope == MapScope.Shared && entry.Live;
                var mayDelete = !live && (entry.Scope == MapScope.Personal || MapStore.CanPublish);

                var item = new NativeItem(MapRowTitle(entry),
                    live
                        ? Translation.Translate("This map is loaded on the server. Unload it before deleting it.")
                        : mayDelete
                            ? Translation.Translate("Delete this map. It cannot be brought back.")
                            : Translation.Translate("You do not have permission to delete maps on this server."))
                {
                    Enabled = mayDelete,
                };

                item.Activated += (sender, args) => Run("Delete map", async () =>
                {
                    var error = await MapStore.DeleteAsync(entry);
                    if (error != null)
                    {
                        Compat.Notify("~r~~h~Map Editor~h~~w~~n~" + error);
                        return;
                    }

                    Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate("Deleted map") + " ~h~" + entry.Name + "~h~.");

                    // Both lists are now stale, and the one being backed out into is the picker.
                    RedrawDeleteMapMenu();
                    SetMenuVisible(_deleteMapMenu, false);
                    RedrawFilepickerMenu();
                    SetMenuVisible(_filepicker, true);
                });

                _deleteMapMenu.Add(item);
            }

            if (_deleteMapMenu.Items.Count > 0)
                _deleteMapMenu.SelectedIndex = 0;
        }

        /// <summary>
        /// One row per published map standing here, and the red row underneath that takes them all at once.
        /// Only ever reached with more than one map up — <see cref="UnloadAutoloadedMaps"/> unloads a lone map
        /// on the spot.
        ///
        /// Every row is a request to the server, and what it does it does for everybody: see
        /// <see cref="MapStore.UnloadAsync"/>.
        /// </summary>
        private void RedrawUnloadAutoloadedMenu()
        {
            _unloadAutoloadedMenu.Clear();

            var names = AutoloadedMaps.Names.ToList();
            for (var i = 0; i < names.Count; i++)
            {
                var index = i;
                var item = new NativeItem(GetSafeShortString(names[i], 40),
                    Translation.Translate("Take this map out of the world — everyone's world. The other published maps stay up."));

                // The menu is left where it is: unloading is a round trip now, and the list is renumbered
                // from what is actually standing once the server has answered. See RefreshServerMapMenus.
                item.Activated += (sender, args) => UnloadAutoloadedMap(index);

                _unloadAutoloadedMenu.Add(item);
            }

            var allItem = new NativeItem("~r~" + Translation.Translate("Unload All Maps"),
                Translation.Translate("Take every published map out of the world, for everyone on the server."));

            allItem.Activated += (sender, args) => UnloadAllAutoloadedMaps();

            _unloadAutoloadedMenu.Add(allItem);
            _unloadAutoloadedMenu.SelectedIndex = 0;
        }


        private void RedrawMetadataMenu()
        {
            _metadataMenu.Clear();

            var saveItem = new NativeItem(_saveScope == MapScope.Shared
                    ? Translation.Translate("Publish Map")
                    : Translation.Translate("Save Map"),
                Translation.Translate(_saveScope == MapScope.Shared
                    ? "Publish this map. It goes up in every player's world straight away, and out of your editor — unload it to edit it again."
                    : "Save this map to your own maps, where only you can see it."));

            saveItem.Activated += (sender, item) =>
            {
                SetMenuVisible(_metadataMenu, false);

                // Through Run: saving takes as many frames as the map has chunks, and the row must not
                // start a second upload while the first is still going out.
                Run("Save map", () => SaveMap(PropStreamer.CurrentMapMetadata.Name, _saveScope));
            };

            if (string.IsNullOrWhiteSpace(PropStreamer.CurrentMapMetadata.Name))
                saveItem.Enabled = false;

            {
                // One name, not a title and a path: there are no paths here, and a second name would only
                // give the two a chance to disagree. The map is stored and listed under this.
                //
                // It has no default on purpose: a new map arrives nameless, wearing the alert badge, and the
                // save row below stays greyed out until this one has been filled in.
                var nameItem = new NativeItem(Translation.Translate("Map Name"),
                    Translation.Translate("The name this map is saved and listed under. Required."));

                if (string.IsNullOrWhiteSpace(PropStreamer.CurrentMapMetadata.Name))
                    nameItem.RightBadgeSet = AlertBadge;
                else
                    nameItem.AltTitle = GetSafeShortString(PropStreamer.CurrentMapMetadata.Name, 20);

                nameItem.Activated += (sender, item) => Run("Map name", async () =>
                {
                    var newName = await Compat.GetUserInput(PropStreamer.CurrentMapMetadata.Name ?? "", MapNames.MaxLength);
                    if (string.IsNullOrWhiteSpace(newName)) return;

                    newName = newName.Trim();
                    if (!MapStore.IsValidName(newName))
                    {
                        Compat.Notify("~r~~h~Map Editor~h~~w~~n~" + Translation.Translate("A map name cannot contain ':' or '#'."));
                        return;
                    }

                    PropStreamer.CurrentMapMetadata.Name = newName;
                    saveItem.Enabled = true;

                    nameItem.RightBadgeSet = null;
                    nameItem.AltTitle = GetSafeShortString(newName, 20);
                });

                _metadataMenu.Add(nameItem);
            }

            {
                var authorItem = new NativeItem(Translation.Translate("Author"));

                if (!string.IsNullOrWhiteSpace(PropStreamer.CurrentMapMetadata.Creator))
                    authorItem.AltTitle = GetSafeShortString(PropStreamer.CurrentMapMetadata.Creator, 20);

                authorItem.Activated += (sender, item) => Run("Map author", async () =>
                {
                    var newName = await Compat.GetUserInput(PropStreamer.CurrentMapMetadata.Creator ?? "", 30);
                    if (string.IsNullOrWhiteSpace(newName)) return;
                    PropStreamer.CurrentMapMetadata.Creator = newName;
                    authorItem.AltTitle = GetSafeShortString(newName, 20);
                });

                _metadataMenu.Add(authorItem);
            }

            {
                var descItem = new NativeItem(Translation.Translate("Description"));

                if (!string.IsNullOrWhiteSpace(PropStreamer.CurrentMapMetadata.Description))
                    descItem.Description = PropStreamer.CurrentMapMetadata.Description;

                descItem.Activated += (sender, item) => Run("Map description", async () =>
                {
                    var newName = await Compat.GetUserInput(PropStreamer.CurrentMapMetadata.Description ?? "", 255);
                    if (string.IsNullOrWhiteSpace(newName)) return;
                    PropStreamer.CurrentMapMetadata.Description = newName;
                    descItem.Description = newName;
                });

                _metadataMenu.Add(descItem);
            }

            // No autoload checkbox: publishing a map *is* putting it into everyone's world, so there is
            // nothing to ask twice. Taking one back down is "Unload Server Maps" on the main menu.

            _metadataMenu.Add(saveItem);

            _metadataMenu.SelectedIndex = saveItem.Enabled ? _metadataMenu.Items.IndexOf(saveItem) : 0;
        }

        /// <summary>
        /// Where the map is going: the player's own maps, or the server's shared ones. Two rows, and that
        /// is the whole menu.
        ///
        /// It stands where the format menu used to. There is nothing to choose about the format any more —
        /// the single-player exports are gone, and a map is written in the editor's own JSON wherever it
        /// goes — and what did need choosing was the destination, which used to hide as a checkbox further
        /// in. Loading needs no such row: <see cref="RedrawFilepickerMenu"/> lists both storages at once.
        /// </summary>
        private void RedrawSaveTargetMenu()
        {
            _saveTargetMenu.Clear();

            var personal = new NativeItem(Translation.Translate("Save Local"),
                Translation.Translate("Keep this map to yourself. It is stored in your own folder on the server — nobody else sees it or can load it. This is also where autosaves go."))
            {
                Tag = MapScope.Personal,
            };

            if (!MapStore.CanSave)
            {
                personal.Enabled = false;
                personal.Description = Translation.Translate("You do not have permission to keep maps on this server.");
                personal.LeftBadgeSet = AlertBadge;
            }

            _saveTargetMenu.Add(personal);

            var shared = new NativeItem(Translation.Translate("Save on the Server"),
                Translation.Translate("Publish this map. It goes up in every player's world straight away and leaves your editor; unloading it — for everyone — is what makes it editable again."))
            {
                Tag = MapScope.Shared,
            };

            if (!MapStore.CanPublish)
            {
                // The server checks this again for itself; the row is greyed out only so that it says why
                // beforehand rather than after the upload.
                shared.Enabled = false;
                shared.Description = Translation.Translate("You do not have permission to save maps on this server.");
                shared.LeftBadgeSet = AlertBadge;
            }

            _saveTargetMenu.Add(shared);
            _saveTargetMenu.SelectedIndex = 0;
        }

        public void AddItemToEntityMenu(MapObject obj)
        {
            if (obj == null) return;
            var name = ObjectDatabase.MainDb.ContainsValue(obj.Hash) ? ObjectDatabase.MainDb.First(pair => pair.Value == obj.Hash).Key : "Unknown World Prop";
            _currentObjectsMenu.Add(new NativeItem("~h~[WORLD]~h~ " + name)
            {
                Tag = new EntityMenuTag { Kind = EntityMenuKind.World, WorldId = obj.Id },
            });
        }

        public void AddItemToEntityMenu(Marker mark)
        {
            if (mark == null) return;
            _currentObjectsMenu.Add(new NativeItem("~h~[MARK]~h~ " + mark.Type)
            {
                Tag = new EntityMenuTag { Kind = EntityMenuKind.Marker, Id = mark.Id },
            });
        }

        public void AddItemToEntityMenu(Laser laser)
        {
            if (laser == null) return;
            _currentObjectsMenu.Add(new NativeItem("~h~[LASER]~h~ " + laser.Pattern)
            {
                Tag = new EntityMenuTag { Kind = EntityMenuKind.Laser, Id = laser.Id },
            });
        }

        public void AddItemToEntityMenu(Entity ent)
        {
            if (ent == null) return;

            var type = ent is Vehicle ? ObjectTypes.Vehicle : ent is Ped ? ObjectTypes.Ped : ObjectTypes.Prop;
            _currentObjectsMenu.Add(new NativeItem(EntityMenuTitle(type, ent.Model.Hash))
            {
                Tag = new EntityMenuTag { Kind = EntityMenuKind.Entity, Id = ent.Handle },
            });
        }

        /// <summary>
        /// The row for an object of the map that is not in the world at the moment, loaded from a file with the
        /// player nowhere near it. It reads exactly like the row it will be once it has been walked up to; the
        /// player has no reason to care which of the two it is, and activating it puts it back either way.
        /// </summary>
        public void AddStreamedItemToEntityMenu(MapObject o, int uid)
        {
            if (o == null) return;

            _currentObjectsMenu.Add(new NativeItem(EntityMenuTitle(o.Type, o.Hash))
            {
                Tag = new EntityMenuTag { Kind = EntityMenuKind.Streamed, Id = uid },
            });
        }

        private static string EntityMenuTitle(ObjectTypes type, int hash)
        {
            switch (type)
            {
                case ObjectTypes.Vehicle:
                    return "~h~[VEH]~h~ " + (ObjectDatabase.VehicleDb.ContainsValue(hash)
                        ? ObjectDatabase.VehicleDb.First(x => x.Value == hash).Key.ToUpper()
                        : "Unknown Vehicle");
                case ObjectTypes.Ped:
                    return "~h~[PED]~h~ " + (ObjectDatabase.PedDb.ContainsValue(hash)
                        ? ObjectDatabase.PedDb.First(x => x.Value == hash).Key.ToUpper()
                        : "Unknown Ped");
                default:
                    return "~h~[PROP]~h~ " + (ObjectDatabase.MainDb.ContainsValue(hash)
                        ? ObjectDatabase.MainDb.First(pair => pair.Value == hash).Key
                        : "Unknown Prop");
            }
        }

        private void RemoveFromEntityMenu(Func<EntityMenuTag, bool> predicate)
        {
            var found = _currentObjectsMenu.Items.FirstOrDefault(item => item.Tag is EntityMenuTag tag && predicate(tag));
            if (found == null) return;
            _currentObjectsMenu.Remove(found);
            if (_currentObjectsMenu.Items.Count == 0)
                SetMenuVisible(_currentObjectsMenu, false);
        }

        public void RemoveItemFromEntityMenu(Entity ent)
        {
            RemoveFromEntityMenu(tag => tag.Kind == EntityMenuKind.Entity && tag.Id == ent.Handle);
        }

        public void RemoveItemFromEntityMenu(string id)
        {
            RemoveFromEntityMenu(tag => tag.Kind == EntityMenuKind.World && tag.WorldId == id);
        }

        public void RemoveMarkerFromEntityMenu(int id)
        {
            RemoveFromEntityMenu(tag => tag.Kind == EntityMenuKind.Marker && tag.Id == id);
        }

        public void RemoveLaserFromEntityMenu(int id)
        {
            RemoveFromEntityMenu(tag => tag.Kind == EntityMenuKind.Laser && tag.Id == id);
        }

        public void OnEntityTeleport(object sender, ItemActivatedArgs e)
        {
            if (!IsInFreecam) return;
            if (!(e.Item.Tag is EntityMenuTag tag)) return;

            switch (tag.Kind)
            {
                case EntityMenuKind.Laser:
                {
                    Laser tmpL = PropStreamer.Lasers.FirstOrDefault(l => l.Id == tag.Id);
                    if (tmpL == null) return;
                    _mainCamera.Position = tmpL.Position + new Vector3(5f, 5f, 10f);
                    if (_settings.SnapCameraToSelectedObject)
                        _mainCamera.PointAt(tmpL.Position);
                    CloseAllMenus();
                    _selectedLaser = tmpL;
                    RedrawObjectInfoMenu(_selectedLaser, true);
                    SetMenuVisible(_objectInfoMenu, true);
                    return;
                }
                case EntityMenuKind.World:
                {
                    var mapObj = PropStreamer.RemovedObjects.FirstOrDefault(obj => obj.Id == tag.WorldId);
                    if (mapObj == null) return;

                    // The object was hidden rather than deleted, so putting it back is a matter of saying so
                    // once. The SP build had to spawn a copy of it, because it had really been deleted.
                    PropStreamer.RestoreWorldObject(mapObj);
                    CloseAllMenus();
                    RemoveItemFromEntityMenu(mapObj.Id);
                    return;
                }
                case EntityMenuKind.Marker:
                {
                    Marker tmpM = PropStreamer.Markers.FirstOrDefault(m => m.Id == tag.Id);
                    if (tmpM == null) return;
                    _mainCamera.Position = tmpM.Position + new Vector3(5f, 5f, 10f);
                    if (_settings.SnapCameraToSelectedObject)
                        _mainCamera.PointAt(tmpM.Position);
                    CloseAllMenus();
                    _selectedMarker = tmpM;
                    RedrawObjectInfoMenu(_selectedMarker, true);
                    SetMenuVisible(_objectInfoMenu, true);
                    return;
                }
                case EntityMenuKind.Streamed:
                {
                    // Picked from the list rather than flown to, so it is nowhere near the player and not
                    // standing at the moment. It goes back into the world here, and holding it as the
                    // selected entity is what keeps it there while the player works on it.
                    var record = PropStreamer.StreamedOut.FirstOrDefault(s => s.Uid == tag.Id);
                    if (record == null) return;

                    Run("Stream in", async () =>
                    {
                        var restored = await StreamInNow(record);
                        if (restored == null) return;
                        SelectFromEntityMenu(restored);
                    });
                    return;
                }
                default:
                {
                    var prop = Compat.Ent(tag.Id);
                    if (prop == null || !prop.Exists()) return;
                    SelectFromEntityMenu(prop);
                    return;
                }
            }
        }

        private void SelectFromEntityMenu(Entity entity)
        {
            if (_settings.SnapCameraToSelectedObject)
            {
                _mainCamera.Position = entity.Position + new Vector3(5f, 5f, 10f);
                _mainCamera.PointAt(entity, Vector3.Zero);
            }
            CloseAllMenus();
            _selectedProp = entity;
            RedrawObjectInfoMenu(_selectedProp, true);
            SetMenuVisible(_objectInfoMenu, true);
        }
    }
}
