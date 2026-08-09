using System;
using System.Globalization;
using System.Xml.Linq;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using MapEditor.Platform;

namespace MapEditor
{
	/// <summary>
	/// Menyoo's .xml spooner files, read directly.
	///
	/// The SP build declared Menyoo's whole schema as five hundred lines of attributed classes and handed
	/// them to XmlSerializer — Mods._0 through Mods._48, tyres, neons, the lot — of which it read four
	/// members. That was the price of letting XmlSerializer do the walking; there is no XmlSerializer here
	/// (see <see cref="Platform.Xml"/>), so what is left is the mapping that was actually being done.
	///
	/// Writing Menyoo files is gone with the rest of the single-player exports: a FiveM client has nowhere
	/// to put one. See <see cref="MapSerializer"/>.
	/// </summary>
	public static class MenyooCompatibility
	{
		/// <summary>Menyoo's type numbers. Anything else is treated as a prop.</summary>
		private const int TypePed = 1;
		private const int TypeVehicle = 2;
		private const int TypeProp = 3;

		public static Map Read(string content)
		{
			var root = XDocument.Parse(content).Root;
			if (root == null || root.Name.LocalName != "SpoonerPlacements")
				throw new FormatException("A Menyoo map has <SpoonerPlacements> at its root.");

			var map = new Map();
			foreach (var placement in root.Elements("Placement"))
			{
				var o = new MapObject();

				switch (Xml.Int(placement, "Type", TypeProp))
				{
					case TypePed: o.Type = ObjectTypes.Ped; break;
					case TypeVehicle: o.Type = ObjectTypes.Vehicle; break;
					default: o.Type = ObjectTypes.Prop; break;
				}

				o.Dynamic = Xml.Bool(placement, "Dynamic", false);
				o.Hash = ModelHash(placement);

				var at = placement.Element("PositionRotation");
				if (at != null)
				{
					o.Position = new Vector3(Xml.Float(at, "X", 0f), Xml.Float(at, "Y", 0f), Xml.Float(at, "Z", 0f));
					o.Rotation = new Vector3(Xml.Float(at, "Pitch", 0f), Xml.Float(at, "Roll", 0f),
						Xml.Float(at, "Yaw", 0f));
				}

				map.Objects.Add(o);
			}

			return map;
		}

		/// <summary>
		/// The model a placement stands for. Menyoo writes the hash as hex and the model's name beside it;
		/// files written by hand often have only the name, so it is the fallback rather than ignored.
		/// </summary>
		private static int ModelHash(XElement placement)
		{
			var hex = Xml.Text(placement, "ModelHash");
			if (hex != null)
			{
				hex = hex.Trim();
				if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex.Substring(2);

				uint hash;
				// Unchecked because a model hash is an unsigned number the editor keeps as a signed one.
				if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash))
					return unchecked((int) hash);
			}

			var name = Xml.Text(placement, "HashName");
			return string.IsNullOrEmpty(name) ? 0 : API.GetHashKey(name);
		}
	}
}
