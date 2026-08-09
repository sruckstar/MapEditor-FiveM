using CitizenFX.Core;

namespace MapEditor
{
	/// <summary>
	/// Keeps a map down to what is around the player: everything the editor put into the world is taken back
	/// out once it is far enough away to be out of sight, and put back the moment the player comes near it
	/// again.
	///
	/// Nothing the game does would otherwise take them out. Every entity the editor spawns is marked persistent
	/// so that the game's own streamer cannot drop it — which is exactly what makes a placed map stay where it
	/// was put, through save loads and long drives across the map — and the price of that is paid on every
	/// frame, everywhere, for every object of every map that is open. A few hundred objects is nothing; a
	/// city-sized map, or a handful of autoloaded ones on top of the one being edited, is a steadily worse frame
	/// rate for scenery nobody is anywhere near.
	///
	/// This class only decides. Taking an entity out of the world and putting it back is left to whoever owns
	/// it — the editor for the map being edited (see MapEditor.Streaming.cs), <see cref="AutoloadedMaps"/> for
	/// the maps standing alongside it — because what has to survive the round trip differs between the two and
	/// neither hands its entities to anyone else.
	/// </summary>
	public static class SmartStreaming
	{
		/// <summary>Streaming switched off: every map stays in the world whole, wherever the player is.</summary>
		public const int Off = -1;

		/// <summary>What a map editor without this setting gets, and what a fresh install starts at.</summary>
		public const int DefaultRange = 500;

		/// <summary>
		/// How far from <see cref="Origin"/> an entity may stand before it is taken out of the world, in
		/// metres, or <see cref="Off"/>. Set from the settings file at startup and whenever the player changes
		/// it in the menu.
		/// </summary>
		public static int Range = DefaultRange;

		public static bool Enabled => Range > 0;

		/// <summary>
		/// Where the map is being looked at from, which is not always where the player is: while the freecam is
		/// up the player is parked behind it, frozen, and it is the camera that flies across the map. Set once
		/// a frame by the editor, so that every decision made during that frame is measured from one spot.
		/// </summary>
		public static Vector3 Origin { get; private set; }

		/// <summary>
		/// Something comes back a little closer in than it went out, so that an entity sitting exactly on the
		/// boundary is not spawned and deleted again on alternate frames as the player shifts their weight.
		/// </summary>
		private const float ReturnRatio = 0.85f;

		/// <summary>
		/// How much may be spawned and deleted in a single frame.
		///
		/// Spawning is the expensive half — a model to put in place, and for a ped its clothes, its weapon and
		/// its scenario on top — so a burst of it is what a player feels as a stutter. Coming over a hill onto
		/// a hundred objects therefore takes a second or two to fill in, which is what any streamer does.
		/// Deleting is cheap enough to allow more of, and it is what buys back the frame rate.
		/// </summary>
		private const int SpawnsPerTick = 6;
		private const int DespawnsPerTick = 12;

		/// <summary>
		/// How many entities are looked at per frame when deciding what has gone out of range. Asking an entity
		/// where it stands is a native call each, and a large map is thousands of them, so the scan is a rolling
		/// one: a map takes a few frames to notice that the player has left rather than costing every frame.
		/// </summary>
		public const int ScanPerTick = 128;

		private static int _spawnsLeft;
		private static int _despawnsLeft;

		/// <summary>
		/// Opens a frame. The budgets are shared by everything that streams, so that a map being edited with
		/// three autoloaded maps around it costs one map's worth of streaming per frame rather than four.
		/// </summary>
		public static void BeginTick(Vector3 origin)
		{
			Origin = origin;
			_spawnsLeft = SpawnsPerTick;
			_despawnsLeft = DespawnsPerTick;
		}

		/// <summary>Claims one of this frame's spawns, or says there are none left.</summary>
		public static bool TakeSpawn()
		{
			if (_spawnsLeft <= 0) return false;
			_spawnsLeft--;
			return true;
		}

		/// <summary>Claims one of this frame's deletions, or says there are none left.</summary>
		public static bool TakeDespawn()
		{
			if (_despawnsLeft <= 0) return false;
			_despawnsLeft--;
			return true;
		}

		/// <summary>Whether something standing at <paramref name="position"/> has been left far enough behind.</summary>
		public static bool IsTooFar(Vector3 position)
		{
			if (!Enabled) return false;
			return DistanceSquared(position) > (float) Range * Range;
		}

		/// <summary>
		/// Whether something taken out of the world at <paramref name="position"/> belongs back in it. With
		/// streaming switched off everything does, which is what puts a map back together after the player
		/// turns it off with half of one missing.
		/// </summary>
		public static bool HasReturned(Vector3 position)
		{
			if (!Enabled) return true;
			var edge = Range * ReturnRatio;
			return DistanceSquared(position) <= edge * edge;
		}

		private static float DistanceSquared(Vector3 position)
		{
			return (position - Origin).LengthSquared();
		}

		/// <summary>
		/// True when the game has the model in memory, false when it does not have it yet — in which case it
		/// has now been asked for and the caller should come back on a later frame.
		///
		/// The SP build returned a Model and let callers test it against null, which worked because SHVDN's
		/// Model is a struct with an implicit conversion from int: a null return read back as hash 0.
		/// CitizenFX's Model is a reference type with a user-defined ==, so that idiom would compare a null
		/// reference through the operator. Hence the Try shape.
		///
		/// <see cref="ObjectPreview.LoadObject"/> is the other way of getting one, and it is the wrong one
		/// here: it waits for as long as it takes, up to two hundred frames, and puts "Loading Model" on the
		/// screen while it does. That is right for a player who asked to load a map and is waiting for it, and
		/// wrong for scenery quietly filling in behind them.
		/// </summary>
		public static bool TryRequestModel(int hash, out Model model)
		{
			model = null;
			if (hash == 0) return false;

			var requested = new Model(hash);
			if (requested.IsLoaded)
			{
				model = requested;
				return true;
			}

			// A model the game does not have would otherwise be asked for every frame forever. Not marked
			// invalid: it spawned once already, so this is not grounds to blacklist the hash.
			if (!requested.IsValid && !requested.IsInCdImage) return false;

			requested.Request();
			// Started here as well as in PropStreamer.EnsureLoaded, for a head start: this record comes back
			// every frame until the drawable is in, so collision gets all of that time rather than only the
			// few frames the spawn will wait.
			Platform.Natives.RequestCollisionForModel(hash);
			return false;
		}
	}
}
