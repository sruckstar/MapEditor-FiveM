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
	/// The map being edited: everything in it, everything the editor keeps beside it, and the spawning and
	/// deleting of it.
	///
	/// The lists below hold what is standing in the world, keyed by entity handle, and
	/// <see cref="StreamedOut"/> holds the rest — the part <see cref="SmartStreaming"/> has taken out because
	/// the player is nowhere near it. Both halves are the map: counts, saving and the entity menu cover the
	/// two together.
	///
	/// Spawning is asynchronous, because a model has to be waited for and FiveM has no fibers (see
	/// <see cref="Frame"/>), and what is spawned is always local to this player — see <see cref="Local"/>.
	/// </summary>
	public static class PropStreamer
	{
		/// <summary>
		/// How many props may stand in the world at once. Lower than a single-player game would allow: a
		/// FiveM client shares that budget with the server's resources, every other player's entities and
		/// whatever else is streamed. A larger map still works — <see cref="SmartStreaming"/> keeps only
		/// what is near the player, and the rest waits in <see cref="StreamedOut"/>.
		/// </summary>
		public static int MAX_OBJECTS = 800;

		/// <summary>
		/// What the editor spawns is never replicated, named here rather than left as a bare <c>false</c> at
		/// each of the three creation calls.
		///
		/// Not a setting, because there is no case where replicating a draft is right. A map under
		/// construction is dragged about, copied and undone, and every one of those would be an entity
		/// created and destroyed across the network against the server's OneSync budget. A finished map does
		/// not need it either: publishing sends the <em>document</em>, and every client spawns the same
		/// objects at the same coordinates. Replication only buys something when two players must see the
		/// <em>same</em> object diverge from the document — a vehicle being driven, a crate being pushed —
		/// and that is decided per object on a published map. See <see cref="Platform.SharedObjects"/>.
		/// </summary>
		private const bool Local = false;

		/// <summary>
		/// How long a model is waited for before the spawn is given up on. CitizenFX's own World.Create*
		/// methods allow the same.
		/// </summary>
		private const int ModelTimeout = 1000;

		/// <summary>
		/// How many frames a model's collision is waited for once its drawable is in memory. Short on
		/// purpose: see <see cref="EnsureCollision"/> for why this wait can never be allowed to decide
		/// whether something spawns.
		/// </summary>
		private const int CollisionFrames = 20;

		/// <summary>
		/// Models whose collision has already been waited for once and never arrived. Without this, a map
		/// built out of collisionless props would pay <see cref="CollisionFrames"/> per prop rather than per
		/// model — five hundred of them turning a load into a two-minute crawl — and would say so in the
		/// console five hundred times.
		/// </summary>
		private static readonly HashSet<int> CollisionlessModels = new HashSet<int>();

	    public static List<int> UsedModels = new List<int>();

		public static List<int> StreamedInHandles = new List<int>();

		public static List<int> StaticProps = new List<int>();

		public static List<int> Vehicles = new List<int>();

		public static List<int> Peds = new List<int>();

        public static List<DynamicPickup> Pickups = new List<DynamicPickup>();

        public static Dictionary<int, string> Identifications = new Dictionary<int, string>();

		public static List<Marker> Markers = new List<Marker>();

		public static Dictionary<int, string> ActiveScenarios = new Dictionary<int, string>();

		public static Dictionary<int, string> ActiveRelationships = new Dictionary<int, string>();

		public static Dictionary<int, WeaponHash> ActiveWeapons = new Dictionary<int, WeaponHash>();

        public static List<int> Doors = new List<int>();

		/// <summary>
		/// The objects whose author has overridden which side owns them once the map is published — see
		/// <see cref="Platform.SharedObjects"/> and <see cref="MapObject.Shared"/>.
		///
		/// Only the ones actually spoken for are in here, and that is the point of a dictionary rather than
		/// the two lists everything else in this class uses: absent has to stay absent. A map that says
		/// nothing about an object keeps following the rule as the rule improves, while one that was
		/// silently written out with today's answer would be frozen at it forever.
		/// </summary>
		public static Dictionary<int, bool> SharedOverrides = new Dictionary<int, bool>();

		public static List<int> ActiveSirens = new List<int>();

	    public static MapMetadata CurrentMapMetadata = new MapMetadata();

		/// <summary>
		/// One entity of the map being edited that <see cref="SmartStreaming"/> has taken back out of the world
		/// because the player left it behind. It is still part of the map in every way that matters — it is
		/// counted, it is saved, and it still has its row in the entity menu — it is simply not standing
		/// anywhere at the moment.
		/// </summary>
		public sealed class StreamedObject
		{
			/// <summary>Everything needed to put it back, in the same shape the map file holds it in.</summary>
			public MapObject Object;

			/// <summary>
			/// What its entity menu row points at while there is no entity handle to point at. Handles are
			/// handed back to the game when the entity goes and are given out again to whatever the game
			/// spawns next, so the row cannot simply keep the old one.
			/// </summary>
			public int Uid;

			/// <summary>
			/// Whether a spawn for this record is already on its way. Putting an entity back is asynchronous
			/// here — there is a model to wait for, and no fiber to wait on — so it takes at least a frame,
			/// and without this the streamer would start the job again on the next one and every one after,
			/// ending up with a pile of copies of the same object.
			/// </summary>
			public bool Spawning;
		}

		public static List<StreamedObject> StreamedOut = new List<StreamedObject>();

		private static int _streamUids;

		public static int PropCount => StreamedInHandles.Count + StreamedOutCount(ObjectTypes.Prop);

		public static int VehicleCount => Vehicles.Count + StreamedOutCount(ObjectTypes.Vehicle);

		public static int PedCount => Peds.Count + StreamedOutCount(ObjectTypes.Ped);

		public static int EntityCount => PropCount + VehicleCount + PedCount;

		/// <summary>
		/// Whether the world is holding as many props as it may. Streaming asks before spawning, because a map
		/// with more props than the limit is perfectly workable as long as no more than the limit are in range
		/// at once, and trying anyway would tell the player they had reached the limit on every frame.
		/// </summary>
		public static bool PropSlotsFull => StreamedInHandles.Count >= MAX_OBJECTS;

		private static int StreamedOutCount(ObjectTypes type)
		{
			var count = 0;
			foreach (var streamed in StreamedOut)
			{
				if (streamed.Object.Type == type) count++;
			}
			return count;
		}

		/// <summary>
		/// Makes sure the game has the model in memory, asking for it if it does not.
		///
		/// The SP build left this to SHVDN's World.CreateVehicle/CreatePed, which requested the model
		/// themselves. Their CitizenFX counterparts do too — but they also spawn networked, with no way to
		/// ask for anything else, which is why this file calls the natives directly (see
		/// <see cref="Natives"/>) and therefore has to do the requesting itself.
		/// </summary>
		private static async Task<bool> EnsureLoaded(Model model)
		{
			if (ReferenceEquals(model, null)) return false;

			// Asked for before the drawable is waited on rather than after, so the two halves stream in
			// alongside each other instead of one starting when the other has finished.
			Natives.RequestCollisionForModel(model.Hash);

			if (!model.IsLoaded && !await model.Request(ModelTimeout)) return false;

			await EnsureCollision(model.Hash);
			return true;
		}

		/// <summary>
		/// Waits, briefly, for a model's collision to follow its drawable into memory.
		///
		/// This never fails a spawn, and that is the whole design of it. Some models genuinely ship without
		/// collision, and for those HAS_COLLISION_FOR_MODEL_LOADED never becomes true no matter how long it
		/// is asked: making the spawn conditional on it would turn "a prop you cannot touch" into "a prop
		/// that is not there", which is the worse of the two. So the wait is bounded, its result is only
		/// reported, and a model that fails it once is never waited for again.
		/// </summary>
		private static async Task EnsureCollision(int hash)
		{
			if (Natives.HasCollisionForModelLoaded(hash)) return;
			if (CollisionlessModels.Contains(hash)) return;

			for (var frame = 0; frame < CollisionFrames; frame++)
			{
				await Frame.Next();
				if (Natives.HasCollisionForModelLoaded(hash)) return;
				Natives.RequestCollisionForModel(hash);
			}

			CollisionlessModels.Add(hash);

			// Not proof of a fault — see above — but it is the one thing that explains an object standing in
			// the world that nothing can touch, so it is worth being able to see which models it happened to.
			Log.Info("Collision for model {0} did not load in {1} frames; it will spawn without collision.",
				ObjectDatabase.Describe(hash), CollisionFrames);
		}

		/// <summary>
		/// Marks a spawned entity as the editor's, so that the game's own streamer cannot take it away again.
		///
		/// The SP build got this for nothing: SHVDN spawns through the script-owned path of the natives. A
		/// local entity here is owned by nobody until it is said to be a mission entity, and an unowned object
		/// is fair game for the population and streaming cleanups. What takes an entity of the map back out of
		/// the world is <see cref="SmartStreaming"/>, deliberately, and nothing else.
		/// </summary>
		private static void Keep(Entity entity)
		{
			if (entity == null) return;
			entity.IsPersistent = true;
		}

		/// <summary>
		/// Whether the world would move this entity of the map being edited if it were left to itself: a prop
		/// with its physics on, a door, or a vehicle or ped that is not frozen scenery.
		///
		/// A pickup is never one: it is put down frozen and stays there. See <see cref="CreatePickup"/>.
		/// </summary>
		public static bool IsDynamic(int handle)
		{
			if (IsPickup(handle)) return false;
			return !StaticProps.Contains(handle) || Doors.Contains(handle);
		}

		/// <summary>
		/// Holds one entity of the map being edited exactly where it was put, and takes the dynamic ones out
		/// of the physics world while they are in it.
		///
		/// <b>Nothing in a draft moves.</b> A dynamic prop dropped in the editor falls, rolls and comes to
		/// rest somewhere else; a vehicle settles onto its springs and slides down a slope; a ped is shoved
		/// aside by whatever walks into it. Every one of those is the map editing itself between the placement
		/// and the save, and it is what the author will find in the file rather than what they put there. What
		/// the document says is what stands, until the document is published — and then it is either the
		/// server's entity (see <see cref="Platform.SharedObjects"/>) or a client's copy of a finished map
		/// (<see cref="AutoloadedMaps"/>), and both of those live properly.
		///
		/// Freezing alone would hold it. The collision comes off on top because a frozen dynamic object is a
		/// wall in the middle of the map being built: it catches the player, it catches whatever is dropped
		/// near it, and it cannot be nudged out of the way precisely because it is frozen. Frozen and
		/// intangible, it is a picture of where the object is going to be. The freeze is also what keeps it
		/// out of the ground — collision is what an object rests on, and one with neither would fall for ever.
		///
		/// What this costs is the crosshair: picking an entity is a shape test, and a shape test asks the
		/// physics world, which is the thing that has just been switched off. So the editor stops relying on
		/// it alone — <see cref="PickIntangible"/> answers for everything taken out here, by measuring the
		/// ray against model boxes, and <see cref="VectorExtensions.RaycastEntity"/> takes whichever of the
		/// two passes found something nearer.
		/// </summary>
		public static void HoldStill(Entity entity)
		{
			if (entity == null || !entity.Exists()) return;

			var solid = !IsDynamic(entity.Handle);

			entity.IsPositionFrozen = true;
			Function.Call(Hash.SET_ENTITY_COLLISION, entity.Handle, solid, true);

			if (solid) Intangible.Remove(entity.Handle);
			else Intangible.Add(entity.Handle);

			var ped = Compat.PedFrom(entity.Handle);
			if (ped != null) StopBehaving(ped);
		}

		/// <summary>
		/// The entities of the map standing in the world without collision — the dynamic ones, and exactly
		/// the ones a shape test cannot find. See <see cref="HoldStill"/>, which is the only thing that
		/// decides this and therefore the only thing that writes here.
		///
		/// A set of its own rather than a question asked of the three lists: the crosshair asks for these
		/// on every frame, and working them out would mean walking eight hundred prop handles through a
		/// list search apiece to find the dozen that qualify.
		/// </summary>
		private static readonly HashSet<int> Intangible = new HashSet<int>();

		/// <summary>
		/// The nearest entity of the map being edited that the segment goes through and the shape test
		/// cannot see, or null. <paramref name="distance"/> is how far along the segment it was met.
		///
		/// This is the other half of picking with the crosshair — see
		/// <see cref="VectorExtensions.RaycastEntity"/> — and it exists because
		/// <see cref="HoldStill"/> takes the dynamic half of the map out of the physics world, where every
		/// shape test looks. The ray is measured against the model's own bounding box instead, in the
		/// entity's frame, so a turned object is hit where it looks like it is rather than where an
		/// axis-aligned box around it would be.
		///
		/// Two passes, for cost: the world-space bounding sphere throws out everything not near the line,
		/// at one native call each, and only what survives is worth the two frame transforms the exact test
		/// needs. A box is not a collision mesh, so this picks up a hollow object slightly before the eye
		/// would — which is the right way round for something being pointed at.
		/// </summary>
		public static Entity PickIntangible(Vector3 from, Vector3 to, out float distance)
		{
			distance = 0f;
			if (Intangible.Count == 0) return null;

			var segment = to - from;
			var length = segment.Length();
			if (length <= 0f) return null;

			var unit = segment * (1f / length);

			Entity nearest = null;
			var nearestFraction = float.MaxValue;

			foreach (var handle in Intangible)
			{
				var entity = Compat.Ent(handle);
				if (entity == null) continue;

				Vector3 min, max;
				if (!LuaBridge.ModelDimensions(entity.Model.Hash, out min, out max)) continue;

				// The coarse pass. The sphere is the box's whole diagonal rather than half of it — more
				// than twice what it needs to be — so that it still covers the model however far the box
				// sits from the point the entity answers with, which for a ped is a metre. Dot product
				// written out: this runs per entity per frame, and the fewer members of CitizenFX's own
				// types it leans on, the fewer there are to go missing on Enhanced (see tools/sandbox-audit).
				var radius = (max - min).Length();
				var toCentre = entity.Position - from;
				var along = toCentre.X * unit.X + toCentre.Y * unit.Y + toCentre.Z * unit.Z;
				if (along < -radius || along > length + radius) continue;

				var perpendicular = toCentre - unit * along;
				if (perpendicular.Length() > radius) continue;

				var fraction = VectorExtensions.SegmentEntersBox(
					Natives.OffsetFromEntity(handle, from), Natives.OffsetFromEntity(handle, to), min, max);

				if (fraction < 0f || fraction >= nearestFraction) continue;

				nearestFraction = fraction;
				nearest = entity;
			}

			if (nearest == null) return null;

			distance = nearestFraction * length;
			return nearest;
		}

		/// <summary>
		/// Takes away everything a ped does, whatever the map says it will do once it is published.
		///
		/// A ped is the one thing in a map that acts on its own account, and the editor is the one place
		/// where that is never wanted: the author is arranging a scene, and a ped that walks off, draws a
		/// weapon or turns on them is arranging it back. So the scenario, the relationship group and the
		/// weapon are <em>recorded</em> while the map is being built and applied to nothing —
		/// <see cref="AutoloadedMaps.Configure"/> puts them on when the map goes up for everybody, which is
		/// the first moment they mean anything.
		/// </summary>
		private static void StopBehaving(Ped ped)
		{
			// Whatever the game handed it on creation, and anything an earlier version of this map started.
			Function.Call(Hash.CLEAR_PED_TASKS, ped.Handle);

			// Nothing around it reaches it now — no gunfire, no car bearing down on it, no player pushing
			// past. Without this the game gives an idle ped a fresh task the moment anything happens near it.
			Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, ped.Handle, true);

			// A ragdoll is the one way a frozen ped still moves.
			Function.Call(Hash.SET_PED_CAN_RAGDOLL, ped.Handle, false);
		}

        public static async Task<Prop> CreateProp(Model model, Vector3 position, Vector3 rotation, bool dynamic, Quaternion q = null, bool force = false, int drawDistance = -1, bool pace = true)
		{
			if (StreamedInHandles.Count >= MAX_OBJECTS)
			{
				Compat.Notify("~r~~h~Map Editor~h~~w~\nYou have reached the prop limit. You cannot place any more props.");
				return null;
			}

			// A breather while a whole map is poured into the world. Counted on what is standing, not the size
			// of the map: with streaming most of a large map is not in the world, and counting that would
			// wait on every prop. Streaming itself passes pace: false, since it already spreads over frames.
			if (pace && StreamedInHandles.Count > 0 && StreamedInHandles.Count % 249 == 0)
                await Frame.Wait(100);

			if (!await EnsureLoaded(model))
			{
				Compat.Notify("~r~~h~Map Editor~h~~w~~n~The prop failed to spawn.");
				return null;
			}

			var prop = Compat.PropFrom(Natives.CreateObjectNoOffset(model.Hash, position, dynamic, Local));
			if (prop == null)
			{
				Compat.Notify("~r~~h~Map Editor~h~~w~~n~The prop failed to spawn.");
				return null;
			}
			Keep(prop);
            prop.Rotation = rotation;
			StreamedInHandles.Add(prop.Handle);
			if (!dynamic) StaticProps.Add(prop.Handle);
			if (q != null)
				Quaternion.SetEntityQuaternion(prop, q);
			prop.Position = position;
		    if (drawDistance != -1)
		        prop.LodDistance = drawDistance;
			// Last, and after the lists know what kind of object this is: what it is held by depends on
			// whether it is dynamic. A door is registered by the caller and asks again there.
			HoldStill(prop);
            UsedModels.Add(model.Hash);
            model.MarkAsNoLongerNeeded();
			return prop;
		}

		public static async Task<Vehicle> CreateVehicle(Model model, Vector3 position, float heading, bool dynamic, Quaternion q = null, int drawDistance = -1)
		{
			// CREATE_VEHICLE given something that is not a vehicle does not fail politely, so the model is
			// checked before it is asked for. The SP build was checked the same way, inside World.CreateVehicle.
			//
			// The model is asked for once and waited a second for, rather than retried: Model.Request does
			// the waiting properly.
			if (ReferenceEquals(model, null) || !model.IsVehicle || !await EnsureLoaded(model))
			{
				Compat.Notify("~r~~h~Map Editor~h~~w~~n~I tried very hard, but the vehicle failed to load.");
				return null;
			}

			var veh = Compat.VehicleFrom(Natives.CreateVehicle(model.Hash, position, heading, Local));
			if (veh == null)
			{
				Compat.Notify("~r~~h~Map Editor~h~~w~~n~I tried very hard, but the vehicle failed to load.");
				return null;
			}

			Keep(veh);
			Vehicles.Add(veh.Handle);
			if (!dynamic) StaticProps.Add(veh.Handle);
			if(q != null)
				Quaternion.SetEntityQuaternion(veh, q);
		    if (drawDistance != -1)
		        veh.LodDistance = drawDistance;
			HoldStill(veh);
            UsedModels.Add(model.Hash);
            model.MarkAsNoLongerNeeded();
            return veh;
		}

		public static async Task<Ped> CreatePed(Model model, Vector3 position, float heading, bool dynamic, Quaternion q = null, int drawDistance = -1)
		{
			if (ReferenceEquals(model, null) || !model.IsPed || !await EnsureLoaded(model))
			{
				Compat.Notify("~r~~h~Map Editor~h~~w~~n~The ped failed to spawn.");
				return null;
			}

			var veh = Compat.PedFrom(Natives.CreatePed(model.Hash, position, heading, Local));
			if (veh == null)
			{
				Compat.Notify("~r~~h~Map Editor~h~~w~~n~The ped failed to spawn.");
				return null;
			}
			Keep(veh);
			Peds.Add(veh.Handle);
			if (!dynamic) StaticProps.Add(veh.Handle);
			if (q != null)
				Quaternion.SetEntityQuaternion(veh, q);
		    if (drawDistance != -1)
		        veh.LodDistance = drawDistance;
			HoldStill(veh);
            UsedModels.Add(model.Hash);
            model.MarkAsNoLongerNeeded();
            return veh;
		}

	    private static int _pickupIds = 0;

	    /// <summary>
	    /// Pickups are the one thing here that cannot be local: CREATE_PICKUP_ROTATE is a networked native.
	    /// Kept working rather than removed, so a map that has them in it still loads.
	    /// </summary>
        public static async Task<DynamicPickup> CreatePickup(Model model, Vector3 position, float heading, int amount, bool dynamic, Quaternion q = null)
        {
            var v_4 = 515;
            int newPickup = -1;

            if (Game.Player.Character.IsInRangeOf(position, 30f))
            {
                newPickup = Function.Call<int>(Hash.CREATE_PICKUP_ROTATE, model.Hash, position.X, position.Y,
                    position.Z, 0, 0, heading, v_4, amount, 0, false, 0);
            }

            var pcObj = new DynamicPickup(newPickup);
            pcObj.Flag = v_4;
            pcObj.Amount = amount;
            pcObj.RealPosition = position;
            if (newPickup != -1)
            {
                var start = 0;
                while (pcObj.ObjectHandle == -1 && start < 20)
                {
                    start++;
                    await Frame.Next();
                }

                pcObj.Dynamic = false;

                var pickupObject = Compat.PropFrom(pcObj.ObjectHandle);
                if (pickupObject != null)
                {
                    pickupObject.IsPersistent = true;
                    if (q != null)
                        Quaternion.SetEntityQuaternion(pickupObject, q);
                }
                pcObj.UpdatePos();
            }

            Pickups.Add(pcObj);
            pcObj.PickupHash = model.Hash;
            pcObj.Timeout = 1;
            pcObj.UID = _pickupIds++;
            return pcObj;
        }

	    public static DynamicPickup GetPickup(int objectHandle)
	    {
            DynamicPickup pc = null;
            foreach (var pickup in Pickups)
            {
                if (pickup.ObjectHandle == objectHandle)
                {
                    pc = pickup;
                    break;
                }
            }

		    return pc;
	    }

        public static DynamicPickup GetPickupByUID(int uid)
        {
            DynamicPickup pc = null;
            foreach (var pickup in Pickups)
            {
                if (pickup.UID == uid)
                {
                    pc = pickup;
                    break;
                }
            }

            return pc;
        }

        public static void RemoveVehicle(int handle)
		{
		    var veh = Compat.VehicleFrom(handle);
		    if (veh != null)
		    {
		        ReleaseModel(veh.Model);
		        veh.Delete();
		    }
			if (Vehicles.Contains(handle)) Vehicles.Remove(handle);
			if (StaticProps.Contains(handle)) StaticProps.Remove(handle);
			Anchors.Remove(handle);
			SharedOverrides.Remove(handle);
			Intangible.Remove(handle);
		}

		public static void RemovePed(int handle)
		{
		    var ped = Compat.PedFrom(handle);
		    if (ped != null)
		    {
		        ReleaseModel(ped.Model);
		        ped.Delete();
		    }
			if (Peds.Contains(handle)) Peds.Remove(handle);
			if (StaticProps.Contains(handle)) StaticProps.Remove(handle);
			Anchors.Remove(handle);
			SharedOverrides.Remove(handle);
			Intangible.Remove(handle);
        }

		/// <summary>
		/// Drops one usage of a model and releases it back to the streamer when nothing else needs it.
		/// </summary>
		private static void ReleaseModel(Model model)
		{
		    UsedModels.Remove(model.Hash);
		    if (!UsedModels.Contains(model.Hash))
		        model.MarkAsNoLongerNeeded();
		}

	    public static void RemovePickup(int objectHandle)
	    {
	        DynamicPickup pc = null;
	        foreach (var pickup in Pickups)
	        {
	            if (pickup.ObjectHandle == objectHandle)
	            {
	                pc = pickup;
                    pc.Remove();
	                break;
	            }
	        }

	        if (pc != null) Pickups.Remove(pc);
	    }

	    public static bool IsPickup(int entity)
	    {
	        return Pickups.Any(pickup => pickup.ObjectHandle == entity);
	    }

	    public static void RemoveEntity(int handle)
		{
		    var entity = handle != 0 ? Compat.Ent(handle) : null;
		    if (entity != null)
		        ReleaseModel(entity.Model);

	        if (IsPickup(handle))
	        {
	            var ourPickup = GetPickup(handle);
	            if (Pickups.Contains(ourPickup)) Pickups.Remove(ourPickup);
                ourPickup.Remove();
	        }
	        else
	        {
	            entity?.Delete();
	        }
	        if (Peds.Contains(handle)) Peds.Remove(handle);
			if (Vehicles.Contains(handle)) Vehicles.Remove(handle);
			if (StreamedInHandles.Contains(handle)) StreamedInHandles.Remove(handle);
			Anchors.Remove(handle);
			SharedOverrides.Remove(handle);
			Intangible.Remove(handle);
		}

		public static void RemoveAll()
		{
			StreamedInHandles.ForEach(i => Compat.Ent(i)?.Delete());
			StreamedInHandles.Clear();
			// Nothing to delete for these: they are the part of the map that is not in the world.
			StreamedOut.Clear();
			Anchors.Clear();
			StaticProps.Clear();
			SharedOverrides.Clear();
			Intangible.Clear();
			Vehicles.ForEach(v => Compat.Ent(v)?.Delete());
			Peds.ForEach(v => Compat.Ent(v)?.Delete());
            Pickups.ForEach(p => p.Remove());
			Vehicles.Clear();
			Peds.Clear();
            Pickups.Clear();
		}

		// --- The game's own objects, taken out of the map --------------------------------------------
		//
		// CREATE_MODEL_HIDE tells the streamer once and it stays said, unlike deleting, which only lasts
		// until the area next streams in. It is also the only version of this a published map can hand to
		// every client. So the list below is a record of what has been hidden, not a per-frame to-do list.

		private static readonly List<MapObject> _removedObjects = new List<MapObject>();

		/// <summary>
		/// The game's own objects the map takes out of the world. Read it freely — to count it, to save it —
		/// but change it through <see cref="RemoveWorldObject"/> and the methods below, which also tell the
		/// game about it.
		/// </summary>
		public static IReadOnlyList<MapObject> RemovedObjects => _removedObjects;

		/// <summary>
		/// How near a hidden object has to be to the spot it was hidden at. The SP build searched a metre
		/// around it for the object to delete; this is the same metre.
		/// </summary>
		private const float HideRadius = 1f;

		/// <summary>Takes one of the game's own objects out of the world, and records that the map does so.</summary>
		public static void RemoveWorldObject(MapObject o)
		{
			if (o == null) return;
			_removedObjects.Add(o);
			Natives.HideModel(o.Position, HideRadius, o.Hash, false);
		}

		/// <summary>Puts one back, and stops the map from taking it out again.</summary>
		public static void RestoreWorldObject(MapObject o)
		{
			if (o == null) return;
			_removedObjects.Remove(o);
			Natives.UnhideModel(o.Position, HideRadius, o.Hash);
		}

		/// <summary>Puts every one of them back. What closing a map does.</summary>
		public static void ClearRemovedObjects()
		{
			foreach (var o in _removedObjects)
				Natives.UnhideModel(o.Position, HideRadius, o.Hash);
			_removedObjects.Clear();
		}

		/// <summary>
		/// Says it all again to the streamer, for a map that arrived with removals already in it — a file
		/// being loaded, or a session being restored.
		/// </summary>
		public static void ReapplyRemovedObjects()
		{
			foreach (var o in _removedObjects)
				Natives.HideModel(o.Position, HideRadius, o.Hash, false);
		}

		/// <summary>
		/// The difference between the spot an entity was spawned to stand at and the spot it then says it is
		/// standing at.
		///
		/// The two are not always the same. A ped is spawned by its feet and answers about its middle, and the
		/// metre between the two is only exactly a metre for a grown human — a child, a dog or a bird answers
		/// from somewhere slightly else. Write down what it answers and spawn it from that again and it moves
		/// by that difference every time, so an afternoon of flying out to a map and back would slowly bury it
		/// in the ground or leave it hovering. The same goes, more coarsely, for anything spawned in the air
		/// that comes to rest a little lower than it was put.
		///
		/// So the difference is measured once, when the entity is put into the world, and taken back off
		/// whenever the map is read out of the world again. It is a property of the model rather than of the
		/// placement, which is why it can be taken off wherever the entity has got to since: an entity the
		/// player has moved is written down at its new spot, corrected the same way.
		/// </summary>
		private static readonly Dictionary<int, Vector3> Anchors = new Dictionary<int, Vector3>();

		/// <summary>
		/// How far the game may be seen to move something on the way in before the measurement is written off
		/// as something other than the offset this is here to undo, and ignored. Nothing legitimate is out by
		/// more than the metre a ped is spawned by.
		/// </summary>
		private const float MaxAnchorCorrection = 2f;

		/// <summary>
		/// Takes an object into the map without putting it into the world, for a map loaded from a file with
		/// most of itself somewhere the player is not. It is part of the map from this moment — counted, saved,
		/// and listed in the entity menu — and streaming spawns it when the player goes to it.
		/// </summary>
		public static StreamedObject AddStreamedOut(MapObject o)
		{
			var record = new StreamedObject { Object = o, Uid = _streamUids++ };
			StreamedOut.Add(record);
			return record;
		}

		/// <summary>
		/// Takes one entity of the map out of the world, keeping everything needed to put it back. The map is
		/// no shorter for it: the snapshot stands in for the entity in the count, in what gets saved, and in
		/// the entity menu, until <see cref="StreamedIn"/> hands the map back a real one.
		/// </summary>
		public static StreamedObject StreamOut(Entity entity)
		{
			if (entity == null) return null;

			var handle = entity.Handle;
			var snapshot = Snapshot(handle);
			if (snapshot == null) return null;

			var record = AddStreamedOut(snapshot);

			// Everything these were holding for this entity has just been written into the snapshot, and the
			// handle they are keyed by is about to be handed back to the game and given out to something else.
			Identifications.Remove(handle);
			ActiveScenarios.Remove(handle);
			ActiveRelationships.Remove(handle);
			ActiveWeapons.Remove(handle);
			Doors.Remove(handle);
			ActiveSirens.Remove(handle);
			StaticProps.Remove(handle);
			StreamedInHandles.Remove(handle);
			Vehicles.Remove(handle);
			Peds.Remove(handle);
			Anchors.Remove(handle);
			SharedOverrides.Remove(handle);
			Intangible.Remove(handle);

			ReleaseModel(entity.Model);
			entity.Delete();
			return record;
		}

		/// <summary>
		/// Hands the map back the entity <paramref name="record"/> stood in for. The caller has already spawned
		/// it and registered whatever the snapshot said about it; this is the record being retired.
		/// </summary>
		public static void StreamedIn(StreamedObject record, Entity entity)
		{
			StreamedOut.Remove(record);
			if (entity == null) return;

			Anchor(entity, record.Object.Position);
		}

		/// <summary>
		/// Writes down what the game did to <paramref name="asked"/> on the way in, so that reading the entity
		/// back out gives the spot it was asked to stand at rather than the spot it answers from. See
		/// <see cref="Anchors"/>.
		/// </summary>
		public static void Anchor(Entity entity, Vector3 asked)
		{
			if (entity == null) return;

			var correction = entity.Position - asked;
			if (correction.Length() > MaxAnchorCorrection)
			{
				Anchors.Remove(entity.Handle);
				return;
			}

			Anchors[entity.Handle] = correction;
		}

		/// <summary>The map object one entity of the map would be saved as, or null if it is no longer there.</summary>
		public static MapObject Snapshot(int handle)
		{
			if (Vehicles.Contains(handle)) return SnapshotVehicle(handle);
			if (Peds.Contains(handle)) return SnapshotPed(handle);
			return SnapshotProp(handle);
		}

		private static MapObject SnapshotProp(int handle)
		{
			var prop = Compat.Ent(handle);
			if (prop == null) return null;

			var snapshot = new MapObject()
			{
				Dynamic = !StaticProps.Contains(handle),
				Hash = prop.Model.Hash,
				Position = prop.Position,
				Quaternion = Quaternion.GetEntityQuaternion(prop),
				Rotation = prop.Rotation,
				Type = ObjectTypes.Prop,
				Door = Doors.Contains(handle),
				Shared = SharedOverride(handle),
				Id = (Identifications.ContainsKey(handle) && !string.IsNullOrWhiteSpace(Identifications[handle])) ? Identifications[handle] : null,
			};

			ApplyAnchor(handle, snapshot);
			return snapshot;
		}

		private static MapObject SnapshotVehicle(int handle)
		{
			var veh = Compat.VehicleFrom(handle);
			if (veh == null) return null;

			var snapshot = new MapObject()
			{
				Dynamic = !StaticProps.Contains(handle),
				Hash = veh.Model.Hash,
				Position = veh.Position,
				Quaternion = Quaternion.GetEntityQuaternion(veh),
				Rotation = veh.Rotation,
				Type = ObjectTypes.Vehicle,
				Shared = SharedOverride(handle),
				Id = (Identifications.ContainsKey(handle) && !string.IsNullOrWhiteSpace(Identifications[handle])) ? Identifications[handle] : null,
				SirensActive = ActiveSirens.Contains(handle),
				PrimaryColor = (int)veh.Mods.PrimaryColor,
				SecondaryColor = (int)veh.Mods.SecondaryColor,
				Livery = Natives.GetLivery(handle),
			};

			ApplyAnchor(handle, snapshot);
			return snapshot;
		}

		private static MapObject SnapshotPed(int handle)
		{
			var ped = Compat.PedFrom(handle);
			if (ped == null) return null;

			var snapshot = new MapObject()
			{
				Dynamic = !StaticProps.Contains(handle),
				Hash = ped.Model.Hash,
				Position = ped.Position,
				Quaternion = Quaternion.GetEntityQuaternion(ped),
				Rotation = ped.Rotation,
				Type = ObjectTypes.Ped,
				Shared = SharedOverride(handle),
				Action = ActiveScenarios.ContainsKey(handle) ? ActiveScenarios[handle] : "None",
				Id = (Identifications.ContainsKey(handle) && !string.IsNullOrWhiteSpace(Identifications[handle])) ? Identifications[handle] : null,
				Relationship = ActiveRelationships.ContainsKey(handle) ? ActiveRelationships[handle] : null,
				Weapon = ActiveWeapons.ContainsKey(handle) ? ActiveWeapons[handle] : (WeaponHash?)null,
				Drawables = PedComponents.ReadDrawables(ped),
				Textures = PedComponents.ReadTextures(ped),
			};

			ApplyAnchor(handle, snapshot);
			return snapshot;
		}

		/// <summary>What the author said about who owns this object once published, or null if they never said.</summary>
		private static bool? SharedOverride(int handle)
		{
			bool shared;
			return SharedOverrides.TryGetValue(handle, out shared) ? shared : (bool?) null;
		}

		/// <summary>Takes the offset measured on the way in back off the position. See <see cref="Anchors"/>.</summary>
		private static void ApplyAnchor(int handle, MapObject snapshot)
		{
			Vector3 correction;
			if (!Anchors.TryGetValue(handle, out correction)) return;

			snapshot.Position -= correction;
		}

		public static MapObject[] GetAllEntities()
		{
			var outList = new List<MapObject>();

			foreach (int handle in StreamedInHandles)
			{
				var snapshot = SnapshotProp(handle);
				if (snapshot != null) outList.Add(snapshot);
			}

			// The map is saved whole whether or not all of it is standing. What was streamed out is written
			// from the snapshot taken on the way out.
			foreach (var streamed in StreamedOut)
				outList.Add(streamed.Object);

			foreach (int v in Vehicles)
			{
				var snapshot = SnapshotVehicle(v);
				if (snapshot != null) outList.Add(snapshot);
			}

			foreach (int v in Peds)
			{
				var snapshot = SnapshotPed(v);
				if (snapshot != null) outList.Add(snapshot);
			}

			foreach (DynamicPickup p in Pickups)
			{
				var pickupObject = Compat.Ent(p.ObjectHandle);
				outList.Add(new MapObject()
				{
					Dynamic = p.Dynamic,
					Hash = p.PickupHash,
					Position = p.RealPosition,
					Quaternion = pickupObject != null ? Quaternion.GetEntityQuaternion(pickupObject) : new Quaternion(),
					Rotation = pickupObject?.Rotation ?? new Vector3(),
					Type = ObjectTypes.Pickup,
					Amount = p.Amount,
					RespawnTimer = p.Timeout,
					Flag = p.Flag,
				});
			}

			return outList.ToArray();
		}

		public static int[] GetAllHandles()
		{
			List<int> outHandles = new List<int>();
			outHandles.AddRange(StreamedInHandles);
			outHandles.AddRange(Vehicles);
			outHandles.AddRange(Peds);
            outHandles.AddRange(Pickups.Select(p => p.ObjectHandle));
			return outHandles.ToArray();
		}

	    private static bool _justTeleported;

		/// <summary>
		/// Called once a frame by the editor.
		///
		/// <paramref name="isInFreecam"/> is passed in rather than read off the editor, as the SP build did,
		/// so that the map and its spawning do not depend on the editor class — the two are ported separately.
		/// </summary>
		public static void Tick(bool isInFreecam)
		{
            foreach (Marker marker in Markers)
			{
                if (!marker.OnlyVisibleInEditor || marker.OnlyVisibleInEditor && isInFreecam)
				Function.Call(Hash.DRAW_MARKER, (int) marker.Type, marker.Position.X, marker.Position.Y, marker.Position.Z, 0f, 0f, 0f,
				 marker.Rotation.X, marker.Rotation.Y, marker.Rotation.Z, marker.Scale.X, marker.Scale.Y, marker.Scale.Z,
				 marker.Red, marker.Green, marker.Blue, marker.Alpha, marker.BobUpAndDown, marker.RotateToCamera, 2, false, false, false);

			    if (marker.TeleportTarget.HasValue && Game.Player.Character.IsInRangeOf(marker.Position, Math.Max(2f, marker.Scale.X)) && !_justTeleported)
			    {
			        if (!Game.Player.Character.IsInVehicle())
			            Game.Player.Character.Position = marker.TeleportTarget.Value;
			        else
			            Game.Player.Character.CurrentVehicle.Position = marker.TeleportTarget.Value;
			        _justTeleported = true;
			    }
			}

		    if (_justTeleported)
		    {
		        var isInRangeOfAny = Markers.Any(m =>
		        {
		            if (!m.TeleportTarget.HasValue) return false;
		            return Game.Player.Character.IsInRangeOf(m.Position, Math.Max(2f, m.Scale.X));
		        });

		        if (!isInRangeOfAny) _justTeleported = false;
		    }

		    foreach (DynamicPickup pickup in Pickups)
		    {
		        // Respawning a pickup takes frames, so this cannot be waited for from inside a frame. It is
		        // started and left to finish on its own; Update returns straight away while one is in flight.
		        Log.Guard("PropStreamer.Tick pickup", pickup.Update);
		    }
		}
	}
}
