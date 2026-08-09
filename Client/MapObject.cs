using System;
using System.Threading.Tasks;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using MapEditor.Platform;

namespace MapEditor
{
	public class MapObject
	{
		public ObjectTypes Type;
		public Vector3 Position;
		public Vector3 Rotation;
		public int Hash;
		public bool Dynamic;
		public Quaternion Quaternion;

        // Prop stuff
	    public bool Door;

		// Ped stuff
		public string Action;
		public string Relationship;
		public WeaponHash? Weapon;

		/// <summary>The piece and the texture worn in each of the ped's twelve slots. See <see cref="PedComponents"/>.</summary>
		public int[] Drawables;
		public int[] Textures;

		// Vehicle stuff
		public bool SirensActive;
	    public int PrimaryColor;
	    public int SecondaryColor;

	    /// <summary>
	    /// The vehicle's livery, or -1 for none. Maps written before liveries were kept say nothing about
	    /// them, and "none" is what those vehicles were saved wearing.
	    /// </summary>
	    public int Livery = -1;

        // Pickup stuff
	    public int Amount;
	    public int RespawnTimer;
	    public int Flag;

		/// <summary>Written as an XML attribute rather than an element by <see cref="MapSerializer"/>.</summary>
		public string Id;

		/// <summary>
		/// Whether the server owns this object once the map is published, when the author has said. Null —
		/// which is what every map written before this existed says, and what the editor leaves it as unless
		/// the checkbox is touched — means "decide by the rule", see <see cref="Platform.SharedObjects"/>.
		///
		/// It says nothing at all about the map being edited: a draft is a hundred percent local on every
		/// client, and this only starts to matter at the moment the map goes into everybody's world.
		/// </summary>
		public bool? Shared;

		/// <summary>
		/// Whether a published copy of this object has to be created by the server rather than by every
		/// client for itself. The author's answer if they gave one, the rule otherwise.
		/// </summary>
		public bool NeedsServer
		{
			get
			{
				return Platform.SharedObjects.NeedsServer(Type.ToString(), Shared, Dynamic, Door, Action,
					Weapon.HasValue ? Weapon.Value.ToString() : null, Relationship);
			}
		}

		/// <summary>What <see cref="NeedsServer"/> would say with nothing written in <see cref="Shared"/>.</summary>
		public bool NeedsServerByRule
		{
			get
			{
				return Platform.SharedObjects.ByRule(Type.ToString(), Dynamic, Door, Action,
					Weapon.HasValue ? Weapon.Value.ToString() : null, Relationship);
			}
		}

		// Which fields a map file carries depends on the object's type. XmlSerializer would normally call
		// these, but it leans on Reflection.Emit, a poor fit for FiveM's Mono sandbox, so MapSerializer
		// writes the XML by hand and calls them itself. The ShouldSerialize* names are kept regardless.
	    public bool ShouldSerializeDoor()
	    {
	        return Type == ObjectTypes.Prop;
	    }

	    public bool ShouldSerializeAction()
	    {
	        return Type == ObjectTypes.Ped;
	    }

	    public bool ShouldSerializeRelationship()
	    {
            return Type == ObjectTypes.Ped;
        }

	    public bool ShouldSerializeWeapon()
	    {
            return Type == ObjectTypes.Ped;
        }

	    public bool ShouldSerializeDrawables()
	    {
            return Type == ObjectTypes.Ped && Drawables != null;
        }

	    public bool ShouldSerializeTextures()
	    {
            return Type == ObjectTypes.Ped && Textures != null;
        }

	    public bool ShouldSerializeSirensActive()
	    {
            return Type == ObjectTypes.Vehicle;
        }

	    public bool ShouldSerializePrimaryColor()
	    {
            return Type == ObjectTypes.Vehicle;
        }

	    public bool ShouldSerializeSecondaryColor()
	    {
            return Type == ObjectTypes.Vehicle;
        }

	    public bool ShouldSerializeLivery()
	    {
            return Type == ObjectTypes.Vehicle;
        }

	    public bool ShouldSerializeAmount()
	    {
            return Type == ObjectTypes.Pickup;
        }

	    public bool ShouldSerializeRespawnTimer()
	    {
            return Type == ObjectTypes.Pickup;
        }

	    public bool ShouldSerializeFlag()
	    {
            return Type == ObjectTypes.Pickup;
        }

	    /// <summary>
	    /// Only written when the author overrode the rule, and only for the types that can become an entity.
	    /// An absent field is the difference between "they wanted this one local" and "nobody said" — and
	    /// the second must keep following the rule as the rule changes.
	    /// </summary>
	    public bool ShouldSerializeShared()
	    {
	        return Shared.HasValue && Type != ObjectTypes.Marker;
	    }
	}

    public class PedDrawables
    {
        public int[] Drawables;
        public int[] Textures;
    }

    public class DynamicPickup
    {
        public DynamicPickup(int handle)
        {
            PickupHandle = handle;
            IsInRange = handle != -1;
        }

        private int _timeout = -1;
        private bool _dynamic = true;

        public bool IsInRange { get; set; }

        public bool Dynamic
        {
            get { return _dynamic; }
            set
            {
                _dynamic = value;
                var obj = Compat.Ent(ObjectHandle);
                if (obj != null)
                    obj.IsPositionFrozen = !value;
            }
        }

        public string PickupName
        {
            get
            {
                if (Enum.IsDefined(typeof (ObjectDatabase.PickupHash), PickupHash))
                    return ((ObjectDatabase.PickupHash) PickupHash).ToString();
                return "";
            }
        }

        public int UID { get; set; }
        public int PickupHash { get; set; }
        public int PickupHandle { get; set; }
        public int Amount { get; set; }
        public int Flag { get; set; }

        public bool PickedUp { get; set; }

        /// <summary>
        /// When the pickup was last taken, as a game-timer reading rather than a wall clock — see
        /// <see cref="Clock"/>. Runtime state, never serialized: a saved map holds the respawn
        /// <see cref="Timeout"/>, not where a previous session's countdown had got to.
        /// </summary>
        public int LastPickup { get; set; }
        
        public int ObjectHandle => Function.Call<int>(Hash.GET_PICKUP_OBJECT, PickupHandle);
        //public Vector3 Position => Function.Call<Vector3>(Hash.GET_PICKUP_COORDS, PickupHandle);


        public void UpdatePos()
        {
            RealPosition = Position;
        }

        public Vector3 RealPosition { get; set; }

        public Vector3 Position
        {
            get { return Compat.Ent(ObjectHandle)?.Position ?? RealPosition; }
            set
            {
                var obj = Compat.Ent(ObjectHandle);
                if (obj != null)
                    obj.Position = value;
                RealPosition = value;
            }
        }

        public Task SetPickupHash(int newHash)
        {
            PickupHash = newHash;
            return ReloadPickup();
        }

        public Task SetPickupFlag(int newFlag)
        {
            Flag = newFlag;
            return ReloadPickup();
        }

        public Task SetAmount(int newAmount)
        {
            Amount = newAmount;
            return ReloadPickup();
        }

        /// <summary>
        /// CREATE_PICKUP_ROTATE is a networked native: on a server the pickup is replicated and counts
        /// against the entity budget, unlike everything else this editor spawns.
        /// </summary>
        private async Task ReloadPickup()
        {
            var newPickup = Function.Call<int>(Hash.CREATE_PICKUP_ROTATE, PickupHash, RealPosition.X, RealPosition.Y, RealPosition.Z, 0, 0, 0, Flag, Amount, 0, false, 0);

            var tmpDyn = new DynamicPickup(newPickup);

            var start = 0;
            while (tmpDyn.ObjectHandle == -1 && start < 20)
            {
                start++;
                await Frame.Next();
                tmpDyn.Position = RealPosition;
            }

            tmpDyn.Dynamic = Dynamic;
            tmpDyn.Timeout = Timeout;

            var newObject = Compat.Ent(tmpDyn.ObjectHandle);
            if (newObject != null)
                newObject.IsPersistent = true;

            if (PickupHandle != -1)
                Remove();


            PickupHandle = newPickup;
        }

        public int Timeout
        {
            get { return _timeout; }
            set
            {
                _timeout = value;
                //Function.Call(Hash.SET_PICKUP_REGENERATION_TIME, PickupHandle, value);
            }
        }

        public bool PickupObjectExists => Function.Call<bool>(Hash.DOES_PICKUP_OBJECT_EXIST, PickupHandle);

        public bool PickupExists => Function.Call<bool>(Hash.DOES_PICKUP_EXIST, PickupHandle);

        public void Remove()
        {
            Function.Call(Hash.REMOVE_PICKUP, PickupHandle);
        }
        
        private bool _updating;

        /// <summary>
        /// Called once a frame. Where the SP build ran this on its fiber and could hold the frame until a
        /// pickup had finished respawning, here the wait spans frames and the next frame's call would arrive
        /// in the middle of it — so a call made while one is already in flight does nothing.
        /// </summary>
        public async Task Update()
        {
            if (_updating) return;
            _updating = true;
            try
            {
                await UpdateCore();
            }
            finally
            {
                _updating = false;
            }
        }

        private async Task UpdateCore()
        {
            var inRange = Game.Player.Character.IsInRangeOf(RealPosition, 20f);

            if (inRange && PickupHandle == -1)
            {
                await ReloadPickup();
            }

            if (PickupHandle != -1)
            {
                Position = RealPosition;

                if (!inRange) return;

                if (!PickupObjectExists && !PickedUp)
                {
                    PickedUp = true;
                    LastPickup = Clock.Milliseconds;
                    Remove();
                }

                if (PickedUp && Clock.Since(LastPickup) > Timeout * 1000 && Timeout != 0)
                {
                    PickedUp = false;
                    await ReloadPickup();
                }
            }
        }
    }

	public enum ObjectTypes
	{
		Prop,
		Vehicle,
		Ped,
		Marker,
        Pickup,
	}
}