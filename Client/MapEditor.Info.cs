using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using MapEditor.Ui.Menus;

namespace MapEditor
{
    public partial class MapEditor
    {
        /// <summary>
        /// A scrollable numeric field. The old NativeUI build backed each of these with a
        /// pre-materialised list of every possible value (3,000,001 entries for a position
        /// axis); NativeDynamicItem computes the next value on demand instead.
        /// </summary>
        private static NativeDynamicItem<float> NumberItem(string title, float initial, Action<float> apply,
            float step, float min, float max)
        {
            var item = new NativeDynamicItem<float>(title, (float)Math.Round(initial, 2));
            item.ItemChanged += (sender, e) =>
            {
                float delta = e.Direction == Direction.Left ? -step : step;
                float value = (float)Math.Round(e.Object + delta, 2);
                if (value < min) value = min;
                if (value > max) value = max;
                apply(value);
                e.Object = value;
            };
            return item;
        }

        private const float PositionMin = -_possibleRangeUnits;
        private const float PositionMax = _possibleRangeUnits;
        private const float _possibleRangeUnits = 15000f; // _possibleRange * 0.01f

        /// <summary>
        /// The row that decides who owns this object once the map is published: the server, as one thing
        /// everybody shares, or every client as a copy of its own.
        ///
        /// It starts on whatever <see cref="Platform.SharedObjects"/> works out from the object itself, and
        /// setting it back to that answer clears the override rather than writing it down — an object nobody
        /// has an opinion about must keep following the rule, or the first save would freeze every object in
        /// the map at whatever today's rule happened to say.
        ///
        /// Nothing here changes the map being edited. A draft is local on every client whatever this says;
        /// it starts to mean something at the moment the map goes into everybody's world.
        /// </summary>
        private NativeCheckboxItem SharedItem(Entity ent)
        {
            var snapshot = PropStreamer.Snapshot(ent.Handle);
            var byRule = snapshot != null && snapshot.NeedsServerByRule;
            var now = snapshot != null && snapshot.NeedsServer;

            var item = new NativeCheckboxItem(Translation.Translate("Shared"), Translation.Translate(
                    "Once this map is published, the server creates this object once for everybody instead of " +
                    "every player spawning their own copy. Needed for anything the game or a player touches " +
                    "afterwards: vehicles, armed or walking peds, things with their physics on.") +
                    " " + Translation.Translate("This map's own answer for this object is:") + " " +
                    Translation.Translate(byRule ? "shared." : "local."),
                now);

            item.CheckboxChanged += (sender, e) =>
            {
                if (item.Checked == byRule) PropStreamer.SharedOverrides.Remove(ent.Handle);
                else PropStreamer.SharedOverrides[ent.Handle] = item.Checked;
            };

            return item;
        }

        private void RedrawObjectInfoMenu(Entity ent, bool refreshIndex)
        {
            if (ent == null) return;
            string name = "";

            if (IsProp(ent))
                name = ObjectDatabase.MainDb.ContainsValue(ent.Model.Hash) ? ObjectDatabase.MainDb.First(x => x.Value == ent.Model.Hash).Key.ToUpper() : "Unknown Prop";
            if (IsVehicle(ent))
                name = ObjectDatabase.VehicleDb.ContainsValue(ent.Model.Hash) ? ObjectDatabase.VehicleDb.First(x => x.Value == ent.Model.Hash).Key.ToUpper() : "Unknown Vehicle";
            if (IsPed(ent))
                name = ObjectDatabase.PedDb.ContainsValue(ent.Model.Hash) ? ObjectDatabase.PedDb.First(x => x.Value == ent.Model.Hash).Key.ToUpper() : "Unknown Ped";

            _objectInfoMenu.Name = "~b~" + name;
            _objectInfoMenu.Clear();

            var posXitem = NumberItem(Translation.Translate("Position X"), ent.Position.X,
                v => SetEntityPosition(ent, new Vector3(v, ent.Position.Y, ent.Position.Z)), ScrollStep, PositionMin, PositionMax);
            var posYitem = NumberItem(Translation.Translate("Position Y"), ent.Position.Y,
                v => SetEntityPosition(ent, new Vector3(ent.Position.X, v, ent.Position.Z)), ScrollStep, PositionMin, PositionMax);
            var posZitem = NumberItem(Translation.Translate("Position Z"), ent.Position.Z,
                v => SetEntityPosition(ent, new Vector3(ent.Position.X, ent.Position.Y, v)), ScrollStep, PositionMin, PositionMax);

            // Pitch/Roll/Yaw map onto Rotation Y/X/Z, matching what the old list handlers wrote.
            var rotXitem = NumberItem(Translation.Translate("Pitch"), ent.Rotation.Y,
                v => SetEntityRotation(ent, new Vector3(ent.Rotation.X, v, ent.Rotation.Z)), ScrollStep, -360f, 360f);
            var rotYitem = NumberItem(Translation.Translate("Roll"), ent.Rotation.X,
                v => SetEntityRotation(ent, new Vector3(v, ent.Rotation.Y, ent.Rotation.Z)), ScrollStep, -360f, 360f);
            var rotZitem = NumberItem(Translation.Translate("Yaw"), ent.Rotation.Z,
                v => SetEntityRotation(ent, new Vector3(ent.Rotation.X, ent.Rotation.Y, v)), ScrollStep, -360f, 360f);

            // Typing a value opens the on-screen keyboard, which is a Task here rather than a blocking call:
            // there is no fiber to hold the frame on while the player types. See Platform.TextInput and Run.
            posXitem.Activated += (sender, item) => Run("Position X", async () =>
            {
                var typed = await Compat.GetUserInput(ent.Position.X.ToString(CultureInfo.InvariantCulture), 10);
                SetObjectVector(ent, new Vector3(GetSafeFloat(typed, ent.Position.X), ent.Position.Y, ent.Position.Z));
            });
            posYitem.Activated += (sender, item) => Run("Position Y", async () =>
            {
                var typed = await Compat.GetUserInput(ent.Position.Y.ToString(CultureInfo.InvariantCulture), 10);
                SetObjectVector(ent, new Vector3(ent.Position.X, GetSafeFloat(typed, ent.Position.Y), ent.Position.Z));
            });
            posZitem.Activated += (sender, item) => Run("Position Z", async () =>
            {
                var typed = await Compat.GetUserInput(ent.Position.Z.ToString(CultureInfo.InvariantCulture), 10);
                SetObjectVector(ent, new Vector3(ent.Position.X, ent.Position.Y, GetSafeFloat(typed, ent.Position.Z)));
            });

            rotXitem.Activated += (sender, item) => Run("Pitch", async () =>
            {
                var typed = await Compat.GetUserInput(ent.Rotation.Y.ToString(CultureInfo.InvariantCulture).Limit(10), 10);
                SetObjectRotation(ent, new Vector3(ent.Rotation.X, GetSafeFloat(typed, ent.Rotation.Y), ent.Rotation.Z));
            });
            rotYitem.Activated += (sender, item) => Run("Roll", async () =>
            {
                var typed = await Compat.GetUserInput(ent.Rotation.X.ToString(CultureInfo.InvariantCulture).Limit(10), 10);
                SetObjectRotation(ent, new Vector3(GetSafeFloat(typed, ent.Rotation.X), ent.Rotation.Y, ent.Rotation.Z));
            });
            rotZitem.Activated += (sender, item) => Run("Yaw", async () =>
            {
                var typed = await Compat.GetUserInput(ent.Rotation.Z.ToString(CultureInfo.InvariantCulture).Limit(10), 10);
                SetObjectRotation(ent, new Vector3(ent.Rotation.X, ent.Rotation.Y, GetSafeFloat(typed, ent.Rotation.Z)));
            });

            var dynamic = new NativeCheckboxItem(Translation.Translate("Dynamic"),
                Translation.Translate("Whether the game gets to move this once the map is published. Nothing " +
                                      "moves while you are building: a dynamic object hangs where you put it, " +
                                      "and out of the way of everything else, until the map goes up."),
                !PropStreamer.StaticProps.Contains(ent.Handle));
            dynamic.CheckboxChanged += (ite, e) =>
            {
                var checkd = dynamic.Checked;
                if (checkd && PropStreamer.StaticProps.Contains(ent.Handle)) PropStreamer.StaticProps.Remove(ent.Handle);
                else if (!checkd && !PropStreamer.StaticProps.Contains(ent.Handle)) PropStreamer.StaticProps.Add(ent.Handle);

                PropStreamer.HoldStill(ent);

                // A prop with physics on will not hold a generated layout, so the rows that open the
                // generators come and go with this one — and "Shared" is decided partly from it too.
                int selected = _objectInfoMenu.SelectedIndex;
                RedrawObjectInfoMenu(ent, false);
                _objectInfoMenu.SelectedIndex = ClampIndex(selected, _objectInfoMenu.Items.Count);
            };

            var ident = new NativeItem("Identification", "Optional identification for easier access during scripting.");
            if (PropStreamer.Identifications.ContainsKey(ent.Handle))
                ident.AltTitle = PropStreamer.Identifications[ent.Handle];

            ident.Activated += (sender, item) => Run("Identification", async () =>
            {
                var hasId = PropStreamer.Identifications.ContainsKey(ent.Handle);
                var newLabel = hasId
                    ? await Compat.GetUserInput(PropStreamer.Identifications[ent.Handle], 20)
                    : await Compat.GetUserInput(20);

                if (newLabel == null) return;

                if (PropStreamer.Identifications.ContainsValue(newLabel))
                {
                    Compat.Notify(Translation.Translate("~r~~h~Map Editor~h~~w~~n~The identification must be unique!"));
                    return;
                }

                if (newLabel.Length > 0 && (Regex.IsMatch(newLabel, @"^\d") || newLabel.StartsWith(".") || newLabel.StartsWith(",") || newLabel.StartsWith("\\")))
                {
                    Compat.Notify(Translation.Translate("~r~~h~Map Editor~h~~w~~n~This identification is invalid!"));
                    return;
                }

                if (hasId)
                    PropStreamer.Identifications[ent.Handle] = newLabel;
                else
                    PropStreamer.Identifications.Add(ent.Handle, newLabel);

                ident.AltTitle = newLabel;
            });

            _objectInfoMenu.Add(posXitem);
            _objectInfoMenu.Add(posYitem);
            _objectInfoMenu.Add(posZitem);
            _objectInfoMenu.Add(rotXitem);
            _objectInfoMenu.Add(rotYitem);
            _objectInfoMenu.Add(rotZitem);

            // The generators lay copies out and expect them to stay put: a vehicle drives off, a ped walks
            // off, and a prop with its physics on falls over. Only a frozen prop holds what they build.
            if (IsProp(ent) && PropStreamer.StaticProps.Contains(ent.Handle))
            {
                var stackingItem = new NativeItem(Translation.Translate("Stacking Tool"), Translation.Translate(
                    "Copy this object along its own X, Y and Z axes, spaced by the model's own size."));
                stackingItem.Activated += (sender, item) => BeginStacking(ent);
                _objectInfoMenu.Add(stackingItem);

                var loopingItem = new NativeItem(Translation.Translate("Looping Generator"), Translation.Translate(
                    "Copy this object around a loop, each copy carried round and turned with it."));
                loopingItem.Activated += (sender, item) => BeginLooping(ent);
                _objectInfoMenu.Add(loopingItem);
            }

            _objectInfoMenu.Add(dynamic);

            // Not offered for a pickup: "who took it" is shared state whatever anybody ticks, and no
            // server-side native creates one, so a published map does not place them. See Server/LiveEntities.cs.
            if (!PropStreamer.IsPickup(ent.Handle)) _objectInfoMenu.Add(SharedItem(ent));

            _objectInfoMenu.Add(ident);

            if (IsProp(ent))
            {
                var doorItem = new NativeCheckboxItem("Door", Translation.Translate("This option overrides the \"Dynamic\" setting."), PropStreamer.Doors.Contains(ent.Handle));
                doorItem.CheckboxChanged += (sender, e) =>
                {
                    if (doorItem.Checked)
                    {
                        PropStreamer.Doors.Add(ent.Handle);
                        Function.Call(Hash.SET_ENTITY_DYNAMIC, ent.Handle, false);
                    }
                    else
                    {
                        PropStreamer.Doors.Remove(ent.Handle);
                        Function.Call(Hash.SET_ENTITY_DYNAMIC, ent.Handle, !PropStreamer.StaticProps.Contains(ent.Handle));
                    }

                    // A door hangs still in the editor and swings once the map is published, like every
                    // other thing here that the game is going to move. See PropStreamer.HoldStill.
                    PropStreamer.HoldStill(ent);
                };
                _objectInfoMenu.Add(doorItem);
            }

            if (IsPed(ent))
            {
                // The three rows below describe the ped; none of them does anything to the one standing here.
                // A ped in the map being edited stands still, unarmed and friendly whatever they say, and the
                // description becomes behaviour when the map is published — see PropStreamer.HoldStill for
                // why, and AutoloadedMaps.Configure for where it happens. So there is nothing to activate:
                // picking a value is the whole of the setting.
                var actions = new List<string> { "None", "Any - Walk", "Any - Warp", "Wander" };
                actions.AddRange(ObjectDatabase.ScrenarioDatabase.Keys);
                var scenarioItem = new NativeListItem<string>(Translation.Translate("Idle Action"), actions.ToArray())
                {
                    Description = Translation.Translate("What the ped does once the map is published. It stands still while you build."),
                    SelectedIndex = ClampIndex(actions.IndexOf(PropStreamer.ActiveScenarios[ent.Handle]), actions.Count),
                };
                scenarioItem.ItemChanged += (item, e) =>
                {
                    PropStreamer.ActiveScenarios[ent.Handle] = e.Object;
                    _changesMade++;
                };
                _objectInfoMenu.Add(scenarioItem);

                var rels = new List<string> { "Ballas", "Grove" };
                rels.AddRange(Enum.GetNames(typeof(Relationship)));
                var relItem = new NativeListItem<string>(Translation.Translate("Relationship"), rels.ToArray())
                {
                    Description = Translation.Translate("Who the ped gets on with once the map is published. It is friendly while you build."),
                    SelectedIndex = ClampIndex(rels.IndexOf(PropStreamer.ActiveRelationships[ent.Handle]), rels.Count),
                };
                relItem.ItemChanged += (item, e) =>
                {
                    PropStreamer.ActiveRelationships[ent.Handle] = e.Object;
                    _changesMade++;
                };
                _objectInfoMenu.Add(relItem);

                var weps = Enum.GetNames(typeof(WeaponHash));
                var wepItem = new NativeListItem<string>(Translation.Translate("Weapon"), weps)
                {
                    Description = Translation.Translate("What the ped is handed once the map is published. It is unarmed while you build."),
                    SelectedIndex = ClampIndex(weps.ToList().IndexOf(PropStreamer.ActiveWeapons[ent.Handle].ToString()), weps.Length),
                };
                wepItem.ItemChanged += (item, e) =>
                {
                    // Enum.Parse(Type, string) is blocked by FiveM's Mono sandbox; see ObjectDatabase.SetupRelationships.
                    WeaponHash weapon;
                    if (!Enum.TryParse(e.Object, out weapon)) return;
                    PropStreamer.ActiveWeapons[ent.Handle] = weapon;
                    _changesMade++;
                };
                _objectInfoMenu.Add(wepItem);

                RedrawPedComponentsMenu((Ped)ent);
                var componentsItem = _objectInfoMenu.AddSubMenu(_pedComponentsMenu);
                componentsItem.Title = Translation.Translate("Ped Components");
                componentsItem.Description = Translation.Translate("Change what the ped wears: its clothes, its hair and its face.");
            }

            if (IsVehicle(ent))
            {
                var veh = (Vehicle)ent;

                var sirentBool = new NativeCheckboxItem(Translation.Translate("Siren"), PropStreamer.ActiveSirens.Contains(ent.Handle));
                sirentBool.CheckboxChanged += (item, e) =>
                {
                    var check = sirentBool.Checked;
                    if (check && !PropStreamer.ActiveSirens.Contains(ent.Handle)) PropStreamer.ActiveSirens.Add(ent.Handle);
                    else if (!check && PropStreamer.ActiveSirens.Contains(ent.Handle)) PropStreamer.ActiveSirens.Remove(ent.Handle);
                    veh.IsSirenActive = check;
                    _changesMade++;
                };
                _objectInfoMenu.Add(sirentBool);

                var colors = (VehicleColor[])Enum.GetValues(typeof(VehicleColor));
                var colorNames = colors.Select(c => c.ToString()).ToArray();

                var primaryColor = new NativeListItem<string>(Translation.Translate("Primary Color"), colorNames)
                {
                    Description = Translation.Translate("The vehicle's main paint."),
                    SelectedIndex = ClampIndex(Array.IndexOf(colors, veh.Mods.PrimaryColor), colors.Length),
                };
                primaryColor.ItemChanged += (item, e) =>
                {
                    veh.Mods.PrimaryColor = colors[e.Index];
                    _changesMade++;
                };
                _objectInfoMenu.Add(primaryColor);

                var secondaryColor = new NativeListItem<string>(Translation.Translate("Secondary Color"), colorNames)
                {
                    Description = Translation.Translate("The vehicle's second paint, worn by its trim and its stripes."),
                    SelectedIndex = ClampIndex(Array.IndexOf(colors, veh.Mods.SecondaryColor), colors.Length),
                };
                secondaryColor.ItemChanged += (item, e) =>
                {
                    veh.Mods.SecondaryColor = colors[e.Index];
                    _changesMade++;
                };
                _objectInfoMenu.Add(secondaryColor);

                int liveryCount = Platform.Natives.LiveryCount(veh.Handle);

                if (liveryCount > 0)
                {
                    var liveries = new List<string> { Translation.Translate("None") };
                    for (int i = 0; i < liveryCount; i++)
                        liveries.Add((i + 1).ToString(CultureInfo.InvariantCulture));

                    var liveryItem = new NativeListItem<string>(Translation.Translate("Livery"), liveries.ToArray())
                    {
                        Description = Translation.Translate("The pattern painted over the vehicle, where its model has any."),
                        // The game counts the liveries from zero and calls "no livery" -1, but the row leads with None.
                        SelectedIndex = ClampIndex(Platform.Natives.GetLivery(veh.Handle) + 1, liveries.Count),
                    };
                    liveryItem.ItemChanged += (item, e) =>
                    {
                        Platform.Natives.SetLivery(veh.Handle, e.Index - 1);
                        _changesMade++;
                    };
                    _objectInfoMenu.Add(liveryItem);
                }
            }

            if (PropStreamer.IsPickup(ent.Handle))
            {
                var pickup = PropStreamer.GetPickup(ent.Handle);

                var amountList = new NativeItem(Translation.Translate("Amount"));
                amountList.AltTitle = pickup.Amount.ToString();
                amountList.Activated += (sender, item) => Run("Pickup amount", async () =>
                {
                    string playerInput = await Compat.GetUserInput(10);
                    int newValue;
                    if (!int.TryParse(playerInput, out newValue) || newValue < -1)
                    {
                        Compat.Notify("~r~~h~Map Editor~h~~n~~w~" + Translation.Translate("Input was not in the correct format."));
                        return;
                    }
                    // Changing a pickup re-creates it, which waits for the new object to appear.
                    await pickup.SetAmount(newValue);
                    amountList.AltTitle = pickup.Amount.ToString();
                    _selectedProp = Compat.Ent(pickup.ObjectHandle);
                    if (_settings.SnapCameraToSelectedObject && _selectedProp != null)
                        _mainCamera.PointAt(_selectedProp, Vector3.Zero);
                });
                _objectInfoMenu.Add(amountList);

                var pickupTypesList = Enum.GetValues(typeof(ObjectDatabase.PickupHash)).Cast<ObjectDatabase.PickupHash>().ToList();
                var itemIndex = pickupTypesList.IndexOf((ObjectDatabase.PickupHash)pickup.PickupHash);

                var pickupTypeItem = new NativeListItem<string>("Type", pickupTypesList.Select(s => s.ToString()).ToArray())
                {
                    SelectedIndex = ClampIndex(itemIndex, pickupTypesList.Count),
                };
                pickupTypeItem.ItemChanged += (sender, e) => Run("Pickup type", async () =>
                {
                    await pickup.SetPickupHash((int)pickupTypesList[e.Index]);
                    _selectedProp = Compat.Ent(pickup.ObjectHandle);
                    if (_settings.SnapCameraToSelectedObject && _selectedProp != null)
                        _mainCamera.PointAt(_selectedProp, Vector3.Zero);
                });
                _objectInfoMenu.Add(pickupTypeItem);

                var timeoutTime = new NativeItem("Regeneration Time");
                timeoutTime.AltTitle = pickup.Timeout.ToString();
                timeoutTime.Activated += (sender, item) => Run("Pickup regeneration", async () =>
                {
                    string playerInput = await Compat.GetUserInput(10);
                    int newValue;
                    if (!int.TryParse(playerInput, out newValue) || newValue < 0)
                    {
                        Compat.Notify("~r~~h~Map Editor~h~~n~~w~" + Translation.Translate("Input was not in the correct format."));
                        return;
                    }
                    pickup.Timeout = newValue;
                    timeoutTime.AltTitle = newValue.ToString();
                });
                _objectInfoMenu.Add(timeoutTime);
            }

            if (refreshIndex && _objectInfoMenu.Items.Count > 0)
                _objectInfoMenu.SelectedIndex = 0;
        }

        private void SetEntityPosition(Entity ent, Vector3 pos)
        {
            if (!IsProp(ent))
                ent.Position = pos;
            else
                ent.PositionNoOffset = pos;

            SyncPickup(ent);
            _changesMade++;
        }

        private void SetEntityRotation(Entity ent, Vector3 rot)
        {
            ent.Quaternion = rot.ToQuaternion();
            _changesMade++;
        }

        public void SetObjectVector(Entity ent, Vector3 vect)
        {
            SetEntityPosition(ent, vect);
            RedrawObjectInfoMenu(ent, false);
        }

        public void SetObjectRotation(Entity ent, Vector3 rot)
        {
            SetEntityRotation(ent, rot);
            RedrawObjectInfoMenu(ent, false);
        }

        public void SetMarkerVector(Marker ent, Vector3 v)
        {
            ent.Position = v;
            RedrawObjectInfoMenu(ent, false);
        }

        public void SetMarkerRotation(Marker ent, Vector3 v)
        {
            ent.Rotation = v;
            RedrawObjectInfoMenu(ent, false);
        }

        public void SetMarkerScale(Marker ent, Vector3 v)
        {
            ent.Scale = v;
            RedrawObjectInfoMenu(ent, false);
        }

        private void RedrawObjectInfoMenu(Marker ent, bool refreshIndex)
        {
            if (ent == null) return;

            _objectInfoMenu.Name = "~b~" + ent.Type + " #" + ent.Id;
            _objectInfoMenu.Clear();

            var type = new NativeListItem<string>(Translation.Translate("Type"), _markersTypes)
            {
                SelectedIndex = ClampIndex(_markersTypes.ToList().IndexOf(ent.Type.ToString()), _markersTypes.Length),
            };
            type.ItemChanged += (ite, e) =>
            {
                MarkerType hash;
                Enum.TryParse(e.Object, out hash);
                ent.Type = hash;
            };

            var posXitem = NumberItem(Translation.Translate("Position X"), ent.Position.X,
                v => ent.Position = new Vector3(v, ent.Position.Y, ent.Position.Z), ScrollStep, PositionMin, PositionMax);
            var posYitem = NumberItem(Translation.Translate("Position Y"), ent.Position.Y,
                v => ent.Position = new Vector3(ent.Position.X, v, ent.Position.Z), ScrollStep, PositionMin, PositionMax);
            var posZitem = NumberItem(Translation.Translate("Position Z"), ent.Position.Z,
                v => ent.Position = new Vector3(ent.Position.X, ent.Position.Y, v), ScrollStep, PositionMin, PositionMax);

            var rotXitem = NumberItem(Translation.Translate("Rotation X"), ent.Rotation.X,
                v => ent.Rotation = new Vector3(v, ent.Rotation.Y, ent.Rotation.Z), ScrollStep, -360f, 360f);
            var rotYitem = NumberItem(Translation.Translate("Rotation Y"), ent.Rotation.Y,
                v => ent.Rotation = new Vector3(ent.Rotation.X, v, ent.Rotation.Z), ScrollStep, -360f, 360f);
            var rotZitem = NumberItem(Translation.Translate("Rotation Z"), ent.Rotation.Z,
                v => ent.Rotation = new Vector3(ent.Rotation.X, ent.Rotation.Y, v), ScrollStep, -360f, 360f);

            var scaleXitem = NumberItem(Translation.Translate("Scale X"), ent.Scale.X,
                v => ent.Scale = new Vector3(v, ent.Scale.Y, ent.Scale.Z), ScrollStep, 0f, 10f);
            var scaleYitem = NumberItem(Translation.Translate("Scale Y"), ent.Scale.Y,
                v => ent.Scale = new Vector3(ent.Scale.X, v, ent.Scale.Z), ScrollStep, 0f, 10f);
            var scaleZitem = NumberItem(Translation.Translate("Scale Z"), ent.Scale.Z,
                v => ent.Scale = new Vector3(ent.Scale.X, ent.Scale.Y, v), ScrollStep, 0f, 10f);

            posXitem.Activated += (sender, item) => Run("Marker position X", async () =>
            {
                var typed = await Compat.GetUserInput(ent.Position.X.ToString(CultureInfo.InvariantCulture), 10);
                SetMarkerVector(ent, new Vector3(GetSafeFloat(typed, ent.Position.X), ent.Position.Y, ent.Position.Z));
            });
            posYitem.Activated += (sender, item) => Run("Marker position Y", async () =>
            {
                var typed = await Compat.GetUserInput(ent.Position.Y.ToString(CultureInfo.InvariantCulture), 10);
                SetMarkerVector(ent, new Vector3(ent.Position.X, GetSafeFloat(typed, ent.Position.Y), ent.Position.Z));
            });
            posZitem.Activated += (sender, item) => Run("Marker position Z", async () =>
            {
                var typed = await Compat.GetUserInput(ent.Position.Z.ToString(CultureInfo.InvariantCulture), 10);
                SetMarkerVector(ent, new Vector3(ent.Position.X, ent.Position.Y, GetSafeFloat(typed, ent.Position.Z)));
            });

            rotXitem.Activated += (sender, item) => Run("Marker rotation X", async () =>
            {
                var typed = await Compat.GetUserInput(ent.Rotation.X.ToString(CultureInfo.InvariantCulture), 10);
                SetMarkerRotation(ent, new Vector3(GetSafeFloat(typed, ent.Rotation.X), ent.Rotation.Y, ent.Rotation.Z));
            });
            rotYitem.Activated += (sender, item) => Run("Marker rotation Y", async () =>
            {
                var typed = await Compat.GetUserInput(ent.Rotation.Y.ToString(CultureInfo.InvariantCulture), 10);
                SetMarkerRotation(ent, new Vector3(ent.Rotation.X, GetSafeFloat(typed, ent.Rotation.Y), ent.Rotation.Z));
            });
            rotZitem.Activated += (sender, item) => Run("Marker rotation Z", async () =>
            {
                var typed = await Compat.GetUserInput(ent.Rotation.Z.ToString(CultureInfo.InvariantCulture), 10);
                SetMarkerRotation(ent, new Vector3(ent.Rotation.X, ent.Rotation.Y, GetSafeFloat(typed, ent.Rotation.Z)));
            });

            scaleXitem.Activated += (sender, item) => Run("Marker scale X", async () =>
            {
                var typed = await Compat.GetUserInput(ent.Scale.X.ToString(CultureInfo.InvariantCulture), 10);
                SetMarkerScale(ent, new Vector3(GetSafeFloat(typed, ent.Scale.X), ent.Scale.Y, ent.Scale.Z));
            });
            scaleYitem.Activated += (sender, item) => Run("Marker scale Y", async () =>
            {
                var typed = await Compat.GetUserInput(ent.Scale.Y.ToString(CultureInfo.InvariantCulture), 10);
                SetMarkerScale(ent, new Vector3(ent.Scale.X, GetSafeFloat(typed, ent.Scale.Y), ent.Scale.Z));
            });
            scaleZitem.Activated += (sender, item) => Run("Marker scale Z", async () =>
            {
                var typed = await Compat.GetUserInput(ent.Scale.Z.ToString(CultureInfo.InvariantCulture), 10);
                SetMarkerScale(ent, new Vector3(ent.Scale.X, ent.Scale.Y, GetSafeFloat(typed, ent.Scale.Z)));
            });

            var possibleColors = Enumerable.Range(0, 256).ToArray();

            var colorR = new NativeListItem<int>(Translation.Translate("Red Color"), possibleColors) { SelectedIndex = ClampIndex(ent.Red, 256) };
            var colorG = new NativeListItem<int>(Translation.Translate("Green Color"), possibleColors) { SelectedIndex = ClampIndex(ent.Green, 256) };
            var colorB = new NativeListItem<int>(Translation.Translate("Blue Color"), possibleColors) { SelectedIndex = ClampIndex(ent.Blue, 256) };
            var colorA = new NativeListItem<int>(Translation.Translate("Transparency"), possibleColors) { SelectedIndex = ClampIndex(ent.Alpha, 256) };

            colorR.ItemChanged += (item, e) => ent.Red = e.Object;
            colorG.ItemChanged += (item, e) => ent.Green = e.Object;
            colorB.ItemChanged += (item, e) => ent.Blue = e.Object;
            colorA.ItemChanged += (item, e) => ent.Alpha = e.Object;

            var bobItem = new NativeCheckboxItem(Translation.Translate("Bop Up And Down"), ent.BobUpAndDown);
            bobItem.CheckboxChanged += (ite, e) => ent.BobUpAndDown = bobItem.Checked;

            var faceCam = new NativeCheckboxItem(Translation.Translate("Face Camera"), ent.RotateToCamera);
            faceCam.CheckboxChanged += (ite, e) => ent.RotateToCamera = faceCam.Checked;

            var targetId = 0;
            if (ent.TeleportTarget.HasValue)
            {
                var ourMarkers = PropStreamer.Markers
                    .Where(m => (m.Position - ent.TeleportTarget.Value).Length() < 1f)
                    .OrderBy(m => (m.Position - ent.TeleportTarget.Value).Length())
                    .ToList();
                if (ourMarkers.Any())
                    targetId = ourMarkers.First().Id + 1;
            }

            var targetOptions = Enumerable.Range(-1, _markerCounter + 1).ToArray();
            var targetPos = new NativeListItem<int>(Translation.Translate("Teleport Marker Target"), targetOptions)
            {
                SelectedIndex = ClampIndex(targetId, Math.Max(1, targetOptions.Length)),
            };
            targetPos.ItemChanged += (sender, e) =>
            {
                if (e.Index == 0)
                {
                    ent.TeleportTarget = null;
                    return;
                }
                ent.TeleportTarget = PropStreamer.Markers.FirstOrDefault(n => n.Id == e.Index - 1)?.Position;
            };

            var loadPointItem = new NativeCheckboxItem(Translation.Translate("Mark as Loading Point"),
                Translation.Translate("Player will be teleported here BEFORE starting to load the map."),
                PropStreamer.CurrentMapMetadata.LoadingPoint.HasValue &&
                (PropStreamer.CurrentMapMetadata.LoadingPoint.Value - ent.Position).Length() < 1f);
            loadPointItem.CheckboxChanged += (sender, e) =>
            {
                PropStreamer.CurrentMapMetadata.LoadingPoint = loadPointItem.Checked ? ent.Position : (Vector3?)null;
            };

            var loadTeleportItem = new NativeCheckboxItem(Translation.Translate("Mark as Starting Point"),
                Translation.Translate("Player will be teleported here AFTER starting to load the map."),
                PropStreamer.CurrentMapMetadata.TeleportPoint.HasValue &&
                (PropStreamer.CurrentMapMetadata.TeleportPoint.Value - ent.Position).Length() < 1f);
            loadTeleportItem.CheckboxChanged += (sender, e) =>
            {
                PropStreamer.CurrentMapMetadata.TeleportPoint = loadTeleportItem.Checked ? ent.Position : (Vector3?)null;
            };

            var visiblityItem = new NativeCheckboxItem(Translation.Translate("Only Visible In Editor"), ent.OnlyVisibleInEditor);
            visiblityItem.CheckboxChanged += (sender, e) => ent.OnlyVisibleInEditor = visiblityItem.Checked;

            _objectInfoMenu.Add(type);
            _objectInfoMenu.Add(posXitem);
            _objectInfoMenu.Add(posYitem);
            _objectInfoMenu.Add(posZitem);
            _objectInfoMenu.Add(rotXitem);
            _objectInfoMenu.Add(rotYitem);
            _objectInfoMenu.Add(rotZitem);
            _objectInfoMenu.Add(scaleXitem);
            _objectInfoMenu.Add(scaleYitem);
            _objectInfoMenu.Add(scaleZitem);
            _objectInfoMenu.Add(colorR);
            _objectInfoMenu.Add(colorG);
            _objectInfoMenu.Add(colorB);
            _objectInfoMenu.Add(colorA);
            _objectInfoMenu.Add(bobItem);
            _objectInfoMenu.Add(faceCam);
            _objectInfoMenu.Add(targetPos);
            _objectInfoMenu.Add(loadPointItem);
            _objectInfoMenu.Add(loadTeleportItem);
            _objectInfoMenu.Add(visiblityItem);

            if (refreshIndex && _objectInfoMenu.Items.Count > 0)
                _objectInfoMenu.SelectedIndex = 0;
        }
    }
}
