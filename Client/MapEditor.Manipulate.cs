using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using MapEditor.Platform;
using Control = CitizenFX.Core.Control;

namespace MapEditor
{
    public partial class MapEditor
    {
        private static readonly Color MultiSelectionColor = Color.FromArgb(200, 20, 200, 20);

        private void ProcessFreelook(Entity hitEnt, float mouseX, float mouseY, float movementModifier, float modifier)
        {
            if (!_menuPool.AreAnyVisible || IsUsingGamepad())
                _mainCamera.Rotation = VectorExtensions.ClampCameraRotation(
                    new Vector3(_mainCamera.Rotation.X + mouseY, _mainCamera.Rotation.Y, _mainCamera.Rotation.Z + mouseX));

            var dir = VectorExtensions.RotationToDirection(_mainCamera.Rotation);
            var rotLeft = _mainCamera.Rotation + new Vector3(0, 0, -10);
            var rotRight = _mainCamera.Rotation + new Vector3(0, 0, 10);
            var right = VectorExtensions.RotationToDirection(rotRight) - VectorExtensions.RotationToDirection(rotLeft);

            var newPos = _mainCamera.Position;
            if (Game.IsControlPressed(0, Control.MoveUpOnly))
                newPos += dir * movementModifier;
            if (Game.IsControlPressed(0, Control.MoveDownOnly))
                newPos -= dir * movementModifier;
            if (Game.IsControlPressed(0, Control.MoveLeftOnly))
                newPos += right * movementModifier;
            if (Game.IsControlPressed(0, Control.MoveRightOnly))
                newPos -= right * movementModifier;
            _mainCamera.Position = newPos;
            Game.Player.Character.PositionNoOffset = _mainCamera.Position - dir * 8f;

            PruneMultiSelection();
            foreach (Entity selected in _multiSelection)
                DrawEntityBox(selected, MultiSelectionColor);

            if (_multiSelectionSnapped)
            {
                ProcessMultiSelectionMove(modifier);
            }
            else if (_snappedProp != null)
            {
                if (!IsProp(_snappedProp))
                    _snappedProp.Position = VectorExtensions.RaycastEverything(new Vector2(0f, 0f), _mainCamera.Position, _mainCamera.Rotation, _snappedProp);
                else
                    _snappedProp.PositionNoOffset = VectorExtensions.RaycastEverything(new Vector2(0f, 0f), _mainCamera.Position, _mainCamera.Rotation, _snappedProp);

                if (Game.IsControlPressed(0, Control.CursorScrollUp) || Game.IsControlPressed(0, Control.FrontendRb))
                {
                    _snappedProp.Rotation = _snappedProp.Rotation - new Vector3(0f, 0f, modifier);
                    if (IsPed(_snappedProp))
                        _snappedProp.Heading = _snappedProp.Rotation.Z;
                }

                if (Game.IsControlPressed(0, Control.CursorScrollDown) || Game.IsControlPressed(0, Control.FrontendLb))
                {
                    _snappedProp.Rotation = _snappedProp.Rotation + new Vector3(0f, 0f, modifier);
                    if (IsPed(_snappedProp))
                        _snappedProp.Heading = _snappedProp.Rotation.Z;
                }

                if (Game.IsControlJustPressed(0, Control.CreatorDelete))
                {
                    RemoveItemFromEntityMenu(_snappedProp);
                    PropStreamer.RemoveEntity(_snappedProp.Handle);
                    if (PropStreamer.Identifications.ContainsKey(_snappedProp.Handle))
                        PropStreamer.Identifications.Remove(_snappedProp.Handle);
                    _snappedProp = null;
                    _changesMade++;
                }

                if (_snappedProp != null && Game.IsControlJustPressed(0, Control.Attack))
                {
                    _snappedProp = null;
                    _changesMade++;
                }

                DrawButtons(_snappedButtons);
            }
            else if (_snappedMarker != null)
            {
                _snappedMarker.Position = VectorExtensions.RaycastEverything(new Vector2(0f, 0f), _mainCamera.Position, _mainCamera.Rotation, Game.Player.Character);

                if (Game.IsControlPressed(0, Control.CursorScrollUp) || Game.IsControlPressed(0, Control.FrontendRb))
                    _snappedMarker.Rotation = _snappedMarker.Rotation - new Vector3(0f, 0f, modifier);

                if (Game.IsControlPressed(0, Control.CursorScrollDown) || Game.IsControlPressed(0, Control.FrontendLb))
                    _snappedMarker.Rotation = _snappedMarker.Rotation + new Vector3(0f, 0f, modifier);

                if (Game.IsControlJustPressed(0, Control.CreatorDelete))
                {
                    RemoveMarkerFromEntityMenu(_snappedMarker.Id);
                    PropStreamer.Markers.Remove(_snappedMarker);
                    _snappedMarker = null;
                    _changesMade++;
                }

                if (Game.IsControlJustPressed(0, Control.Attack))
                {
                    _snappedMarker = null;
                    _changesMade++;
                }

                DrawButtons(_snappedButtons);
            }
            else if (_snappedLaser != null)
            {
                // A laser is carried a metre above whatever the crosshair finds, the same offset it was put
                // down at: a grating dropped flush onto the floor is one nobody can walk into.
                _snappedLaser.Position = VectorExtensions.RaycastEverything(new Vector2(0f, 0f),
                    _mainCamera.Position, _mainCamera.Rotation, Game.Player.Character) + new Vector3(0f, 0f, 1f);

                if (Game.IsControlPressed(0, Control.CursorScrollUp) || Game.IsControlPressed(0, Control.FrontendRb))
                    _snappedLaser.Rotation = _snappedLaser.Rotation - new Vector3(0f, 0f, modifier);

                if (Game.IsControlPressed(0, Control.CursorScrollDown) || Game.IsControlPressed(0, Control.FrontendLb))
                    _snappedLaser.Rotation = _snappedLaser.Rotation + new Vector3(0f, 0f, modifier);

                if (Game.IsControlJustPressed(0, Control.CreatorDelete))
                {
                    RemoveLaserFromEntityMenu(_snappedLaser.Id);
                    PropStreamer.Lasers.Remove(_snappedLaser);
                    _snappedLaser = null;
                    _changesMade++;
                }

                if (Game.IsControlJustPressed(0, Control.Attack))
                {
                    _snappedLaser = null;
                    _changesMade++;
                }

                DrawButtons(_snappedButtons);
            }
            else
            {
                if (_settings.CrosshairType == CrosshairType.Orb)
                {
                    var pos = VectorExtensions.RaycastEverything(new Vector2(0f, 0f), _mainCamera.Position, _mainCamera.Rotation, Game.Player.Character);
                    var color = Color.FromArgb(255, 200, 20, 20);
                    if (hitEnt != null && hitEnt.Handle != 0 && !PropStreamer.GetAllHandles().Contains(hitEnt.Handle))
                        color = Color.FromArgb(255, 20, 20, 255);
                    else if (hitEnt != null && hitEnt.Handle != 0 && PropStreamer.GetAllHandles().Contains(hitEnt.Handle))
                        color = Color.FromArgb(255, 200, 200, 20);
                    Function.Call(Hash.DRAW_MARKER, 28, pos.X, pos.Y, pos.Z, 0f, 0f, 0f, 0f, 0f, 0f, 0.20f, 0.20f, 0.20f, color.R, color.G, color.B, color.A, false, true, 2, false, false, false, false);
                }

                if (Game.IsControlJustPressed(0, Control.Aim))
                {
                    if (_multiSelection.Count > 0)
                    {
                        BeginMultiSelectionMove();
                    }
                    // CanTouch is what stops two people in a session dragging the same crate. Asked here
                    // rather than after the fact so the refusal costs no round trip and the crate never
                    // moves at all; see MapEditor.Collab.cs.
                    else if (hitEnt != null && PropStreamer.GetAllHandles().Contains(hitEnt.Handle) && CanTouch(hitEnt))
                    {
                        _snappedProp = WrapEntity(hitEnt);
                        _changesMade++;
                    }
                    else
                    {
                        var pos = VectorExtensions.RaycastEverything(new Vector2(0f, 0f), _mainCamera.Position, _mainCamera.Rotation, Game.Player.Character);
                        Marker mark = PropStreamer.Markers.FirstOrDefault(m => (m.Position - pos).Length() < 2f);
                        if (mark != null && CanTouch(mark))
                        {
                            _snappedMarker = mark;
                            _changesMade++;
                        }
                        else
                        {
                            // Markers first and lasers after, because a marker is the smaller of the two and
                            // is the thing more likely to be underneath one.
                            Laser laser = LaserNear(pos);
                            if (laser != null && CanTouch(laser))
                            {
                                _snappedLaser = laser;
                                _changesMade++;
                            }
                        }
                    }
                }

                if (Game.IsControlJustPressed(0, Control.Attack) && IsMultiSelectKeyDown())
                {
                    // Refused at the point of adding rather than at the point of moving: a group is moved
                    // as one, so one object of somebody else's in it would stop the whole group.
                    if (hitEnt != null && PropStreamer.GetAllHandles().Contains(hitEnt.Handle) &&
                        (_multiSelection.Any(e => e != null && e.Handle == hitEnt.Handle) || CanTouch(hitEnt)))
                        ToggleMultiSelection(hitEnt);
                }
                else if (Game.IsControlJustPressed(0, Control.Attack))
                {
                    // A plain click always starts a fresh selection.
                    ClearMultiSelection();

                    if (hitEnt != null && PropStreamer.GetAllHandles().Contains(hitEnt.Handle) && CanTouch(hitEnt))
                    {
                        _selectedProp = WrapEntity(hitEnt);
                        RedrawObjectInfoMenu(_selectedProp, true);
                        CloseAllMenus();
                        SetMenuVisible(_objectInfoMenu, true);
                        if (_settings.SnapCameraToSelectedObject)
                            _mainCamera.PointAt(_selectedProp, Vector3.Zero);
                        _changesMade++;
                    }
                    else
                    {
                        var pos = VectorExtensions.RaycastEverything(new Vector2(0f, 0f), _mainCamera.Position, _mainCamera.Rotation, Game.Player.Character);
                        Marker mark = PropStreamer.Markers.FirstOrDefault(m => (m.Position - pos).Length() < 2f);
                        if (mark != null && CanTouch(mark))
                        {
                            _selectedMarker = mark;
                            RedrawObjectInfoMenu(_selectedMarker, true);
                            CloseAllMenus();
                            SetMenuVisible(_objectInfoMenu, true);
                            _changesMade++;
                        }
                        else
                        {
                            Laser laser = LaserNear(pos);
                            if (laser != null && CanTouch(laser))
                            {
                                _selectedLaser = laser;
                                RedrawObjectInfoMenu(_selectedLaser, true);
                                CloseAllMenus();
                                SetMenuVisible(_objectInfoMenu, true);
                                _changesMade++;
                            }
                        }
                    }
                }

                if (Game.IsControlJustReleased(0, Control.LookBehind))
                {
                    if (_multiSelection.Count > 0)
                    {
                        // Copying spawns, and spawning waits for a model: it cannot be finished inside a
                        // frame. See Run.
                        Run("Copy selection", CopyMultiSelection);
                    }
                    else if (hitEnt != null)
                    {
                        Run("Copy entity", async () =>
                        {
                            var copy = await CopyEntity(hitEnt);
                            if (copy == null) return;
                            _snappedProp = copy;
                            _changesMade++;
                        });
                    }
                    else
                    {
                        var pos = VectorExtensions.RaycastEverything(new Vector2(0f, 0f), _mainCamera.Position, _mainCamera.Rotation, Game.Player.Character);
                        Marker mark = PropStreamer.Markers.FirstOrDefault(m => (m.Position - pos).Length() < 2f);
                        if (mark != null)
                        {
                            var tmpMark = CloneMarker(mark);
                            AddItemToEntityMenu(tmpMark);
                            PropStreamer.Markers.Add(tmpMark);
                            _snappedMarker = tmpMark;
                            _changesMade++;
                        }
                        else
                        {
                            Laser laser = LaserNear(pos);
                            if (laser != null)
                            {
                                var tmpLaser = CloneLaser(laser);
                                AddItemToEntityMenu(tmpLaser);
                                PropStreamer.Lasers.Add(tmpLaser);
                                _snappedLaser = tmpLaser;
                                _changesMade++;
                            }
                        }
                    }
                }

                // Copying takes whatever is under the crosshair, editor-placed or not; starring its model is
                // the other half of that, for game objects that are only ever found by looking at them.
                if (Game.IsControlJustPressed(0, Control.Context) && hitEnt != null)
                {
                    FavoriteAimedEntity(hitEnt);
                }

                if (Game.IsControlJustPressed(0, Control.CreatorDelete))
                {
                    if (_multiSelection.Count > 0)
                    {
                        DeleteMultiSelection();
                    }
                    else if (hitEnt != null && PropStreamer.GetAllHandles().Contains(hitEnt.Handle) && CanTouch(hitEnt))
                    {
                        RemoveItemFromEntityMenu(hitEnt);
                        if (PropStreamer.Identifications.ContainsKey(hitEnt.Handle))
                            PropStreamer.Identifications.Remove(hitEnt.Handle);
                        if (PropStreamer.ActiveScenarios.ContainsKey(hitEnt.Handle))
                            PropStreamer.ActiveScenarios.Remove(hitEnt.Handle);
                        if (PropStreamer.ActiveRelationships.ContainsKey(hitEnt.Handle))
                            PropStreamer.ActiveRelationships.Remove(hitEnt.Handle);
                        if (PropStreamer.ActiveWeapons.ContainsKey(hitEnt.Handle))
                            PropStreamer.ActiveWeapons.Remove(hitEnt.Handle);
                        PropStreamer.RemoveEntity(hitEnt.Handle);
                        _changesMade++;
                    }
                    else if (hitEnt != null && !PropStreamer.GetAllHandles().Contains(hitEnt.Handle) && IsProp(hitEnt))
                    {
                        MapObject tmpObj = new MapObject()
                        {
                            Hash = hitEnt.Model.Hash,
                            Position = hitEnt.Position,
                            Rotation = hitEnt.Rotation,
                            Quaternion = Quaternion.GetEntityQuaternion(hitEnt),
                            Type = ObjectTypes.Prop,
                            Id = _mapObjCounter.ToString(),
                        };
                        _mapObjCounter++;

                        // Deleting one of the game's own objects only lasts until the area next streams in.
                        // CREATE_MODEL_HIDE says it once and it stays said, and it is the only version of
                        // this a published map can hand to every client. See PropStreamer.RemoveWorldObject.
                        PropStreamer.RemoveWorldObject(tmpObj);
                        AddItemToEntityMenu(tmpObj);
                        _changesMade++;
                    }
                    else
                    {
                        var pos = VectorExtensions.RaycastEverything(new Vector2(0f, 0f), _mainCamera.Position, _mainCamera.Rotation, Game.Player.Character);
                        Marker mark = PropStreamer.Markers.FirstOrDefault(m => (m.Position - pos).Length() < 2f);
                        if (mark != null && CanTouch(mark))
                        {
                            PropStreamer.Markers.Remove(mark);
                            RemoveMarkerFromEntityMenu(mark.Id);
                            _changesMade++;
                        }
                        else
                        {
                            Laser laser = LaserNear(pos);
                            if (laser != null && CanTouch(laser))
                            {
                                PropStreamer.Lasers.Remove(laser);
                                RemoveLaserFromEntityMenu(laser.Id);
                                _changesMade++;
                            }
                        }
                    }
                }

                DrawButtons(_freelookButtons);
            }
        }

        /// <summary>
        /// The raycast already hands back the concrete Prop/Vehicle/Ped subclass, so the entity
        /// can be used as-is.
        /// </summary>
        private static Entity WrapEntity(Entity hitEnt)
        {
            return hitEnt;
        }

        private Marker CloneMarker(Marker mark)
        {
            var tmpMark = new Marker()
            {
                BobUpAndDown = mark.BobUpAndDown,
                Red = mark.Red,
                Green = mark.Green,
                Blue = mark.Blue,
                Alpha = mark.Alpha,
                Position = mark.Position,
                RotateToCamera = mark.RotateToCamera,
                Rotation = mark.Rotation,
                Scale = mark.Scale,
                Type = mark.Type,
                Id = _markerCounter,
            };
            _markerCounter++;
            return tmpMark;
        }

        /// <summary>
        /// The laser nearest the point the crosshair found, or null if none is close enough.
        ///
        /// Its <see cref="Laser.Position"/> is what is measured against, not its beams: the beams are drawn
        /// where the numbers say and nothing in the world can be aimed at, so the handle a builder grabs a
        /// laser by has to be the middle of it. Two metres, the same reach markers are picked up from.
        /// </summary>
        private static Laser LaserNear(Vector3 point)
        {
            Laser nearest = null;
            var best = 2f;

            foreach (var laser in PropStreamer.Lasers)
            {
                var distance = (laser.Position - point).Length();
                if (distance >= best) continue;

                best = distance;
                nearest = laser;
            }

            return nearest;
        }

        /// <summary>
        /// A copy of a laser, under a name of its own. Everything the builder authored is carried across;
        /// what is not is the session id and the burn in progress, both of which belong to the original.
        /// </summary>
        private Laser CloneLaser(Laser laser)
        {
            _laserCounter++;

            var copy = new Laser
            {
                Pattern = laser.Pattern,
                Position = laser.Position,
                Rotation = laser.Rotation,
                BeamLength = laser.BeamLength,
                Width = laser.Width,
                Height = laser.Height,
                BeamCount = laser.BeamCount,
                Density = laser.Density,
                Thickness = laser.Thickness,
                Red = laser.Red,
                Green = laser.Green,
                Blue = laser.Blue,
                Alpha = laser.Alpha,
                Textured = laser.Textured,
                Rhythm = laser.Rhythm,
                OnSeconds = laser.OnSeconds,
                OffSeconds = laser.OffSeconds,
                ChasePeriod = laser.ChasePeriod,
                ChaseOnFraction = laser.ChaseOnFraction,
                Amplitude = laser.Amplitude,
                Frequency = laser.Frequency,
                Speed = laser.Speed,
                DealsDamage = laser.DealsDamage,
                DamagePerSecond = laser.DamagePerSecond,
                ActivationRange = laser.ActivationRange,
                HitRadius = laser.HitRadius,
                OnlyVisibleInEditor = laser.OnlyVisibleInEditor,
                Id = _laserCounter,
            };
            return copy;
        }

        /// <summary>
        /// Duplicates an entity in place and returns the copy, or null if it could not be created.
        ///
        /// Asynchronous where the SP build's was not: every spawn here goes through PropStreamer, which has
        /// to wait for the model, and FiveM has no fiber to wait on. See <see cref="Platform.Frame"/>.
        /// </summary>
        private async Task<Entity> CopyEntity(Entity hitEnt)
        {
            if (hitEnt == null || !hitEnt.Exists()) return null;

            if (IsProp(hitEnt))
            {
                var isDoor = PropStreamer.Doors.Contains(hitEnt.Handle);
                Entity newProp = await PropStreamer.CreateProp(hitEnt.Model, hitEnt.Position, hitEnt.Rotation,
                    (!PropStreamer.StaticProps.Contains(hitEnt.Handle) && !isDoor), q: Quaternion.GetEntityQuaternion(hitEnt),
                    force: true, drawDistance: _settings.DrawDistance);
                AddItemToEntityMenu(newProp);
                if (isDoor && newProp != null)
                {
                    PropStreamer.Doors.Add(newProp.Handle);
                    PropStreamer.HoldStill(newProp);
                }
                return newProp;
            }

            if (IsVehicle(hitEnt))
            {
                Entity newVehicle = await PropStreamer.CreateVehicle(hitEnt.Model, hitEnt.Position, hitEnt.Rotation.Z,
                    !PropStreamer.StaticProps.Contains(hitEnt.Handle), drawDistance: _settings.DrawDistance);
                AddItemToEntityMenu(newVehicle);
                return newVehicle;
            }

            if (IsPed(hitEnt))
            {
                // Cloning copies the ped's whole appearance in one call, hence not going through
                // PropStreamer.CreatePed. It is synchronous too: the original's model is already loaded.
                //
                // Clone's heading argument does nothing in CitizenFX — the clone faces whichever way the
                // original does — so the heading is set afterwards rather than asked for.
                Entity newPed = ((Ped)hitEnt).Clone();
                AddItemToEntityMenu(newPed);
                if (newPed == null) return null;

                newPed.Heading = hitEnt.Rotation.Z;
                newPed.IsPersistent = true;
                PropStreamer.Peds.Add(newPed.Handle);

                if (_settings.DrawDistance != -1)
                    newPed.LodDistance = _settings.DrawDistance;

                if (PropStreamer.StaticProps.Contains(hitEnt.Handle))
                    PropStreamer.StaticProps.Add(newPed.Handle);

                // Clone copies the original's tasks along with its face, and the copy has to be as inert as
                // everything else the editor is holding.
                PropStreamer.HoldStill(newPed);

                if (!PropStreamer.ActiveScenarios.ContainsKey(newPed.Handle))
                    PropStreamer.ActiveScenarios.Add(newPed.Handle, "None");

                if (PropStreamer.ActiveRelationships.ContainsKey(hitEnt.Handle))
                    PropStreamer.ActiveRelationships[newPed.Handle] = PropStreamer.ActiveRelationships[hitEnt.Handle];
                else if (!PropStreamer.ActiveRelationships.ContainsKey(newPed.Handle))
                    PropStreamer.ActiveRelationships.Add(newPed.Handle, DefaultRelationship.ToString());

                if (PropStreamer.ActiveWeapons.ContainsKey(hitEnt.Handle))
                    PropStreamer.ActiveWeapons[newPed.Handle] = PropStreamer.ActiveWeapons[hitEnt.Handle];
                else if (!PropStreamer.ActiveWeapons.ContainsKey(newPed.Handle))
                    PropStreamer.ActiveWeapons.Add(newPed.Handle, WeaponHash.Unarmed);

                return newPed;
            }

            return null;
        }

        /// <summary>
        /// Whether the multi-select modifier is held.
        ///
        /// The SP build read the Ctrl key straight off the keyboard through Game.IsKeyPressed. FiveM has no
        /// key state to read — no System.Windows.Forms, and no P/Invoke into user32 — so this goes through a
        /// game control instead. Control.Duck is LCTRL on a keyboard, which is the key the SP build looked
        /// for and the key the instructional buttons have always advertised for this.
        /// </summary>
        private static bool IsMultiSelectKeyDown()
        {
            return Game.IsControlPressed(0, Control.Duck);
        }

        /// <summary>
        /// Adds the entity to the multi-selection, or drops it if it was already picked.
        /// </summary>
        private void ToggleMultiSelection(Entity ent)
        {
            int index = _multiSelection.FindIndex(e => e.Handle == ent.Handle);
            if (index != -1)
            {
                _multiSelection.RemoveAt(index);
                _multiSelectionOffsets.RemoveAt(index);
                return;
            }

            _multiSelection.Add(ent);
            _multiSelectionOffsets.Add(Vector3.Zero);
        }

        private void ClearMultiSelection()
        {
            if (_multiSelectionSnapped)
                EndMultiSelectionMove();

            _multiSelection.Clear();
            _multiSelectionOffsets.Clear();
        }

        /// <summary>
        /// Drops entities that were deleted or removed from the map behind our back, e.g. through the entity menu.
        /// </summary>
        private void PruneMultiSelection()
        {
            if (_multiSelection.Count == 0) return;

            var handles = PropStreamer.GetAllHandles();
            for (int i = _multiSelection.Count - 1; i >= 0; i--)
            {
                var ent = _multiSelection[i];
                if (ent != null && ent.Exists() && handles.Contains(ent.Handle)) continue;

                _multiSelection.RemoveAt(i);
                _multiSelectionOffsets.RemoveAt(i);
            }

            if (_multiSelection.Count == 0)
                _multiSelectionSnapped = false;
        }

        private Vector3 CrosshairPosition()
        {
            return VectorExtensions.RaycastEverything(new Vector2(0f, 0f), _mainCamera.Position, _mainCamera.Rotation, Game.Player.Character);
        }

        private void BeginMultiSelectionMove()
        {
            var anchor = CrosshairPosition();
            for (int i = 0; i < _multiSelection.Count; i++)
            {
                var ent = _multiSelection[i];
                _multiSelectionOffsets[i] = ent.Position - anchor;
                // Without this the crosshair raycast lands on the props being dragged and the group creeps
                // towards the camera instead of following the ground.
                Function.Call(Hash.SET_ENTITY_COLLISION, ent.Handle, false, true);
            }
            _multiSelectionSnapped = true;
        }

        private void EndMultiSelectionMove()
        {
            foreach (Entity ent in _multiSelection)
            {
                if (ent == null || !ent.Exists()) continue;

                // Not a plain "collision back on": a dynamic object was already standing there without any,
                // and the drag is not what is supposed to decide that. See PropStreamer.HoldStill.
                PropStreamer.HoldStill(ent);
            }
            _multiSelectionSnapped = false;
        }

        private void ProcessMultiSelectionMove(float modifier)
        {
            if (Game.IsControlPressed(0, Control.CursorScrollUp) || Game.IsControlPressed(0, Control.FrontendRb))
                RotateMultiSelection(-modifier);

            if (Game.IsControlPressed(0, Control.CursorScrollDown) || Game.IsControlPressed(0, Control.FrontendLb))
                RotateMultiSelection(modifier);

            var anchor = CrosshairPosition();
            for (int i = 0; i < _multiSelection.Count; i++)
                SetEntityPosition(_multiSelection[i], anchor + _multiSelectionOffsets[i]);

            if (Game.IsControlJustPressed(0, Control.CreatorDelete))
            {
                DeleteMultiSelection();
                return;
            }

            if (Game.IsControlJustPressed(0, Control.Attack))
            {
                EndMultiSelectionMove();
                _changesMade++;
            }

            DrawButtons(_snappedButtons);
        }

        /// <summary>
        /// Spins the whole group around the crosshair, both the entities and the offsets they hold it by.
        /// </summary>
        private void RotateMultiSelection(float angle)
        {
            var rad = (float)VectorExtensions.DegToRad(angle);
            var cos = (float)Math.Cos(rad);
            var sin = (float)Math.Sin(rad);

            for (int i = 0; i < _multiSelection.Count; i++)
            {
                var offset = _multiSelectionOffsets[i];
                _multiSelectionOffsets[i] = new Vector3(offset.X * cos - offset.Y * sin, offset.X * sin + offset.Y * cos, offset.Z);

                var ent = _multiSelection[i];
                ent.Rotation = ent.Rotation + new Vector3(0f, 0f, angle);
                if (IsPed(ent))
                    ent.Heading = ent.Rotation.Z;
            }
        }

        /// <summary>
        /// Takes an entity off the map: its row in the entity menu, everything keyed by its handle, and
        /// the entity itself. Handles have to be cleaned up before the entity goes, and every one of them,
        /// or the next entity to be handed the same handle inherits what was left behind.
        /// </summary>
        private void DeleteEditorEntity(Entity ent)
        {
            if (ent == null || !ent.Exists()) return;

            RemoveItemFromEntityMenu(ent);
            PropStreamer.Identifications.Remove(ent.Handle);
            PropStreamer.ActiveScenarios.Remove(ent.Handle);
            PropStreamer.ActiveRelationships.Remove(ent.Handle);
            PropStreamer.ActiveWeapons.Remove(ent.Handle);
            PropStreamer.Doors.Remove(ent.Handle);

            PropStreamer.RemoveEntity(ent.Handle);
        }

        private void DeleteMultiSelection()
        {
            foreach (Entity ent in _multiSelection)
                DeleteEditorEntity(ent);

            _multiSelection.Clear();
            _multiSelectionOffsets.Clear();
            _multiSelectionSnapped = false;
            _changesMade++;
        }

        /// <summary>
        /// Duplicates every selected entity and hands the copies to the cursor as the new selection.
        /// </summary>
        private async Task CopyMultiSelection()
        {
            var copies = new List<Entity>();
            // A snapshot: the selection is cleared below, and each copy takes frames during which the player
            // is still free to click.
            foreach (Entity ent in _multiSelection.ToList())
            {
                if (ent == null || !ent.Exists()) continue;
                var copy = await CopyEntity(ent);
                if (copy != null) copies.Add(copy);
            }

            if (copies.Count == 0) return;

            ClearMultiSelection();
            foreach (Entity copy in copies)
            {
                _multiSelection.Add(copy);
                _multiSelectionOffsets.Add(Vector3.Zero);
            }
            BeginMultiSelectionMove();
            _changesMade++;
        }

        private void ProcessSelectedProp(float modifier)
        {
            var tmp = _controlsRotate ? Color.FromArgb(200, 200, 20, 20) : Color.FromArgb(200, 200, 200, 10);
            Vector3 min, max;
            LuaBridge.ModelDimensions(_selectedProp.Model.Hash, out min, out max);
            var modelDims = max - min;
            Function.Call(Hash.DRAW_MARKER, 0, _selectedProp.Position.X, _selectedProp.Position.Y, _selectedProp.Position.Z + modelDims.Z + 2f, 0f, 0f, 0f, 0f, 0f, 0f, 2f, 2f, 2f, tmp.R, tmp.G, tmp.B, tmp.A, 1, 0, 2, 2, 0, 0, 0);

            DrawEntityBox(_selectedProp, tmp);

            if (Game.IsControlJustReleased(0, Control.Duck))
                _controlsRotate = !_controlsRotate;

            if (Game.IsControlPressed(0, Control.FrontendRb))
            {
                float pedMod = _selectedProp is Ped ? -1f : 0f;
                if (!_controlsRotate)
                    MoveSelectedProp(new Vector3(0f, 0f, (modifier / 4) + pedMod));
                else
                {
                    _selectedProp.Quaternion = new Vector3(_selectedProp.Rotation.X, _selectedProp.Rotation.Y, _selectedProp.Rotation.Z - (modifier / 4)).ToQuaternion();
                    if (IsPed(_selectedProp))
                        _selectedProp.Heading = _selectedProp.Rotation.Z;
                }
                _changesMade++;
            }

            if (Game.IsControlPressed(0, Control.FrontendLb))
            {
                float pedMod = _selectedProp is Ped ? 1f : 0f;
                if (!_controlsRotate)
                    MoveSelectedProp(new Vector3(0f, 0f, -((modifier / 4) + pedMod)));
                else
                {
                    _selectedProp.Quaternion = new Vector3(_selectedProp.Rotation.X, _selectedProp.Rotation.Y, _selectedProp.Rotation.Z + (modifier / 4)).ToQuaternion();
                    if (IsPed(_selectedProp))
                        _selectedProp.Heading = _selectedProp.Rotation.Z;
                }
                _changesMade++;
            }

            if (Game.IsControlPressed(0, Control.MoveUpOnly))
            {
                float pedMod = IsPed(_selectedProp) ? -1f : 0f;
                if (!_controlsRotate)
                {
                    var dir = VectorExtensions.RotationToDirection(_mainCamera.Rotation) * (modifier / 4);
                    MoveSelectedProp(new Vector3(dir.X, dir.Y, pedMod));
                }
                else
                    _selectedProp.Quaternion = new Vector3(_selectedProp.Rotation.X + (modifier / 4), _selectedProp.Rotation.Y, _selectedProp.Rotation.Z).ToQuaternion();
                _changesMade++;
            }

            if (Game.IsControlPressed(0, Control.MoveDownOnly))
            {
                float pedMod = _selectedProp is Ped ? 1f : 0f;
                if (!_controlsRotate)
                {
                    var dir = VectorExtensions.RotationToDirection(_mainCamera.Rotation) * (modifier / 4);
                    MoveSelectedProp(new Vector3(-dir.X, -dir.Y, -pedMod));
                }
                else
                    _selectedProp.Quaternion = new Vector3(_selectedProp.Rotation.X - (modifier / 4), _selectedProp.Rotation.Y, _selectedProp.Rotation.Z).ToQuaternion();
                _changesMade++;
            }

            if (Game.IsControlPressed(0, Control.MoveLeftOnly))
            {
                float pedMod = _selectedProp is Ped ? -1f : 0f;
                if (!_controlsRotate)
                {
                    var right = CameraRight(modifier);
                    MoveSelectedProp(new Vector3(right.X, right.Y, pedMod));
                }
                else
                    _selectedProp.Quaternion = new Vector3(_selectedProp.Rotation.X, _selectedProp.Rotation.Y + (modifier / 4), _selectedProp.Rotation.Z).ToQuaternion();
                _changesMade++;
            }

            if (Game.IsControlPressed(0, Control.MoveRightOnly))
            {
                float pedMod = _selectedProp is Ped ? 1f : 0f;
                if (!_controlsRotate)
                {
                    var right = CameraRight(modifier);
                    MoveSelectedProp(new Vector3(-right.X, -right.Y, -pedMod));
                }
                else
                    _selectedProp.Quaternion = new Vector3(_selectedProp.Rotation.X, _selectedProp.Rotation.Y - (modifier / 4), _selectedProp.Rotation.Z).ToQuaternion();
                _changesMade++;
            }

            if (Game.IsControlJustReleased(0, Control.MoveLeftOnly) ||
                Game.IsControlJustReleased(0, Control.MoveRightOnly) ||
                Game.IsControlJustReleased(0, Control.MoveUpOnly) ||
                Game.IsControlJustReleased(0, Control.MoveDownOnly) ||
                Game.IsControlJustReleased(0, Control.FrontendLb) ||
                Game.IsControlJustReleased(0, Control.FrontendRb))
            {
                RedrawObjectInfoMenu(_selectedProp, false);
            }

            if (Game.IsControlJustReleased(0, Control.LookBehind))
            {
                Run("Copy selected", async () =>
                {
                    Entity mainProp = await CopySelectedProp();
                    if (mainProp == null) return;

                    _changesMade++;
                    _selectedProp = mainProp;
                    if (_settings.SnapCameraToSelectedObject && _mainCamera != null)
                        _mainCamera.PointAt(_selectedProp, Vector3.Zero);
                    RedrawObjectInfoMenu(_selectedProp, true);
                });
            }

            if (_selectedProp != null && Game.IsControlJustPressed(0, Control.CreatorDelete))
            {
                if (PropStreamer.Identifications.ContainsKey(_selectedProp.Handle))
                    PropStreamer.Identifications.Remove(_selectedProp.Handle);
                if (PropStreamer.ActiveScenarios.ContainsKey(_selectedProp.Handle))
                    PropStreamer.ActiveScenarios.Remove(_selectedProp.Handle);
                if (PropStreamer.ActiveRelationships.ContainsKey(_selectedProp.Handle))
                    PropStreamer.ActiveRelationships.Remove(_selectedProp.Handle);
                if (PropStreamer.ActiveWeapons.ContainsKey(_selectedProp.Handle))
                    PropStreamer.ActiveWeapons.Remove(_selectedProp.Handle);
                RemoveItemFromEntityMenu(_selectedProp);
                PropStreamer.RemoveEntity(_selectedProp.Handle);
                _selectedProp = null;
                SetMenuVisible(_objectInfoMenu, false);
                _mainCamera.StopPointing();
                _changesMade++;
            }

            if (_selectedProp != null && (Game.IsControlJustPressed(0, Control.PhoneCancel) || Game.IsControlJustPressed(0, Control.Attack)))
            {
                _selectedProp = null;
                SetMenuVisible(_objectInfoMenu, false);
                _mainCamera.StopPointing();
                _changesMade++;
            }

            DrawButtons(_selectedButtons);
        }

        private Vector3 CameraRight(float modifier)
        {
            var rotLeft = _mainCamera.Rotation + new Vector3(0, 0, -10);
            var rotRight = _mainCamera.Rotation + new Vector3(0, 0, 10);
            return (VectorExtensions.RotationToDirection(rotRight) - VectorExtensions.RotationToDirection(rotLeft)) * (modifier / 2);
        }

        /// <summary>
        /// Props need PositionNoOffset so they don't get shifted by their model's bounding origin.
        /// </summary>
        private void MoveSelectedProp(Vector3 delta)
        {
            var target = _selectedProp.Position + delta;
            if (!IsProp(_selectedProp))
                _selectedProp.Position = target;
            else
                _selectedProp.PositionNoOffset = target;
        }

        private async Task<Entity> CopySelectedProp()
        {
            var source = _selectedProp;
            if (source == null || !source.Exists()) return null;

            return await CopyEntity(source);
        }

        private void ProcessSelectedMarker(float modifier)
        {
            if (Game.IsControlJustReleased(0, Control.Duck))
                _controlsRotate = !_controlsRotate;

            if (Game.IsControlPressed(0, Control.FrontendRb))
            {
                if (!_controlsRotate)
                    _selectedMarker.Position += new Vector3(0f, 0f, (modifier / 4));
                else
                    _selectedMarker.Rotation += new Vector3(0f, 0f, modifier);
                _changesMade++;
            }

            if (Game.IsControlPressed(0, Control.FrontendLb))
            {
                if (!_controlsRotate)
                    _selectedMarker.Position -= new Vector3(0f, 0f, (modifier / 4));
                else
                    _selectedMarker.Rotation -= new Vector3(0f, 0f, modifier);
                _changesMade++;
            }

            if (Game.IsControlPressed(0, Control.MoveUpOnly))
            {
                if (!_controlsRotate)
                {
                    var dir = VectorExtensions.RotationToDirection(_mainCamera.Rotation) * (modifier / 4);
                    _selectedMarker.Position += new Vector3(dir.X, dir.Y, 0f);
                }
                else
                    _selectedMarker.Rotation += new Vector3(modifier, 0f, 0f);
                _changesMade++;
            }

            if (Game.IsControlPressed(0, Control.MoveDownOnly))
            {
                if (!_controlsRotate)
                {
                    var dir = VectorExtensions.RotationToDirection(_mainCamera.Rotation) * (modifier / 4);
                    _selectedMarker.Position -= new Vector3(dir.X, dir.Y, 0f);
                }
                else
                    _selectedMarker.Rotation -= new Vector3(modifier, 0f, 0f);
                _changesMade++;
            }

            if (Game.IsControlPressed(0, Control.MoveLeftOnly))
            {
                if (!_controlsRotate)
                {
                    var right = CameraRight(modifier);
                    _selectedMarker.Position += new Vector3(right.X, right.Y, 0f);
                }
                else
                    _selectedMarker.Rotation += new Vector3(0f, modifier, 0f);
                _changesMade++;
            }

            if (Game.IsControlPressed(0, Control.MoveRightOnly))
            {
                if (!_controlsRotate)
                {
                    var right = CameraRight(modifier);
                    _selectedMarker.Position -= new Vector3(right.X, right.Y, 0f);
                }
                else
                    _selectedMarker.Rotation -= new Vector3(0f, modifier, 0f);
                _changesMade++;
            }

            if (Game.IsControlJustReleased(0, Control.MoveLeftOnly) ||
                Game.IsControlJustReleased(0, Control.MoveRightOnly) ||
                Game.IsControlJustReleased(0, Control.MoveUpOnly) ||
                Game.IsControlJustReleased(0, Control.MoveDownOnly) ||
                Game.IsControlJustReleased(0, Control.FrontendLb) ||
                Game.IsControlJustReleased(0, Control.FrontendRb))
            {
                RedrawObjectInfoMenu(_selectedMarker, false);
            }

            if (Game.IsControlJustReleased(0, Control.LookBehind))
            {
                var tmpMark = CloneMarker(_selectedMarker);
                PropStreamer.Markers.Add(tmpMark);
                AddItemToEntityMenu(tmpMark);
                _selectedMarker = tmpMark;
                RedrawObjectInfoMenu(_selectedMarker, true);
                _changesMade++;
            }

            if (Game.IsControlJustPressed(0, Control.CreatorDelete))
            {
                PropStreamer.Markers.Remove(_selectedMarker);
                RemoveMarkerFromEntityMenu(_selectedMarker.Id);
                _selectedMarker = null;
                SetMenuVisible(_objectInfoMenu, false);
                _mainCamera.StopPointing();
                _changesMade++;
            }

            if (_selectedMarker != null && (Game.IsControlJustPressed(0, Control.PhoneCancel) || Game.IsControlJustPressed(0, Control.Attack)))
            {
                _selectedMarker = null;
                SetMenuVisible(_objectInfoMenu, false);
                _mainCamera.StopPointing();
                _changesMade++;
            }

            DrawButtons(_selectedButtons);
        }

        /// <summary>
        /// The selected laser under the same controls a selected marker has: the arrows move or turn it
        /// depending on <c>_controlsRotate</c>, LB/RB raise and lower it, and the whole of it is one row of
        /// numbers, so nothing has to be spawned or moved in the world for any of it to take effect.
        /// </summary>
        private void ProcessSelectedLaser(float modifier)
        {
            if (Game.IsControlJustReleased(0, Control.Duck))
                _controlsRotate = !_controlsRotate;

            if (Game.IsControlPressed(0, Control.FrontendRb))
            {
                if (!_controlsRotate)
                    _selectedLaser.Position += new Vector3(0f, 0f, (modifier / 4));
                else
                    _selectedLaser.Rotation += new Vector3(0f, 0f, modifier);
                _changesMade++;
            }

            if (Game.IsControlPressed(0, Control.FrontendLb))
            {
                if (!_controlsRotate)
                    _selectedLaser.Position -= new Vector3(0f, 0f, (modifier / 4));
                else
                    _selectedLaser.Rotation -= new Vector3(0f, 0f, modifier);
                _changesMade++;
            }

            if (Game.IsControlPressed(0, Control.MoveUpOnly))
            {
                if (!_controlsRotate)
                {
                    var dir = VectorExtensions.RotationToDirection(_mainCamera.Rotation) * (modifier / 4);
                    _selectedLaser.Position += new Vector3(dir.X, dir.Y, 0f);
                }
                else
                    _selectedLaser.Rotation += new Vector3(modifier, 0f, 0f);
                _changesMade++;
            }

            if (Game.IsControlPressed(0, Control.MoveDownOnly))
            {
                if (!_controlsRotate)
                {
                    var dir = VectorExtensions.RotationToDirection(_mainCamera.Rotation) * (modifier / 4);
                    _selectedLaser.Position -= new Vector3(dir.X, dir.Y, 0f);
                }
                else
                    _selectedLaser.Rotation -= new Vector3(modifier, 0f, 0f);
                _changesMade++;
            }

            if (Game.IsControlPressed(0, Control.MoveLeftOnly))
            {
                if (!_controlsRotate)
                {
                    var right = CameraRight(modifier);
                    _selectedLaser.Position += new Vector3(right.X, right.Y, 0f);
                }
                else
                    _selectedLaser.Rotation += new Vector3(0f, modifier, 0f);
                _changesMade++;
            }

            if (Game.IsControlPressed(0, Control.MoveRightOnly))
            {
                if (!_controlsRotate)
                {
                    var right = CameraRight(modifier);
                    _selectedLaser.Position -= new Vector3(right.X, right.Y, 0f);
                }
                else
                    _selectedLaser.Rotation -= new Vector3(0f, modifier, 0f);
                _changesMade++;
            }

            if (Game.IsControlJustReleased(0, Control.MoveLeftOnly) ||
                Game.IsControlJustReleased(0, Control.MoveRightOnly) ||
                Game.IsControlJustReleased(0, Control.MoveUpOnly) ||
                Game.IsControlJustReleased(0, Control.MoveDownOnly) ||
                Game.IsControlJustReleased(0, Control.FrontendLb) ||
                Game.IsControlJustReleased(0, Control.FrontendRb))
            {
                RedrawObjectInfoMenu(_selectedLaser, false);
            }

            if (Game.IsControlJustReleased(0, Control.LookBehind))
            {
                var tmpLaser = CloneLaser(_selectedLaser);
                PropStreamer.Lasers.Add(tmpLaser);
                AddItemToEntityMenu(tmpLaser);
                _selectedLaser = tmpLaser;
                RedrawObjectInfoMenu(_selectedLaser, true);
                _changesMade++;
            }

            if (Game.IsControlJustPressed(0, Control.CreatorDelete))
            {
                PropStreamer.Lasers.Remove(_selectedLaser);
                RemoveLaserFromEntityMenu(_selectedLaser.Id);
                _selectedLaser = null;
                SetMenuVisible(_objectInfoMenu, false);
                _mainCamera.StopPointing();
                _changesMade++;
            }

            if (_selectedLaser != null && (Game.IsControlJustPressed(0, Control.PhoneCancel) || Game.IsControlJustPressed(0, Control.Attack)))
            {
                _selectedLaser = null;
                SetMenuVisible(_objectInfoMenu, false);
                _mainCamera.StopPointing();
                _changesMade++;
            }

            DrawButtons(_selectedButtons);
        }
    }
}
