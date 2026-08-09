using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using CitizenFX.Core.UI;
using MapEditor.Ui.Elements;
using MapEditor.Ui.Menus;
using MapEditor.Ui.Scaleform;
using MapEditor.Ui.Tools;
using MapEditor.Platform;
using Control = CitizenFX.Core.Control;
using Font = CitizenFX.Core.UI.Font;
// System.Drawing has a SizeF of its own and it is the one the Enhanced sandbox refuses; see Client/Ui/SizeF.cs.
using SizeF = MapEditor.Ui.SizeF;

namespace MapEditor
{
    public partial class MapEditor
    {
        private InstructionalButtons _freelookButtons;
        private InstructionalButtons _selectedButtons;
        private InstructionalButtons _snappedButtons;
        private InstructionalButtons _stackingButtons;
        private InstructionalButtons _loopingButtons;

        private void BuildInstructionalButtons()
        {
            // Scaleforms are Visible = false by default and Draw() is a no-op until it's set.
            _freelookButtons = new InstructionalButtons(
                new InstructionalButton(Translation.Translate("Spawn Prop"), Control.Enter),
                new InstructionalButton(Translation.Translate("Spawn Ped"), Control.FrontendPause),
                new InstructionalButton(Translation.Translate("Spawn Vehicle"), Control.NextCamera),
                new InstructionalButton(Translation.Translate("Spawn Marker"), Control.Phone),
                new InstructionalButton(Translation.Translate("Spawn Pickup"), Control.ThrowGrenade),
                new InstructionalButton(Translation.Translate("Move Entity"), Control.Aim),
                new InstructionalButton(Translation.Translate("Select Entity"), Control.Attack),
                // Control.Duck is the LCTRL binding on keyboard, which is what IsMultiSelectKeyDown() reads.
                new InstructionalButton(Translation.Translate("Add to Selection"), Control.Duck),
                new InstructionalButton(Translation.Translate("Copy Entity"), Control.LookBehind),
                // The same key stars the highlighted model in the object picker.
                new InstructionalButton(Translation.Translate("Favorite Entity"), Control.Context),
                new InstructionalButton(Translation.Translate("Delete Entity"), Control.CreatorDelete))
            {
                Visible = true,
            };
            _freelookButtons.Update();

            _selectedButtons = new InstructionalButtons(
                new InstructionalButton("", Control.MoveLeftRight),
                new InstructionalButton("", Control.MoveUpDown),
                new InstructionalButton("", Control.FrontendRb),
                new InstructionalButton(Translation.Translate("Move Entity"), Control.FrontendLb),
                new InstructionalButton(Translation.Translate("Switch to Rotation"), Control.Duck),
                new InstructionalButton(Translation.Translate("Copy Entity"), Control.LookBehind),
                new InstructionalButton(Translation.Translate("Delete Entity"), Control.CreatorDelete),
                new InstructionalButton(Translation.Translate("Accept"), Control.Attack))
            {
                Visible = true,
            };
            _selectedButtons.Update();

            _snappedButtons = new InstructionalButtons(
                new InstructionalButton("", Control.FrontendRb),
                new InstructionalButton(Translation.Translate("Rotate Entity"), Control.FrontendLb),
                new InstructionalButton(Translation.Translate("Delete Entity"), Control.CreatorDelete),
                new InstructionalButton(Translation.Translate("Accept"), Control.Attack))
            {
                Visible = true,
            };
            _snappedButtons.Update();

            _stackingButtons = new InstructionalButtons(
                new InstructionalButton(Translation.Translate("Exit Tool"), Control.PhoneCancel),
                new InstructionalButton(Translation.Translate("Abort Current Stacking"), Control.CreatorDelete),
                new InstructionalButton(Translation.Translate("Multiplier"), Control.Sprint))
            {
                Visible = true,
            };
            _stackingButtons.Update();

            _loopingButtons = new InstructionalButtons(
                new InstructionalButton(Translation.Translate("Exit Tool"), Control.PhoneCancel),
                new InstructionalButton(Translation.Translate("Abort Current Looping"), Control.CreatorDelete),
                new InstructionalButton(Translation.Translate("Multiplier"), Control.Sprint))
            {
                Visible = true,
            };
            _loopingButtons.Update();
        }

        /// <summary>
        /// The stacking and looping tools each own the object they were opened on and drive their own
        /// controls, so whatever else the editor would do with those controls has to stand down.
        /// </summary>
        private bool IsGeneratorToolActive
        {
            get { return _stackingBase != null || _loopingBase != null; }
        }

        /// <summary>
        /// Whether the player is on a controller rather than mouse and keyboard. Answers about the *last*
        /// input, not what is plugged in, so picking up a pad mid-session switches to the pad's sensitivity.
        ///
        /// True for mouse and keyboard, hence the negation. FiveM carries this hash under two names, and the
        /// older one — _IS_INPUT_DISABLED — says the opposite of what it does.
        /// </summary>
        private static bool IsUsingGamepad()
        {
            return !Function.Call<bool>(Hash._IS_USING_KEYBOARD, 2);
        }

        private void DrawButtons(InstructionalButtons buttons)
        {
            if (!_settings.InstructionalButtons) return;
            buttons.Draw();
        }

		/// <summary>
		/// One frame of the editor.
		///
		/// Returns a Task because that is the shape FiveM's Tick takes, but deliberately awaits nothing: the
		/// runtime does not call a tick handler again until the last has finished, so awaiting a model load
		/// here would freeze the editor for as long as the streamer took. Anything that waits goes through
		/// <see cref="Run"/> and finishes on its own.
		/// </summary>
		public Task OnTick()
		{
			// Nothing at all while the player is typing. The keyboard is a Task here and the frames keep
			// coming, and this frame is what breaks the box: HIDE_HUD_AND_RADAR_THIS_FRAME below hides the
			// keyboard with the rest of the HUD, and the freecam would read the typing as camera controls.
			if (TextInput.IsOpen) return Task.FromResult(0);

			// Before anything that spawns or places anything, including the first-tick load below: every map
			// that goes into the world this frame is streamed against the same spot.
			SmartStreaming.BeginTick(CurrentStreamingOrigin);

			// The published maps, then the map the player was editing when the resource last went down. Both
			// spawn entities, so both take frames.
			//
			// Announced on one frame and started on the next, the rule Boot.Step follows: a net event queued
			// during a frame only leaves at the end of it, so starting the work in the announcing frame would
			// take the announcement down with it if it hung.
			if (!_hasLoaded)
			{
				if (!_firstLoadAnnounced)
				{
					_firstLoadAnnounced = true;
					Boot.Note("first load (published maps, then session restore) starts on the next frame");
				}
				else
				{
					_hasLoaded = true;
					Log.Guard("MapEditor first load", async () =>
					{
						await AutoloadedMaps.LoadAll();
						await RestoreSession();
					});
				}
			}

			_menuPool.Process();
			PropStreamer.Tick(IsInFreecam);
			AutoloadedMaps.Tick();
			ProcessSmartStreaming();
			WarmObjectRows();

			// After streaming, so that an object put back into the world this frame already has its
			// session id on it before the pass that reports what changed looks at it. Outside the freecam
			// as well as inside: somebody who has stepped out of the editor is still in the session and
			// still has to receive everyone else's work.
			ProcessCollab();
			DrawCollabFeed();

			if (PropStreamer.EntityCount > 0 || PropStreamer.RemovedObjects.Count > 0 || PropStreamer.Markers.Count > 0 || PropStreamer.Pickups.Count > 0)
			{
				_currentEntitiesItem.Enabled = true;
				_currentEntitiesItem.Description = "";
			}
			else
			{
				_currentEntitiesItem.Enabled = false;
				_currentEntitiesItem.Description = Translation.Translate("There are no current entities.");
			}

			if (AutoloadedMaps.Any)
			{
				// Greyed out without the permission rather than hidden: what is standing in the world is
				// worth counting even for a player who may not take it down.
				_unloadAutoloadedItem.Enabled = MapStore.CanUnload;
				_unloadAutoloadedItem.AltTitle = AutoloadedMaps.MapCount.ToString();
			}
			else
			{
				_unloadAutoloadedItem.Enabled = false;
				_unloadAutoloadedItem.AltTitle = "";
			}

			// The count beside the row is the only place outside the session's own menus that says a map is
			// not being built alone. Left blank rather than showing "1" when it is: a session of one is a
			// session waiting for somebody, and the row already says so when opened.
			if (Collab.Active)
			{
				_collabItem.AltTitle = (Collab.Peers.Count + 1).ToString();
				_collabItem.Description = Translation.Translate("You are building this map with other players.");
			}
			else
			{
				_collabItem.AltTitle = "";
				_collabItem.Description = Collab.Available
					? Translation.Translate("Open this map to the other players, or join a map somebody else is building.")
					: Translation.Translate("This server does not let players build maps together.");
			}

			if (ModManager.HasMods)
			{
				_externalModItem.Enabled = true;
				_externalModItem.Description = Translation.Translate("Hand the map over to another resource instead of saving it.");
			}
			else
			{
				_externalModItem.Enabled = false;
				_externalModItem.Description = Translation.Translate("No external mods are connected to Map Editor.");
			}

			if (Game.IsControlPressed(0, Control.LookBehind) && Game.IsControlJustPressed(0, Control.FrontendLb) && !_menuPool.AreAnyVisible && _settings.Gamepad)
			{
				_mainMenu.Visible = !_mainMenu.Visible;
			}

            // Whole minutes elapsed, not the minute *component* of the gap: the latter never reaches the 60
            // the settings list offers, so that setting would silently never autosave.
		    if (_settings.AutosaveInterval != -1 && Clock.Elapsed(_lastAutosave, _settings.AutosaveInterval * 60000) && PropStreamer.EntityCount > 0 && _changesMade > 0 && PropStreamer.EntityCount != _loadedEntities)
		    {
                // Started rather than awaited: this is a frame and the save is a round trip. Always into the
                // player's own maps — an autosave is working state, not something to publish over everyone.
                Log.Guard("Autosave", () => SaveMap(AutosaveName, MapScope.Personal));
		        _lastAutosave = Clock.Milliseconds;
		    }

		    if (_currentObjectsMenu.Visible)
		    {
                if (Game.IsControlJustPressed(0, Control.PhoneLeft))
                {
                    if (_currentObjectsMenu.SelectedIndex <= 100)
                        _currentObjectsMenu.SelectedIndex = 0;
                    else
                        _currentObjectsMenu.SelectedIndex -= 100;
                }

                if (Game.IsControlJustPressed(0, Control.PhoneRight))
                {
                    if (_currentObjectsMenu.SelectedIndex >= _currentObjectsMenu.Items.Count - 101)
                        _currentObjectsMenu.SelectedIndex = _currentObjectsMenu.Items.Count - 1;
                    else
                        _currentObjectsMenu.SelectedIndex += 100;
                }
            }

            //
            // BELOW ONLY WHEN MAP EDITOR IS ACTIVE
            //

            if (!IsInFreecam)
            {
                // A tick late on purpose: the freecam sets the player down inside the interior it was holding,
                // and releasing in the same frame would let go before the game notices anyone is in there.
                ReleaseHeldInteriors();
                return Task.FromResult(0);
            }

            // Before anything that can end the tick early, so the interiors stay held while the object
            // picker is up over them.
            HoldInteriorsAroundCamera();

            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)Control.CharacterWheel);
			Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)Control.SelectWeapon);
			Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)Control.FrontendPause);
			Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)Control.NextCamera);
			Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)Control.Phone);
			Function.Call(Hash.HIDE_HUD_AND_RADAR_THIS_FRAME);

			// The object picker has to stay out until the player has left the tool that owns the object.
			// The two below it are already held off by AreAnyVisible.
			if (Game.IsControlJustPressed(0, Control.Enter) && !_isChoosingObject && !IsGeneratorToolActive)
			{
			    BeginChoosingObject(ObjectTypes.Prop);
			}

			if (Game.IsControlJustPressed(0, Control.NextCamera) && !_isChoosingObject && !IsGeneratorToolActive)
			{
			    BeginChoosingObject(ObjectTypes.Vehicle);
			}

            if (Game.IsControlJustPressed(0, Control.FrontendPause) && !_isChoosingObject && !IsGeneratorToolActive)
			{
			    BeginChoosingObject(ObjectTypes.Ped);
			}

			if (Game.IsControlJustPressed(0, Control.Phone) && !_isChoosingObject && !_menuPool.AreAnyVisible)
			{
				ClearMultiSelection();
				_snappedProp = null;
				_selectedProp = null;
				_snappedMarker = null;
				_selectedMarker = null;

				var yellow = Colors.Yellow;
				var tmpMark = new Marker()
				{
					Red = yellow.R,
					Green = yellow.G,
					Blue = yellow.B,
					Alpha = yellow.A,
					Scale = new Vector3(0.75f, 0.75f, 0.75f),
					Type =  MarkerType.UpsideDownCone,
					Position = VectorExtensions.RaycastEverything(new Vector2(0f, 0f), _mainCamera.Position, _mainCamera.Rotation, Game.Player.Character),
					Id = _markerCounter,
				};
				PropStreamer.Markers.Add(tmpMark);
				_snappedMarker = tmpMark;
				_markerCounter++;
			    _changesMade++;
				AddItemToEntityMenu(_snappedMarker);
			}

            if (Game.IsControlJustPressed(0, Control.ThrowGrenade) && !_isChoosingObject && !_menuPool.AreAnyVisible)
            {
                ClearMultiSelection();
                _snappedProp = null;
                _selectedProp = null;
                _snappedMarker = null;
                _selectedMarker = null;

                var at = VectorExtensions.RaycastEverything(new Vector2(0f, 0f), _mainCamera.Position,
                    _mainCamera.Rotation, Game.Player.Character);

                // Spawning waits for a model and for the pickup's own object to appear, so not in the frame.
                Run("Spawn pickup", async () =>
                {
                    var pickup = await PropStreamer.CreatePickup((int) ObjectDatabase.PickupHash.Parachute, at, 0f, 100, false);
                    if (pickup == null) return;

                    _changesMade++;
                    AddItemToEntityMenu(pickup);
                    _snappedProp = Compat.Ent(pickup.ObjectHandle);
                });
            }

            if (_isChoosingObject)
            {
                // Every way out of the picker runs from a menu's Closed handler, so a picker with no menu on
                // screen is a state nothing can leave: the return below swallows the freecam controls and
                // BeginChoosingObject refuses to reopen. Whatever hid the menu, give the camera back.
                if (!_categoriesMenu.Visible && !_objectsMenu.Visible && !_searchMenu.Visible)
                {
                    LeaveObjectPicker();
                }
                else
                {
                    ProcessObjectPreview();
                    return Task.FromResult(0);
                }
            }

            World.RenderingCamera = _mainCamera;

			if (_settings.PropCounterDisplay)
			    DrawEntityCounter();

			DrawCollabWorld();

			Entity hitEnt = VectorExtensions.RaycastEntity(new Vector2(0f, 0f), _mainCamera.Position, _mainCamera.Rotation);

			if (_settings.CrosshairType == CrosshairType.Crosshair)
			{
			    DrawCrosshair(hitEnt);
			}

			if (_settings.WorldObjectNames)
			    DrawWorldObjectNames(hitEnt);

            Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, 0);
            Function.Call(Hash.ENABLE_CONTROL_ACTION, 0, (int)Control.LookLeftRight);
            Function.Call(Hash.ENABLE_CONTROL_ACTION, 0, (int)Control.LookUpDown);
            Function.Call(Hash.ENABLE_CONTROL_ACTION, 0, (int)Control.CursorX);
            Function.Call(Hash.ENABLE_CONTROL_ACTION, 0, (int)Control.CursorY);
            Function.Call(Hash.ENABLE_CONTROL_ACTION, 0, (int)Control.FrontendPauseAlternate);

            // The typed wrapper, not Function.Call<float>: on Enhanced the generic Call hands back whatever
			// GetResAuto boxed, and that is never a float.
            var mouseX = API.GetControlNormal(0, (int)Control.LookLeftRight);
			var mouseY = API.GetControlNormal(0, (int)Control.LookUpDown);

			mouseX *= -1;
			mouseY *= -1;

		    bool gamepad = IsUsingGamepad();

		    if (gamepad)
		    {
		        mouseX *= _settings.GamepadCameraSensitivity;
		        mouseY *= _settings.GamepadCameraSensitivity;
		    }
		    else
		    {
		        mouseX *= _settings.CameraSensivity;
		        mouseY *= _settings.CameraSensivity;
		    }

            float movementModifier = 1f;
            if (Game.IsControlPressed(0, Control.Sprint))
                movementModifier = 5f;
            else if (Game.IsControlPressed(0, Control.CharacterWheel))
                movementModifier = 0.3f;

		    // 1 - 60, so the base speed runs 0.03 - 2.
		    movementModifier *= (gamepad ? _settings.GamepadMovementSensitivity : _settings.KeyboardMovementSensitivity) / 30f;

            float modifier = 1f;
            if (Game.IsControlPressed(0, Control.Sprint))
                modifier = 5f;
            else if (Game.IsControlPressed(0, Control.CharacterWheel))
                modifier = 0.3f;

			// The base object stays put while its stack or its loop is being configured, so the tool takes
			// the controls that would otherwise move the selected object.
			if (_stackingBase != null)
			{
			    ProcessStacking();
			}
			else if (_loopingBase != null)
			{
			    ProcessLooping();
			}
			else if (_selectedProp == null && _selectedMarker == null)
			{
			    ProcessFreelook(hitEnt, mouseX, mouseY, movementModifier, modifier);
			}
            else if(_selectedProp != null)
            {
                ProcessSelectedProp(modifier);
            }
			else if (_selectedMarker != null)
			{
			    ProcessSelectedMarker(modifier);
			}

			return Task.FromResult(0);
		}

        private void BeginChoosingObject(ObjectTypes type)
        {
            var oldType = _currentObjectType;
            _currentObjectType = type;
            bool sameType = oldType == _currentObjectType;

            if (!sameType)
            {
                // Categories are per-type, so switching type invalidates both the list and the choice.
                RedrawCategoriesMenu(_currentObjectType);
                _currentCategory = null;
                _dlcFilterItem = null;
                _objectsMenu.Clear();
            }

            _isChoosingObject = true;
            ClearMultiSelection();
            _snappedProp = null;
            _selectedProp = null;
            CloseAllMenus();

            if (_quitWithSearchVisible && sameType)
            {
                SetMenuVisible(_searchMenu, true);
                OnIndexChange(_searchMenu, new SelectedEventArgs(_searchMenu.SelectedIndex, 0));
            }
            else if (_currentCategory != null && sameType)
            {
                // Placing several objects from one category is the common case, so drop straight back
                // into it instead of making the player pick it again every time.
                SetMenuVisible(_objectsMenu, true);
                OnIndexChange(_objectsMenu, new SelectedEventArgs(_objectsMenu.SelectedIndex, 0));
            }
            else
            {
                SetMenuVisible(_categoriesMenu, true);
            }
        }

        private void ProcessObjectPreview()
        {
            if (_previewProp != null)
            {
                _previewProp.Rotation = _previewProp.Rotation + (_zAxis ? new Vector3(0f, 0f, 2.5f) : new Vector3(2.5f, 0f, 0f));
                if (_zAxis && IsPed(_previewProp))
                    _previewProp.Heading = _previewProp.Rotation.Z;
                DrawEntityBox(_previewProp, Colors.White);
            }

            if (Game.IsControlJustPressed(0, Control.SelectWeapon))
                _zAxis = !_zAxis;

            if (_objectPreviewCamera == null)
            {
                _objectPreviewCamera = World.CreateCamera(new Vector3(1200.016f, 4000.998f, 86.05062f), new Vector3(0f, 0f, 0f), 60f);
                _objectPreviewCamera.PointAt(_objectPreviewPos);
            }

            if (Game.IsControlPressed(0, Control.MoveDownOnly))
                _objectPreviewCamera.Position -= new Vector3(0f, 0.5f, 0f);

            if (Game.IsControlPressed(0, Control.MoveUpOnly))
                _objectPreviewCamera.Position += new Vector3(0f, 0.5f, 0f);

            // A list item scrolls its value with these same two controls, so paging stands down while the
            // cursor sits on the DLC filter, or one press would do both.
            bool onDlcFilter = _dlcFilterItem != null && _objectsMenu.SelectedIndex == 0;

            // Paging stops at the first model rather than at the filter above it, so that it always lands
            // on something to preview.
            int firstObject = _dlcFilterItem != null ? 1 : 0;

            // Paging by 100 only makes sense on the object list; the category list is short.
            if (_objectsMenu.Visible && !onDlcFilter && Game.IsControlJustPressed(0, Control.PhoneLeft))
            {
                if (_objectsMenu.SelectedIndex - 100 <= firstObject)
                    _objectsMenu.SelectedIndex = firstObject;
                else
                    _objectsMenu.SelectedIndex -= 100;
                OnIndexChange(_objectsMenu, new SelectedEventArgs(_objectsMenu.SelectedIndex, 0));
            }

            if (_objectsMenu.Visible && !onDlcFilter && Game.IsControlJustPressed(0, Control.PhoneRight))
            {
                if (_objectsMenu.SelectedIndex >= _objectsMenu.Items.Count - 101)
                    _objectsMenu.SelectedIndex = _objectsMenu.Items.Count - 1;
                else
                    _objectsMenu.SelectedIndex += 100;
                OnIndexChange(_objectsMenu, new SelectedEventArgs(_objectsMenu.SelectedIndex, 0));
            }

            World.RenderingCamera = _objectPreviewCamera;

            if (Game.IsControlJustPressed(0, Control.Context))
                ToggleFavorite();

            if (Game.IsControlJustPressed(0, Control.Jump))
            {
                // The on-screen keyboard is a Task in CitizenFX: there are no fibers to block the frame on
                // while the player types.
                Run("Search", async () =>
                {
                    string query = await Compat.GetUserInput(255);
                    if (string.IsNullOrWhiteSpace(query)) return;
                    if (query[0] == ' ')
                        query = query.Remove(0, 1);

                    // The player can have backed out of the picker entirely while the keyboard was up.
                    if (!_isChoosingObject) return;

                    SetMenuVisible(_objectsMenu, false);
                    SetMenuVisible(_categoriesMenu, false);
                    RedrawSearchMenu(query, _currentObjectType);
                    if (_searchMenu.Items.Count != 0)
                        OnIndexChange(_searchMenu, new SelectedEventArgs(0, 0));
                    _searchMenu.Name = "~b~" + Translation.Translate("SEARCH RESULTS FOR") + " \"" + query.ToUpper() + "\"";
                    SetMenuVisible(_searchMenu, true);
                });
            }
        }

        // The crosshair: four bars around a clear gap, drawn with DrawRect. Drawn rather than textured, so
        // there is nothing to load, register or ask a runtime texture dictionary for.

        /// <summary>The UI layer lays every element out in a fixed 1920x1080 space and scales it to the real
        /// resolution itself, so the middle of the screen is here whatever the player is running.</summary>
        private const float LemonUiWidth = 1920f;
        private const float LemonUiHeight = 1080f;

        private const float CrosshairArm = 14f;
        private const float CrosshairThickness = 2f;

        /// <summary>Left clear in the middle, so the bars frame what is being aimed at instead of covering it.</summary>
        private const float CrosshairGap = 5f;

        /// <summary>White into empty air, blue on one of the game's objects, yellow on one of ours.</summary>
        private static readonly Color CrosshairOnWorld = Color.FromArgb(255, 100, 180, 255);
        private static readonly Color CrosshairOnOurs = Colors.Yellow;

        private ScaledRectangle[] _crosshairArms;

        /// <summary>Draws the four bars. Built once: the middle of the screen does not move.</summary>
        private void DrawCrosshair(Entity hitEnt)
        {
            bool onSomething = hitEnt != null && hitEnt.Handle != 0;
            bool onOurs = onSomething && PropStreamer.GetAllHandles().Contains(hitEnt.Handle);

            var color = onSomething ? (onOurs ? CrosshairOnOurs : CrosshairOnWorld) : Colors.White;

            if (_crosshairArms == null)
            {
                const float centreX = LemonUiWidth * 0.5f;
                const float centreY = LemonUiHeight * 0.5f;
                const float half = CrosshairThickness * 0.5f;

                var across = new SizeF(CrosshairArm, CrosshairThickness);
                var down = new SizeF(CrosshairThickness, CrosshairArm);

                _crosshairArms = new[]
                {
                    new ScaledRectangle(new PointF(centreX - CrosshairGap - CrosshairArm, centreY - half), across),
                    new ScaledRectangle(new PointF(centreX + CrosshairGap, centreY - half), across),
                    new ScaledRectangle(new PointF(centreX - half, centreY - CrosshairGap - CrosshairArm), down),
                    new ScaledRectangle(new PointF(centreX - half, centreY + CrosshairGap), down),
                };
            }

            foreach (var arm in _crosshairArms)
            {
                arm.Color = color;
                arm.Draw();
            }
        }

        private void DrawEntityCounter()
        {
            const int interval = 45;
            var bottomRight = SafeZone.BottomRight;
            var background = Color.FromArgb(180, 255, 255, 255);
            var white = Colors.White;

            void Row(int slot, string label, string value)
            {
                float y = bottomRight.Y - (90 + (slot * interval));
                float valueY = bottomRight.Y - (102 + (slot * interval));
                float bgY = bottomRight.Y - (100 + (slot * interval));

                new ScaledTexture(new PointF(bottomRight.X - 248, bgY), new SizeF(250, 37), "timerbars", "all_black_bg")
                {
                    Color = background,
                }.Draw();

                new ScaledText(new PointF(bottomRight.X - 90, y), label, 0.3f, Font.ChaletLondon)
                {
                    Alignment = Alignment.Right,
                    Color = white,
                }.Draw();

                new ScaledText(new PointF(bottomRight.X - 20, valueY), value, 0.5f, Font.ChaletLondon)
                {
                    Alignment = Alignment.Right,
                    Color = white,
                }.Draw();
            }

            Row(5, Translation.Translate("PICKUPS"), PropStreamer.Pickups.Count.ToString());
            Row(4, Translation.Translate("MARKERS"), PropStreamer.Markers.Count.ToString());
            Row(3, Translation.Translate("WORLD"), PropStreamer.RemovedObjects.Count.ToString());
            // The map's own count, not the world's: what streaming has taken out is still part of the map,
            // and a counter that fell as the player flew away would be reporting something else.
            Row(2, Translation.Translate("PROPS"), PropStreamer.PropCount.ToString());
            Row(1, Translation.Translate("VEHICLES"), PropStreamer.VehicleCount.ToString());
            Row(0, Translation.Translate("PEDS"), PropStreamer.PedCount.ToString());
        }

        private void DrawEntityBox(Entity ent, Color color)
        {
            if(ent == null || (_settings.BoundingBox.HasValue && !_settings.BoundingBox.Value)) return;

            Vector3 min, max;
            LuaBridge.ModelDimensions(ent.Model.Hash, out min, out max);
            var modelSize = max - min;
            modelSize = new Vector3(modelSize.X/2, modelSize.Y/2, modelSize.Z/2);

            var b1 = GetEntityOffset(ent, new Vector3(-modelSize.X, -modelSize.Y, -modelSize.Z * 0));
            var b2 = GetEntityOffset(ent, new Vector3(-modelSize.X, modelSize.Y, -modelSize.Z * 0));
            var b3 = GetEntityOffset(ent, new Vector3(modelSize.X, -modelSize.Y, -modelSize.Z * 0));
            var b4 = GetEntityOffset(ent, new Vector3(modelSize.X, modelSize.Y, -modelSize.Z * 0));

            var a1 = GetEntityOffset(ent, new Vector3(-modelSize.X, -modelSize.Y, modelSize.Z * 2));
            var a2 = GetEntityOffset(ent, new Vector3(-modelSize.X, modelSize.Y, modelSize.Z * 2));
            var a3 = GetEntityOffset(ent, new Vector3(modelSize.X, -modelSize.Y, modelSize.Z * 2));
            var a4 = GetEntityOffset(ent, new Vector3(modelSize.X, modelSize.Y, modelSize.Z * 2));

            World.DrawLine(a1, a2, color);
            World.DrawLine(a2, a4, color);
            World.DrawLine(a4, a3, color);
            World.DrawLine(a3, a1, color);

            World.DrawLine(b1, b2, color);
            World.DrawLine(b2, b4, color);
            World.DrawLine(b4, b3, color);
            World.DrawLine(b3, b1, color);

            World.DrawLine(a1, b1, color);
            World.DrawLine(a2, b2, color);
            World.DrawLine(a3, b3, color);
            World.DrawLine(a4, b4, color);
        }

        private Vector3 GetEntityOffset(Entity ent, Vector3 offset)
        {
            // The typed wrapper, not Function.Call<Vector3>: the generic Call cannot return a vector on
            // Enhanced. The native takes its arguments by value, so unlike the raycast in LuaBridge it
            // needs no help from Lua.
            return API.GetOffsetFromEntityInWorldCoords(ent.Handle, offset.X, offset.Y, offset.Z);
        }
    }
}
