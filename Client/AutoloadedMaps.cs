using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using MapEditor.Platform;

namespace MapEditor
{
	/// <summary>
	/// The maps the server has published: what stands in this client's world because somebody put it there
	/// for everybody, as opposed to the one map this player is editing.
	///
	/// The set is the server's to decide and it moves while the game runs — publishing adds a map, unloading
	/// takes one away, and the server says so with a broadcast that lands in <see cref="Sync"/>.
	/// <see cref="LoadAll"/> is the same reconciliation done once at startup against an empty world.
	///
	/// Deliberately kept out of <see cref="PropStreamer"/>: a published map is scenery the player stands in,
	/// not the map they are editing. Staying out of the streamer's lists keeps it out of the entity menu, out
	/// of what gets saved, and standing through "New Map".
	///
	/// What is spawned is always local to this player. A published map is the same document on every client,
	/// so replicating it would have every player spawn a networked copy of the same scenery on top of
	/// everyone else's, at the server's entity budget once per player.
	/// </summary>
	public static class AutoloadedMaps
	{
		/// <summary>
		/// One prop, vehicle or ped of an autoloaded map, and the entity standing for it — nothing at all
		/// while the player is far enough away that <see cref="SmartStreaming"/> has taken it out.
		///
		/// Always put back from the map object it was read out of the file as, never from a fresh reading of
		/// the entity that was there: nobody edits or saves an autoloaded map, so a hundred trips past it
		/// must not walk it away from what its file says.
		/// </summary>
		private sealed class LoadedEntity
		{
			public LoadedEntity(MapObject o)
			{
				Object = o;
			}

			public readonly MapObject Object;

			/// <summary>Null while it is not in the world, which is not the same as <see cref="Gone"/>.</summary>
			public Entity Entity;

			/// <summary>
			/// Set once the entity turns out to have been deleted by somebody else. These are spawned
			/// persistent, so the game cannot have done it, and streaming it back in would be arguing with
			/// whoever did.
			/// </summary>
			public bool Gone;

			/// <summary>Whether a spawn for this one is already on its way. See <see cref="StreamEntities"/>.</summary>
			public bool Spawning;

			/// <summary>
			/// Set when the map this belongs to has been taken out of the world. A spawn already in flight
			/// finishes anyway and nothing can call it back, so without this it would hand its entity to a
			/// record nobody holds any more. The continuation checks this and deletes what it just made.
			/// </summary>
			public bool Discarded;
		}

		/// <summary>
		/// What one autoloaded map put into the world. Kept per map rather than in one shared pile so that a
		/// single map can be taken back out while the others stay standing.
		/// </summary>
		private sealed class LoadedMap
		{
			public LoadedMap(string key, string stamp, string name)
			{
				Key = key;
				Stamp = stamp;
				Name = name;
			}

			/// <summary>
			/// What the map is called in the server's storage. <see cref="Name"/> is what the document calls
			/// itself and is only ever shown to the player; this is what the catalogue is matched on.
			/// </summary>
			public string Key { get; }

			/// <summary>
			/// The version of the entry this was spawned from — see <see cref="MapEntry.Stamp"/>. A map
			/// republished over the top of itself keeps its name and changes this, and that is the only way
			/// <see cref="Sync"/> can tell that what is standing is no longer what the server holds.
			/// </summary>
			public string Stamp { get; }

			public string Name { get; }

			public readonly List<LoadedEntity> Entities = new List<LoadedEntity>();
			public readonly List<int> Pickups = new List<int>();
			public readonly List<Marker> Markers = new List<Marker>();
			public readonly List<MapObject> RemovedObjects = new List<MapObject>();
		}

		private static readonly List<LoadedMap> Maps = new List<LoadedMap>();

		private static bool _justTeleported;

		/// <summary>How near a hidden object has to be to the spot it was hidden at. The same metre the SP
		/// build searched for the object to delete, and the same one <see cref="PropStreamer"/> uses.</summary>
		private const float HideRadius = 1f;

		public static int MapCount => Maps.Count;

		/// <summary>
		/// Everything the loaded maps are made of, whether or not it happens to be in the world right now: a
		/// count that fell as the player drove away would be answering a question nobody asked.
		/// </summary>
		public static int EntityCount => Maps.Sum(m => m.Entities.Count + m.Pickups.Count);

		public static bool Any => Maps.Count > 0;

		/// <summary>
		/// The maps standing here by their display name, in the order <see cref="KeyAt"/> indexes them — what
		/// the unload menu puts in its rows.
		/// </summary>
		public static IEnumerable<string> Names => Maps.Select(m => m.Name);

		/// <summary>
		/// The storage names of the maps standing right now, which is what the picker greys out and what
		/// <see cref="MapStore.UnloadAsync"/> is called with.
		/// </summary>
		public static IEnumerable<string> Keys => Maps.Select(m => m.Key);

		/// <summary>Whether that stored map is one of the ones standing here.</summary>
		public static bool IsLoaded(string key)
		{
			return Maps.Any(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>
		/// Puts up everything the server has published, once, at startup. Announced to the player, unlike
		/// <see cref="Sync"/>, which announces each change as it happens.
		/// </summary>
		public static async Task LoadAll()
		{
			await Sync(announce: false);

			if (Maps.Count > 0)
				Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate("Maps loaded from the server:") + " ~h~" +
				              string.Join(", ", Names.ToArray()) + "~h~.");
		}

		/// <summary>Set while <see cref="Sync"/> is working. See there.</summary>
		private static bool _syncing;

		/// <summary>Whether the catalogue moved again while a sync was running.</summary>
		private static bool _syncAgain;

		/// <summary>
		/// Brings what is standing here into line with what the server says is published.
		///
		/// This is the whole of "a published map applies to everyone, at once": the server broadcasts that
		/// its catalogue moved, every client fetches it, and every client — the author's included — arrives
		/// here and puts up what is new, takes down what is gone, and replaces what was published again over
		/// the top of itself. Nothing is pushed and nothing is diffed on the wire, so a client that missed a
		/// broadcast is put right by the next one, and one that has just joined gets the same answer by
		/// running this once against an empty world.
		///
		/// It cannot run twice at once — spawning a map takes many frames, and a second pass would spawn
		/// what the first one is halfway through spawning — so a broadcast arriving mid-flight is remembered
		/// and served by one more pass at the end.
		/// </summary>
		public static async Task Sync(bool announce = true)
		{
			if (_syncing)
			{
				_syncAgain = true;
				return;
			}

			_syncing = true;
			try
			{
				do
				{
					_syncAgain = false;
					await SyncOnce(announce);
				}
				while (_syncAgain);
			}
			finally
			{
				_syncing = false;
			}
		}

		private static async Task SyncOnce(bool announce)
		{
			var published = MapStore.List(MapScope.Shared).Where(entry => entry.Live).ToList();

			// Down first: a map republished over itself is one entry with a new stamp, and taking the old
			// copy out before the new one goes in keeps the two from standing in the same place.
			for (var i = Maps.Count - 1; i >= 0; i--)
			{
				var standing = Maps[i];
				if (published.Any(entry => string.Equals(entry.Name, standing.Key, StringComparison.OrdinalIgnoreCase)
				                           && entry.Stamp == standing.Stamp))
					continue;

				Maps.RemoveAt(i);
				Remove(standing);

				if (announce)
					Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate("Unloaded from the server:") +
					              " ~h~" + standing.Name + "~h~.");
			}

			if (Maps.Count == 0) _justTeleported = false;

			foreach (var entry in published)
			{
				if (Maps.Any(m => string.Equals(m.Key, entry.Name, StringComparison.OrdinalIgnoreCase))) continue;

				try
				{
					var loaded = await Load(entry);
					if (loaded != null && announce)
						Compat.Notify("~b~~h~Map Editor~h~~w~~n~" + Translation.Translate("Loaded from the server:") +
						              " ~h~" + loaded.Name + "~h~.");
				}
				catch (Exception e)
				{
					Compat.Notify("~r~~h~Map Editor~h~~w~~n~" + Translation.Translate("Map failed to load, see error below."));
					Compat.Notify(e.Message);
					Log.Error("AutoloadedMaps.Load (" + entry.Name + ")", e);
				}
			}
		}

		private static async Task<LoadedMap> Load(MapEntry entry)
		{
			var content = await MapStore.ReadAsync(entry);
			if (content == null) return null;

			var format = MapSerializer.Detect(content);
			if (!format.HasValue)
			{
				Log.Info("Published map '{0}' is in an unrecognised format and was skipped.", entry.Name);
				return null;
			}

			var map = new MapSerializer().DeserializeFromString(content, format.Value);
			if (map == null) return null;

			var loaded = new LoadedMap(entry.Name, entry.Stamp,
				map.Metadata != null && !string.IsNullOrWhiteSpace(map.Metadata.Name)
					? map.Metadata.Name
					: entry.Name);

			// Listed before anything is spawned, so a map that throws halfway through still owns what it put
			// into the world and can be unloaded again.
			Maps.Add(loaded);

			var toServer = 0;

			foreach (var o in map.Objects)
			{
				if (o == null) continue;

				// The server's, not ours. Anything whose state stops following from the document once spawned
				// — a car somebody can get into, a ped that walks or shoots — exists once for the session as
				// a server entity, and a local copy would stand inside it. See Platform.SharedObjects for the
				// rule and Server/LiveEntities.cs for the other half. Pickups land here too: they need the
				// server and the server has no native for them, so nobody places them.
				if (o.NeedsServer)
				{
					toServer++;
					continue;
				}

				var record = new LoadedEntity(o);
				loaded.Entities.Add(record);

				// Only the part of the map the player is near goes into the world; the rest follows from
				// <see cref="StreamEntities"/> as they reach it.
				if (SmartStreaming.HasReturned(o.Position))
					record.Entity = await Spawn(o);
			}

			foreach (var o in map.RemoveFromWorld)
			{
				if (o == null) continue;
				loaded.RemovedObjects.Add(o);

				// CREATE_MODEL_HIDE says it once and it stays said, so this is the whole of it — unlike
				// deleting, which only lasts until the area next streams in.
				Natives.HideModel(o.Position, HideRadius, o.Hash, false);
			}

			foreach (var marker in map.Markers)
			{
				if (marker != null) loaded.Markers.Add(marker);
			}

			if (toServer > 0)
				Log.Info("'{0}': {1} of {2} objects are the server's and are not spawned here.",
					entry.Name, toServer, map.Objects.Count);

			return loaded;
		}

		/// <summary>
		/// Puts one object of an autoloaded map into the world, and hands back what it put there.
		///
		/// <paramref name="streaming"/> says nobody is waiting for it: it is scenery filling in behind a player
		/// who is walking towards it, so a model that is not in memory yet is asked for and the whole thing is
		/// left for a later frame rather than blocking on it with "Loading Model" on the screen.
		/// </summary>
		private static async Task<Entity> Spawn(MapObject o, bool streaming = false)
		{
			Model model;
			if (streaming)
			{
				if (!SmartStreaming.TryRequestModel(o.Hash, out model)) return null;
			}
			else
			{
				var request = await ObjectPreview.LoadObject(o.Hash);
				if (!request.Loaded) return null;
				model = request.Model;
			}

			Entity spawned = null;

			// Local, never networked: the same document spawns on every client, so replicating it would put
			// each player's copy of the scenery on top of everyone else's.
			const bool networked = false;

			switch (o.Type)
			{
				case ObjectTypes.Prop:
				{
					// A door is spawned static so the game does not drop it, then unfrozen so it can still swing.
					var prop = Compat.PropFrom(Natives.CreateObjectNoOffset(model.Hash, o.Position, o.Dynamic && !o.Door, networked));
					if (prop == null) break;

					prop.Rotation = o.Rotation;
					prop.PositionNoOffset = o.Position;
					spawned = prop;
					break;
				}
				case ObjectTypes.Vehicle:
				{
					if (!model.IsVehicle) break;
					spawned = Compat.VehicleFrom(Natives.CreateVehicle(model.Hash, o.Position, o.Rotation.Z, networked));
					break;
				}
				case ObjectTypes.Ped:
				{
					if (!model.IsPed) break;

					// Peds stand on their feet where props hang from their centre, and the editor stores the centre.
					spawned = Compat.PedFrom(Natives.CreatePed(model.Hash, o.Position - new Vector3(0f, 0f, 1f), o.Rotation.Z, networked));
					break;
				}
			}

			Configure(spawned, o);

			// Persistence stops the game streaming the map out the moment the player walks away, and stops
			// the population cleanup taking a local entity nothing else owns. Smart streaming then does the
			// same thing deliberately and reversibly — the game's streamer would never put it back.
			if (spawned != null) spawned.IsPersistent = true;

			model.MarkAsNoLongerNeeded();
			return spawned;
		}

		/// <summary>
		/// Makes an entity into what the map says it is: everything except where it stands, which is decided
		/// by whoever created it.
		///
		/// Shared by both halves of a published map on purpose. The objects this client spawned for itself
		/// go through here, and so do the ones the server created and handed over — a server can place an
		/// entity but it has no native for a task, a relationship group, a livery, a siren or a quaternion,
		/// so the client that has control finishes the job. One routine rather than two, because a
		/// difference between them would be a difference in what two players see standing in the same place,
		/// and it would only show up with two people in the same session.
		/// </summary>
		internal static void Configure(Entity entity, MapObject o)
		{
			if (entity == null || o == null) return;

			// Not for a ped: one is only ever placed standing, so its heading is all the rotation that means
			// anything and the editor never writes a quaternion for it.
			if (o.Type != ObjectTypes.Ped && o.Quaternion != null && !IsUnset(o.Quaternion))
				Quaternion.SetEntityQuaternion(entity, o.Quaternion);

			switch (o.Type)
			{
				case ObjectTypes.Prop:
					entity.IsPositionFrozen = !o.Dynamic && !o.Door;
					break;

				case ObjectTypes.Vehicle:
				{
					var vehicle = Compat.VehicleFrom(entity.Handle);
					if (vehicle == null) break;

					vehicle.Mods.PrimaryColor = (VehicleColor) o.PrimaryColor;
					vehicle.Mods.SecondaryColor = (VehicleColor) o.SecondaryColor;
					ApplyLivery(vehicle.Handle, o);
					vehicle.IsSirenActive = o.SirensActive;
					vehicle.IsPositionFrozen = !o.Dynamic;

					// A locally spawned car is one nobody else has, and getting in is a task on the ped —
					// tasks replicate, so the driver would be seen sitting in mid-air by everyone whose copy
					// is elsewhere. Locking says this is scenery when the door is tried. A car the author
					// left shared is created by the server and never reaches here.
					if (!o.NeedsServer)
					{
						Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, vehicle.Handle, LockedNoPlayerAttempt);
						Function.Call(Hash.SET_VEHICLE_UNDRIVEABLE, vehicle.Handle, true);
					}
					break;
				}

				case ObjectTypes.Ped:
				{
					var ped = Compat.PedFrom(entity.Handle);
					if (ped == null) break;

					ped.IsPositionFrozen = !o.Dynamic;
					PedComponents.Apply(ped, o.Drawables, o.Textures);

					if (o.Weapon.HasValue && o.Weapon.Value != WeaponHash.Unarmed)
						ped.Weapons.Give(o.Weapon.Value, 999, true, true);

					if (!string.IsNullOrEmpty(o.Relationship) && o.Relationship != "Companion")
						ObjectDatabase.SetPedRelationshipGroup(ped, o.Relationship);

					StartScenario(ped, o.Action);
					break;
				}
			}
		}

		/// <summary>
		/// Puts the map's livery on the vehicle, and answers whether it is now wearing it.
		///
		/// Its own method, and one that checks, because a livery is the one thing on a vehicle that the game
		/// will accept and then quietly not do. A car that has only this moment appeared over the network is
		/// not yet in a state to take the call: it is swallowed and nothing says so. The caller that has to
		/// live with that is <see cref="SharedEntities"/>, which asks again on later frames; here the answer
		/// is only ever "on" or "not yet".
		///
		/// Which native puts it on is <see cref="Natives.SetLivery"/>'s business — there are two kinds of
		/// livery and the wrong one is silence as well.
		///
		/// True when the map asks for no livery: there is nothing to wait for.
		/// </summary>
		internal static bool ApplyLivery(int handle, MapObject o)
		{
			if (o == null || o.Type != ObjectTypes.Vehicle || o.Livery < 0) return true;
			if (handle == 0 || !Function.Call<bool>(Hash.DOES_ENTITY_EXIST, handle)) return false;

			if (Natives.GetLivery(handle) == o.Livery) return true;

			// A livery the model does not have is not something to wait for, and saying "not yet" about it
			// keeps the whole entity unanswered until the request ages out. Maps saved before liveries were
			// read correctly all land here, with an index exactly one past the end of the list: the number
			// written down was the count. There is no way back to which one the author picked, so the line
			// says what has to be done by hand.
			var count = Natives.LiveryCount(handle);
			if (count > 0 && o.Livery >= count)
			{
				Log.Info("A vehicle asks for livery {0} and its model has {1} ({2}). Pick the livery again " +
					"and save the map: it was written down by a build that recorded the count in its place.",
					o.Livery, count, count == 1 ? "only 0" : "0 to " + (count - 1));
				return true;
			}

			Natives.SetLivery(handle, o.Livery);

			return Natives.GetLivery(handle) == o.Livery;
		}

		/// <summary>
		/// SET_VEHICLE_DOORS_LOCKED's "cannot be entered, and the player does not even try". Stronger than
		/// plain locked (2), which still plays the tug on the handle.
		/// </summary>
		private const int LockedNoPlayerAttempt = 10;

		/// <summary>A quaternion that was never written to the map file, as opposed to a real rotation.</summary>
		private static bool IsUnset(Quaternion q)
		{
			return q.X == 0 && q.Y == 0 && q.Z == 0 && q.W == 0;
		}

		private static void StartScenario(Ped ped, string action)
		{
			if (string.IsNullOrEmpty(action) || action == "None") return;

			switch (action)
			{
				case "Any":
				case "Any - Walk":
					Function.Call(Hash.TASK_USE_NEAREST_SCENARIO_TO_COORD, ped.Handle, ped.Position.X, ped.Position.Y,
						ped.Position.Z, 100f, -1);
					return;
				case "Any - Warp":
					Function.Call(Hash.TASK_USE_NEAREST_SCENARIO_TO_COORD_WARP, ped.Handle, ped.Position.X,
						ped.Position.Y, ped.Position.Z, 100f, -1);
					return;
				case "Wander":
					ped.Task.WanderAround();
					return;
			}

			string scenario;
			if (ObjectDatabase.ScrenarioDatabase.TryGetValue(action, out scenario))
				Function.Call(Hash.TASK_START_SCENARIO_IN_PLACE, ped.Handle, scenario, 0, 0);
		}

		/// <summary>Every marker still standing, from whichever maps are still loaded.</summary>
		private static IEnumerable<Marker> AllMarkers => Maps.SelectMany(m => m.Markers);

		public static void Tick()
		{
			// Before the early return: server-owned objects are not in these lists, so a map made entirely
			// of them would otherwise never be finished off.
			SharedEntities.Tick();

			if (Maps.Count == 0) return;

			StreamEntities();

			foreach (var marker in AllMarkers)
			{
				if (marker.OnlyVisibleInEditor && !MapEditor.IsInFreecam) continue;

				// Called as a raw native, the way PropStreamer draws the editor's own markers, and not
				// through World.DrawMarker: that wrapper's last two parameters are the texture dictionary
				// and name, and it hands them to NativeApi.PushArg as they are. PushArg picks its branch by
				// isinst on the boxed value, so a null string matches nothing and comes out of the bottom as
				// "Unsupported type String" — a thrown frame every tick, for the whole of any published map
				// that has a marker in it. Passing 0 for the two texture slots is what "no texture" is at
				// this level anyway.
				Function.Call(Hash.DRAW_MARKER, (int) marker.Type, marker.Position.X, marker.Position.Y, marker.Position.Z,
					0f, 0f, 0f, marker.Rotation.X, marker.Rotation.Y, marker.Rotation.Z,
					marker.Scale.X, marker.Scale.Y, marker.Scale.Z,
					marker.Red, marker.Green, marker.Blue, marker.Alpha,
					marker.BobUpAndDown, marker.RotateToCamera, 2, false, false, false, false);
			}

			TickTeleports();
		}

		/// <summary>How far through the loaded maps the rolling scan has got. See <see cref="StreamEntities"/>.</summary>
		private static int _scanCursor;

		/// <summary>
		/// Keeps the loaded maps down to the parts of them the player is anywhere near, the same way the editor
		/// does it for the map being edited. See <see cref="SmartStreaming"/>.
		///
		/// Published maps are the reason the setting is worth having at all: they are loaded whole and stand
		/// there for the rest of the session, wherever the player goes, and there can be any number of them.
		/// Walked a slice at a time across frames, because asking every entity of every map where it is, on
		/// every frame, is the cost this is meant to be saving.
		/// </summary>
		private static void StreamEntities()
		{
			var total = 0;
			foreach (var map in Maps)
				total += map.Entities.Count;

			if (total == 0) return;

			var toScan = Math.Min(total, SmartStreaming.ScanPerTick);
			for (var i = 0; i < toScan; i++)
			{
				if (_scanCursor >= total) _scanCursor = 0;

				var entry = EntryAt(_scanCursor++);
				if (entry == null || entry.Gone || entry.Spawning) continue;

				// With the setting off there is nothing to ask about what is already standing, and asking
				// would spend every frame on the cost the player turned off.
				if (entry.Entity != null && !SmartStreaming.Enabled) continue;

				if (entry.Entity == null)
				{
					if (!SmartStreaming.HasReturned(entry.Object.Position)) continue;
					if (!SmartStreaming.TakeSpawn()) return;

					// Started rather than waited for: this runs inside a frame. A spawn that comes back empty
					// has asked for its model and is retried on a later pass; the flag keeps the frames in
					// between from starting it over and over.
					var pending = entry;
					pending.Spawning = true;
					Log.Guard("AutoloadedMaps stream in", async () =>
					{
						try
						{
							var spawned = await Spawn(pending.Object, streaming: true);

							// The map can have been unloaded while this waited for its model — a broadcast
							// lands between any two frames — leaving nobody to hold what was just made.
							if (pending.Discarded)
							{
								if (spawned != null && spawned.Exists()) spawned.Delete();
								return;
							}

							pending.Entity = spawned;
						}
						finally
						{
							pending.Spawning = false;
						}
					});
					continue;
				}

				if (!entry.Entity.Exists())
				{
					// Deleted by the player or by another resource: these are persistent, so the game did not
					// do it.
					entry.Entity = null;
					entry.Gone = true;
					continue;
				}

				if (!SmartStreaming.IsTooFar(entry.Entity.Position)) continue;

				// Only reachable while the freecam is down: distance is measured from the camera while it
				// is up.
				var vehicle = Game.Player.Character.CurrentVehicle;
				if (vehicle != null && vehicle.Handle == entry.Entity.Handle) continue;

				if (!SmartStreaming.TakeDespawn()) return;

				entry.Entity.Delete();
				entry.Entity = null;
			}
		}

		/// <summary>The entities of every loaded map as one list, for the rolling scan to walk.</summary>
		private static LoadedEntity EntryAt(int index)
		{
			foreach (var map in Maps)
			{
				if (index < map.Entities.Count) return map.Entities[index];
				index -= map.Entities.Count;
			}

			return null;
		}

		private static void TickTeleports()
		{
			foreach (var marker in AllMarkers)
			{
				if (!marker.TeleportTarget.HasValue || _justTeleported) continue;
				if (!Game.Player.Character.IsInRangeOf(marker.Position, Math.Max(2f, marker.Scale.X))) continue;

				if (Game.Player.Character.IsInVehicle())
					Game.Player.Character.CurrentVehicle.Position = marker.TeleportTarget.Value;
				else
					Game.Player.Character.Position = marker.TeleportTarget.Value;

				_justTeleported = true;
			}

			// Held down until the player leaves the pad, or they would be bounced straight back on arrival.
			if (_justTeleported && !AllMarkers.Any(m => m.TeleportTarget.HasValue &&
			                                            Game.Player.Character.IsInRangeOf(m.Position, Math.Max(2f, m.Scale.X))))
				_justTeleported = false;
		}

		/// <summary>
		/// What the map at that row of <see cref="Names"/> is called in storage, for the menu to hand to
		/// <see cref="MapStore.UnloadAsync"/>. Null if the index is not one.
		///
		/// There is deliberately no local "unload this one": a published map stands on every client, and one
		/// player taking their own copy out would leave them looking at a world nobody else is in. The server
		/// decides, and this side finds out through <see cref="Sync"/> like everybody else.
		/// </summary>
		public static string KeyAt(int index)
		{
			return index < 0 || index >= Maps.Count ? null : Maps[index].Key;
		}

		/// <summary>
		/// Takes every published map back out of this client's world, and nobody else's — for a resource that
		/// is stopping. What the server holds is untouched, so the instance that replaces this one puts the
		/// same maps back up.
		/// </summary>
		public static void UnloadAll()
		{
			foreach (var map in Maps)
				Remove(map);

			Maps.Clear();
			SharedEntities.Clear();
			_justTeleported = false;
		}

		/// <summary>
		/// Everything one map put into the world goes, and the world objects it hid come back, since nothing is
		/// holding them down any more. A map still loaded that hides the same object says so again on its next
		/// load; within one session that is a hole this leaves, and it is the same hole the SP build had.
		/// </summary>
		private static void Remove(LoadedMap map)
		{
			foreach (var entry in map.Entities)
			{
				// Marked before anything is deleted: a spawn still in flight finishes after this returns, and
				// this is the only thing that tells it not to keep what it made.
				entry.Discarded = true;

				// Nothing to delete for the ones streaming has already taken out of the world.
				if (entry.Entity != null && entry.Entity.Exists())
					entry.Entity.Delete();
			}

			foreach (var pickup in map.Pickups)
				Function.Call(Hash.REMOVE_PICKUP, pickup);

			foreach (var o in map.RemovedObjects)
				Natives.UnhideModel(o.Position, HideRadius, o.Hash);

			map.Entities.Clear();
			map.Pickups.Clear();
			map.Markers.Clear();
			map.RemovedObjects.Clear();
		}
	}
}
