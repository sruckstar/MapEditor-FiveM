using System;
using System.Collections.Generic;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using MapEditor.Platform;

namespace MapEditor
{
	/// <summary>
	/// Draws the lasers of a map and burns whoever walks into them.
	///
	/// This is the LasersCreator library (../Lasers Creator), which is itself a clean rewrite of the game's
	/// own decompiled laser-grid script <c>fmmc_lasers.c</c>, brought over to FiveM and to a map that is a
	/// document rather than a program. The maths is the same: the four beam generators (func_29 / func_46 /
	/// func_47 / func_30), the four drawing layers of func_31 and func_32, the keyframe on/off sequencer of
	/// func_50–func_61, and the damage-over-time accumulator of func_13. Four things had to change, and
	/// each of them is a consequence of where this now runs.
	///
	/// <b>1. Nothing is an emitter object.</b> There the caller built a <c>LaserArea</c> and handed it
	/// direction vectors; here a laser is <see cref="Laser"/>, a row of numbers in a map file that somebody
	/// edits with a menu. So the patterns are laid out from a position and a rotation, centred on the
	/// position, and the axes come out of <see cref="Axes"/> rather than being passed in.
	///
	/// <b>2. Time is the game's clock, not the laser's.</b> The library measured from the moment an area was
	/// registered, which is a different moment on every machine. Two players standing in front of the same
	/// blinking laser have to see it blink together, and a published map has no shared "start" to measure
	/// from — so the phase is a function of <see cref="Clock.Milliseconds"/>, which every client on a server
	/// reads the same. This is the whole reason a laser can be published and co-edited at all.
	///
	/// <b>3. The narrow phase is geometry rather than a shape test.</b> The original follows its broad-phase
	/// early-out with START_SHAPE_TEST_SWEPT_SPHERE. That native answers through a Vector3 pointer, which the
	/// C# runtime on the Enhanced client cannot push (see <see cref="LuaBridge"/>), and running one per beam
	/// per frame through the Lua bridge would cost more than it is worth. Instead the beam segment is
	/// measured against a capsule standing where the ped stands, in C#, and what the shape test was really
	/// buying — "is there a crate between the emitter and them" — is bought back with a single probe per
	/// laser per frame in <see cref="Blocked"/>.
	///
	/// <b>4. Every call that is not certain to exist can be switched off after it throws once.</b> Six of the
	/// natives below are called by hash because they have no name in the runtime's enum, and a hash that a
	/// future game build moves would otherwise throw on every beam of every frame — which on this client
	/// means a dead tick and nothing in any log (see Platform.Log). Each group is guarded, disabled on its
	/// first failure, and said once in the console; a laser with its glow switched off is still a laser.
	/// </summary>
	public static class LaserRenderer
	{
		// --- Natives called by hash ---------------------------------------------------------------
		//
		// Names and parameters per alloc8or's gta5-nativedb; these have no member in the runtime's Hash
		// enum, which is why they are written out. See the class remarks for what happens when one is wrong.

		private const ulong DRAW_TEXTURED_POLY = 0x29280002282F1928;
		private const ulong DRAW_CAPSULE_LIGHT = 0x330F4FA20FB57738;
		private const ulong DRAW_MARKER_GLOW = 0xE59B0A106CC15FC2;
		private const ulong MAKE_GLOWS_ADDITIVE = 0xDC60226A3F4D9F42;
		private const ulong SET_BLEND_STATE_ADDITIVE = 0x01677A72A8BDCD1A;
		private const ulong SET_BLEND_STATE_NORMAL = 0x976D155439608592;
		private const ulong GENERATE_PED_DAMAGE_EVENT = 0x6D1FCD0950EFA3DD;

		/// <summary>The multiplayer dictionary the game's own laser beams are textured from.</summary>
		private const string BeamTextureDict = "mpinvperscommon";

		private const string BeamCoreTexture = "beam_middle";
		private const string BeamGlowTexture = "beam_glow_tapered";

		/// <summary>
		/// Radius of the volumetric capsule light wrapped round each beam, in metres — the glow that spills
		/// onto the walls and floor. The original animates it between about 0.5 and 3 with the view; a fixed
		/// value reads well for beams that are not part of a cutscene.
		/// </summary>
		private const float GlowRadius = 1.0f;

		/// <summary>Intensity of that light. func_31 uses roughly 10 × its attenuation.</summary>
		private const float GlowIntensity = 6f;

		/// <summary>
		/// Beams drawn in one frame, across every laser of every map standing. A ceiling rather than a
		/// budget: a map with sixty overlapping lasers in one room is somebody's mistake, and the frame it
		/// costs should be spent finding that out rather than on the floor.
		/// </summary>
		private const int MaxBeamsPerFrame = 400;

		private static bool _glowsOk = true;
		private static bool _texturedOk = true;
		private static bool _blendOk = true;
		private static bool _damageEventOk = true;

		/// <summary>
		/// Whether the game-wide "glows composite additively" flag has been turned on. Set once, the first
		/// time a beam is drawn, and cleared in <see cref="Shutdown"/> — the pairing func_28 does at its own
		/// init and cleanup. It is the game's flag rather than this resource's, so leaving it on would tint
		/// every other script's markers and sprites for the rest of the session.
		/// </summary>
		private static bool _glowsAdditive;

		/// <summary>Whether the additive blend state was switched on this frame and has to be put back.</summary>
		private static bool _blendOnThisFrame;

		/// <summary>Beams drawn so far this frame. Reset by <see cref="BeginFrame"/>.</summary>
		private static int _drawnThisFrame;

		/// <summary>How long the last frame took, in seconds. Taken once, by <see cref="BeginFrame"/>.</summary>
		private static float _frameSeconds;

		private static int _lastFrameStamp;

		/// <summary>Reused so that a frame of beams is not a frame of garbage. See <see cref="BuildBeams"/>.</summary>
		private static readonly List<Beam> Scratch = new List<Beam>();

		/// <summary>One straight beam, as an emitter produced it.</summary>
		public struct Beam
		{
			public Vector3 Start;
			public Vector3 End;

			/// <summary>Which beam of its laser this is, which is what the rhythm switches on and off.</summary>
			public int Index;
		}

		// --- Density ------------------------------------------------------------------------------

		/// <summary>How much a density level multiplies the authored beam count by.</summary>
		public static float CountMultiplier(LaserDensity density)
		{
			switch (density)
			{
				case LaserDensity.Sparse: return 0.45f;
				case LaserDensity.Low: return 0.7f;
				case LaserDensity.High: return 1.4f;
				case LaserDensity.Maximum: return 2f;
				default: return 1f;
			}
		}

		/// <summary>How much a density level multiplies the authored spread by. Below one packs beams tighter.</summary>
		public static float SpacingMultiplier(LaserDensity density)
		{
			switch (density)
			{
				case LaserDensity.Sparse: return 1.6f;
				case LaserDensity.Low: return 1.25f;
				case LaserDensity.High: return 0.8f;
				case LaserDensity.Maximum: return 0.55f;
				default: return 1f;
			}
		}

		/// <summary>How much a density level multiplies damage by.</summary>
		public static float DamageMultiplier(LaserDensity density)
		{
			switch (density)
			{
				case LaserDensity.Sparse: return 0.5f;
				case LaserDensity.Low: return 0.75f;
				case LaserDensity.High: return 1.3f;
				case LaserDensity.Maximum: return 1.6f;
				default: return 1f;
			}
		}

		/// <summary>How many beams a laser actually emits, once its density has had its say.</summary>
		public static int ResolvedBeamCount(Laser laser)
		{
			if (laser.Pattern == LaserPattern.Single) return 1;

			var count = (int) Math.Ceiling(Math.Max(1, laser.BeamCount) * CountMultiplier(laser.Density));
			if (count < 1) count = 1;

			// The ceiling is per laser as well as per frame: a Maximum-density array of two hundred authored
			// beams is four hundred draws on its own, and one laser must not be able to eat the whole frame.
			return count > MaxBeamsPerFrame ? MaxBeamsPerFrame : count;
		}

		// --- Geometry -----------------------------------------------------------------------------

		/// <summary>
		/// The three axes a laser's pattern is laid out along, from its rotation.
		///
		/// <paramref name="forward"/> is where it points, and comes from pitch and yaw the way every other
		/// direction in this editor does. The other two are built perpendicular to it and then rolled around
		/// it, so that roll turns a wall of beams onto its side rather than being quietly ignored.
		/// </summary>
		public static void Axes(Laser laser, out Vector3 forward, out Vector3 right, out Vector3 up)
		{
			forward = Normalize(VectorExtensions.RotationToDirection(laser.Rotation));
			if (forward == Vector3.Zero) forward = new Vector3(0f, 1f, 0f);

			var worldUp = new Vector3(0f, 0f, 1f);

			// Straight up or straight down leaves no horizontal cross product; north stands in, which is the
			// same convention the freecam uses when it is looking at its own feet.
			var flat = Normalize(Cross(forward, worldUp));
			if (flat == Vector3.Zero) flat = new Vector3(1f, 0f, 0f);

			var perp = Cross(flat, forward);

			var roll = DegToRad(laser.Rotation.Y);
			var cos = (float) Math.Cos(roll);
			var sin = (float) Math.Sin(roll);

			right = flat * cos + perp * sin;
			up = perp * cos - flat * sin;
		}

		/// <summary>
		/// This frame's beams for one laser, into <paramref name="into"/> — which is cleared first and is
		/// expected to be reused, because a laser rebuilds its beams on every one of sixty frames a second.
		///
		/// The four patterns are func_29, func_46, func_47 and func_30, all re-centred on
		/// <see cref="Laser.Position"/> so that a laser turns and moves about the middle of itself: the
		/// library anchored them at a corner, which is right when a script places them by hand and wrong when
		/// somebody is dragging one around with a mouse.
		/// </summary>
		public static void BuildBeams(Laser laser, float time, List<Beam> into)
		{
			into.Clear();
			if (laser == null) return;

			Vector3 forward, right, up;
			Axes(laser, out forward, out right, out up);

			var count = ResolvedBeamCount(laser);
			var spacing = SpacingMultiplier(laser.Density);
			var length = Math.Max(0.05f, laser.BeamLength);
			var half = forward * (length * 0.5f);

			switch (laser.Pattern)
			{
				case LaserPattern.Single:
				{
					into.Add(NewBeam(laser.Position - half, laser.Position + half, 0));
					return;
				}

				case LaserPattern.Grid:
				{
					var width = Math.Max(0f, laser.Width) * spacing;
					var step = count > 1 ? width / (count - 1) : 0f;
					var first = laser.Position - right * (width * 0.5f);

					for (var i = 0; i < count; i++)
					{
						var centre = first + right * (step * i);
						into.Add(NewBeam(centre - half, centre + half, i));
					}
					return;
				}

				case LaserPattern.Wall:
				{
					// Rows climb the up axis and each beam runs along the right axis, which is what makes a
					// wall a wall however the laser is turned.
					var height = Math.Max(0f, laser.Height) * spacing;
					var step = count > 1 ? height / (count - 1) : 0f;
					var first = laser.Position - up * (height * 0.5f);
					var run = right * (length * 0.5f);

					for (var i = 0; i < count; i++)
					{
						var centre = first + up * (step * i);
						into.Add(NewBeam(centre - run, centre + run, i));
					}
					return;
				}

				default:
				{
					// Wave: the row is laid along the right axis and each beam's start is pushed along the up
					// axis by a sine that scrolls with time — func_47's f_9 / f_10 / f_11.
					var width = Math.Max(0f, laser.Width) * spacing;
					var step = count > 1 ? width / (count - 1) : 0f;
					var first = laser.Position - right * (width * 0.5f);
					var phase = time * laser.Speed;

					for (var i = 0; i < count; i++)
					{
						var along = step * i;
						var swing = laser.Amplitude * (float) Math.Sin((along + phase) * laser.Frequency);
						var centre = first + right * along + up * swing;
						into.Add(NewBeam(centre - half, centre + half, i));
					}
					return;
				}
			}
		}

		/// <summary>
		/// Whether beam <paramref name="index"/> of <paramref name="laser"/> is lit at <paramref name="time"/>.
		///
		/// The library gated whole emitters, because an area there held several; a laser here is one array,
		/// so the rhythm runs along its beams instead. That is what makes <see cref="LaserRhythm.Chase"/>
		/// worth having: a gap travelling across a grating is something a player can time their way through,
		/// while a whole grating winking on and off is the blink they already have.
		/// </summary>
		public static bool IsLit(Laser laser, int index, float time)
		{
			switch (laser.Rhythm)
			{
				case LaserRhythm.Blink:
				{
					var on = Math.Max(0f, laser.OnSeconds);
					var period = on + Math.Max(0f, laser.OffSeconds);
					if (period <= 0.0001f) return true;
					return time - (float) Math.Floor(time / period) * period < on;
				}

				case LaserRhythm.Chase:
				{
					var period = Math.Max(0.0001f, laser.ChasePeriod);
					var count = Math.Max(1, ResolvedBeamCount(laser));
					var fraction = Clamp01(laser.ChaseOnFraction);

					var raw = time / period + (float) index / count;
					var phase = raw - (float) Math.Floor(raw);
					return phase < fraction;
				}

				default:
					return true;
			}
		}

		// --- The frame ----------------------------------------------------------------------------

		/// <summary>
		/// Called once at the top of a frame, before any laser is drawn: resets the frame's beam ceiling and
		/// takes the one clock reading the whole frame's damage is measured against.
		///
		/// The blend state is deliberately <b>not</b> switched here. This runs on every frame of the session,
		/// including the overwhelming majority that have no laser anywhere near the player, and two native
		/// calls a frame to bracket nothing is two native calls a frame. It goes on with the first beam
		/// instead, and <see cref="EndFrame"/> only puts it back if one was drawn.
		/// </summary>
		public static void BeginFrame()
		{
			_drawnThisFrame = 0;

			// Once a frame rather than once per burning laser: read per laser, the second laser to burn
			// somebody in the same frame would measure zero elapsed time and never deal a whole point.
			var now = Clock.Milliseconds;
			var elapsed = _lastFrameStamp == 0 ? 0 : Clock.Since(_lastFrameStamp);
			_lastFrameStamp = now;

			// Clamped, because a loading hitch must not deliver two seconds of laser in one frame.
			_frameSeconds = elapsed <= 0 ? 0f : (elapsed > 250 ? 0.25f : elapsed / 1000f);
		}

		/// <summary>Called once at the bottom of a frame. Restores normal blending. See <see cref="BeginFrame"/>.</summary>
		public static void EndFrame()
		{
			if (!_blendOnThisFrame) return;
			_blendOnThisFrame = false;
			SetBlendState(false);
		}

		/// <summary>
		/// The clock every laser's rhythm and wave is a function of, in seconds.
		///
		/// The game's own timer rather than a moment this laser was created at, and that is the point: it is
		/// the same number on every client of a server to within a frame, so a blinking laser blinks together
		/// for everybody looking at it and a published map needs nothing sent to keep it that way. See the
		/// class remarks.
		/// </summary>
		public static float Time
		{
			get { return Clock.Milliseconds / 1000f; }
		}

		/// <summary>
		/// Draws one laser and, if <paramref name="mayBurn"/>, burns the player standing in it.
		///
		/// <paramref name="cameraPosition"/> is the eye the beam ribbons are turned to face — the freecam's
		/// while the editor has one, the gameplay camera's otherwise. It is passed in rather than read here
		/// so that this class needs to know nothing about which of the two is rendering.
		/// </summary>
		public static void Tick(Laser laser, Vector3 cameraPosition, bool mayBurn)
		{
			if (laser == null) return;

			var player = Game.Player.Character;
			var alive = player != null && player.Exists() && !player.IsDead;
			var here = alive ? player.Position : cameraPosition;

			// Out of range: nothing drawn, nothing tested, and the burn forgotten — walking back in has to
			// start a fresh contact rather than resume one from wherever the player last left.
			if (laser.ActivationRange > 0f && Distance(here, laser.Position) > laser.ActivationRange)
			{
				laser.DamageDebt = 0f;
				laser.WasBurning = false;
				return;
			}

			if (_drawnThisFrame >= MaxBeamsPerFrame) return;

			var time = Time;
			BuildBeams(laser, time, Scratch);
			if (Scratch.Count == 0) return;

			if (laser.Textured && _texturedOk) RequestBeamAssets();

			// Chest height, as func_33 tests from: the ped's own position sits at their feet, and a beam at
			// ankle height would otherwise be the only one that could ever be found near them.
			var low = alive ? player.Position + new Vector3(0f, 0f, 0.3f) : Vector3.Zero;
			var high = alive ? player.Position + new Vector3(0f, 0f, 1.6f) : Vector3.Zero;

			var burning = 0;
			var haveHit = false;
			var hitPoint = Vector3.Zero;
			var hitFrom = Vector3.Zero;

			for (var i = 0; i < Scratch.Count; i++)
			{
				if (_drawnThisFrame >= MaxBeamsPerFrame) break;

				var beam = Scratch[i];
				if (!IsLit(laser, beam.Index, time)) continue;

				DrawBeam(laser, beam, cameraPosition);
				_drawnThisFrame++;

				if (!alive || !mayBurn) continue;

				Vector3 onBeam;
				var gap = SegmentDistance(beam.Start, beam.End, low, high, out onBeam);

				// The two phases of func_33 collapse into one comparison here: its cheap early-out existed to
				// keep a swept-sphere shape test off, and there is no shape test left to keep off.
				if (gap > Math.Max(0.01f, laser.HitRadius)) continue;

				burning++;
				if (!haveHit)
				{
					haveHit = true;
					hitPoint = onBeam;
					hitFrom = beam.Start;
				}
			}

			if (!haveHit || !alive || !mayBurn)
			{
				laser.DamageDebt = 0f;
				laser.WasBurning = false;
				return;
			}

			// One probe for the whole laser rather than one per beam: what the shape test in the original
			// bought is "is the player actually in the light", and a crate that hides them from the nearest
			// beam hides them from its neighbours too.
			if (Blocked(hitFrom, hitPoint, player))
			{
				laser.DamageDebt = 0f;
				laser.WasBurning = false;
				return;
			}

			Burn(laser, player, hitPoint, burning);
		}

		/// <summary>
		/// Whether something solid stands between the emitter and the point the beam met the player.
		///
		/// The player is what the ray is told to ignore, so a hit means an obstacle and nothing else. This is
		/// the one native call a laser makes per frame, and it is made only once the geometry has already
		/// said somebody is standing in the beam.
		/// </summary>
		private static bool Blocked(Vector3 from, Vector3 to, Ped player)
		{
			// The world, the props in it and whatever any script has spawned. Peds are deliberately left out:
			// the only one that could be in the way is the player, and the player is what the ray ignores.
			const IntersectOptions solid =
				IntersectOptions.Map | IntersectOptions.MissionEntities | IntersectOptions.Objects;

			var probe = LuaBridge.Probe(from, to, solid, player);

			// No answer is not an obstacle: the bridge being unavailable must not quietly switch every laser
			// in the map off. See LuaBridge.ProbeResult.Answered.
			return probe.Answered && probe.DidHit;
		}

		/// <summary>
		/// Damage over time, the accumulator of func_13: rate × beams × frame, applied in whole points, with
		/// the strike flinch at the hit point and the victim set alight when a laser is what killed them.
		/// </summary>
		private static void Burn(Laser laser, Ped player, Vector3 hitPoint, int beams)
		{
			if (!laser.DealsDamage)
			{
				// A tripwire still counts as contact, so that switching damage back on starts from nothing
				// rather than from however long somebody had been standing in a harmless beam.
				laser.DamageDebt = 0f;
				laser.WasBurning = true;
				return;
			}

			var dps = laser.DamagePerSecond * DamageMultiplier(laser.Density);
			laser.DamageDebt += dps * beams * _frameSeconds;

			if (_damageEventOk && !player.IsRagdoll)
			{
				try
				{
					Function.Call((Hash) GENERATE_PED_DAMAGE_EVENT, player.Handle,
						hitPoint.X, hitPoint.Y, hitPoint.Z, (uint) WeaponHash.Pistol);
				}
				catch (Exception e)
				{
					_damageEventOk = false;
					Log.Info("LaserRenderer: the ped damage event native is unavailable ({0}); " +
					         "lasers still burn, without the flinch.", e.Message);
				}
			}

			var whole = (int) Math.Floor(laser.DamageDebt);
			if (whole > 0)
			{
				laser.DamageDebt -= whole;
				Function.Call(Hash.APPLY_DAMAGE_TO_PED, player.Handle, whole, true, 0);

				if (player.IsDead) Function.Call(Hash.START_ENTITY_FIRE, player.Handle);
			}

			laser.WasBurning = true;
		}

		// --- Drawing ------------------------------------------------------------------------------

		/// <summary>
		/// One beam, in the four layers func_31 draws it in: a volumetric capsule light along its length, a
		/// glowing dot where each end meets a surface, a soft tapered-glow ribbon and a bright core ribbon.
		/// The two ribbons face the camera, which is the C# equivalent of func_32's orientation natives —
		/// they take an in/out Vector3, which is the one shape this runtime cannot hand a native at all.
		/// </summary>
		private static void DrawBeam(Laser laser, Beam beam, Vector3 cameraPosition)
		{
			var delta = beam.End - beam.Start;
			var length = delta.Length();
			if (length < 0.0001f) return;

			// The first beam of the frame is what turns additive compositing on; see BeginFrame for why it is
			// not switched at the top of every frame instead.
			if (!_blendOnThisFrame)
			{
				_blendOnThisFrame = true;
				SetBlendState(true);
			}

			var dir = delta / length;
			var mid = (beam.Start + beam.End) * 0.5f;

			var r = Clamp255(laser.Red);
			var g = Clamp255(laser.Green);
			var b = Clamp255(laser.Blue);
			var a = Clamp255(laser.Alpha);

			if (_glowsOk)
			{
				try
				{
					Function.Call((Hash) DRAW_CAPSULE_LIGHT,
						mid.X, mid.Y, mid.Z, dir.X, dir.Y, dir.Z,
						r, g, b, GlowRadius, GlowIntensity, Math.Max(0.05f, length - 0.05f), 250f);

					DrawMarkerGlow(beam.Start + dir * 0.05f, r, g, b);
					DrawMarkerGlow(beam.End - dir * 0.05f, r, g, b);
				}
				catch (Exception e)
				{
					_glowsOk = false;
					Log.Info("LaserRenderer: the beam glow natives are unavailable ({0}); " +
					         "lasers are drawn without their light.", e.Message);
				}
			}

			// The ribbon's length axis is the beam and its width axis is perpendicular to both the beam and
			// the line of sight, which is what turns its flat face towards the eye.
			var toCam = cameraPosition - mid;
			if (toCam.LengthSquared() < 0.0001f) toCam = new Vector3(0f, 0f, 1f);
			toCam = Normalize(toCam);

			var across = Normalize(Cross(dir, toCam));
			if (across == Vector3.Zero) across = Normalize(Cross(dir, new Vector3(0f, 0f, 1f)));
			if (across == Vector3.Zero) return;

			var thickness = Math.Max(0.001f, laser.Thickness);
			var halfLen = length * 0.5f;

			if (laser.Textured && _texturedOk && BeamAssetsReady)
			{
				// func_32 overshoots the ends slightly, which is what keeps the tapered glow from being cut
				// square where the beam stops.
				Quad(mid, dir, across, halfLen + 0.1f, thickness * 1.7f, r, g, b, a, BeamGlowTexture);
				Quad(mid, dir, across, halfLen + 0.1f, thickness, Whiten(r), Whiten(g), Whiten(b), a, BeamCoreTexture);
				return;
			}

			// Texture-free stand-in: under additive blending the two layers sum into a glowing core, which is
			// close enough that a client whose texture dictionary never arrives still sees a laser.
			Quad(mid, dir, across, halfLen, thickness * 1.6f, r, g, b, Math.Min(a, 200), null);
			Quad(mid, dir, across, halfLen, thickness * 0.5f, Whiten(r), Whiten(g), Whiten(b), a, null);
		}

		private static void DrawMarkerGlow(Vector3 position, int r, int g, int b)
		{
			Function.Call((Hash) DRAW_MARKER_GLOW, position.X, position.Y, position.Z, 0.116f, r, g, b, 0.9f);
		}

		/// <summary>
		/// One beam-body quad, as two triangles with func_32's winding: corners (-w,+L) (+w,+L) (-w,-L)
		/// (+w,-L), triangles (v3,v0,v9) and (v6,v9,v0). Drawn from both sides, because a billboard computed
		/// in C# does not promise which of its faces ends up towards the eye and a laser that vanishes when
		/// you walk round it is worse than twice the draw calls.
		/// </summary>
		private static void Quad(Vector3 mid, Vector3 along, Vector3 across, float halfLen, float halfWidth,
			int r, int g, int b, int a, string texture)
		{
			var l = along * halfLen;
			var w = across * halfWidth;

			var v0 = mid - w + l;
			var v3 = mid + w + l;
			var v6 = mid - w - l;
			var v9 = mid + w - l;

			if (texture != null)
			{
				try
				{
					TexturedTriangle(v3, v0, v9, r, g, b, a, texture);
					TexturedTriangle(v6, v9, v0, r, g, b, a, texture);
					TexturedTriangle(v9, v0, v3, r, g, b, a, texture);
					TexturedTriangle(v0, v9, v6, r, g, b, a, texture);
					return;
				}
				catch (Exception e)
				{
					_texturedOk = false;
					Log.Info("LaserRenderer: the textured polygon native is unavailable ({0}); " +
					         "lasers fall back to plain polygons.", e.Message);
				}
			}

			SolidTriangle(v3, v0, v9, r, g, b, a);
			SolidTriangle(v6, v9, v0, r, g, b, a);
			SolidTriangle(v9, v0, v3, r, g, b, a);
			SolidTriangle(v0, v9, v6, r, g, b, a);
		}

		private static void SolidTriangle(Vector3 v1, Vector3 v2, Vector3 v3, int r, int g, int b, int a)
		{
			Function.Call(Hash.DRAW_POLY,
				v1.X, v1.Y, v1.Z, v2.X, v2.Y, v2.Z, v3.X, v3.Y, v3.Z, r, g, b, a);
		}

		private static void TexturedTriangle(Vector3 v1, Vector3 v2, Vector3 v3, int r, int g, int b, int a,
			string texture)
		{
			Function.Call((Hash) DRAW_TEXTURED_POLY,
				v1.X, v1.Y, v1.Z, v2.X, v2.Y, v2.Z, v3.X, v3.Y, v3.Z,
				r, g, b, a,
				BeamTextureDict, texture,
				1f, 0f, 1f, 0f, 0f, 1f, 1f, 1f, 1f);
		}

		// --- Render state and assets --------------------------------------------------------------

		private static void RequestBeamAssets()
		{
			if (!BeamAssetsReady)
				Function.Call(Hash.REQUEST_STREAMED_TEXTURE_DICT, BeamTextureDict, false);
		}

		private static bool BeamAssetsReady
		{
			get { return Function.Call<bool>(Hash.HAS_STREAMED_TEXTURE_DICT_LOADED, BeamTextureDict); }
		}

		/// <summary>
		/// Switches additive compositing on for the beams and off again afterwards. Two natives rather than
		/// one: the first is the game-wide "glows add" flag func_28 pairs at init and cleanup — so it is set
		/// once and left — the second the per-frame blend state the polygons themselves are drawn under.
		/// </summary>
		private static void SetBlendState(bool additive)
		{
			if (!_blendOk) return;

			try
			{
				if (additive && !_glowsAdditive)
				{
					Function.Call((Hash) MAKE_GLOWS_ADDITIVE, true);
					_glowsAdditive = true;
				}

				Function.Call((Hash) (additive ? SET_BLEND_STATE_ADDITIVE : SET_BLEND_STATE_NORMAL));
			}
			catch (Exception e)
			{
				_blendOk = false;
				Log.Info("LaserRenderer: the blend state natives are unavailable ({0}); " +
				         "lasers are drawn without additive glow.", e.Message);
			}
		}

		/// <summary>
		/// Puts the render state back the way it was found. Called when the resource stops: see
		/// <see cref="_glowsAdditive"/> for why leaving it on is not this resource's to do.
		/// </summary>
		public static void Shutdown()
		{
			if (!_glowsAdditive || !_blendOk) return;
			_glowsAdditive = false;

			try
			{
				Function.Call((Hash) SET_BLEND_STATE_NORMAL);
				Function.Call((Hash) MAKE_GLOWS_ADDITIVE, false);
			}
			catch (Exception e)
			{
				Log.Error("LaserRenderer.Shutdown", e);
			}
		}

		// --- Small maths --------------------------------------------------------------------------

		private static Beam NewBeam(Vector3 start, Vector3 end, int index)
		{
			var beam = new Beam();
			beam.Start = start;
			beam.End = end;
			beam.Index = index;
			return beam;
		}

		/// <summary>
		/// The shortest distance between two segments, and — through <paramref name="onFirst"/> — where on
		/// the first one it is measured from. The narrow phase: the first segment is the beam, the second the
		/// line down the middle of the ped, and the answer against <see cref="PedRadius"/> is whether the
		/// beam is cutting through them. Standard clamped parametric solve, with the parallel case split off
		/// because its denominator is zero.
		/// </summary>
		private static float SegmentDistance(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 onFirst)
		{
			var d1 = q1 - p1;
			var d2 = q2 - p2;
			var r = p1 - p2;

			var a = Dot(d1, d1);
			var e = Dot(d2, d2);
			var f = Dot(d2, r);

			float s, t;

			if (a <= 1e-6f && e <= 1e-6f)
			{
				onFirst = p1;
				return (p1 - p2).Length();
			}

			if (a <= 1e-6f)
			{
				s = 0f;
				t = Clamp01(f / e);
			}
			else
			{
				var c = Dot(d1, r);
				if (e <= 1e-6f)
				{
					t = 0f;
					s = Clamp01(-c / a);
				}
				else
				{
					var b = Dot(d1, d2);
					var denom = a * e - b * b;

					s = denom > 1e-6f ? Clamp01((b * f - c * e) / denom) : 0f;
					t = (b * s + f) / e;

					if (t < 0f)
					{
						t = 0f;
						s = Clamp01(-c / a);
					}
					else if (t > 1f)
					{
						t = 1f;
						s = Clamp01((b - c) / a);
					}
				}
			}

			onFirst = p1 + d1 * s;
			return (onFirst - (p2 + d2 * t)).Length();
		}

		private static float Distance(Vector3 a, Vector3 b)
		{
			return (a - b).Length();
		}

		private static float Dot(Vector3 a, Vector3 b)
		{
			return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
		}

		private static Vector3 Cross(Vector3 a, Vector3 b)
		{
			return new Vector3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
		}

		private static Vector3 Normalize(Vector3 v)
		{
			var length = v.Length();
			return length < 1e-5f ? Vector3.Zero : v / length;
		}

		private static float DegToRad(float degrees)
		{
			return (float) (degrees * Math.PI / 180.0);
		}

		private static float Clamp01(float value)
		{
			if (value < 0f) return 0f;
			return value > 1f ? 1f : value;
		}

		private static int Clamp255(int value)
		{
			if (value < 0) return 0;
			return value > 255 ? 255 : value;
		}

		/// <summary>Pushes a channel towards white, so the core of the beam is over-bright like a real laser.</summary>
		private static int Whiten(int channel)
		{
			var value = channel + 160;
			return value > 255 ? 255 : value;
		}
	}
}
