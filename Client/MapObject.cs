using CitizenFX.Core;

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

		/// <summary>Written as an XML attribute rather than an element by <see cref="MapSerializer"/>.</summary>
		public string Id;

		/// <summary>
		/// What this object is called inside a co-editing session, or 0 outside one.
		///
		/// Runtime only, and deliberately not part of any map format: it names one object for as long as
		/// several people are looking at it together, and means nothing the moment the session ends. A map
		/// saved out of a session is the same document it would have been alone. See <see cref="Collab"/>.
		/// </summary>
		public int Uid;

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

	public enum ObjectTypes
	{
		Prop,
		Vehicle,
		Ped,
		Marker,
	}
}