using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using CitizenFX.Core;
using MapEditor.Platform;

namespace MapEditor
{
	/// <summary>
	/// Reads and writes maps. Every format is a string in and a string out: a FiveM client cannot open a
	/// file, so where the text comes from and where it goes is <see cref="MapStore"/>'s business, not this
	/// class's.
	///
	/// Three things changed beyond the loss of file I/O:
	///
	/// * <b>No XmlSerializer.</b> It builds its readers and writers with Reflection.Emit, which the Mono
	///   sandbox is a poor host for, and the failure would land on somebody else's server rather than here.
	///   The XML is read by hand instead, element for element as XmlSerializer laid it out — the shape was
	///   taken from a run of the SP build's own serializer. <see cref="MapObject"/>'s ShouldSerializeXxx
	///   methods are still what decides which members a given object type carries.
	/// * <b>Format.CSharpCode is gone, and Format.Json and Format.FiveMResource replace it.</b> A .cs file
	///   is only a map to somebody running ScriptHookVDotNet. Json is the native format now — compact, and
	///   small enough to sit in one net event's worth of chunks — and <see cref="BuildResource"/> is how a
	///   finished map leaves the editor as something a server can run.
	/// * <b>Only the native format is written.</b> Saving as the SP editor's XML, as Menyoo, as Simple
	///   Trainer, as Spooner or as a raw listing is gone: those files were only ever of use to somebody with
	///   the single-player build installed, and a FiveM client has nowhere to put one — every one of them
	///   would have gone straight back into this server's storage, which is the one place a .SP00N is no
	///   use at all. Reading them stays, so an old document is still openable; <see cref="Detect"/> is what
	///   works out which one it is.
	/// </summary>
	public class MapSerializer
	{
		public enum Format
		{
			/// <summary>The native format: what the editor saves, autosaves and hands to the server.</summary>
			Json,

			/// <summary>The SP editor's XML. <b>Read only</b> — see the class remarks.</summary>
			NormalXml,

			/// <summary>Read only.</summary>
			SimpleTrainer,

			/// <summary>Read only.</summary>
			SpoonerLegacy,

			/// <summary>Read only.</summary>
			Menyoo,

			/// <summary>
			/// A runnable FiveM resource. Not a single document, so it is not a <see cref="SerializeToString"/>
			/// format at all: see <see cref="BuildResource"/>.
			/// </summary>
			FiveMResource,
		}

		/// <summary>One file of a generated resource. See <see cref="BuildResource"/>.</summary>
		public class ResourceFile
		{
			public ResourceFile(string name, string content)
			{
				Name = name;
				Content = content;
			}

			public string Name { get; private set; }
			public string Content { get; private set; }
		}

		// --- Entry points -------------------------------------------------------------------------------

		public Map DeserializeFromString(string content, Format format)
		{
			if (content == null) throw new ArgumentNullException("content");

			switch (format)
			{
				case Format.Json: return FromJson(content);
				case Format.NormalXml: return FromXml(content);
				case Format.Menyoo: return MenyooCompatibility.Read(content);
				case Format.SimpleTrainer: return FromSimpleTrainer(content);
				case Format.SpoonerLegacy: return FromSpoonerLegacy(content);
				default:
					throw new NotSupportedException(format + " maps cannot be read back in.");
			}
		}

		public string SerializeToString(Map map, Format format)
		{
			if (map == null) throw new ArgumentNullException("map");

			// A prop at the origin never made it into the world — a model that failed to load, or an entity
			// deleted out from under the editor. Saving it would mark the centre of the map for nothing.
			map.Objects.RemoveAll(mo => mo.Position == Vector3.Zero);

			switch (format)
			{
				case Format.Json: return ToJson(map, false);
				case Format.FiveMResource:
					throw new NotSupportedException(
						"A FiveM resource is a folder, not a document: call BuildResource instead.");
				default:
					throw new NotSupportedException(format + " maps can only be read, not written.");
			}
		}

		/// <summary>
		/// What a stored map is, judged by its content.
		///
		/// A file name used to say this: ".xml" was the editor's own format, ".ini" SimpleTrainer's. There
		/// are no file names here — a map is a KVP entry named by the player — so the document has to speak
		/// for itself. Returns null for text that is none of the formats this can read.
		/// </summary>
		public static Format? Detect(string content)
		{
			if (string.IsNullOrEmpty(content)) return null;

			var trimmed = content.TrimStart();
			if (trimmed.Length == 0) return null;

			if (trimmed[0] == '{') return Format.Json;

			if (trimmed[0] == '<')
			{
				// Menyoo and the editor's own XML are told apart by their root element, which the XML
				// declaration and any comments sit in front of.
				if (trimmed.IndexOf("<SpoonerPlacements", StringComparison.OrdinalIgnoreCase) >= 0)
					return Format.Menyoo;
				return Format.NormalXml;
			}

			// Both ini-shaped formats open with a section header; SimpleTrainer's first one is always the
			// player's own position, which Spooner has no equivalent of.
			if (trimmed[0] == '[')
				return trimmed.StartsWith("[Player]", StringComparison.OrdinalIgnoreCase)
					? Format.SimpleTrainer
					: Format.SpoonerLegacy;

			return null;
		}

		// --- The native format: JSON --------------------------------------------------------------------
		//
		// Vectors are three-element arrays rather than objects: a map holds thousands of them, and
		// "position":[1,2,3] against {"x":1,"y":2,"z":3} decides whether an entry fits without chunking.
		// Which members an object carries follows the same rules the XML follows.

		/// <summary>
		/// 2 added the optional <c>shared</c> flag on an object. 3 added <c>lasers</c> and dropped pickups.
		/// Older documents read unchanged: an absent <c>shared</c> means "decide by the rule" there too, an
		/// absent <c>lasers</c> is a map with none, and a <c>Pickup</c> among the objects is skipped — the
		/// editor no longer places them, and every field a pickup carried was about being one.
		/// </summary>
		private const int JsonFormatVersion = 3;

		private static string ToJson(Map map, bool forExport)
		{
			var objects = Json.Array();
			foreach (var o in map.Objects) objects.Add(ObjectToJson(o, forExport));

			var removed = Json.Array();
			foreach (var o in map.RemoveFromWorld) removed.Add(ObjectToJson(o, forExport));

			var markers = Json.Array();
			foreach (var m in map.Markers)
			{
				// A marker the builder put down to line something up is not part of the map they are
				// publishing; in the editor it is the only place it means anything.
				if (forExport && m.OnlyVisibleInEditor) continue;
				markers.Add(MarkerToJson(m));
			}

			var lasers = Json.Array();
			foreach (var l in map.Lasers)
			{
				// The same rule markers follow, for the same reason.
				if (forExport && l.OnlyVisibleInEditor) continue;
				lasers.Add(LaserToJson(l));
			}

			var document = Json.Object()
				.Set("format", "mapeditor-map")
				.Set("version", JsonFormatVersion)
				.Set("objects", objects)
				.Set("removeFromWorld", removed)
				.Set("markers", markers)
				.Set("lasers", lasers);

			if (map.Metadata != null) document.Set("metadata", MetadataToJson(map.Metadata));
			return document.ToJson();
		}

		private static Json ObjectToJson(MapObject o, bool forExport)
		{
			var json = Json.Object()
				.Set("type", o.Type.ToString())
				.Set("hash", o.Hash)
				.Set("position", VectorToJson(o.Position))
				.Set("rotation", VectorToJson(o.Rotation))
				.Set("dynamic", o.Dynamic);

			if (o.Id != null) json.Set("id", o.Id);
			if (o.Quaternion != null)
				json.Set("quaternion", Json.Array()
					.Add(Json.Of(o.Quaternion.X)).Add(Json.Of(o.Quaternion.Y))
					.Add(Json.Of(o.Quaternion.Z)).Add(Json.Of(o.Quaternion.W)));

			if (o.ShouldSerializeDoor()) json.Set("door", o.Door);

			// Only present when the author overrode the rule; see MapObject.ShouldSerializeShared. Left out
			// of the exported resource, which has no server half to honour it.
			if (!forExport && o.ShouldSerializeShared()) json.Set("shared", o.Shared.Value);

			if (o.ShouldSerializeAction() && o.Action != null) json.Set("action", o.Action);
			if (o.ShouldSerializeRelationship() && o.Relationship != null) json.Set("relationship", o.Relationship);
			if (o.ShouldSerializeWeapon() && o.Weapon.HasValue) json.Set("weapon", WeaponToJson(o.Weapon.Value));
			if (o.ShouldSerializeDrawables()) json.Set("drawables", IntsToJson(o.Drawables));
			if (o.ShouldSerializeTextures()) json.Set("textures", IntsToJson(o.Textures));

			// Resolved for the exported resource, which has neither the scenario database nor the weapon
			// names to resolve them with. Written alongside the editor's own members, not in place of them:
			// reopening the map has to give the player back the labels they chose.
			if (forExport && o.Type == ObjectTypes.Ped)
			{
				var scenario = ScenarioToken(o.Action);
				if (scenario != null) json.Set("scenario", scenario);

				// "Pistol" is the name of an enum member, not of a weapon: the game knows it as
				// WEAPON_PISTOL, and only the number the enum holds is the same on both sides.
				if (o.Weapon.HasValue) json.Set("weaponHash", Json.Of((double) (uint) o.Weapon.Value));
			}

			if (o.ShouldSerializeSirensActive()) json.Set("sirens", o.SirensActive);
			if (o.ShouldSerializePrimaryColor()) json.Set("primaryColor", o.PrimaryColor);
			if (o.ShouldSerializeSecondaryColor()) json.Set("secondaryColor", o.SecondaryColor);
			if (o.ShouldSerializeLivery()) json.Set("livery", o.Livery);

			return json;
		}

		/// <summary>
		/// A laser, written out in full — every field, every time, with no ShouldSerialize equivalent.
		///
		/// Unlike a map object, whose members depend on whether it is a prop or a ped, a laser has one shape
		/// and the fields a given pattern does not use are still the builder's: switching a wall back to a
		/// wave has to give them the amplitude they chose the first time. The whole thing is about seventy
		/// bytes, which is a hundredth of what one prop costs.
		/// </summary>
		private static Json LaserToJson(Laser l)
		{
			return Json.Object()
				.Set("pattern", l.Pattern.ToString())
				.Set("position", VectorToJson(l.Position))
				.Set("rotation", VectorToJson(l.Rotation))
				.Set("beamLength", l.BeamLength)
				.Set("width", l.Width)
				.Set("height", l.Height)
				.Set("beamCount", l.BeamCount)
				.Set("density", l.Density.ToString())
				.Set("thickness", l.Thickness)
				.Set("red", l.Red)
				.Set("green", l.Green)
				.Set("blue", l.Blue)
				.Set("alpha", l.Alpha)
				.Set("textured", l.Textured)
				.Set("rhythm", l.Rhythm.ToString())
				.Set("onSeconds", l.OnSeconds)
				.Set("offSeconds", l.OffSeconds)
				.Set("chasePeriod", l.ChasePeriod)
				.Set("chaseOnFraction", l.ChaseOnFraction)
				.Set("amplitude", l.Amplitude)
				.Set("frequency", l.Frequency)
				.Set("speed", l.Speed)
				.Set("dealsDamage", l.DealsDamage)
				.Set("damagePerSecond", l.DamagePerSecond)
				.Set("activationRange", l.ActivationRange)
				.Set("hitRadius", l.HitRadius)
				.Set("onlyVisibleInEditor", l.OnlyVisibleInEditor)
				.Set("id", l.Id);
		}

		private static Json MarkerToJson(Marker m)
		{
			var json = Json.Object()
				.Set("type", (int) m.Type)
				.Set("position", VectorToJson(m.Position))
				.Set("rotation", VectorToJson(m.Rotation))
				.Set("scale", VectorToJson(m.Scale))
				.Set("red", m.Red)
				.Set("green", m.Green)
				.Set("blue", m.Blue)
				.Set("alpha", m.Alpha)
				.Set("bobUpAndDown", m.BobUpAndDown)
				.Set("rotateToCamera", m.RotateToCamera)
				.Set("onlyVisibleInEditor", m.OnlyVisibleInEditor)
				.Set("id", m.Id);

			if (m.TeleportTarget.HasValue) json.Set("teleportTarget", VectorToJson(m.TeleportTarget.Value));
			return json;
		}

		private static Json MetadataToJson(MapMetadata meta)
		{
			var json = Json.Object()
				.Set("creator", meta.Creator)
				.Set("name", meta.Name)
				.Set("description", meta.Description)
				.Set("autoload", meta.Autoload);

			if (meta.LoadingPoint.HasValue) json.Set("loadingPoint", VectorToJson(meta.LoadingPoint.Value));
			if (meta.TeleportPoint.HasValue) json.Set("teleportPoint", VectorToJson(meta.TeleportPoint.Value));
			return json;
		}

		private static Json VectorToJson(Vector3 v)
		{
			return Json.Array().Add(Json.Of(v.X)).Add(Json.Of(v.Y)).Add(Json.Of(v.Z));
		}

		private static Json IntsToJson(int[] values)
		{
			var array = Json.Array();
			if (values != null)
				foreach (var value in values) array.Add(Json.Of(value));
			return array;
		}

		/// <summary>
		/// A weapon by the name SHVDN and CitizenFX both know it by, or by its raw hash where neither does.
		/// Written as a string either way so that reading it back never has to guess which it was.
		/// </summary>
		private static Json WeaponToJson(WeaponHash weapon)
		{
			return Json.Of(Enum.IsDefined(typeof (WeaponHash), weapon)
				? weapon.ToString()
				: ((uint) weapon).ToString(CultureInfo.InvariantCulture));
		}

		private static Map FromJson(string content)
		{
			var document = Json.Parse(content);
			if (document.Kind != JsonKind.Object)
				throw new FormatException("A map document is a JSON object.");

			var map = new Map();

			// Null is what a pickup reads back as, and there is nothing to put in its place: the editor no
			// longer creates them and every field one carried was about being one. See ObjectFromJson.
			foreach (var item in document["objects"].Items)
			{
				var o = ObjectFromJson(item);
				if (o != null) map.Objects.Add(o);
			}

			foreach (var item in document["removeFromWorld"].Items)
			{
				var o = ObjectFromJson(item);
				if (o != null) map.RemoveFromWorld.Add(o);
			}

			foreach (var item in document["markers"].Items) map.Markers.Add(MarkerFromJson(item));
			foreach (var item in document["lasers"].Items) map.Lasers.Add(LaserFromJson(item));

			if (document.Has("metadata")) map.Metadata = MetadataFromJson(document["metadata"]);
			return map;
		}

		/// <summary>
		/// One object of a map, read from the same JSON the whole document is written in.
		///
		/// For the server's shared entities: the server forwards the object verbatim to the client that has
		/// to finish configuring it, and reading it with the map reader is what keeps the two ends from
		/// growing separate ideas of what a field means. See Server/LiveEntities.cs and SharedEntities.
		/// </summary>
		public static MapObject ObjectFromJsonText(string json)
		{
			if (string.IsNullOrEmpty(json)) return null;

			var document = Json.TryParse(json);
			return document == null || document.Kind != JsonKind.Object ? null : ObjectFromJson(document);
		}

		// The same readers and writers, one piece of a map at a time, for a co-editing session — where a
		// change travels as the object it is about rather than as a document. Nothing here is a second
		// implementation of anything: a session's crate is written by the same code that writes a saved
		// crate and read by the same code that reads one, so the two can never grow apart. See Collab.

		/// <summary>One object of a map, in the form the map file holds it in.</summary>
		public static Json ObjectJson(MapObject o)
		{
			return ObjectToJson(o, false);
		}

		/// <summary>One object of a map, read back. Null for anything that is not one.</summary>
		public static MapObject ObjectFrom(Json json)
		{
			return json == null || json.Kind != JsonKind.Object ? null : ObjectFromJson(json);
		}

		public static Json MarkerJson(Marker m)
		{
			return MarkerToJson(m);
		}

		public static Marker MarkerFrom(Json json)
		{
			return json == null || json.Kind != JsonKind.Object ? null : MarkerFromJson(json);
		}

		public static Json LaserJson(Laser l)
		{
			return LaserToJson(l);
		}

		public static Laser LaserFrom(Json json)
		{
			return json == null || json.Kind != JsonKind.Object ? null : LaserFromJson(json);
		}

		public static Json MetadataJson(MapMetadata meta)
		{
			return MetadataToJson(meta);
		}

		public static MapMetadata MetadataFrom(Json json)
		{
			return json == null || json.Kind != JsonKind.Object ? null : MetadataFromJson(json);
		}

		private static MapObject ObjectFromJson(Json json)
		{
			var typeName = json["type"].AsString("Prop");

			// A map written while the editor still placed pickups. Null rather than a prop wearing a pickup's
			// model: the game's pickup models are not props, and one would stand there as a floating oddity
			// nobody put down. What replaced them is Laser, which no old document can have.
			if (string.Equals(typeName, "Pickup", StringComparison.OrdinalIgnoreCase)) return null;

			var o = new MapObject();

			ObjectTypes type;
			if (Enum.TryParse(typeName, out type)) o.Type = type;

			o.Hash = json["hash"].AsInt(0);
			o.Position = VectorFromJson(json["position"]);
			o.Rotation = VectorFromJson(json["rotation"]);
			o.Dynamic = json["dynamic"].AsBool(false);
			o.Id = json["id"].AsString(null);
			o.Door = json["door"].AsBool(false);

			// Absent stays absent: "nobody said" has to survive a round trip, or the first save would freeze
			// every object at whatever the rule happened to answer that day.
			if (json.Has("shared")) o.Shared = json["shared"].AsBool(false);

			var quaternion = json["quaternion"];
			if (quaternion.Kind == JsonKind.Array && quaternion.Count >= 4)
				o.Quaternion = new Quaternion
				{
					X = quaternion[0].AsFloat(0f),
					Y = quaternion[1].AsFloat(0f),
					Z = quaternion[2].AsFloat(0f),
					W = quaternion[3].AsFloat(0f),
				};

			o.Action = json["action"].AsString(null);
			o.Relationship = json["relationship"].AsString(null);
			o.Weapon = WeaponFromString(json["weapon"].AsString(null));
			o.Drawables = IntsFromJson(json["drawables"]);
			o.Textures = IntsFromJson(json["textures"]);

			o.SirensActive = json["sirens"].AsBool(false);
			o.PrimaryColor = json["primaryColor"].AsInt(0);
			o.SecondaryColor = json["secondaryColor"].AsInt(0);
			// A map written before liveries were kept says nothing about them, and -1 is "none".
			o.Livery = json["livery"].AsInt(-1);

			return o;
		}

		/// <summary>
		/// A laser, read back. Every default is the one <see cref="Laser"/>'s field carries, so a document
		/// written by a future editor that has one more knob still opens with the knob it does not know
		/// about set to something sensible rather than to zero.
		/// </summary>
		private static Laser LaserFromJson(Json json)
		{
			var l = new Laser
			{
				Position = VectorFromJson(json["position"]),
				Rotation = VectorFromJson(json["rotation"]),
				BeamLength = json["beamLength"].AsFloat(8f),
				Width = json["width"].AsFloat(6f),
				Height = json["height"].AsFloat(3f),
				BeamCount = json["beamCount"].AsInt(8),
				Thickness = json["thickness"].AsFloat(0.03f),
				Red = json["red"].AsInt(255),
				Green = json["green"].AsInt(40),
				Blue = json["blue"].AsInt(40),
				Alpha = json["alpha"].AsInt(255),
				Textured = json["textured"].AsBool(true),
				OnSeconds = json["onSeconds"].AsFloat(1.5f),
				OffSeconds = json["offSeconds"].AsFloat(0.5f),
				ChasePeriod = json["chasePeriod"].AsFloat(3f),
				ChaseOnFraction = json["chaseOnFraction"].AsFloat(0.5f),
				Amplitude = json["amplitude"].AsFloat(1.5f),
				Frequency = json["frequency"].AsFloat(0.6f),
				Speed = json["speed"].AsFloat(1f),
				DealsDamage = json["dealsDamage"].AsBool(true),
				DamagePerSecond = json["damagePerSecond"].AsFloat(250f),
				ActivationRange = json["activationRange"].AsFloat(60f),
				HitRadius = json["hitRadius"].AsFloat(0.35f),
				OnlyVisibleInEditor = json["onlyVisibleInEditor"].AsBool(false),
				Id = json["id"].AsInt(0),
			};

			// Written by name rather than by number, so that inserting a pattern into the enum cannot silently
			// turn every wall in every saved map into a wave. An unknown name keeps the field's default.
			LaserPattern pattern;
			if (Enum.TryParse(json["pattern"].AsString(""), out pattern)) l.Pattern = pattern;

			LaserDensity density;
			if (Enum.TryParse(json["density"].AsString(""), out density)) l.Density = density;

			LaserRhythm rhythm;
			if (Enum.TryParse(json["rhythm"].AsString(""), out rhythm)) l.Rhythm = rhythm;

			return l;
		}

		private static Marker MarkerFromJson(Json json)
		{
			var m = new Marker
			{
				Type = (MarkerType) json["type"].AsInt(0),
				Position = VectorFromJson(json["position"]),
				Rotation = VectorFromJson(json["rotation"]),
				Scale = VectorFromJson(json["scale"]),
				Red = json["red"].AsInt(255),
				Green = json["green"].AsInt(255),
				Blue = json["blue"].AsInt(255),
				Alpha = json["alpha"].AsInt(255),
				BobUpAndDown = json["bobUpAndDown"].AsBool(false),
				RotateToCamera = json["rotateToCamera"].AsBool(false),
				OnlyVisibleInEditor = json["onlyVisibleInEditor"].AsBool(false),
				Id = json["id"].AsInt(0),
			};

			if (json.Has("teleportTarget")) m.TeleportTarget = VectorFromJson(json["teleportTarget"]);
			return m;
		}

		private static MapMetadata MetadataFromJson(Json json)
		{
			var meta = new MapMetadata
			{
				Creator = json["creator"].AsString(""),
				Name = json["name"].AsString(null),
				Description = json["description"].AsString(""),
				Autoload = json["autoload"].AsBool(false),
			};

			if (json.Has("loadingPoint")) meta.LoadingPoint = VectorFromJson(json["loadingPoint"]);
			if (json.Has("teleportPoint")) meta.TeleportPoint = VectorFromJson(json["teleportPoint"]);
			return meta;
		}

		private static Vector3 VectorFromJson(Json json)
		{
			if (json.Kind != JsonKind.Array || json.Count < 3) return Vector3.Zero;
			return new Vector3(json[0].AsFloat(0f), json[1].AsFloat(0f), json[2].AsFloat(0f));
		}

		private static int[] IntsFromJson(Json json)
		{
			if (json.Kind != JsonKind.Array) return null;

			var values = new int[json.Count];
			for (int i = 0; i < values.Length; i++) values[i] = json[i].AsInt(0);
			return values;
		}

		// --- The SP editor's format: XML (read only) ------------------------------------------------------

		private static Map FromXml(string content)
		{
			var root = XDocument.Parse(content).Root;
			if (root == null || root.Name.LocalName != "Map")
				throw new FormatException("A Map Editor XML map has <Map> at its root.");

			var map = new Map();

			// Nulls are the pickups an old document has in it; see ObjectFromXml and ObjectFromJson.
			var objects = root.Element("Objects");
			if (objects != null)
				foreach (var element in objects.Elements("MapObject"))
				{
					var o = ObjectFromXml(element);
					if (o != null) map.Objects.Add(o);
				}

			var removed = root.Element("RemoveFromWorld");
			if (removed != null)
				foreach (var element in removed.Elements("MapObject"))
				{
					var o = ObjectFromXml(element);
					if (o != null) map.RemoveFromWorld.Add(o);
				}

			var markers = root.Element("Markers");
			if (markers != null)
				foreach (var element in markers.Elements("Marker")) map.Markers.Add(MarkerFromXml(element));

			var metadata = root.Element("Metadata");
			if (metadata != null) map.Metadata = MetadataFromXml(metadata);

			return map;
		}

		private static MapObject ObjectFromXml(XElement element)
		{
			var typeName = Xml.Text(element, "Type") ?? "Prop";
			if (string.Equals(typeName, "Pickup", StringComparison.OrdinalIgnoreCase)) return null;

			var o = new MapObject();

			ObjectTypes type;
			if (Enum.TryParse(typeName, out type)) o.Type = type;

			var id = element.Attribute("Id");
			if (id != null) o.Id = id.Value;

			o.Position = VectorFromXml(element.Element("Position")) ?? Vector3.Zero;
			o.Rotation = VectorFromXml(element.Element("Rotation")) ?? Vector3.Zero;
			o.Hash = Xml.Int(element, "Hash", 0);
			o.Dynamic = Xml.Bool(element, "Dynamic", false);
			o.Door = Xml.Bool(element, "Door", false);

			var quaternion = element.Element("Quaternion");
			if (quaternion != null && !Xml.IsNil(quaternion))
				o.Quaternion = new Quaternion
				{
					X = Xml.Float(quaternion, "X", 0f),
					Y = Xml.Float(quaternion, "Y", 0f),
					Z = Xml.Float(quaternion, "Z", 0f),
					W = Xml.Float(quaternion, "W", 0f),
				};

			o.Action = Xml.Text(element, "Action");
			o.Relationship = Xml.Text(element, "Relationship");
			o.Weapon = WeaponFromString(Xml.Text(element, "Weapon"));
			o.Drawables = IntsFromXml(element.Element("Drawables"));
			o.Textures = IntsFromXml(element.Element("Textures"));

			o.SirensActive = Xml.Bool(element, "SirensActive", false);
			o.PrimaryColor = Xml.Int(element, "PrimaryColor", 0);
			o.SecondaryColor = Xml.Int(element, "SecondaryColor", 0);
			o.Livery = Xml.Int(element, "Livery", -1);

			return o;
		}

		private static Marker MarkerFromXml(XElement element)
		{
			var m = new Marker
			{
				Position = VectorFromXml(element.Element("Position")) ?? Vector3.Zero,
				Rotation = VectorFromXml(element.Element("Rotation")) ?? Vector3.Zero,
				Scale = VectorFromXml(element.Element("Scale")) ?? Vector3.Zero,
				TeleportTarget = VectorFromXml(element.Element("TeleportTarget")),
				Red = Xml.Int(element, "Red", 255),
				Green = Xml.Int(element, "Green", 255),
				Blue = Xml.Int(element, "Blue", 255),
				Alpha = Xml.Int(element, "Alpha", 255),
				BobUpAndDown = Xml.Bool(element, "BobUpAndDown", false),
				RotateToCamera = Xml.Bool(element, "RotateToCamera", false),
				OnlyVisibleInEditor = Xml.Bool(element, "OnlyVisibleInEditor", false),
				Id = Xml.Int(element, "Id", 0),
			};

			// SHVDN and CitizenFX name the marker types themselves, and the two lists were written
			// independently: a name one of them does not have falls back to the number, which both agree on.
			var typeText = Xml.Text(element, "Type");
			MarkerType markerType;
			int markerNumber;
			if (Enum.TryParse(typeText ?? "", out markerType)) m.Type = markerType;
			else if (int.TryParse(typeText ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out markerNumber))
				m.Type = (MarkerType) markerNumber;

			return m;
		}

		private static MapMetadata MetadataFromXml(XElement element)
		{
			return new MapMetadata
			{
				Creator = Xml.Text(element, "Creator") ?? "",
				Name = Xml.Text(element, "Name"),
				Description = Xml.Text(element, "Description") ?? "",
				Autoload = Xml.Bool(element, "Autoload", false),
				LoadingPoint = VectorFromXml(element.Element("LoadingPoint")),
				TeleportPoint = VectorFromXml(element.Element("TeleportPoint")),
			};
		}

		private static Vector3? VectorFromXml(XElement element)
		{
			if (element == null || Xml.IsNil(element)) return null;
			return new Vector3(Xml.Float(element, "X", 0f), Xml.Float(element, "Y", 0f),
				Xml.Float(element, "Z", 0f));
		}

		private static int[] IntsFromXml(XElement element)
		{
			if (element == null || Xml.IsNil(element)) return null;

			var values = new List<int>();
			foreach (var child in element.Elements("int"))
			{
				int value;
				if (int.TryParse(child.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
					values.Add(value);
			}
			return values.ToArray();
		}

		/// <summary>A weapon written either by name or as a raw hash, as both this and the XML writer emit.</summary>
		private static WeaponHash? WeaponFromString(string text)
		{
			if (string.IsNullOrEmpty(text)) return null;

			WeaponHash named;
			if (Enum.TryParse(text, out named) && Enum.IsDefined(typeof (WeaponHash), named)) return named;

			uint raw;
			if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out raw))
				return (WeaponHash) raw;

			return null;
		}

		// --- Trainer formats ----------------------------------------------------------------------------

		private static Map FromSimpleTrainer(string content)
		{
			var map = new Map();
			var section = "";
			var fields = new Dictionary<string, string>();

			foreach (var line in Lines(content))
			{
				if (line.StartsWith("[") && line.EndsWith("]"))
				{
					var previous = section;
					section = line;
					if (previous == "" || previous == "[Player]") continue;

					var placed = SimpleTrainerObject(fields);
					if (placed != null) map.Objects.Add(placed);
					fields = new Dictionary<string, string>();
					continue;
				}

				if (section == "[Player]") continue;

				var split = line.Split('=');
				if (split.Length >= 2) fields[split[0]] = split[1];
			}

			// The last section has no header after it to close it.
			var last = SimpleTrainerObject(fields);
			if (last != null) map.Objects.Add(last);

			return map;
		}

		/// <summary>One [n] section of a SimpleTrainer file, or null where it did not carry a placement.</summary>
		private static MapObject SimpleTrainerObject(Dictionary<string, string> fields)
		{
			if (!fields.ContainsKey("Model")) return null;

			return new MapObject
			{
				Hash = Number(fields, "Model", 0),
				Position = new Vector3(Number(fields, "x", 0f), Number(fields, "y", 0f), Number(fields, "z", 0f)),
				// SimpleTrainer keeps no pitch or roll: what it calls a heading goes in Z, and the quaternion
				// carries the rest of the orientation.
				Rotation = new Vector3(Number(fields, "qz", 0f), Number(fields, "qw", 0f), Number(fields, "h", 0f)),
				Dynamic = Number(fields, "Dynamic", 0) == 1,
				Quaternion = new Quaternion
				{
					X = Number(fields, "qx", 0f),
					Y = Number(fields, "qy", 0f),
					Z = Number(fields, "qz", 0f),
					W = Number(fields, "qw", 0f),
				},
			};
		}

		private static Map FromSpoonerLegacy(string content)
		{
			var map = new Map();
			var fields = new Dictionary<string, string>();

			foreach (var line in Lines(content))
			{
				if (line.StartsWith("[") && line.EndsWith("]"))
				{
					var placed = SpoonerLegacyObject(fields);
					if (placed != null) map.Objects.Add(placed);
					fields = new Dictionary<string, string>();
					continue;
				}

				var split = line.Split('=');
				if (split.Length >= 2) fields[split[0].Trim()] = split[1].Trim();
			}

			var last = SpoonerLegacyObject(fields);
			if (last != null) map.Objects.Add(last);

			return map;
		}

		private static MapObject SpoonerLegacyObject(Dictionary<string, string> fields)
		{
			string hash;
			if (!fields.ContainsKey("Type") || !fields.TryGetValue("Hash", out hash)) return null;

			int model;
			if (!int.TryParse(hash, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out model)) return null;

			var type = Number(fields, "Type", 3);
			return new MapObject
			{
				Hash = model,
				Position = new Vector3(Number(fields, "X", 0f), Number(fields, "Y", 0f), Number(fields, "Z", 0f)),
				Rotation = new Vector3(Number(fields, "Pitch", 0f), Number(fields, "Roll", 0f), Number(fields, "Yaw", 0f)),
				Type = type == 1 ? ObjectTypes.Ped : type == 2 ? ObjectTypes.Vehicle : ObjectTypes.Prop,
			};
		}

		// --- Shared helpers -----------------------------------------------------------------------------

		/// <summary>
		/// File.ReadAllLines, over text that never was a file. Blank lines are nothing to any reader.
		///
		/// Builds the list up front instead of yielding: a yield-return iterator would be compiled into a
		/// state machine that reads Environment.CurrentManagedThreadId, which FiveM's Mono sandbox denies.
		/// See ResourceFiles.Lines for the same story with the laziness kept.
		/// </summary>
		private static List<string> Lines(string content)
		{
			var lines = new List<string>();
			foreach (var line in content.Split('\n'))
			{
				var trimmed = line.TrimEnd('\r');
				if (trimmed.Length > 0) lines.Add(trimmed);
			}
			return lines;
		}

		private static float Number(Dictionary<string, string> fields, string name, float fallback)
		{
			string text;
			float value;
			return fields.TryGetValue(name, out text) &&
			       float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
				? value
				: fallback;
		}

		private static int Number(Dictionary<string, string> fields, string name, int fallback)
		{
			string text;
			int value;
			return fields.TryGetValue(name, out text) &&
			       int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
				? value
				: fallback;
		}

		/// <summary>
		/// The idle action a ped was given in the editor, as the game knows it: null for none, an "@" name
		/// for the three actions that are not scenarios at all, and otherwise the string the game knows the
		/// scenario by. The names the editor shows are its own labels; only
		/// <see cref="ObjectDatabase.ScrenarioDatabase"/> knows what each of them stands for.
		/// </summary>
		private static string ScenarioToken(string action)
		{
			if (string.IsNullOrEmpty(action) || action == "None") return null;

			switch (action)
			{
				case "Any":
				case "Any - Walk": return "@walk";
				case "Any - Warp": return "@warp";
				case "Wander": return "@wander";
			}

			string scenario;
			return ObjectDatabase.ScrenarioDatabase.TryGetValue(action, out scenario) ? scenario : null;
		}

		// --- Export: a runnable FiveM resource ----------------------------------------------------------

		/// <summary>
		/// The map as a resource a server can start, in the three files it is made of.
		///
		/// This is what <c>Format.CSharpCode</c> was for the SP build: the way a finished map leaves the
		/// editor as something that stands up on its own, with no Map Editor underneath it. The map itself
		/// stays data — <c>map.json</c> is the same native document the editor saves — and <c>client.lua</c>
		/// is the same loader for every map, so a map can be edited by hand afterwards without anything
		/// needing to be regenerated.
		///
		/// The client cannot write these anywhere: the text goes to the server, which puts the folder in
		/// place with SaveResourceFile.
		/// </summary>
		public static IList<ResourceFile> BuildResource(Map map, string resourceName)
		{
			if (map == null) throw new ArgumentNullException("map");

			var name = ResourceName(resourceName);
			var metadata = map.Metadata ?? new MapMetadata();

			var manifest = ManifestTemplate
				.Replace("{NAME}", LuaString(name))
				// A map that never got as far as being saved has no name of its own to be described by, so
				// the description falls back to the resource name the export was given.
				.Replace("{DESCRIPTION}", LuaString(string.IsNullOrEmpty(metadata.Description)
					? (string.IsNullOrWhiteSpace(metadata.Name) ? name : metadata.Name) + ", exported from Map Editor"
					: metadata.Description))
				.Replace("{AUTHOR}", LuaString(metadata.Creator ?? ""));

			var loader = LoaderTemplate
				.Replace("{STREAMING_RANGE}", SmartStreaming.Range.ToString(CultureInfo.InvariantCulture));

			return new List<ResourceFile>
			{
				new ResourceFile("fxmanifest.lua", manifest),
				new ResourceFile("client.lua", loader),
				new ResourceFile("map.json", ToJson(map, true)),
			};
		}

		/// <summary>
		/// A resource name FiveM will start. It is a folder name on the server and an identifier in every
		/// event and export that names the resource, so the player's map title is reduced to what both can
		/// carry rather than passed through and left to fail at start-up.
		/// </summary>
		public static string ResourceName(string title)
		{
			var name = new StringBuilder();
			foreach (var c in (title ?? "").ToLowerInvariant())
			{
				if (c >= 'a' && c <= 'z' || c >= '0' && c <= '9') name.Append(c);
				else if (c == '-' || c == '_') name.Append(c);
				else if (name.Length > 0 && name[name.Length - 1] != '_') name.Append('_');
			}

			var trimmed = name.ToString().Trim('_');
			// A name starting with a digit is legal, one that is empty is not.
			return trimmed.Length == 0 ? "mapeditor_map" : trimmed;
		}

		private static string LuaString(string value)
		{
			var escaped = (value ?? "")
				.Replace("\\", "\\\\")
				.Replace("'", "\\'")
				.Replace("\r", "")
				.Replace("\n", "\\n");
			return "'" + escaped + "'";
		}

		private const string ManifestTemplate = @"fx_version 'cerulean'
game 'gta5'

name {NAME}
description {DESCRIPTION}
author {AUTHOR}
version '1.0.0'

client_script 'client.lua'

-- The map is read at runtime rather than compiled in, so it can be edited in place.
files {
    'map.json'
}
";

		/// <summary>
		/// The loader an exported map ships with: the same file for every map, reading the map out of
		/// map.json beside it.
		///
		/// It carries its own copy of what <see cref="SmartStreaming"/> does, because an exported map runs
		/// with nothing but FiveM underneath it — there is no Map Editor there to ask. The numbers and the
		/// decisions are deliberately the same ones, so that an exported map behaves the way it did in the
		/// editor it was built in.
		///
		/// Everything is spawned local (isNetwork = false): the map is the same data on every client, so
		/// each of them standing up their own copy costs the server nothing, while replicating one copy
		/// would spend the entity budget of the whole server on scenery. See section 5.7 of the port plan.
		/// </summary>
		private const string LoaderTemplate = @"--[[
    Generated by Map Editor.

    The map is not put into the world all at once and left there: only the part of it the player is
    anywhere near stands at any moment, and the rest goes out and comes back as they move. See stream().

    Everything here is local to this client. Every player running this resource builds the same map from
    the same map.json, so nothing is replicated and nothing counts against the server's entity budget.
]]

-- How far away the map's props stay drawn, in metres.
local LOD_DISTANCE = 3000

-- How far from the player something may stand before it is taken back out of the world, in metres, or
-- -1 to keep the whole map standing wherever the player is. This is the smart streaming range as it
-- stood in the editor when the map was exported; change it here and the map streams to whatever you
-- change it to.
local STREAMING_RANGE = {STREAMING_RANGE}

-- Something comes back a little closer in than it went out, so that an entity sitting exactly on the
-- boundary is not spawned and deleted again on alternate frames as the player shifts their weight.
local RETURN_RATIO = 0.85

-- How much may be spawned and deleted in a single frame. Spawning is the expensive half, so a burst of
-- it is what a player feels as a stutter; deleting is cheap enough to allow more of, and it is what
-- buys back the frame rate.
local SPAWNS_PER_TICK = 6
local DESPAWNS_PER_TICK = 12

-- How many entities are looked at per frame when deciding what has gone out of range. The scan is a
-- rolling one: a large map takes a few frames to notice the player has left, rather than costing every
-- frame.
local SCAN_PER_TICK = 128

-- A ped stands on its feet and a prop hangs from its centre; the editor stores the centre for both.
local PED_GROUND_OFFSET = 1.0

-- How many laser beams may be drawn in one frame, across every laser of the map. A ceiling rather than a
-- budget: sixty overlapping lasers in one room is a mistake, and the frame it costs should be spent
-- finding that out.
local LASER_MAX_BEAMS = 400

--- How wide a ped is, added to a laser's own hit radius. The beams are measured against one line down the
--- middle of the player rather than against their body, so without this a beam through somebody's shoulder
--- measures half a metre away from them and burns nobody. Client/LaserRenderer.cs carries the same number.
local LASER_PED_RADIUS = 0.35

--- Where a ped's own position sits in the body: this far below it are the soles of their feet, and this far
--- above it is the top of their head. It is PED_GROUND_OFFSET again, from the other end — a ped answers from
--- its middle and stands on its feet — and the laser used to be written as though the position were the feet,
--- measuring beams against a line from +0.3 to +1.6. That is the shoulders to well above the head: nothing
--- below the shoulders was tested, so a beam lying across a room passed a metre under everything that could
--- be hit and burned nobody while drawing perfectly. Client/LaserRenderer.cs carries the same two numbers.
local LASER_PED_FOOT = 0.95
local LASER_PED_HEAD = 0.85

--- What may stand between the emitter and the player and stop a beam: peds and objects, the flags the
--- game's own laser script uses, and deliberately not the map. A laser's beams end wherever the room does —
--- inside the far wall, under the floor — and the probe starts at that end, so asking about the world would
--- have every laser report itself blocked on its first metre while drawing perfectly.
local LASER_BLOCKERS = 24

-- The multiplayer dictionary the game's own laser beams are textured from.
local BEAM_DICT = 'mpinvperscommon'
local BEAM_CORE = 'beam_middle'
local BEAM_GLOW = 'beam_glow_tapered'

local entries = {}
local markers = {}
local lasers = {}
local hidden = {}
local scanCursor = 1

local function streamingEnabled()
    return STREAMING_RANGE > 0
end

local function vec(t)
    if type(t) ~= 'table' then return vector3(0.0, 0.0, 0.0) end
    return vector3(t[1] + 0.0, t[2] + 0.0, t[3] + 0.0)
end

local function distanceSquared(position)
    local origin = GetEntityCoords(PlayerPedId(), true)
    -- The parentheses are not decoration: ^ binds tighter than the unary # in Lua.
    local distance = #(position - origin)
    return distance * distance
end

local function isTooFar(position)
    if not streamingEnabled() then return false end
    return distanceSquared(position) > STREAMING_RANGE * STREAMING_RANGE
end

local function hasReturned(position)
    if not streamingEnabled() then return true end
    local edge = STREAMING_RANGE * RETURN_RATIO
    return distanceSquared(position) <= edge * edge
end

--- The model, once the game has it. Without `wait` it asks and hands back false, leaving the caller to
--- come back on a later frame; a model the game does not have at all is never asked for twice.
local function requestModel(hash, wait)
    if hash == 0 then return false end
    if HasModelLoaded(hash) then return true end
    if not IsModelInCdimage(hash) or not IsModelValid(hash) then return false end

    RequestModel(hash)
    if not wait then return false end

    local tries = 0
    while not HasModelLoaded(hash) and tries < 200 do
        Wait(0)
        tries = tries + 1
    end
    return HasModelLoaded(hash)
end

local function spawnProp(entry)
    local position = entry.position

    -- A door hangs on its own hinge: it is spawned static so the game does not drop it, and then left
    -- unfrozen so the player can still swing it.
    local dynamic = entry.dynamic and not entry.door
    local frozen = not entry.dynamic and not entry.door

    local handle = CreateObjectNoOffset(entry.hash, position.x, position.y, position.z, false, false,
        dynamic)
    if handle == 0 then return nil end

    if entry.quaternion then
        SetEntityQuaternion(handle, entry.quaternion[1], entry.quaternion[2], entry.quaternion[3],
            entry.quaternion[4])
    else
        SetEntityRotation(handle, entry.rotation.x, entry.rotation.y, entry.rotation.z, 2, true)
    end

    FreezeEntityPosition(handle, frozen)
    return handle
end

local function spawnVehicle(entry)
    local position = entry.position
    local handle = CreateVehicle(entry.hash, position.x, position.y, position.z, entry.rotation.z,
        false, false)
    if handle == 0 then return nil end

    SetVehicleColours(handle, entry.primaryColor or 0, entry.secondaryColor or 0)

    -- A livery lives either in the vehicle's own slot or, on everything added since, in mod slot 48,
    -- where SetVehicleLivery cannot reach it. No mod kit, no mods reported at all, so one goes on first.
    if entry.livery and entry.livery >= 0 then
        SetVehicleModKit(handle, 0)
        if GetNumVehicleMods(handle, 48) > 0 then
            SetVehicleMod(handle, 48, entry.livery, false)
        else
            SetVehicleLivery(handle, entry.livery)
        end
    end

    SetVehicleSiren(handle, entry.sirens == true)
    FreezeEntityPosition(handle, not entry.dynamic)
    return handle
end

local function startScenario(ped, scenario)
    if not scenario then return end

    local position = GetEntityCoords(ped, true)
    if scenario == '@walk' then
        TaskUseNearestScenarioToCoord(ped, position.x, position.y, position.z, 100.0, -1)
    elseif scenario == '@warp' then
        TaskUseNearestScenarioToCoordWarp(ped, position.x, position.y, position.z, 100.0, -1)
    elseif scenario == '@wander' then
        TaskWanderStandard(ped, 10.0, 10)
    else
        TaskStartScenarioInPlace(ped, scenario, 0, true)
    end
end

local function spawnPed(entry)
    local position = entry.position
    local handle = CreatePed(4, entry.hash, position.x, position.y, position.z - PED_GROUND_OFFSET,
        entry.rotation.z, false, false)
    if handle == 0 then return nil end

    FreezeEntityPosition(handle, not entry.dynamic)

    if entry.weapon and entry.weapon ~= 0 then
        GiveWeaponToPed(handle, entry.weapon, 999, false, true)
    end

    if entry.drawables then
        for slot = 1, #entry.drawables do
            local texture = entry.textures and entry.textures[slot] or 0
            SetPedComponentVariation(handle, slot - 1, entry.drawables[slot], texture, 0)
        end
    end

    startScenario(handle, entry.scenario)
    return handle
end

--- Puts one object of the map into the world. `streaming` says nobody is waiting for it: it is scenery
--- filling in behind a player walking towards it, so a model that is not in memory yet is asked for and
--- the whole thing left for a later frame.
local function spawn(entry, streaming)
    if not requestModel(entry.hash, not streaming) then return nil end

    local handle
    if entry.kind == 'Prop' then
        handle = spawnProp(entry)
    elseif entry.kind == 'Vehicle' then
        handle = spawnVehicle(entry)
    elseif entry.kind == 'Ped' then
        handle = spawnPed(entry)
    end

    if handle then
        SetEntityLodDist(handle, LOD_DISTANCE)
        -- What stops the game's own streamer from taking the map out again the moment the player walks
        -- away. Which is exactly what this script then does instead, deliberately and reversibly.
        SetEntityAsMissionEntity(handle, true, true)
    end

    SetModelAsNoLongerNeeded(entry.hash)
    return handle
end

--- Keeps the map down to the part of it the player is anywhere near. Walked a slice at a time across
--- frames rather than whole every frame, because asking every entity of the map where it is, on every
--- frame, is the cost this is meant to be saving.
local function stream()
    local total = #entries
    if total == 0 then return end

    local spawnsLeft = SPAWNS_PER_TICK
    local despawnsLeft = DESPAWNS_PER_TICK
    local playerVehicle = GetVehiclePedIsIn(PlayerPedId(), false)

    for _ = 1, math.min(total, SCAN_PER_TICK) do
        if scanCursor > total then scanCursor = 1 end

        local entry = entries[scanCursor]
        scanCursor = scanCursor + 1

        if not entry.gone then
            if not entry.handle then
                -- With streaming off everything belongs in the world, which is what fills the map in.
                if hasReturned(entry.position) then
                    if spawnsLeft <= 0 then return end
                    spawnsLeft = spawnsLeft - 1
                    -- A spawn that comes back empty-handed has asked for its model and is tried again on
                    -- a later pass, once the game has it.
                    entry.handle = spawn(entry, true)
                end
            elseif not DoesEntityExist(entry.handle) then
                -- Deleted by the player or by another resource: these are mission entities, so the game
                -- did not do it, and putting it back would be arguing with whoever did.
                entry.handle = nil
                entry.gone = true
            elseif streamingEnabled() and isTooFar(GetEntityCoords(entry.handle, true)) then
                -- A car deleted out from under whoever is driving it is worth one comparison to rule out.
                if entry.handle ~= playerVehicle then
                    if despawnsLeft <= 0 then return end
                    despawnsLeft = despawnsLeft - 1

                    SetEntityAsMissionEntity(entry.handle, true, true)
                    DeleteEntity(entry.handle)
                    entry.handle = nil
                end
            end
        end
    end
end

--- Markers are not entities: they only exist for the frame they are drawn in.
local function drawMarkers()
    for i = 1, #markers do
        local m = markers[i]
        DrawMarker(m.type, m.position.x, m.position.y, m.position.z, 0.0, 0.0, 0.0,
            m.rotation.x, m.rotation.y, m.rotation.z, m.scale.x, m.scale.y, m.scale.z,
            m.red, m.green, m.blue, m.alpha, m.bob, m.faceCamera, 2, m.rotate, nil, nil, false)
    end
end

--[[
    Lasers.

    Neither are these entities: a laser is the row of numbers the editor saved, and the beams are rebuilt
    from it on every frame. What follows is the same arithmetic Client/LaserRenderer.cs does, which is
    itself the game's own fmmc_lasers.c behind a smaller surface — and it is written out a second time here
    for the same reason drawMarkers is: an exported map runs with nothing but FiveM underneath it, so there
    is no Map Editor to ask. Change one of the two and the other has to follow, or a map will look different
    exported from how it looked in the editor it was built in.

    The clock is the session's, not the resource's and not the game's: GetNetworkTime is held in step by the
    server, so every client reads it the same and a blinking laser blinks together for everybody without a
    single byte being sent. GetGameTimer, which this was written against first, counts from the moment each
    player launched their own game and is a different number on every machine — it only looked shared.
]]

local function laserDensity(name)
    if name == 'Sparse' then return 0.45, 1.6, 0.5 end
    if name == 'Low' then return 0.7, 1.25, 0.75 end
    if name == 'High' then return 1.4, 0.8, 1.3 end
    if name == 'Maximum' then return 2.0, 0.55, 1.6 end
    return 1.0, 1.0, 1.0
end

local function normalize(v)
    local length = #v
    if length < 0.00001 then return vector3(0.0, 0.0, 0.0) end
    return v / length
end

local function cross(a, b)
    return vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x)
end

--- The three axes a laser's pattern is laid out along, from its pitch/roll/yaw.
local function laserAxes(l)
    local z, x = math.rad(l.rotation.z), math.rad(l.rotation.x)
    local flatten = math.abs(math.cos(x))
    local forward = normalize(vector3(-math.sin(z) * flatten, math.cos(z) * flatten, math.sin(x)))
    if #forward < 0.00001 then forward = vector3(0.0, 1.0, 0.0) end

    local flat = normalize(cross(forward, vector3(0.0, 0.0, 1.0)))
    if #flat < 0.00001 then flat = vector3(1.0, 0.0, 0.0) end
    local perp = cross(flat, forward)

    local roll = math.rad(l.rotation.y)
    local c, s = math.cos(roll), math.sin(roll)
    return forward, flat * c + perp * s, perp * c - flat * s
end

local function laserBeamCount(l)
    if l.pattern == 'Single' then return 1 end
    local countScale = laserDensity(l.density)
    local count = math.ceil(math.max(1, l.beamCount) * countScale)
    if count < 1 then count = 1 end
    if count > LASER_MAX_BEAMS then count = LASER_MAX_BEAMS end
    return count
end

--- Whether beam `index` is lit at `time`. Steady, Blink and Chase; the rhythm runs along the beams of one
--- laser rather than across several, which is what makes Chase a gap somebody can time their way through.
local function laserLit(l, index, time)
    if l.rhythm == 'Blink' then
        local on = math.max(0.0, l.onSeconds)
        local period = on + math.max(0.0, l.offSeconds)
        if period <= 0.0001 then return true end
        return time % period < on
    end

    if l.rhythm == 'Chase' then
        local period = math.max(0.0001, l.chasePeriod)
        local count = math.max(1, laserBeamCount(l))
        local fraction = math.min(1.0, math.max(0.0, l.chaseOnFraction))
        return (time / period + index / count) % 1.0 < fraction
    end

    return true
end

--- This frame's beams for one laser, as { s = start, e = end, i = index } — centred on its position, so a
--- laser turns and moves about the middle of itself.
local function laserBeams(l, time)
    local forward, right, up = laserAxes(l)
    local count = laserBeamCount(l)
    local _, spacing = laserDensity(l.density)
    local length = math.max(0.05, l.beamLength)
    local half = forward * (length * 0.5)
    local out = {}

    if l.pattern == 'Single' then
        out[1] = { s = l.position - half, e = l.position + half, i = 0 }
        return out
    end

    if l.pattern == 'Wall' then
        local height = math.max(0.0, l.height) * spacing
        local step = count > 1 and height / (count - 1) or 0.0
        local first = l.position - up * (height * 0.5)
        local run = right * (length * 0.5)
        for i = 1, count do
            local centre = first + up * (step * (i - 1))
            out[i] = { s = centre - run, e = centre + run, i = i - 1 }
        end
        return out
    end

    local width = math.max(0.0, l.width) * spacing
    local step = count > 1 and width / (count - 1) or 0.0
    local first = l.position - right * (width * 0.5)

    if l.pattern == 'Wave' then
        local phase = time * l.speed
        for i = 1, count do
            local along = step * (i - 1)
            local swing = l.amplitude * math.sin((along + phase) * l.frequency)
            local centre = first + right * along + up * swing
            out[i] = { s = centre - half, e = centre + half, i = i - 1 }
        end
        return out
    end

    -- Grid.
    for i = 1, count do
        local centre = first + right * (step * (i - 1))
        out[i] = { s = centre - half, e = centre + half, i = i - 1 }
    end
    return out
end

local function whiten(channel)
    return math.min(255, channel + 160)
end

--[[
    Six of the natives the beams are drawn with have no name in this runtime's list, so they are called by
    hash — the same six, and for the same reason, as in Client/LaserRenderer.cs. A hash a future game build
    moves would otherwise throw on every beam of every frame, so each group is tried, switched off on its
    first failure, and said once in the console. A laser with its glow switched off is still a laser.
]]
local DRAW_SPRITE_POLY = 0x29280002282F1928
local DRAW_CAPSULE_LIGHT = 0x330F4FA20FB57738
local DRAW_MARKER_GLOW = 0xE59B0A106CC15FC2
local SET_BLEND_STATE_ADDITIVE = 0x01677A72A8BDCD1A
local SET_BLEND_STATE_NORMAL = 0x976D155439608592
local GENERATE_PED_DAMAGE_EVENT = 0x6D1FCD0950EFA3DD

local glowsOk, texturedOk, blendOk, damageEventOk = true, true, true, true

--- Calls a native by hash and reports whether it worked. `flag` names the group in the message.
local function tryNative(flag, what, hash, ...)
    if not flag then return false end
    local ok, err = pcall(Citizen.InvokeNative, hash, ...)
    if not ok then
        print(('^3[map] the %s native is unavailable (%s); lasers carry on without it.^7'):format(what, err))
    end
    return ok
end

--- One beam-body quad, as two triangles drawn from both sides: the billboard is computed rather than asked
--- of the game, so which face ends up towards the eye is not promised.
local function laserQuad(mid, along, across, halfLen, halfWidth, r, g, b, a, texture)
    local l = along * halfLen
    local w = across * halfWidth
    local v0, v3, v6, v9 = mid - w + l, mid + w + l, mid - w - l, mid + w - l
    local order = { v3, v0, v9, v6, v9, v0, v9, v0, v3, v0, v9, v6 }

    for i = 1, 12, 3 do
        local p1, p2, p3 = order[i], order[i + 1], order[i + 2]
        if texture and texturedOk then
            texturedOk = tryNative(texturedOk, 'textured polygon', DRAW_SPRITE_POLY,
                p1.x, p1.y, p1.z, p2.x, p2.y, p2.z, p3.x, p3.y, p3.z,
                r, g, b, a, BEAM_DICT, texture, 1.0, 0.0, 1.0, 0.0, 0.0, 1.0, 1.0, 1.0, 1.0)
        else
            DrawPoly(p1.x, p1.y, p1.z, p2.x, p2.y, p2.z, p3.x, p3.y, p3.z, r, g, b, a)
        end
    end
end

--- One beam, in the four layers the game's own laser grid draws it in: a capsule light along its length, a
--- glowing dot at each end, a soft tapered-glow ribbon and a bright core ribbon.
local function drawBeam(l, beam, camera, textured)
    local delta = beam.e - beam.s
    local length = #delta
    if length < 0.0001 then return end

    local dir = delta / length
    local mid = (beam.s + beam.e) * 0.5
    local r, g, b, a = l.red, l.green, l.blue, l.alpha

    if glowsOk then
        local head, tail = beam.s + dir * 0.05, beam.e - dir * 0.05
        glowsOk = tryNative(glowsOk, 'capsule light', DRAW_CAPSULE_LIGHT,
            mid.x, mid.y, mid.z, dir.x, dir.y, dir.z, r, g, b,
            1.0, 6.0, math.max(0.05, length - 0.05), 250.0)
        glowsOk = tryNative(glowsOk, 'marker glow', DRAW_MARKER_GLOW,
            head.x, head.y, head.z, 0.116, r, g, b, 0.9)
        glowsOk = tryNative(glowsOk, 'marker glow', DRAW_MARKER_GLOW,
            tail.x, tail.y, tail.z, 0.116, r, g, b, 0.9)
    end

    local toCam = camera - mid
    if #toCam < 0.0001 then toCam = vector3(0.0, 0.0, 1.0) end
    local across = normalize(cross(dir, normalize(toCam)))
    if #across < 0.00001 then across = normalize(cross(dir, vector3(0.0, 0.0, 1.0))) end
    if #across < 0.00001 then return end

    local thickness = math.max(0.001, l.thickness)
    local halfLen = length * 0.5

    if textured and texturedOk then
        laserQuad(mid, dir, across, halfLen + 0.1, thickness * 1.7, r, g, b, a, BEAM_GLOW)
        laserQuad(mid, dir, across, halfLen + 0.1, thickness, whiten(r), whiten(g), whiten(b), a, BEAM_CORE)
    else
        laserQuad(mid, dir, across, halfLen, thickness * 1.6, r, g, b, math.min(a, 200), nil)
        laserQuad(mid, dir, across, halfLen, thickness * 0.5, whiten(r), whiten(g), whiten(b), a, nil)
    end
end

--- The shortest distance between two segments, and where on the first it is measured from. The narrow
--- phase: the first segment is the beam, the second the line down the middle of the ped.
local function segmentDistance(p1, q1, p2, q2)
    local d1, d2, r = q1 - p1, q2 - p2, p1 - p2
    local a, e, f = #(d1) * #(d1), #(d2) * #(d2), d2.x * r.x + d2.y * r.y + d2.z * r.z
    local s, t = 0.0, 0.0

    if a <= 0.000001 and e <= 0.000001 then return #(p1 - p2), p1 end

    if a <= 0.000001 then
        t = math.min(1.0, math.max(0.0, f / e))
    else
        local c = d1.x * r.x + d1.y * r.y + d1.z * r.z
        if e <= 0.000001 then
            s = math.min(1.0, math.max(0.0, -c / a))
        else
            local b = d1.x * d2.x + d1.y * d2.y + d1.z * d2.z
            local denom = a * e - b * b
            if denom > 0.000001 then s = math.min(1.0, math.max(0.0, (b * f - c * e) / denom)) end
            t = (b * s + f) / e
            if t < 0.0 then
                t = 0.0
                s = math.min(1.0, math.max(0.0, -c / a))
            elseif t > 1.0 then
                t = 1.0
                s = math.min(1.0, math.max(0.0, (b - c) / a))
            end
        end
    end

    local onFirst = p1 + d1 * s
    return #(onFirst - (p2 + d2 * t)), onFirst
end

--- Whether something solid stands between the emitter and the point the beam met the player. One probe for
--- the whole laser: a crate that hides them from the nearest beam hides them from its neighbours too.
---
--- `hit` is compared rather than tested because it is a BOOL written through a pointer, and Lua receives
--- those as the number 0 or 1 rather than as a boolean — the generated natives push every BOOL
--- out-parameter through PointerValueInt. In Lua 0 is true, so returning it bare made every laser report
--- itself permanently blocked and burn nobody, while drawing perfectly.
local function laserBlocked(from, to, ped)
    local handle = StartExpensiveSynchronousShapeTestLosProbe(
        from.x, from.y, from.z, to.x, to.y, to.z, LASER_BLOCKERS, ped, 7)
    local status, hit = GetShapeTestResult(handle)
    return status == 2 and hit ~= nil and hit ~= false and hit ~= 0
end

local laserLastStamp = 0

local function drawLasers()
    if #lasers == 0 then return end

    -- Two clocks, and they are not interchangeable. How long the frame took is measured with the game's own
    -- timer, which is monotonic; the session clock is steered by the server and a correction to it, measured
    -- as frame time, would be a second of laser damage in one frame.
    local now = GetGameTimer()
    local elapsed = laserLastStamp == 0 and 0 or (now - laserLastStamp)
    laserLastStamp = now
    local frameSeconds = elapsed <= 0 and 0.0 or math.min(0.25, elapsed / 1000.0)

    -- What the beams themselves are drawn from. Read back as unsigned, because the network clock is a 32-bit
    -- millisecond count arriving through a signed int and half of its range comes back negative; the editor's
    -- own Clock.SharedSeconds does exactly the same, so the two cannot disagree about what time it is.
    -- Before the session clock has started there is nothing shared to read and the game's timer stands in.
    local shared = HasNetworkTimeStarted() and GetNetworkTime() or GetGameTimer()
    if shared < 0 then shared = shared + 4294967296 end
    local time = shared / 1000.0
    local ped = PlayerPedId()
    local alive = ped ~= 0 and not IsEntityDead(ped)
    local here = GetEntityCoords(ped, true)
    local camera = GetFinalRenderedCamCoord()
    local drawn = 0

    if not HasStreamedTextureDictLoaded(BEAM_DICT) then RequestStreamedTextureDict(BEAM_DICT, false) end
    local ready = HasStreamedTextureDictLoaded(BEAM_DICT)

    -- Additive while the beams are drawn, normal again afterwards: overlapping beams have to brighten, and
    -- the blend state is the game's rather than this resource's. Switched on by the first beam rather than
    -- at the top, so that a map whose lasers are all on the other side of town brackets nothing.
    local blendOn = false

    for i = 1, #lasers do
        local l = lasers[i]

        if drawn < LASER_MAX_BEAMS and
            (l.activationRange <= 0.0 or #(here - l.position) <= l.activationRange) then

            -- The line down the middle of the ped, feet to head. See LASER_PED_FOOT: the coordinates a ped
            -- answers with are the middle of the body, not the feet.
            local low = here - vector3(0.0, 0.0, LASER_PED_FOOT)
            local high = here + vector3(0.0, 0.0, LASER_PED_HEAD)
            local beams = laserBeams(l, time)
            local burning, hitPoint, hitFrom = 0, nil, nil
            local radius = math.max(0.01, l.hitRadius) + LASER_PED_RADIUS

            for b = 1, #beams do
                if drawn >= LASER_MAX_BEAMS then break end
                local beam = beams[b]

                if laserLit(l, beam.i, time) then
                    if not blendOn then
                        blendOn = true
                        blendOk = tryNative(blendOk, 'additive blend state', SET_BLEND_STATE_ADDITIVE)
                    end

                    drawBeam(l, beam, camera, l.textured and ready)
                    drawn = drawn + 1

                    if alive then
                        local gap, onBeam = segmentDistance(beam.s, beam.e, low, high)
                        if gap <= radius then
                            burning = burning + 1
                            if not hitPoint then hitPoint, hitFrom = onBeam, beam.s end
                        end
                    end
                end
            end

            if hitPoint and alive and not laserBlocked(hitFrom, hitPoint, ped) then
                if l.dealsDamage then
                    local _, _, damageScale = laserDensity(l.density)
                    l.debt = (l.debt or 0.0) + l.damagePerSecond * damageScale * burning * frameSeconds

                    if damageEventOk and not IsPedRagdoll(ped) then
                        damageEventOk = tryNative(damageEventOk, 'ped damage event', GENERATE_PED_DAMAGE_EVENT,
                            ped, hitPoint.x, hitPoint.y, hitPoint.z, GetHashKey('WEAPON_PISTOL'))
                    end

                    local whole = math.floor(l.debt)
                    if whole > 0 then
                        l.debt = l.debt - whole
                        -- Five arguments, as the game's own scripts pass: the native grew two trailing
                        -- parameters, and one called with fewer does not fail — it reads whatever the
                        -- argument buffer held.
                        ApplyDamageToPed(ped, whole, true, 0, 0)
                        if IsEntityDead(ped) then StartEntityFire(ped) end
                    end
                else
                    l.debt = 0.0
                end
            else
                l.debt = 0.0
            end
        else
            l.debt = 0.0
        end
    end

    if blendOn then blendOk = tryNative(blendOk, 'normal blend state', SET_BLEND_STATE_NORMAL) end
end

--- The game streams its own props back in as the player comes and goes. CREATE_MODEL_HIDE survives that,
--- unlike deleting the prop, which is why the removals are applied once here rather than every frame.
local function applyRemovals()
    for i = 1, #hidden do
        local h = hidden[i]
        CreateModelHide(h.position.x, h.position.y, h.position.z, 1.0, h.hash, false)
    end
end

local function readMap()
    local text = LoadResourceFile(GetCurrentResourceName(), 'map.json')
    if not text then
        print('^1[map] map.json is missing from this resource.^7')
        return false
    end

    local ok, document = pcall(json.decode, text)
    if not ok or type(document) ~= 'table' then
        print('^1[map] map.json could not be read.^7')
        return false
    end

    for _, o in ipairs(document.objects or {}) do
        if o.type == 'Prop' or o.type == 'Vehicle' or o.type == 'Ped' then
            entries[#entries + 1] = {
                kind = o.type,
                hash = o.hash,
                position = vec(o.position),
                rotation = vec(o.rotation),
                quaternion = o.quaternion,
                dynamic = o.dynamic == true,
                door = o.door == true,
                weapon = o.weaponHash,
                drawables = o.drawables,
                textures = o.textures,
                scenario = o.scenario,
                primaryColor = o.primaryColor,
                secondaryColor = o.secondaryColor,
                livery = o.livery,
                sirens = o.sirens,
            }
        end
    end

    for _, o in ipairs(document.removeFromWorld or {}) do
        hidden[#hidden + 1] = { hash = o.hash, position = vec(o.position) }
    end

    for _, m in ipairs(document.markers or {}) do
        markers[#markers + 1] = {
            type = m.type or 0,
            position = vec(m.position),
            rotation = vec(m.rotation),
            scale = vec(m.scale),
            red = m.red or 255,
            green = m.green or 255,
            blue = m.blue or 255,
            alpha = m.alpha or 255,
            bob = m.bobUpAndDown == true,
            faceCamera = m.rotateToCamera == true,
            rotate = false,
        }
    end

    -- Every default here is the one the editor's own Laser carries, so a map written by a later editor that
    -- has one more knob still runs, with the knob it does not know about at something sensible.
    --
    -- The counts and colours go through math.floor and the sizes through +0.0 rather than being taken as
    -- they come: json.decode hands back whichever numeric subtype the text happened to look like, and the
    -- native invoker picks int or float from that subtype. A colour that arrived as 255.0 would be pushed
    -- as a float into a parameter the native reads as an integer.
    for _, l in ipairs(document.lasers or {}) do
        lasers[#lasers + 1] = {
            pattern = l.pattern or 'Grid',
            position = vec(l.position),
            rotation = vec(l.rotation),
            beamLength = (l.beamLength or 8.0) + 0.0,
            width = (l.width or 6.0) + 0.0,
            height = (l.height or 3.0) + 0.0,
            beamCount = math.floor(l.beamCount or 8),
            density = l.density or 'Medium',
            thickness = (l.thickness or 0.03) + 0.0,
            red = math.floor(l.red or 255),
            green = math.floor(l.green or 40),
            blue = math.floor(l.blue or 40),
            alpha = math.floor(l.alpha or 255),
            textured = l.textured ~= false,
            rhythm = l.rhythm or 'Steady',
            onSeconds = l.onSeconds or 1.5,
            offSeconds = l.offSeconds or 0.5,
            chasePeriod = l.chasePeriod or 3.0,
            chaseOnFraction = l.chaseOnFraction or 0.5,
            amplitude = l.amplitude or 1.5,
            frequency = l.frequency or 0.6,
            speed = l.speed or 1.0,
            dealsDamage = l.dealsDamage ~= false,
            damagePerSecond = l.damagePerSecond or 250.0,
            activationRange = l.activationRange or 60.0,
            hitRadius = l.hitRadius or 0.35,
            debt = 0.0,
        }
    end

    return true
end

--- The part of the map the player starts inside goes in in one go, waiting for its models: the map is
--- expected to be standing by the time they can see it. Everything further out is left to stream() to
--- fill in as they walk towards it.
local function spawnNearby()
    for i = 1, #entries do
        local entry = entries[i]
        if hasReturned(entry.position) then
            entry.handle = spawn(entry, false)
        end
    end
end

Citizen.CreateThread(function()
    if not readMap() then return end

    while not NetworkIsSessionStarted() do Wait(100) end
    Wait(500)

    applyRemovals()
    spawnNearby()

    while true do
        Wait(0)
        stream()
        drawMarkers()
        drawLasers()
    end
end)

--- The map belongs to this resource, so it leaves with it: without this a restart would stack a second
--- copy of every prop on top of the first.
AddEventHandler('onResourceStop', function(resource)
    if resource ~= GetCurrentResourceName() then return end

    for i = 1, #entries do
        local entry = entries[i]
        if entry.handle and DoesEntityExist(entry.handle) then
            SetEntityAsMissionEntity(entry.handle, true, true)
            DeleteEntity(entry.handle)
        end
    end

    -- Nothing to take down for the markers or the lasers: neither is in the world between frames.
    for i = 1, #hidden do
        local h = hidden[i]
        RemoveModelHide(h.position.x, h.position.y, h.position.z, 1.0, h.hash, false)
    end
end)
";
	}
}
