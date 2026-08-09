using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using MapEditor.Ui;
using MapEditor.Ui.Menus;
using MapEditor.Ui.Scaleform;
using MapEditor.Platform;
using Control = CitizenFX.Core.Control;

namespace MapEditor
{
    /// <summary>
    /// The editor. FiveM has no key events and no way to yield outside an async method, so the tick, input
    /// and teardown all belong to <see cref="MapEditorClient"/> and this is a plain class it drives.
    /// </summary>
    public partial class MapEditor
    {
        public static bool IsInFreecam;
        private bool _isChoosingObject;

        private readonly NativeMenu _categoriesMenu;
        private readonly NativeMenu _objectsMenu;
        private readonly NativeMenu _searchMenu;
        private readonly NativeMenu _mainMenu;
	    private readonly NativeMenu _saveTargetMenu;
        private readonly NativeMenu _metadataMenu;
	    private readonly NativeMenu _objectInfoMenu;
	    private readonly NativeMenu _settingsMenu;
	    private readonly NativeMenu _currentObjectsMenu;
        private readonly NativeMenu _filepicker;

        /// <summary>
        /// The saved maps, to take one away with. A player has no folder to delete a map out of, so without
        /// this row a saved map could never be removed at all.
        /// </summary>
        private readonly NativeMenu _deleteMapMenu;

		/// <summary>
		/// Only opened when more than one map is loaded: with a single map the row unloads it outright.
		/// </summary>
		private readonly NativeMenu _unloadAutoloadedMenu;

	    private readonly NativeItem _currentEntitiesItem;

		/// <summary>Greyed out until a map has actually been autoloaded, since there is nothing else to unload.</summary>
		private readonly NativeItem _unloadAutoloadedItem;

		/// <summary>Greyed out until an external resource subscribes through <see cref="ModManager"/>, which
		/// can happen at any time after startup.</summary>
		private readonly NativeItem _externalModItem;

        private readonly ObjectPool _menuPool = new ObjectPool();

        private Entity _previewProp;
        private Entity _snappedProp;
        private Entity _selectedProp;

        /// <summary>
        /// Entities picked with the multi-select control + Attack. While <see cref="_multiSelectionSnapped"/>
        /// is set they follow the crosshair as one rigid group, each keeping the offset at the same index in
        /// <see cref="_multiSelectionOffsets"/>.
        /// </summary>
        private readonly List<Entity> _multiSelection = new List<Entity>();
        private readonly List<Vector3> _multiSelectionOffsets = new List<Vector3>();
        private bool _multiSelectionSnapped;

        private Marker _snappedMarker;
	    private Marker _selectedMarker;

        private Camera _mainCamera;
        private Camera _objectPreviewCamera;

        /// <summary>
        /// Where the player stood when the freecam went up: the fallback for putting them back down when the
        /// camera is over somewhere no ground can be found. See <see cref="ReturnPlayerToGround"/>.
        /// </summary>
        private Vector3 _freecamEntryPosition;

        /// <summary>
        /// Interiors the freecam has found, mapped to where each was found. Pinned in memory and switched on
        /// again every frame until the freecam goes down. See <see cref="HoldInteriorsAroundCamera"/>.
        /// </summary>
        private readonly Dictionary<int, Vector3> _heldInteriors = new Dictionary<int, Vector3>();

        /// <summary>
        /// Where the camera was when the scan pass in progress started, not where it is now.
        /// </summary>
        private Vector3 _interiorScanOrigin;

        /// <summary>How far into <see cref="InteriorScanOffsets"/> the pass in progress has got.</summary>
        private int _interiorScanCursor;

        private readonly Vector3 _objectPreviewPos = new Vector3(1200.133f, 4000.958f, 85.9f);

        private bool _zAxis = true;
	    private bool _controlsRotate;
        private bool _quitWithSearchVisible;

	    private bool _hasLoaded;

	    /// <summary>Whether the frame before the first load has told the server what is about to run. See OnTick.</summary>
	    private bool _firstLoadAnnounced;
	    private int _mapObjCounter = 0;
	    private int _markerCounter = 0;

	    private const Relationship DefaultRelationship = Relationship.Companion;

	    private ObjectTypes _currentObjectType;

		/// <summary>Null while the player is still on <see cref="_categoriesMenu"/> and has not picked one.</summary>
		private ObjectCategory _currentCategory;

		/// <summary>
		/// The DLC <see cref="_objectsMenu"/> is narrowed to, kept across categories so the choice sticks
		/// while browsing. Reset to <see cref="Dlc.AllName"/> for a category that has no such objects.
		/// </summary>
		private string _dlcFilter = Dlc.AllName;

		/// <summary>
		/// The row that sets <see cref="_dlcFilter"/>, always first in <see cref="_objectsMenu"/>. Null for
		/// a list that offers no filter, i.e. vehicles and peds.
		/// </summary>
		private NativeListItem<string> _dlcFilterItem;

	    private readonly Settings _settings;

	    private readonly string[] _markersTypes = Enum.GetNames(typeof(MarkerType)).ToArray();

        /// <summary>Step used by every scrollable position/rotation/scale field, in units per input.</summary>
        private const float ScrollStep = 0.01f;

        private static readonly int _possibleRange = 1500000;

        /// <summary>
        /// The name the editor autosaves under. Stored like any other save, so it appears in the player's
        /// picker beside the rest and loads back the same way.
        /// </summary>
        private const string AutosaveName = "Autosave";

        // Autosaving
        private int _loadedEntities = 0;
        private int _changesMade = 0;
        /// <summary>Game-timer reading, not a wall clock — see <see cref="Clock"/>.</summary>
        private int _lastAutosave = Clock.Milliseconds;

        /// <summary>
        /// Guards the menu Closed handlers so that only a real user "back" triggers the
        /// editor-state cleanup, not our own programmatic show/hide calls.
        /// </summary>
        private bool _programmaticMenuChange;

        /// <summary>
        /// Set while a spawn is still in flight. Spawning is asynchronous — a model has to be waited for and
        /// FiveM has no fibers — so a held control would otherwise start the same job every frame and place
        /// a hundred copies of one object. See <see cref="Run"/>.
        /// </summary>
        private bool _busy;

        /// <summary>
        /// Which of the two storages "Save Map" writes to, picked in <see cref="_saveTargetMenu"/> and reset
        /// to the player's own folder every time saving starts afresh.
        /// </summary>
        private MapScope _saveScope = MapScope.Personal;

		public MapEditor(Settings settings)
        {
            _settings = settings;

			BuildInstructionalButtons();

			// No banner, so the UI layer reserves no space above the subtitle: the menu must sit at
			// the very top of the screen with no negative offset or it draws off-screen.
			_objectInfoMenu = new NativeMenu("", "~b~" + Translation.Translate("PROPERTIES"))
			{
			    Banner = null,
			};
			_objectInfoMenu.Buttons.Visible = false;
			_menuPool.Add(_objectInfoMenu);

			BuildPedComponentsMenu();
			BuildStackingMenu();
			BuildLoopingMenu();

			ModManager.InitMenu();

			_objectsMenu = new NativeMenu("Map Editor", "~b~" + Translation.Translate("PLACE OBJECT"));

            _categoriesMenu = new NativeMenu("Map Editor", "~b~" + Translation.Translate("PLACE OBJECT"));
            _categoriesMenu.ItemActivated += OnCategorySelect;
            _categoriesMenu.Closed += OnCategoriesMenuClosed;
            _menuPool.Add(_categoriesMenu);
            // Search spans every object, so it stays reachable without first entering a category.
            _categoriesMenu.Buttons.Add(new InstructionalButton(Translation.Translate("Search"), Control.Jump));
            RedrawCategoriesMenu();

            _objectsMenu.ItemActivated += OnObjectSelect;
            _objectsMenu.SelectedIndexChanged += OnIndexChange;
            _objectsMenu.Closed += OnObjectsMenuClosed;
            _menuPool.Add(_objectsMenu);

            _objectsMenu.Buttons.Add(new InstructionalButton(Translation.Translate("Change Axis"), Control.SelectWeapon));
            _objectsMenu.Buttons.Add(new InstructionalButton(Translation.Translate("Zoom"), Control.MoveUpDown));
            _objectsMenu.Buttons.Add(new InstructionalButton(Translation.Translate("Search"), Control.Jump));
            _objectsMenu.Buttons.Add(new InstructionalButton(Translation.Translate("Favorite"), Control.Context));

            _searchMenu = new NativeMenu("Map Editor", "~b~" + Translation.Translate("PLACE OBJECT"));
            _searchMenu.ItemActivated += OnObjectSelect;
            _searchMenu.SelectedIndexChanged += OnIndexChange;
            _searchMenu.Closed += OnSearchMenuClosed;
            _menuPool.Add(_searchMenu);

            _searchMenu.Buttons.Add(new InstructionalButton(Translation.Translate("Change Axis"), Control.SelectWeapon));
            _searchMenu.Buttons.Add(new InstructionalButton(Translation.Translate("Zoom"), Control.MoveUpDown));
            _searchMenu.Buttons.Add(new InstructionalButton(Translation.Translate("Search"), Control.Jump));
            _searchMenu.Buttons.Add(new InstructionalButton(Translation.Translate("Favorite"), Control.Context));

            _mainMenu = new NativeMenu("Map Editor", "~b~" + Translation.Translate("MAIN MENU"));
            _mainMenu.Buttons.Visible = false;
            // The earliest warning that the object list is coming: building its rows takes seconds, and this
            // spends them while the player is reading the main menu.
            _mainMenu.Shown += (sender, args) => BeginObjectRowWarmup();

            var enterExitItem = new NativeItem(Translation.Translate("Enter/Exit Map Editor"));
            enterExitItem.Activated += (sender, args) => ToggleFreecam();
            _mainMenu.Add(enterExitItem);

            var newMapItem = new NativeItem(Translation.Translate("New Map"), Translation.Translate("Remove all current objects and start a new map."));
            newMapItem.Activated += (sender, args) => NewMap();
            _mainMenu.Add(newMapItem);

            var saveMapItem = new NativeItem(Translation.Translate("Save Map"), Translation.Translate("Save all current objects."));
            saveMapItem.Activated += (sender, args) => BeginSaveMap();
            _mainMenu.Add(saveMapItem);

            var loadMapItem = new NativeItem(Translation.Translate("Load Map"), Translation.Translate("Load objects from a saved map and add them to the world."));
            loadMapItem.Activated += (sender, args) => BeginLoadMap();
            _mainMenu.Add(loadMapItem);

            _unloadAutoloadedItem = new NativeItem(Translation.Translate("Unload Server Maps"),
                Translation.Translate("Take a published map out of the world. It comes out of everyone's world, and it is what makes the map editable again."));
            _unloadAutoloadedItem.Activated += (sender, args) => UnloadAutoloadedMaps();
            _mainMenu.Add(_unloadAutoloadedItem);

            _menuPool.Add(_mainMenu);

            _unloadAutoloadedMenu = new NativeMenu("Map Editor", "~b~" + Translation.Translate("UNLOAD SERVER MAPS"));
            _unloadAutoloadedMenu.Buttons.Visible = false;
            _unloadAutoloadedMenu.Parent = _mainMenu;
            _menuPool.Add(_unloadAutoloadedMenu);

            // Anybody on the server published or unloaded a map. The menus are rebuilt explicitly because
            // neither redraws itself while open.
            MapStore.SharedMapsChanged = () => Log.Guard("Published maps changed", async () =>
            {
                await AutoloadedMaps.Sync();
                RefreshServerMapMenus();
            });

			_saveTargetMenu = new NativeMenu("Map Editor", "~b~" + Translation.Translate("SAVE MAP"));
			_saveTargetMenu.Buttons.Visible = false;
			_saveTargetMenu.Parent = _mainMenu;
	        RedrawSaveTargetMenu();
			_saveTargetMenu.ItemActivated += OnSaveTargetSelect;
			_menuPool.Add(_saveTargetMenu);

            _metadataMenu = new NativeMenu("Map Editor", "~b~" + Translation.Translate("SAVE MAP"));
            _metadataMenu.Buttons.Visible = false;
		    _metadataMenu.Parent = _saveTargetMenu;
		    RedrawMetadataMenu();
            _menuPool.Add(_metadataMenu);

            _filepicker = new NativeMenu("Map Editor", "~b~" + Translation.Translate("PICK MAP"));
            _filepicker.Buttons.Visible = false;
		    _filepicker.Parent = _mainMenu;
            _menuPool.Add(_filepicker);

            _deleteMapMenu = new NativeMenu("Map Editor", "~b~" + Translation.Translate("DELETE MAP"));
            _deleteMapMenu.Buttons.Visible = false;
            _deleteMapMenu.Parent = _filepicker;
            _menuPool.Add(_deleteMapMenu);

			_settingsMenu = new NativeMenu("Map Editor", "~b~" + Translation.Translate("SETTINGS"));
			BuildSettingsMenu();
			_menuPool.Add(_settingsMenu);

			_currentObjectsMenu = new NativeMenu("Map Editor", "~b~" + Translation.Translate("CURRENT ENTITES"));
	        _currentObjectsMenu.ItemActivated += OnEntityTeleport;
			_currentObjectsMenu.Buttons.Visible = false;
            _menuPool.Add(_currentObjectsMenu);

            // AddSubMenu's string overload sets the right-hand AltTitle, not the row title:
            // the title always comes from the submenu's Subtitle. Set it explicitly instead.
	        _currentEntitiesItem = _mainMenu.AddSubMenu(_currentObjectsMenu);
	        _currentEntitiesItem.Title = Translation.Translate("Current Entities");

	        var settingsItem = _mainMenu.AddSubMenu(_settingsMenu);
	        settingsItem.Title = Translation.Translate("Settings");

	        _externalModItem = _mainMenu.AddSubMenu(ModManager.ModMenu);
	        _externalModItem.Title = Translation.Translate("Create Map for External Mod");

			_mainMenu.SelectedIndex = 0;
			_menuPool.Add(ModManager.ModMenu);
        }

        /// <summary>Opens or closes the main menu. What the "mapeditor" command does.</summary>
        public void ToggleMenu()
        {
            // The server's answer to mapeditor.use. Honoured rather than enforced — a modified client would
            // ignore it — so what actually protects the server is the check on writing to its own storage.
            if (!MapStore.CanUseEditor)
            {
                // Named separately: a missing permission is between this player and the owner, while OneSync
                // being off is a server that cannot run the resource at all and is a one-line fix.
                Compat.Notify("~r~~h~Map Editor~h~~w~~n~" + Translation.Translate(
                    MapStore.ServerHasOneSync
                        ? "The map editor is not available on this server."
                        : "This server runs without OneSync, which the map editor requires. Ask the owner to start it with '+set onesync on'."));
                return;
            }

            if (_menuPool.AreAnyVisible && !_mainMenu.Visible) return;
            _mainMenu.Visible = !_mainMenu.Visible;
        }

        /// <summary>
        /// Starts a job that has to wait for something — a model, the on-screen keyboard — from a place that
        /// cannot wait: a tick or a menu handler, both of which return void.
        ///
        /// Only one job runs at a time, or a held control would queue up a spawn per frame.
        /// <see cref="Log.Guard"/> keeps an exception inside an async void from taking the resource down
        /// without a stack.
        /// </summary>
        private void Run(string context, Func<Task> body)
        {
            if (_busy) return;
            _busy = true;
            Log.Guard(context, async () =>
            {
                try
                {
                    await body();
                }
                finally
                {
                    _busy = false;
                }
            });
        }

        /// <summary>
        /// Show/hide a menu without tripping the Closed handlers that clean up editor state.
        /// </summary>
        private void SetMenuVisible(NativeMenu menu, bool visible)
        {
            // Showing a menu raises SelectedIndexChanged, which loads a preview model. Anything that throws
            // there must not leave the flag set, or every Closed handler is dead from then on.
            _programmaticMenuChange = true;
            try
            {
                menu.Visible = visible;
            }
            finally
            {
                _programmaticMenuChange = false;
            }
        }

        private void CloseAllMenus()
        {
            _programmaticMenuChange = true;
            try
            {
                _menuPool.HideAll();
            }
            finally
            {
                _programmaticMenuChange = false;
            }
        }

        /// <summary>
        /// Leaves the object picker and takes the preview prop with it. The picker owns the camera and
        /// swallows the freecam controls for as long as <see cref="_isChoosingObject"/> is set, so every path
        /// that takes the last picker menu off the screen has to come through here.
        /// </summary>
        private void LeaveObjectPicker()
        {
            _isChoosingObject = false;
            _previewProp?.Delete();
            _previewProp = null;
        }

        /// <summary>
        /// Backing out of the category list is what actually leaves the object picker; the object list
        /// below it only steps back up a level.
        /// </summary>
        private void OnCategoriesMenuClosed(object sender, EventArgs e)
        {
            if (_programmaticMenuChange || !_isChoosingObject) return;
            LeaveObjectPicker();
        }

        private void OnObjectsMenuClosed(object sender, EventArgs e)
        {
            if (_programmaticMenuChange || !_isChoosingObject) return;

            // Back from a category's objects returns to the category list, keeping its selection.
            _previewProp?.Delete();
            _previewProp = null;
            _currentCategory = null;
            SetMenuVisible(_categoriesMenu, true);
        }

        private void OnSearchMenuClosed(object sender, EventArgs e)
        {
            if (_programmaticMenuChange || !_isChoosingObject) return;

            // Search can be opened straight from the category list, so back out to whichever level it came
            // from.
            if (_currentCategory == null)
            {
                SetMenuVisible(_categoriesMenu, true);
                return;
            }

            // Starring a search result while browsing favorites changes the category being backed out into,
            // so it is the one that cannot simply be shown again as it was.
            var favorites = Favorites.CategoryFor(_currentObjectType);
            if (ReferenceEquals(_currentCategory, favorites))
            {
                if (favorites.Objects.Count == 0)
                {
                    _currentCategory = null;
                    SetMenuVisible(_categoriesMenu, true);
                    return;
                }

                RedrawObjectsMenu(favorites, _currentObjectType, _objectsMenu.SelectedIndex);
            }

            SetMenuVisible(_objectsMenu, true);
            OnIndexChange(_objectsMenu, new SelectedEventArgs(0, 0));
            _objectsMenu.Name = "~b~" + _currentCategory.Name.ToUpper();
        }

        /// <summary>
        /// The resource is being stopped or restarted, and nothing else will run. Everything the editor holds
        /// is game-side and outlives it — a rendering camera, a frozen invisible player, a preview prop, the
        /// whole placed map — with no script left that could undo any of it, so it is all given back here.
        ///
        /// The map is the player's work: it goes to KVP and comes back on the next start (see
        /// <see cref="SessionRestore"/>) rather than being stranded in the world belonging to nothing.
        /// </summary>
        public void OnAborted()
        {
            // First, so that whatever the rest runs into the player still has their camera and character back.
            HandBackToPlayer();

            // These come back from their own entries on the next start, so anything left standing here would
            // be spawned a second time on top of itself.
            AutoloadedMaps.UnloadAll();

            // If the map could not be written, the world is the only place it still exists — so it stays.
            if (SessionRestore.Save())
            {
                // The record of what is hidden went into the session entry and the next instance re-applies
                // it. Leaving them hidden with nothing tracking them would lose a piece of the map.
                PropStreamer.ClearRemovedObjects();
                PropStreamer.RemoveAll();
            }
        }

        /// <summary>
        /// Gives the player back their character, camera and the interiors the editor was holding open, and
        /// leaves the map standing: this undoes only the state that makes the game unplayable.
        ///
        /// Separate from <see cref="OnAborted"/> because the shell also calls it when the editor is switched
        /// off for throwing every frame. An editor that throws inside <see cref="ToggleFreecam"/> has already
        /// frozen and hidden the player, who would otherwise have no way back short of rejoining.
        ///
        /// Safe to call at any time and more than once.
        /// </summary>
        public void HandBackToPlayer()
        {
            _previewProp?.Delete();
            _previewProp = null;

            if (IsInFreecam)
            {
                IsInFreecam = false;

                var player = Game.Player.Character;
                player.IsPositionFrozen = false;
                player.IsVisible = true;

                // Before the cameras go: it works out where to put the player down from where the freecam
                // was left.
                ReturnPlayerToGround();

                World.RenderingCamera = null;
                World.DestroyAllCameras();
                _mainCamera = null;
                _objectPreviewCamera = null;
            }

            // Outside the block above: the freecam can have been switched off in the same frame the editor
            // stopped, before the tick that would have released these ever ran.
            ReleaseHeldInteriors();
        }

        private void ToggleFreecam()
        {
            ClearMultiSelection();
            IsInFreecam = !IsInFreecam;
            Game.Player.Character.IsPositionFrozen = IsInFreecam;
            Game.Player.Character.IsVisible = !IsInFreecam;
            World.RenderingCamera = null;
            if (!IsInFreecam)
            {
                ReturnPlayerToGround();
                return;
            }
            // Taken before the first tick of freelook drags the player out from under the camera.
            _freecamEntryPosition = Game.Player.Character.Position;

            World.DestroyAllCameras();
            _mainCamera = World.CreateCamera(GameplayCamera.Position, VectorExtensions.ClampCameraRotation(GameplayCamera.Rotation), 60f);
            _objectPreviewCamera = World.CreateCamera(new Vector3(1200.016f, 3980.998f, 86.05062f), new Vector3(0f, 0f, 0f), 60f);
            World.RenderingCamera = _mainCamera;
        }

        /// <summary>
        /// Sets the player down on the ground under the freecam when it hands them back.
        ///
        /// Measured from the camera, not the player: freelook parks the player eight metres behind the camera
        /// (see <see cref="ProcessFreelook"/>), which in an interior is usually through a wall or under the
        /// floor, with nothing below them for a very long way.
        ///
        /// The drop is a probe straight down rather than GET_ENTITY_HEIGHT_ABOVE_GROUND, which measures
        /// against the world's terrain and does not see interior floors. A probe hits those, and placed props
        /// and vehicles with them.
        ///
        /// If neither probe finds anything, the player goes back to <see cref="_freecamEntryPosition"/>.
        /// </summary>
        private void ReturnPlayerToGround()
        {
            var player = Game.Player.Character;
            var playerPos = player.Position;
            var hasCamera = _mainCamera != null && _mainCamera.Exists();

            Vector3? landing = null;

            if (hasCamera)
            {
                var cameraPos = _mainCamera.Position;
                var ground = GroundBelow(cameraPos, player);
                if (ground.HasValue) landing = new Vector3(cameraPos.X, cameraPos.Y, ground.Value + 1f);
            }

            if (!landing.HasValue)
            {
                // Nothing under the camera. Started a little above the player so a floor they are level with,
                // or slightly sunk into, is in front of the probe rather than behind it.
                var ground = GroundBelow(playerPos + new Vector3(0f, 0f, 0.5f), player);
                if (ground.HasValue) landing = new Vector3(playerPos.X, playerPos.Y, ground.Value + 1f);
            }

            // Not probed for: it is a spot they were already standing on. Zero means it was never taken, the
            // freecam having gone up some way other than ToggleFreecam.
            var entry = _freecamEntryPosition != Vector3.Zero ? _freecamEntryPosition : playerPos;
            var target = landing ?? entry;

            // The camera can have carried the player far enough that the ground under them is not in memory
            // yet. The flag holds them up until it is.
            Function.Call(Hash.REQUEST_COLLISION_AT_COORD, target.X, target.Y, target.Z);
            Function.Call(Hash.SET_ENTITY_LOAD_COLLISION_FLAG, player.Handle, true);

            player.Position = target;
        }

        /// <summary>The close-up half of the scan. Fine enough to catch a room the size of a shop and to tell
        /// one floor of a tower from the next, which are barely three metres apart.</summary>
        private const float InteriorNearRadius = 25f;
        private const float InteriorNearHeight = 24f;
        private const float InteriorNearStep = 4f;
        private const float InteriorNearStepZ = 3f;

        /// <summary>The wide half, walked coarsely: it finds whole buildings before the camera arrives, and
        /// what it misses the fine half picks up as the camera closes in.</summary>
        private const float InteriorFarRadius = 90f;
        private const float InteriorFarHeight = 48f;
        private const float InteriorFarStep = 12f;
        private const float InteriorFarStepZ = 6f;

        /// <summary>Points looked at per frame. The grid is some thousands, so a pass takes about half a
        /// second at sixty frames — thin enough not to be felt.</summary>
        private const int InteriorScanSamplesPerTick = 192;

        /// <summary>How far the camera has to fly before the grid is walked again around where it now is.</summary>
        private const float InteriorRescanDistance = 10f;

        /// <summary>How far behind the camera a held interior has to be left before it is given back to the
        /// game. See <see cref="ForgetDistantInteriors"/>.</summary>
        private const float InteriorForgetDistance = 400f;

        /// <summary>
        /// The offsets from the camera that <see cref="HoldInteriorsAroundCamera"/> looks at, nearest first so
        /// a pass cut short has still covered everything close by.
        ///
        /// Two cylinders, one fine and one coarse, both taller than a building: an interior is only found by
        /// asking about a point inside it, so anything the grid steps over is missed. Built once.
        /// </summary>
        private static readonly Vector3[] InteriorScanOffsets = BuildInteriorScanOffsets();

        private static Vector3[] BuildInteriorScanOffsets()
        {
            var offsets = new List<Vector3>();

            AddInteriorScanPoints(offsets, InteriorNearRadius, InteriorNearHeight, InteriorNearStep, InteriorNearStepZ, 0f, 0f);
            AddInteriorScanPoints(offsets, InteriorFarRadius, InteriorFarHeight, InteriorFarStep, InteriorFarStepZ,
                InteriorNearRadius, InteriorNearHeight);

            offsets.Sort((a, b) => a.LengthSquared().CompareTo(b.LengthSquared()));
            return offsets.ToArray();
        }

        /// <summary>
        /// Fills a cylinder of <paramref name="radius"/> and half-height <paramref name="height"/> around the
        /// origin with points <paramref name="step"/> apart, leaving out the ones already covered by a finer
        /// cylinder of <paramref name="skipRadius"/> and <paramref name="skipHeight"/>.
        /// </summary>
        private static void AddInteriorScanPoints(List<Vector3> offsets, float radius, float height, float step,
            float stepZ, float skipRadius, float skipHeight)
        {
            // Counted out in whole steps from the middle, so the grid stays centred on the camera however the
            // radius and step divide.
            var acrossSteps = (int)(radius / step);
            var upSteps = (int)(height / stepZ);

            for (var ix = -acrossSteps; ix <= acrossSteps; ix++)
            for (var iy = -acrossSteps; iy <= acrossSteps; iy++)
            {
                var x = ix * step;
                var y = iy * step;

                // A circle rather than the square the loops walk: the corners lie half again as far out as the
                // radius asked for, a third more points for no more building.
                var distanceSq = x * x + y * y;
                if (distanceSq > radius * radius) continue;

                var skipColumn = distanceSq <= skipRadius * skipRadius;

                for (var iz = -upSteps; iz <= upSteps; iz++)
                {
                    var z = iz * stepZ;
                    if (skipColumn && Math.Abs(z) <= skipHeight) continue;

                    offsets.Add(new Vector3(x, y, z));
                }
            }
        }

        /// <summary>
        /// Keeps every interior around the freecam loaded and drawn for as long as the freecam is up.
        ///
        /// The game decides what to stream from where the player is, and the player is not where the camera
        /// is: freelook parks them eight metres behind it (see <see cref="ProcessFreelook"/>) and drags them
        /// along frozen. Take the camera through a wall and the player leaves the interior's bounds with it,
        /// which the game reads as having left — the interior is deactivated and the room becomes an empty
        /// shell. Flying back in does not restore it, since the player is teleported rather than walked in.
        ///
        /// Holding only the interior the camera stands in is not enough: a tower is a stack of interiors, one
        /// per floor, and the game hands out one at a time. So the whole neighbourhood is asked for instead —
        /// <see cref="InteriorScanOffsets"/> is walked around the camera, and every interior found is pinned
        /// in memory and set active every frame. What is found is held until the freecam goes down, bar
        /// <see cref="ForgetDistantInteriors"/>.
        ///
        /// A pass is spread over as many frames as it takes at <see cref="InteriorScanSamplesPerTick"/> points
        /// each, nearest offsets first so wherever the camera is comes back on the first frame.
        /// </summary>
        private void HoldInteriorsAroundCamera()
        {
            var cameraPos = _mainCamera.Position;

            // A pass restarts from where the camera is now once it runs out of points, or once the camera has
            // flown far enough that the pass is being run around a spot it has left. Cutting one short costs
            // nothing: what it had left are the far offsets, and the near ones are already covered.
            if (_interiorScanCursor >= InteriorScanOffsets.Length ||
                (cameraPos - _interiorScanOrigin).Length() > InteriorRescanDistance)
            {
                ForgetDistantInteriors(cameraPos);
                _interiorScanOrigin = cameraPos;
                _interiorScanCursor = 0;
            }

            var passEnd = Math.Min(_interiorScanCursor + InteriorScanSamplesPerTick, InteriorScanOffsets.Length);
            for (; _interiorScanCursor < passEnd; _interiorScanCursor++)
            {
                var at = _interiorScanOrigin + InteriorScanOffsets[_interiorScanCursor];

                // Answered from the map data rather than from what is in memory, which is what makes scanning
                // ahead work: nearly everything found here is not loaded yet.
                var interior = Function.Call<int>(Hash.GET_INTERIOR_AT_COORDS, at.X, at.Y, at.Z);
                if (interior == 0 || _heldInteriors.ContainsKey(interior)) continue;

                Function.Call(Hash.PIN_INTERIOR_IN_MEMORY, interior);
                _heldInteriors.Add(interior, at);
            }

            // Every frame and for every one: the player dragged along behind the camera keeps wandering out
            // through walls, and the game reads each of those as having left and switches the interior off.
            foreach (var interior in _heldInteriors.Keys)
                Function.Call(Hash.SET_INTERIOR_ACTIVE, interior, true);
        }

        /// <summary>
        /// Lets go of the interiors the camera has left more than <see cref="InteriorForgetDistance"/> behind.
        ///
        /// Interiors are held whole — geometry, collision and props — so a long flight across the map would
        /// otherwise stack up every building passed through. The distance is far outside both view and scan
        /// radius, so anything dropped is found and pinned again within a pass on the way back.
        /// </summary>
        private void ForgetDistantInteriors(Vector3 cameraPos)
        {
            List<int> distant = null;

            foreach (var held in _heldInteriors)
            {
                if ((held.Value - cameraPos).Length() <= InteriorForgetDistance) continue;
                (distant ?? (distant = new List<int>())).Add(held.Key);
            }

            if (distant == null) return;

            foreach (var interior in distant)
            {
                Function.Call(Hash.UNPIN_INTERIOR, interior);
                _heldInteriors.Remove(interior);
            }
        }

        /// <summary>
        /// Hands everything <see cref="HoldInteriorsAroundCamera"/> is holding back to the game, which is then
        /// free to stream each of them out again once nothing is left inside it. Safe to call on any tick: it
        /// does nothing unless there are interiors being held.
        /// </summary>
        private void ReleaseHeldInteriors()
        {
            if (_heldInteriors.Count == 0) return;

            foreach (var interior in _heldInteriors.Keys)
                Function.Call(Hash.UNPIN_INTERIOR, interior);

            _heldInteriors.Clear();

            // The next freecam session starts its own pass rather than carrying on through this one's.
            _interiorScanCursor = 0;
            _interiorScanOrigin = Vector3.Zero;
        }

        /// <summary>
        /// The height of the first solid thing under <paramref name="from"/>, or null when the probe comes
        /// back with nothing.
        ///
        /// The probe is the synchronous kind on purpose: this is asked once, and the ordinary one's "not ready
        /// yet" would read as "nothing below" and leave the player hanging in the air. Hence also telling the
        /// probe not answering apart from it answering with nothing.
        ///
        /// Fired from Lua — CitizenFX's GET_SHAPE_TEST_RESULT wrapper cannot run on the Enhanced client at
        /// all. See <see cref="LuaBridge"/>.
        /// </summary>
        private static float? GroundBelow(Vector3 from, Entity toIgnore)
        {
            const float probeLength = 1000f;

            var flags = IntersectOptions.Map | IntersectOptions.Objects | IntersectOptions.Unk2;

            var ground = LuaBridge.Probe(from, new Vector3(from.X, from.Y, from.Z - probeLength), flags, toIgnore);

            if (!ground.Answered) return null;

            return ground.DidHit ? ground.Position.Z : (float?)null;
        }

        /// <summary>
        /// Empties the editor: everything it is holding goes out of the world and the metadata starts again.
        ///
        /// <paramref name="quiet"/> is for the caller that has already said something better. Publishing ends
        /// here — the map has just been handed to the server — and "Loaded new map." would be answering a
        /// question the player did not ask.
        /// </summary>
        private void NewMap(bool quiet = false)
        {
            ClearMultiSelection();
            PropStreamer.RemoveAll();
            PropStreamer.Markers.Clear();
            _currentObjectsMenu.Clear();
            PropStreamer.Identifications.Clear();
            PropStreamer.ActiveScenarios.Clear();
            PropStreamer.ActiveRelationships.Clear();
            PropStreamer.ActiveWeapons.Clear();
            PropStreamer.Doors.Clear();
            PropStreamer.CurrentMapMetadata = new MapMetadata();
            ModManager.Disconnect();

            // The game's own objects the map had hidden come back: CREATE_MODEL_HIDE is undone by saying so,
            // and the game puts the real object back itself.
            PropStreamer.ClearRemovedObjects();

            _loadedEntities = 0;
            _changesMade = 0;
            _lastAutosave = Clock.Milliseconds;

            if (!quiet)
                Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate("Loaded new map."));
        }

        /// <summary>
        /// Published maps are not part of the map being edited, so "New Map" leaves them standing and this is
        /// the only thing that takes them away — everywhere at once. See <see cref="MapStore.UnloadAsync"/>.
        ///
        /// With one map there is nothing to choose between, so it goes without a menu holding a single row.
        /// </summary>
        private void UnloadAutoloadedMaps()
        {
            if (!AutoloadedMaps.Any) return;

            if (!MapStore.CanUnload)
            {
                Compat.Notify("~r~~h~Map Editor~h~~w~~n~" +
                              Translation.Translate("You do not have permission to unload the server's maps."));
                return;
            }

            if (AutoloadedMaps.MapCount == 1)
            {
                UnloadAutoloadedMap(0);
                return;
            }

            RedrawUnloadAutoloadedMenu();
            SetMenuVisible(_mainMenu, false);
            SetMenuVisible(_unloadAutoloadedMenu, true);
        }

        /// <summary>
        /// Asks the server to take one published map down, by its row in the unload menu.
        ///
        /// Nothing is removed locally: the server answers, everyone is told, and this client reconciles its
        /// world like every other one.
        /// </summary>
        private void UnloadAutoloadedMap(int index)
        {
            var entry = StandingMap(AutoloadedMaps.KeyAt(index));
            if (entry == null)
            {
                // Standing here but no longer in the catalogue: the server has moved on, and the next
                // reconciliation takes it out anyway.
                Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate("That map is no longer on the server."));
                return;
            }

            // Through Run because this is a round trip: the row must not start a second while the first is out.
            Run("Unload server map", async () =>
            {
                var error = await MapStore.UnloadAsync(entry);
                if (error != null)
                {
                    Compat.Notify("~r~~h~Map Editor~h~~w~~n~" + error);
                    return;
                }

                Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate("Unloaded for everyone:") +
                              " ~h~" + entry.Name + "~h~.");

                // The broadcast reaches this client too and repeats all of this harmlessly. Done here as well
                // so the menu has caught up by the time the round trip returns, not a network hop later.
                await AutoloadedMaps.Sync(announce: false);
                RefreshServerMapMenus();
            });
        }

        /// <summary>
        /// Takes every published map down, one request each. Sequential rather than fired together: each is a
        /// write on the server, and the rate limiter would refuse the tail of a burst.
        /// </summary>
        private void UnloadAllAutoloadedMaps()
        {
            if (!AutoloadedMaps.Any) return;

            var entries = AutoloadedMaps.Keys.Select(StandingMap).Where(entry => entry != null).ToList();
            if (entries.Count == 0) return;

            Run("Unload all server maps", async () =>
            {
                var unloaded = 0;
                foreach (var entry in entries)
                {
                    var error = await MapStore.UnloadAsync(entry);
                    if (error != null)
                    {
                        Compat.Notify("~r~~h~Map Editor~h~~w~~n~" + error);
                        break;
                    }
                    unloaded++;
                }

                if (unloaded > 0)
                    Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate("Unloaded for everyone:") +
                                  " ~h~" + unloaded + "~h~.");

                await AutoloadedMaps.Sync(announce: false);
                RefreshServerMapMenus();
            });
        }

        /// <summary>The catalogue entry a map standing in the world came from, or null if it has left it.</summary>
        private static MapEntry StandingMap(string key)
        {
            if (key == null) return null;

            return MapStore.List(MapScope.Shared)
                .FirstOrDefault(map => map.Live && string.Equals(map.Name, key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Rebuilds the menus that show what the server is holding: the unload list and the file picker, where
        /// a published map is greyed out. Neither redraws itself while open, and the unload list renumbers —
        /// its rows are indices into what is standing. Only the menus actually up; the rest rebuild on entry.
        /// </summary>
        private void RefreshServerMapMenus()
        {
            if (_unloadAutoloadedMenu.Visible)
            {
                if (AutoloadedMaps.MapCount > 1)
                {
                    RedrawUnloadAutoloadedMenu();
                }
                else
                {
                    // Nothing left to choose between; the main menu's own row unloads a lone map outright.
                    SetMenuVisible(_unloadAutoloadedMenu, false);
                    SetMenuVisible(_mainMenu, true);
                }
            }

            if (_filepicker.Visible) RedrawFilepickerMenu();
            if (_deleteMapMenu.Visible) RedrawDeleteMapMenu();
        }

        private void BeginSaveMap()
        {
            if (ModManager.HasCurrentMod)
            {
                Run("BeginSaveMap (external mod)", async () =>
                {
                    string name = await Compat.GetUserInput(255);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        Compat.Notify("~r~~h~Map Editor~h~~n~~w~" + Translation.Translate("The map name was empty."));
                        return;
                    }
                    Map tmpMap = new Map();
                    tmpMap.Objects.AddRange(PropStreamer.GetAllEntities());
                    tmpMap.RemoveFromWorld.AddRange(PropStreamer.RemovedObjects);
                    tmpMap.Markers.AddRange(PropStreamer.Markers);
                    tmpMap.Metadata = PropStreamer.CurrentMapMetadata;
                    Compat.Notify("~b~~h~Map Editor~h~~n~~w~" + Translation.Translate("Map sent to external mod for saving."));
                    ModManager.NotifyMapSaved(tmpMap, name);
                });
                return;
            }

            if (!MapStore.ServerAvailable)
            {
                // Said before the player types a name and description: both storages are the server's, and a
                // FiveM client has no third place to write to.
                Compat.Notify("~r~~h~Map Editor~h~~w~~n~" + Translation.Translate(MapStore.NoServerMessage));
                return;
            }

            SetMenuVisible(_mainMenu, false);
            RedrawSaveTargetMenu();
            SetMenuVisible(_saveTargetMenu, true);
        }

        /// <summary>
        /// Loading no longer starts with a format: a map has no extension to read one off, so the picker
        /// reads it out of the document itself. See <see cref="MapSerializer.Detect"/>.
        /// </summary>
        private void BeginLoadMap()
        {
            if (!MapStore.ServerAvailable)
            {
                Compat.Notify("~r~~h~Map Editor~h~~w~~n~" + Translation.Translate(MapStore.NoServerMessage));
                return;
            }

            SetMenuVisible(_mainMenu, false);
            RedrawFilepickerMenu();
            SetMenuVisible(_filepicker, true);

            // The catalogue is a startup cache and somebody may have published since. Refreshed behind the
            // menu: it opens on what is known and redraws only if the answer differs, so a player who has
            // already scrolled to a row does not lose it.
            var before = ServerMapSignature();
            Log.Guard("Refresh server maps", async () =>
            {
                if (!await MapStore.RefreshServer()) return;
                if (!_filepicker.Visible || ServerMapSignature() == before) return;

                RedrawFilepickerMenu();
            });
        }

        /// <summary>
        /// What the catalogue looked like, closely enough to tell whether it has changed. Whether a map is
        /// standing counts as a change: that is what decides whether its row can be opened at all.
        /// </summary>
        private static string ServerMapSignature()
        {
            var sb = new StringBuilder();
            foreach (var entry in MapStore.List(MapScope.Shared))
                sb.Append(entry.Name).Append('#').Append(entry.Stamp).Append(entry.Live ? "*" : "").Append('|');
            foreach (var entry in MapStore.List(MapScope.Personal))
                sb.Append(entry.Name).Append('#').Append(entry.Stamp).Append('|');
            return sb.ToString();
        }

        /// <summary>
        /// Which storage the map is going into. Every row carries its scope in its Tag, and the metadata menu
        /// behind it is the same either way. There is no format to pick: the editor writes one format, since
        /// anything else would only land in the server's own storage. See <see cref="MapSerializer"/>.
        /// </summary>
        private void OnSaveTargetSelect(object sender, ItemActivatedArgs e)
        {
            if (!(e.Item.Tag is MapScope scope)) return;

            _saveScope = scope;

            SetMenuVisible(_saveTargetMenu, false);
            RedrawMetadataMenu();
            SetMenuVisible(_metadataMenu, true);
        }

	    private void SaveSettings()
	    {
		    _settings.Save();
	    }

		/// <summary>
		/// Picks the map back up where the previous instance left it, if the resource was restarted with a map
		/// open. It comes back as the map being edited — selectable, saveable, cleared by "New Map" — which is
		/// the point: the objects were standing either way, and this makes them the player's again.
		/// </summary>
		private async Task RestoreSession()
		{
			if (!SessionRestore.Pending) return;

			var content = SessionRestore.Read();

			// Discarded as soon as it is read: this is a hand-off between two instances, not a saved map, and
			// must not be restored again on a later start.
			SessionRestore.Discard();

			if (string.IsNullOrEmpty(content)) return;

			await LoadMap(content, MapSerializer.Format.Json, null, restoring: true);

			Compat.Notify("~b~~h~Map Editor~h~~w~~n~" +
			              Translation.Translate("Restored the map you were working on before the script reloaded."));
		}

		/// <summary>
		/// Loads one saved map out of the store, working out its format from the document itself.
		/// </summary>
		private async Task LoadMap(MapEntry entry)
		{
			// A published map already stands in everyone's world, so editing it would build a second copy on
			// top of the one the player can see. It has to come down for everyone first. Checked here rather
			// than on the server: asking for a document twice is a mess on one screen, not an attack.
			if (entry.Scope == MapScope.Shared && entry.Live)
			{
				Compat.Notify("~r~~h~Map Editor~h~~w~~n~" + Translation.Translate(
					"That map is loaded on the server. Unload it first — it comes out of the world for everyone."));
				return;
			}

			// Every map is a round trip, and on a slow link a visible one: better than looking frozen.
			Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate("Fetching the map from the server..."));

			var content = await MapStore.ReadAsync(entry);
			if (content == null)
			{
				Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate("That map is no longer there."));
				return;
			}

			var format = MapSerializer.Detect(content);
			if (!format.HasValue)
			{
				Compat.Notify("~r~~h~Map Editor~h~~w~~n~" + Translation.Translate("That map is in a format Map Editor does not recognise."));
				return;
			}

			await LoadMap(content, format.Value, entry.Name);
		}

		/// <summary>
		/// Puts a map document into the world. A FiveM client cannot open a file, so the caller brings the
		/// text and <paramref name="name"/> is the entry it came out of — null for a restored session.
		///
		/// <paramref name="restoring"/> marks a map the editor is picking back up after a resource restart:
		/// it was never really unloaded, so it keeps the metadata it had and stays where it is instead of
		/// dragging the player back to its loading point.
		/// </summary>
	    private async Task LoadMap(string content, MapSerializer.Format format, string name, bool restoring = false)
	    {
		    try
		    {
			    var map2Load = new MapSerializer().DeserializeFromString(content, format);
			    if (map2Load == null) return;

			    // A map replaces what the editor is holding; it is not poured on top of it. Loading two maps
			    // in a row used to leave both standing as one document, and loading the same map twice left a
			    // second copy of every object inside the first — with no way back out of it but deleting them
			    // one at a time, since the two are indistinguishable from that moment on.
			    //
			    // After the document has been read and not before: one the editor cannot parse must not cost
			    // the player the map they had open. A restore is not a load — the editor is empty and this is
			    // the same map coming back to it.
			    if (!restoring) NewMap(quiet: true);

		        if (!restoring && map2Load.Metadata != null && map2Load.Metadata.LoadingPoint.HasValue)
		        {
		            Game.Player.Character.Position = map2Load.Metadata.LoadingPoint.Value;
		            await Frame.Wait(500);

                    // The player is somewhere else now, and what is worth spawning is measured from where
                    // they are rather than where they were when the frame started.
                    SmartStreaming.BeginTick(CurrentStreamingOrigin);
		        }

			    foreach (MapObject o in map2Load.Objects)
			    {
				    if(o == null) continue;
			        _loadedEntities++;

                    if (o.Type == ObjectTypes.Pickup)
                    {
                        var newPickup = await PropStreamer.CreatePickup(o.Hash, o.Position, o.Rotation.Z, o.Amount, o.Dynamic, o.Quaternion);
                        newPickup.Timeout = o.RespawnTimer;
                        AddItemToEntityMenu(newPickup);
                        if (o.Id != null && !PropStreamer.Identifications.ContainsKey(newPickup.ObjectHandle))
                            PropStreamer.Identifications.Add(newPickup.ObjectHandle, o.Id);
                        continue;
                    }

                    // The far side of a large map is loaded without entering the world: it is in the map, the
                    // entity menu and what gets saved, and streaming spawns it if the player goes there. A
                    // named object is always spawned, in step with IsEntityBusy, which never streams one out:
                    // whatever scripts this map knows it by name and expects to find it standing.
                    if (o.Id == null && !SmartStreaming.HasReturned(o.Position))
                    {
                        AddStreamedItemToEntityMenu(o, PropStreamer.AddStreamedOut(o).Uid);
                        continue;
                    }

                    // The same spawn smart streaming uses to put an entity back, so a map flown out of and
                    // back into is still the map that was loaded.
                    var entity = await SpawnMapObject(o);
                    if (entity == null) continue;

                    AddItemToEntityMenu(entity);
			    }

			    // The game's own objects this map takes out of the world. CREATE_MODEL_HIDE says so once and
			    // it stays said, so there is nothing to repeat every frame. See PropStreamer.RemoveWorldObject.
			    foreach (MapObject o in map2Load.RemoveFromWorld)
			    {
					if(o == null) continue;

					o.Id = _mapObjCounter.ToString(CultureInfo.InvariantCulture);
					_mapObjCounter++;
					PropStreamer.RemoveWorldObject(o);
					AddItemToEntityMenu(o);
			    }

			    foreach (Marker marker in map2Load.Markers)
			    {
				    if(marker == null) continue;
			        _markerCounter++;
			        marker.Id = _markerCounter;
					PropStreamer.Markers.Add(marker);
					AddItemToEntityMenu(marker);
			    }

		        if (!restoring && map2Load.Metadata != null && map2Load.Metadata.TeleportPoint.HasValue)
		            Game.Player.Character.Position = map2Load.Metadata.TeleportPoint.Value;

		        PropStreamer.CurrentMapMetadata = map2Load.Metadata ?? new MapMetadata();

		        // A map is called what it is stored as: a document saying otherwise was renamed underneath it.
		        // A restore is the exception — the session entry it came back from is not its name.
		        if (!restoring && name != null)
		            PropStreamer.CurrentMapMetadata.Name = name;

		        // The caller says what a restore has to say, and it is not "loaded map <session>".
		        if (!restoring)
			        Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate("Loaded map") + " ~h~" + (name ?? "") + "~h~.");

		        ModManager.NotifyMapLoaded(map2Load, name);
		    }
		    catch (Exception e)
		    {
				Compat.Notify("~r~~h~Map Editor~h~~w~~n~" + Translation.Translate("Map failed to load, see error below."));
				Compat.Notify(e.Message);
				Log.Error("LoadMap", e);
			}
	    }

		/// <summary>
		/// Writes the map into the store under <paramref name="name"/>. There is no path: the server derives
		/// a file name, and the format is read back out of the document (see <see cref="MapSerializer.Detect"/>).
		///
		/// <paramref name="scope"/> says which of the server's two storages. Either can be refused for reasons
		/// this side has no opinion on — permission, size, object count, rate — so the server's own sentence
		/// is shown as written.
		///
		/// <b>Publishing hands the map over.</b> A map saved to the shared storage goes up in every player's
		/// world and the editor lets go of it: the author's copy comes out here and the published one comes
		/// back through <see cref="AutoloadedMaps.Sync"/>, or the author would stand in two maps and edit the
		/// one nobody else can see.
		/// </summary>
	    private async Task SaveMap(string name, MapScope scope)
	    {
			if (string.IsNullOrWhiteSpace(name))
			{
				Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate("The map name was empty!"));
				return;
			}

			name = name.Trim();

			if (!MapStore.IsValidName(name))
			{
				Compat.Notify("~r~~h~Map Editor~h~~w~~n~" + Translation.Translate("A map name cannot contain ':' or '#'."));
				return;
			}

			var ser = new MapSerializer();
			var tmpmap = new Map();
			try
			{
				tmpmap.Objects.AddRange(PropStreamer.GetAllEntities());
				tmpmap.RemoveFromWorld.AddRange(PropStreamer.RemovedObjects);
				tmpmap.Markers.AddRange(PropStreamer.Markers);
			    tmpmap.Metadata = PropStreamer.CurrentMapMetadata;

				Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate(scope == MapScope.Shared
					? "Publishing the map to the server..."
					: "Sending the map to the server..."));

				// Said before it happens rather than discovered afterwards. A pickup is shared state by
				// definition — "who took it" has no other answer — and no server-side native creates one, so
				// a published map places none. A local copy each would look like it works and would not.
				if (scope == MapScope.Shared)
				{
					var pickups = 0;
					foreach (var o in tmpmap.Objects)
					{
						if (o != null && o.Type == ObjectTypes.Pickup) pickups++;
					}

					if (pickups > 0)
						Compat.Notify("~o~~h~Map Editor~h~~w~~n~" + pickups + " " + Translation.Translate(
							"pickup(s) will not be placed: the server cannot create pickups, and a local one " +
							"would give every player their own."));
				}

				var error = await MapStore.WriteAsync(scope, name,
					ser.SerializeToString(tmpmap, MapSerializer.Format.Json));

				if (error != null)
				{
					Compat.Notify("~r~~h~Map Editor~h~~w~~n~" + Translation.Translate("Map failed to save, see error below."));
					Compat.Notify(error);
					return;
				}

				Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate(scope == MapScope.Shared
					               ? "Published on the server as"
					               : "Saved to your own maps as") + " ~h~" + name + "~h~.");
			    _changesMade = 0;

			    ModManager.NotifyMapSaved(tmpmap, name);

			    // The map is the server's now. Clearing the editor takes this client's copy out of the world so
			    // the one the server puts back is the only one standing.
			    if (scope == MapScope.Shared)
			    {
				    NewMap(quiet: true);
				    Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate(
					    "It is now standing for everyone. Your editor is empty; unload the map to edit it again."));

				    // Done here as well so publishing does not depend on hearing one's own broadcast: to this
				    // client, the map vanishing and not coming back would look like the save having failed.
				    await AutoloadedMaps.Sync(announce: false);
				    RefreshServerMapMenus();
			    }
			}
			catch (Exception e)
			{
				Compat.Notify("~r~~h~Map Editor~h~~w~~n~" + Translation.Translate("Map failed to save, see error below."));
				Compat.Notify(e.Message);
				Log.Error("SaveMap", e);
			}
		}

        public static float GetSafeFloat(string input, float lastFloat)
        {
            float output;
            if (!float.TryParse(input, NumberStyles.Any,  CultureInfo.InvariantCulture, out output))
            {
                return lastFloat;
            }

            if (output < -(_possibleRange*0.01f) || output > (_possibleRange*0.01f))
            {
                return lastFloat;
            }
            return output;
        }

        // The handle rather than the entity, in all three. Passing an Entity compiles — InputArgument has a
        // conversion from INativeValue — but that conversion boxes the NativeValue as a UInt64, which PushArg
        // does not accept, so the call dies inside the runtime with "Unsupported type Object" and never names
        // the entity. tools/sandbox-audit reads these conversions out of the IL and reports them.
        public static bool IsPed(Entity ent)
        {
            return Function.Call<bool>(Hash.IS_ENTITY_A_PED, ent.Handle);
        }

        public static bool IsVehicle(Entity ent)
        {
            return Function.Call<bool>(Hash.IS_ENTITY_A_VEHICLE, ent.Handle);
        }

        public static bool IsProp(Entity ent)
        {
            return Function.Call<bool>(Hash.IS_ENTITY_AN_OBJECT, ent.Handle);
        }
	}
}
