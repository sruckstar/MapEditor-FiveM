using System;
using System.Globalization;
using MapEditor.Platform;

namespace MapEditor
{
	/// <summary>
	/// The map being edited, parked in KVP for the moment the resource is not running.
	///
	/// A resource restart tears the script down and builds it again from nothing, but everything it spawned
	/// is game-side and stays exactly where it was — with no script left that knows those entities exist.
	/// The player is left looking at a map they can no longer select, save, or even delete, and only
	/// rejoining the server takes it away. So the map is written out here on the way down and taken back out
	/// of the world, and the instance that replaces this one puts it back as the map it is editing.
	///
	/// The SP build used a file's last-write time to tell a reload from a previous run of the game. KVP
	/// carries no timestamps, so the time is written into the entry itself.
	/// </summary>
	public static class SessionRestore
	{
		private const string ContentKey = "mapeditor:session";
		private const string SavedAtKey = "mapeditor:session:savedAt";

		/// <summary>
		/// The game aborts its scripts when the player disconnects as well, so leaving also leaves an entry
		/// behind. A restart has the next instance asking for it within seconds of the last one writing it,
		/// and nothing else comes close, so age is what tells the two apart. An older entry is from a
		/// previous session: the world it belonged to is gone, and springing its map on the player now would
		/// be nothing but a surprise.
		/// </summary>
		private const int MaxAgeMs = 5 * 60 * 1000;

		/// <summary>
		/// Whether a map is waiting to be put back. An entry too old to be one is dropped here.
		///
		/// The age is measured on the game timer rather than on a wall clock — <see cref="Clock"/> says why
		/// there is no wall clock to measure it on — and that turns out to be the better instrument anyway:
		/// the timer counts from the game starting, so it says how long ago the entry was written *and*
		/// whether it was written by this run of the game at all. A reading from the future is one from a
		/// previous run, whose world is doubly gone.
		/// </summary>
		public static bool Pending
		{
			get
			{
				if (!Kvp.Exists(ContentKey)) return false;

				int savedAt;
				if (int.TryParse(Kvp.GetString(SavedAtKey) ?? "", NumberStyles.Integer,
						CultureInfo.InvariantCulture, out savedAt))
				{
					int age = Clock.Since(savedAt);
					if (age >= 0 && age <= MaxAgeMs) return true;
				}

				Discard();
				return false;
			}
		}

		/// <summary>The stored map, or null when there is nothing to restore.</summary>
		public static string Read()
		{
			return Kvp.GetString(ContentKey);
		}

		/// <summary>
		/// Writes out everything the editor is holding, and says whether the map is now safe to take out of the
		/// world. An empty map is nothing to come back to, so it leaves no entry at all — and takes away any
		/// older one, which would otherwise be restored in its place.
		///
		/// False means the map is only in the world: without an entry to put it back from, deleting it would be
		/// the end of the player's work, so it is left standing to be cleaned up by rejoining the server.
		/// </summary>
		public static bool Save()
		{
			var map = new Map();
			map.Objects.AddRange(PropStreamer.GetAllEntities());
			map.RemoveFromWorld.AddRange(PropStreamer.RemovedObjects);
			map.Markers.AddRange(PropStreamer.Markers);
			map.Metadata = PropStreamer.CurrentMapMetadata;

			if (map.Objects.Count == 0 && map.RemoveFromWorld.Count == 0 && map.Markers.Count == 0)
			{
				Discard();
				return true;
			}

			try
			{
				// The name comes back with it: this is a hand-off between two instances of the same editor,
				// so the map has to return still tied to the entry the player has been saving it to.
				Kvp.SetString(ContentKey, new MapSerializer().SerializeToString(map, MapSerializer.Format.Json));
				Kvp.SetString(SavedAtKey, Clock.Milliseconds.ToString(CultureInfo.InvariantCulture));
				return true;
			}
			catch (Exception e)
			{
				// The script is already on its way out, so there is nothing left to notify through: the client
				// console is the only place this can be said.
				Log.Error("SessionRestore.Save", e);
				Discard();
				return false;
			}
		}

		public static void Discard()
		{
			try
			{
				Kvp.Delete(ContentKey);
				Kvp.Delete(SavedAtKey);
			}
			catch (Exception) { }
		}
	}
}
