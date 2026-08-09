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
		/// 2 added the optional <c>shared</c> flag on an object. Nothing else moved, and a version 1 document
		/// reads unchanged: an absent flag is exactly what it means there too — decide by the rule.
		/// </summary>
		private const int JsonFormatVersion = 2;

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

			var document = Json.Object()
				.Set("format", "mapeditor-map")
				.Set("version", JsonFormatVersion)
				.Set("objects", objects)
				.Set("removeFromWorld", removed)
				.Set("markers", markers);

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

			if (o.ShouldSerializeAmount()) json.Set("amount", o.Amount);
			if (o.ShouldSerializeRespawnTimer()) json.Set("respawnTimer", o.RespawnTimer);
			if (o.ShouldSerializeFlag()) json.Set("flag", o.Flag);

			return json;
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
			foreach (var item in document["objects"].Items) map.Objects.Add(ObjectFromJson(item));
			foreach (var item in document["removeFromWorld"].Items) map.RemoveFromWorld.Add(ObjectFromJson(item));
			foreach (var item in document["markers"].Items) map.Markers.Add(MarkerFromJson(item));

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
			var o = new MapObject();

			ObjectTypes type;
			if (Enum.TryParse(json["type"].AsString("Prop"), out type)) o.Type = type;

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

			o.Amount = json["amount"].AsInt(0);
			o.RespawnTimer = json["respawnTimer"].AsInt(0);
			o.Flag = json["flag"].AsInt(0);

			return o;
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

			var objects = root.Element("Objects");
			if (objects != null)
				foreach (var element in objects.Elements("MapObject")) map.Objects.Add(ObjectFromXml(element));

			var removed = root.Element("RemoveFromWorld");
			if (removed != null)
				foreach (var element in removed.Elements("MapObject")) map.RemoveFromWorld.Add(ObjectFromXml(element));

			var markers = root.Element("Markers");
			if (markers != null)
				foreach (var element in markers.Elements("Marker")) map.Markers.Add(MarkerFromXml(element));

			var metadata = root.Element("Metadata");
			if (metadata != null) map.Metadata = MetadataFromXml(metadata);

			return map;
		}

		private static MapObject ObjectFromXml(XElement element)
		{
			var o = new MapObject();

			ObjectTypes type;
			if (Enum.TryParse(Xml.Text(element, "Type") ?? "Prop", out type)) o.Type = type;

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

			o.Amount = Xml.Int(element, "Amount", 0);
			o.RespawnTimer = Xml.Int(element, "RespawnTimer", 0);
			o.Flag = Xml.Int(element, "Flag", 0);

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

local entries = {}
local markers = {}
local hidden = {}
local pickupHandles = {}
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
        if o.type == 'Pickup' then
            -- A pickup is not an entity and the game keeps it out of the entity budget: it has a range of
            -- its own, so it is put in once and left to it rather than streamed.
            local p = vec(o.position)
            local handle = CreatePickupRotate(o.hash, p.x, p.y, p.z, 0.0, 0.0,
                vec(o.rotation).z, o.flag or 515, o.amount or 0, 0, false, 0)
            if handle ~= 0 then pickupHandles[#pickupHandles + 1] = handle end
        elseif o.type == 'Prop' or o.type == 'Vehicle' or o.type == 'Ped' then
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

    for i = 1, #pickupHandles do
        RemovePickup(pickupHandles[i])
    end

    for i = 1, #hidden do
        local h = hidden[i]
        RemoveModelHide(h.position.x, h.position.y, h.position.z, 1.0, h.hash, false)
    end
end)
";
	}
}
