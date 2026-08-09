using CitizenFX.Core;

namespace MapEditor
{
	/// <summary>
	/// The shape a laser lays its beams out in. The four the game's own heist script has, under the names
	/// the LasersCreator library gave them:
	/// <list type="bullet">
	/// <item><see cref="Grid"/> — a rake of parallel beams fanned across a width (func_29).</item>
	/// <item><see cref="Wall"/> — horizontal beams stacked from the floor upwards (func_46).</item>
	/// <item><see cref="Wave"/> — a curtain whose beams ripple along a sine that scrolls (func_47).</item>
	/// <item><see cref="Single"/> — one straight tripwire (func_30).</item>
	/// </list>
	/// </summary>
	public enum LaserPattern
	{
		Single,
		Grid,
		Wall,
		Wave,
	}

	/// <summary>
	/// How thickly a laser packs its beams and how hard they burn. One knob standing in for three, so that
	/// making a corridor harder is one row of the menu rather than three guesses that have to agree.
	/// </summary>
	public enum LaserDensity
	{
		Sparse,
		Low,
		Medium,
		High,
		Maximum,
	}

	/// <summary>
	/// What the beams do over time. <see cref="Steady"/> is always on; <see cref="Blink"/> turns the whole
	/// laser on and off together; <see cref="Chase"/> runs the on-phase along the beams so a gap travels
	/// across the array and can be timed. The library's keyframe sequencer (func_50–func_61) reduced to the
	/// three shapes anybody actually authors.
	/// </summary>
	public enum LaserRhythm
	{
		Steady,
		Blink,
		Chase,
	}

	/// <summary>
	/// One laser installation in the map being edited.
	///
	/// <b>It is a description, not an entity.</b> Like <see cref="Marker"/> and unlike everything in
	/// <see cref="PropStreamer.StreamedInHandles"/>, a laser exists only for the frame it is drawn in:
	/// <see cref="LaserRenderer"/> rebuilds its beams from these numbers every tick and hands them to the
	/// drawing natives. That is the whole reason lasers are the right thing to put where pickups were.
	/// A pickup was a networked entity whose state — who took it — stopped following from the document the
	/// moment it was created, so it could be neither published nor co-edited (see
	/// <see cref="Platform.SharedObjects"/>). A laser has no state at all: every client computes the same
	/// beams from the same fields, which is exact synchronisation for nothing, and the damage it deals is
	/// dealt by each client to its own player, which is the only place that answer can come from.
	///
	/// Which of the fields below mean anything depends on <see cref="Pattern"/>; the ones that do not are
	/// still carried, so switching the pattern back gets the numbers the builder chose the first time.
	/// </summary>
	public class Laser
	{
		public LaserPattern Pattern = LaserPattern.Grid;

		/// <summary>The middle of the installation. Every pattern is built centred on it.</summary>
		public Vector3 Position;

		/// <summary>
		/// Which way it faces, in the same pitch/roll/yaw the editor's rotation fields use everywhere else
		/// (X pitch, Y roll, Z yaw). <see cref="LaserRenderer"/> turns it into the three axes the patterns
		/// are laid out along.
		/// </summary>
		public Vector3 Rotation;

		/// <summary>How long each beam is, in metres.</summary>
		public float BeamLength = 8f;

		/// <summary>How far across the beams are fanned out, in metres. Unused by <see cref="LaserPattern.Single"/>.</summary>
		public float Width = 6f;

		/// <summary>The floor-to-top span a <see cref="LaserPattern.Wall"/> stacks its rows over, in metres.</summary>
		public float Height = 3f;

		/// <summary>How many beams before <see cref="Density"/> scales it. Always at least one.</summary>
		public int BeamCount = 8;

		public LaserDensity Density = LaserDensity.Medium;

		/// <summary>Half-width of the drawn beam, in metres. Visual only: it does not widen the hit test.</summary>
		public float Thickness = 0.03f;

		public int Red = 255;
		public int Green = 40;
		public int Blue = 40;
		public int Alpha = 255;

		/// <summary>
		/// Draw the beams as the game's own textured, glowing ribbons rather than as flat polygons. On by
		/// default; the flat version is there for a client where the multiplayer texture dictionary the
		/// ribbons come from will not stream in.
		/// </summary>
		public bool Textured = true;

		public LaserRhythm Rhythm = LaserRhythm.Steady;

		/// <summary>Seconds lit, for <see cref="LaserRhythm.Blink"/>.</summary>
		public float OnSeconds = 1.5f;

		/// <summary>Seconds dark, for <see cref="LaserRhythm.Blink"/>.</summary>
		public float OffSeconds = 0.5f;

		/// <summary>Seconds one full pass takes, for <see cref="LaserRhythm.Chase"/>.</summary>
		public float ChasePeriod = 3f;

		/// <summary>How much of the array is lit at once, 0..1, for <see cref="LaserRhythm.Chase"/>.</summary>
		public float ChaseOnFraction = 0.5f;

		/// <summary>Peak sideways displacement of a <see cref="LaserPattern.Wave"/>, in metres.</summary>
		public float Amplitude = 1.5f;

		/// <summary>Spatial frequency of a <see cref="LaserPattern.Wave"/>.</summary>
		public float Frequency = 0.6f;

		/// <summary>How fast a <see cref="LaserPattern.Wave"/> scrolls.</summary>
		public float Speed = 1f;

		/// <summary>
		/// Whether being caught in the beams hurts. With it off the laser still draws and still detects the
		/// player, so it is a tripwire rather than a trap — which is what most of a decorative map wants.
		/// </summary>
		public bool DealsDamage = true;

		/// <summary>
		/// Damage a fully-caught ped takes per second, before <see cref="Density"/>'s multiplier. 250 is the
		/// number the heist script uses (Global_1935234.f_3).
		/// </summary>
		public float DamagePerSecond = 250f;

		/// <summary>
		/// How near the player has to be for the laser to be drawn and tested at all, in metres. Not an
		/// optimisation that can be left out: a map with forty lasers in it would otherwise be forty arrays
		/// of beams drawn and hit-tested every frame from the other side of the city.
		/// </summary>
		public float ActivationRange = 60f;

		/// <summary>
		/// How near a beam has to pass the middle of a ped to count as cutting through them, in metres.
		/// Wider than a beam is drawn, because it is measured against one line down the middle of the ped
		/// rather than against their skeleton: it is the radius of the capsule standing in for the swept
		/// sphere func_33 uses. Widen it for a laser that ought to catch somebody sliding past.
		/// </summary>
		public float HitRadius = 0.35f;

		/// <summary>
		/// A laser the builder put down to line something up rather than to be part of the finished map.
		/// Drawn in the editor only, and left out of a published map, exactly as for <see cref="Marker"/>.
		/// </summary>
		public bool OnlyVisibleInEditor;

		/// <summary>What the entity menu calls it. Local to one editing session, like a marker's.</summary>
		public int Id;

		/// <summary>
		/// What this laser is called inside a co-editing session, or 0 outside one. Runtime only: it is
		/// minted when the session first sees the laser and means nothing once the session ends, so it is
		/// never written to a map file. See <see cref="Collab"/>.
		/// </summary>
		public int Uid;

		/// <summary>
		/// Damage earned this second but not yet whole. Runtime only, and deliberately kept on the laser
		/// rather than in a table beside it: the alternative is a dictionary keyed by an object the editor
		/// creates and throws away all day, which is a leak with no owner. Never written to a map — a saved
		/// laser holds how hard it burns, not how far through burning somebody it had got.
		/// </summary>
		public float DamageDebt;

		/// <summary>Whether the player was in the beams last frame. Runtime only, see <see cref="DamageDebt"/>.</summary>
		public bool WasBurning;
	}
}
