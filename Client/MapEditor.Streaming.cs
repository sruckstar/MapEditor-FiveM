using System;
using System.Linq;
using System.Threading.Tasks;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using MapEditor.Platform;

namespace MapEditor
{
    public partial class MapEditor
    {
        /// <summary>
        /// Where smart streaming measures from this frame: the freecam while it is up, the player otherwise.
        ///
        /// The two are never the same spot. Freelook parks the player eight metres behind the camera and drags
        /// them along frozen, and it is the camera the map is being looked at from — measuring from the player
        /// would be right by accident while the freecam is up and wrong the moment they are not the same thing,
        /// which is every frame of the object picker and every frame the player is put down somewhere.
        /// </summary>
        private Vector3 CurrentStreamingOrigin => IsInFreecam && _mainCamera != null && _mainCamera.Exists()
            ? _mainCamera.Position
            : Game.Player.Character.Position;

        /// <summary>How far through the map the rolling out-of-range scan has got. See <see cref="StreamOutDistantEntities"/>.</summary>
        private int _streamScanCursor;

        /// <summary>
        /// Keeps the map being edited down to the part of it the player is anywhere near: what has been left
        /// behind goes out of the world, what has been come back to comes back. See <see cref="SmartStreaming"/>
        /// for why, and <see cref="AutoloadedMaps.Tick"/> for the same thing done to the maps standing alongside
        /// this one.
        /// </summary>
        private void ProcessSmartStreaming()
        {
            // Putting back first, and unconditionally: with streaming switched off everything has returned, so
            // this is also what puts a map back together when the player turns the setting off.
            StreamInReturnedEntities();

            if (!SmartStreaming.Enabled) return;
            StreamOutDistantEntities();
        }

        /// <summary>
        /// Takes whatever the player has left far enough behind back out of the world.
        ///
        /// Asking an entity where it stands is a native call each and a large map is thousands of them, so the
        /// map is walked a slice at a time across frames rather than whole every frame. The cursor is an index
        /// into the three lists laid end to end, and those lists shift as entities are placed, deleted and
        /// streamed out under it: an entity stepped over that way is simply looked at on the next pass, a
        /// fraction of a second later, which is soon enough for something the player is hundreds of metres from.
        /// </summary>
        private void StreamOutDistantEntities()
        {
            var toScan = Math.Min(EditorEntityCount, SmartStreaming.ScanPerTick);

            for (var i = 0; i < toScan; i++)
            {
                var total = EditorEntityCount;
                if (total == 0)
                {
                    _streamScanCursor = 0;
                    return;
                }
                if (_streamScanCursor >= total) _streamScanCursor = 0;

                var handle = EditorEntityAt(_streamScanCursor);
                var entity = handle == 0 ? null : Compat.Ent(handle);

                if (entity == null || !SmartStreaming.IsTooFar(entity.Position) || IsEntityBusy(handle))
                {
                    _streamScanCursor++;
                    continue;
                }

                if (!SmartStreaming.TakeDespawn()) return;

                var record = PropStreamer.StreamOut(entity);
                if (record == null)
                {
                    _streamScanCursor++;
                    continue;
                }

                // Its row stays in the entity menu — the map is no shorter for this — but the handle it was
                // pointing at has just been handed back to the game, and the game gives handles out again.
                RetagEntityMenuItem(EntityMenuKind.Entity, handle, EntityMenuKind.Streamed, record.Uid);

                // The lists closed up over the gap, so the cursor is already pointing at the next entity.
            }
        }

        /// <summary>
        /// Puts back whatever the player has come back to.
        ///
        /// The spawn itself is asynchronous and is not waited for: this runs inside a frame, and holding the
        /// frame until the streamer answered is exactly what a tick must never do. So each record is started
        /// and marked as in flight (see <see cref="PropStreamer.StreamedObject.Spawning"/>), and lands on some
        /// later frame of its own accord. A spawn that finds its model not in memory yet is not a failure: the
        /// model has been asked for and the next pass over this record puts it in.
        /// </summary>
        private void StreamInReturnedEntities()
        {
            var streamed = PropStreamer.StreamedOut;

            for (var i = streamed.Count - 1; i >= 0; i--)
            {
                var record = streamed[i];
                if (record.Spawning) continue;
                if (!SmartStreaming.HasReturned(record.Object.Position)) continue;

                // A map larger than the world will hold still works with streaming on, so long as no more
                // than the limit are in range. Over that the rest waits, silently: saying so every frame
                // would be worse than the gap.
                if (record.Object.Type == ObjectTypes.Prop && PropStreamer.PropSlotsFull) continue;

                if (!SmartStreaming.TakeSpawn()) return;

                var pending = record;
                pending.Spawning = true;
                Log.Guard("Stream in", async () =>
                {
                    try
                    {
                        var entity = await SpawnMapObject(pending.Object, streaming: true);
                        if (entity == null) return;

                        PropStreamer.StreamedIn(pending, entity);
                        RetagEntityMenuItem(EntityMenuKind.Streamed, pending.Uid, EntityMenuKind.Entity, entity.Handle);
                    }
                    finally
                    {
                        pending.Spawning = false;
                    }
                });
            }
        }

        /// <summary>
        /// Puts one streamed-out entity back where it belongs there and then, for a player who has asked for it
        /// by name in the entity menu rather than by flying to it. Waits for the model the way loading a map
        /// does: the player is waiting on this one.
        /// </summary>
        private async Task<Entity> StreamInNow(PropStreamer.StreamedObject record)
        {
            if (record.Spawning) return null;
            record.Spawning = true;
            try
            {
                var entity = await SpawnMapObject(record.Object);
                if (entity == null) return null;

                PropStreamer.StreamedIn(record, entity);
                RetagEntityMenuItem(EntityMenuKind.Streamed, record.Uid, EntityMenuKind.Entity, entity.Handle);
                return entity;
            }
            finally
            {
                record.Spawning = false;
            }
        }

        private static int EditorEntityCount =>
            PropStreamer.StreamedInHandles.Count + PropStreamer.Vehicles.Count + PropStreamer.Peds.Count;

        /// <summary>The map's props, vehicles and peds as one list, for the rolling scan to walk.</summary>
        private static int EditorEntityAt(int index)
        {
            var props = PropStreamer.StreamedInHandles;
            if (index < props.Count) return props[index];
            index -= props.Count;

            var vehicles = PropStreamer.Vehicles;
            if (index < vehicles.Count) return vehicles[index];
            index -= vehicles.Count;

            var peds = PropStreamer.Peds;
            return index < peds.Count ? peds[index] : 0;
        }

        /// <summary>
        /// Whether an entity is one streaming has to leave alone. Everything here is something that is holding
        /// the entity itself rather than a description of it, and would be left holding a deleted one.
        /// </summary>
        private bool IsEntityBusy(int handle)
        {
            if (handle == 0) return true;

            // Whatever the player has in their hands, in every sense the editor has of that.
            if (_selectedProp != null && _selectedProp.Handle == handle) return true;
            if (_snappedProp != null && _snappedProp.Handle == handle) return true;
            if (_previewProp != null && _previewProp.Handle == handle) return true;
            if (_stackingBase != null && _stackingBase.Handle == handle) return true;
            if (_loopingBase != null && _loopingBase.Handle == handle) return true;
            if (_multiSelection.Any(e => e != null && e.Handle == handle)) return true;

            // A named entity was handed to whatever else drives this map — another resource holding its
            // handle. Deleting it would leave them with nothing, and there is no way to hand over the
            // replacement.
            if (PropStreamer.Identifications.ContainsKey(handle)) return true;

            // Only reachable with the freecam down, since distance is measured from the camera while it is
            // up — but a car deleted out from under its driver is worth one comparison a frame to rule out.
            var vehicle = Game.Player.Character.CurrentVehicle;
            if (vehicle != null && vehicle.Handle == handle) return true;

            return false;
        }

        /// <summary>
        /// Puts one map object into the world as part of the map being edited, and registers everything the
        /// editor keeps beside the entity itself — its label, its door flag, its scenario, its relationship,
        /// its weapon, its siren — under the handle it was given.
        ///
        /// Shared by <see cref="LoadMap"/> and by streaming, which is the point: an entity that goes out of the
        /// world and comes back has to come back as exactly the thing loading the map would have produced, or
        /// the map drifts a little further from itself on every trip.
        ///
        /// <paramref name="streaming"/> says nobody is waiting for this one. A map the player asked to load
        /// waits for its models and takes a breather every couple of hundred props; scenery filling in behind
        /// them does neither, and comes back empty-handed for the caller to try again on a later frame.
        /// </summary>
        private async Task<Entity> SpawnMapObject(MapObject o, bool streaming = false)
        {
            if (o == null) return null;

            Model model;
            if (streaming)
            {
                // Never waits: the model is in memory or has just been asked for, and this record comes round
                // again in a frame or two. See SmartStreaming.TryRequestModel for why it is the Try shape.
                if (!SmartStreaming.TryRequestModel(o.Hash, out model)) return null;
            }
            else
            {
                var request = await ObjectPreview.LoadObject(o.Hash);
                if (!request.Loaded) return null;
                model = request.Model;
            }

            switch (o.Type)
            {
                case ObjectTypes.Prop:
                {
                    var prop = await PropStreamer.CreateProp(model, o.Position, o.Rotation, o.Dynamic && !o.Door,
                        IsUnsetQuaternion(o.Quaternion) ? null : o.Quaternion,
                        drawDistance: _settings.DrawDistance, pace: !streaming);
                    if (prop == null) return null;

                    if (o.Door)
                    {
                        // A door swings once the map is published and hangs still until then, like everything
                        // else that moves. Asked again because the spawn did not know it was one.
                        PropStreamer.Doors.Add(prop.Handle);
                        PropStreamer.HoldStill(prop);
                    }

                    RegisterIdentification(o, prop.Handle);
                    RegisterShared(o, prop.Handle);
                    return prop;
                }
                case ObjectTypes.Vehicle:
                {
                    // Unlike a ped, a vehicle is quite happy sitting at an angle — on a ramp, on its roof — and
                    // that angle is in the map file, so it is put back on.
                    var vehicle = await PropStreamer.CreateVehicle(model, o.Position, o.Rotation.Z, o.Dynamic,
                        IsUnsetQuaternion(o.Quaternion) ? null : o.Quaternion, _settings.DrawDistance);
                    if (vehicle == null) return null;

                    vehicle.Mods.PrimaryColor = (VehicleColor) o.PrimaryColor;
                    vehicle.Mods.SecondaryColor = (VehicleColor) o.SecondaryColor;
                    if (o.Livery >= 0) Natives.SetLivery(vehicle.Handle, o.Livery);

                    if (o.SirensActive)
                    {
                        PropStreamer.ActiveSirens.Add(vehicle.Handle);
                        vehicle.IsSirenActive = true;
                    }

                    RegisterIdentification(o, vehicle.Handle);
                    RegisterShared(o, vehicle.Handle);
                    return vehicle;
                }
                case ObjectTypes.Ped:
                {
                    // Peds stand on their feet where props hang from their centre, which is what the editor
                    // stores. A ped is only ever placed standing, so its heading is all the rotation that
                    // means anything and the quaternion is left alone.
                    var ped = await PropStreamer.CreatePed(model, o.Position - new Vector3(0f, 0f, 1f), o.Rotation.Z,
                        o.Dynamic, drawDistance: _settings.DrawDistance);
                    if (ped == null) return null;

                    // What it looks like is put on; what it does is not. A ped in the map being edited does
                    // nothing at all — see PropStreamer.HoldStill, which the spawn has already been through.
                    // These three are the map's description of the ped and go back out with it on save;
                    // AutoloadedMaps.Configure is what turns them into behaviour, once it is published.
                    PedComponents.Apply(ped, o.Drawables, o.Textures);
                    PropStreamer.ActiveScenarios[ped.Handle] = string.IsNullOrEmpty(o.Action) ? "None" : o.Action;
                    PropStreamer.ActiveRelationships[ped.Handle] = o.Relationship ?? DefaultRelationship.ToString();
                    PropStreamer.ActiveWeapons[ped.Handle] = o.Weapon ?? WeaponHash.Unarmed;

                    RegisterIdentification(o, ped.Handle);
                    RegisterShared(o, ped.Handle);
                    return ped;
                }
            }

            return null;
        }

        private static void RegisterIdentification(MapObject o, int handle)
        {
            if (string.IsNullOrWhiteSpace(o.Id)) return;
            PropStreamer.Identifications[handle] = o.Id;
        }

        /// <summary>
        /// Carries the author's answer about who owns this object once published back onto the entity, so
        /// that loading a map and saving it again says the same thing. An object nobody spoke for is not
        /// recorded at all — see <see cref="PropStreamer.SharedOverrides"/>.
        /// </summary>
        private static void RegisterShared(MapObject o, int handle)
        {
            if (!o.Shared.HasValue) return;
            PropStreamer.SharedOverrides[handle] = o.Shared.Value;
        }

        /// <summary>
        /// A quaternion that was never written to the map file, as opposed to a real rotation. Handing it to
        /// the game as one collapses the entity into a corner of itself.
        /// </summary>
        private static bool IsUnsetQuaternion(Quaternion q)
        {
            return q == null || (q.X == 0 && q.Y == 0 && q.Z == 0 && q.W == 0);
        }

        /// <summary>
        /// Points a "Current Entities" row at what it now has to point at. The row itself does not move: an
        /// entity going out of the world and coming back is not the player's business, and a map they are
        /// halfway through picking through must not reshuffle under them because they walked away from it.
        /// </summary>
        private void RetagEntityMenuItem(EntityMenuKind fromKind, int fromId, EntityMenuKind toKind, int toId)
        {
            foreach (var item in _currentObjectsMenu.Items)
            {
                var tag = item.Tag as EntityMenuTag;
                if (tag == null || tag.Kind != fromKind || tag.Id != fromId) continue;

                tag.Kind = toKind;
                tag.Id = toId;
                return;
            }
        }
    }
}
