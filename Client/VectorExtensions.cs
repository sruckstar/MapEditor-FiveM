using System;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using MapEditor.Platform;

namespace MapEditor
{
    public static class VectorExtensions
    {
        /// <summary>
        /// How far up or down the camera may look. Straight up and straight down are left out on purpose:
        /// at exactly 90 degrees the heading of the camera stops being defined.
        /// </summary>
        private const float MaxCameraPitch = 89f;

        /// <summary>
        /// Keeps a camera rotation in the range the game accepts. Pitched past vertical, the game flips the
        /// camera over instead: it mirrors the pitch back under 90 degrees and hands back 180 degrees of roll.
        /// That roll is what knocks the camera off its axis, and it sticks because it gets read back in on
        /// every following frame.
        /// </summary>
        public static Vector3 ClampCameraRotation(Vector3 rotation)
        {
            var pitch = rotation.X;
            if (pitch > MaxCameraPitch) pitch = MaxCameraPitch;
            else if (pitch < -MaxCameraPitch) pitch = -MaxCameraPitch;

            // Roll goes to zero: freelook never rolls on its own, so anything left in there is the flip above.
            return new Vector3(pitch, 0f, (float)BoundRotationDeg(rotation.Z));
        }

        public static float Denormalize(this float h)
        {
            return h < 0f ? h + 360f : h;
        }

        public static Vector3 Denormalize(this Vector3 v)
        {
            return new Vector3(v.X.Denormalize(), v.Y.Denormalize(), v.Z.Denormalize());
        }

        public static float ToRadians(this float val)
        {
            return (float)(Math.PI / 180) * val;
        }

        public static float ToDegrees(this float val)
        {
            return (float) (val*(180/Math.PI));
        }

        public static Vector3 TransformVector(this Vector3 i, Func<float, float> method)
        {
            return new Vector3()
            {
                X = method(i.X),
                Y = method(i.Y),
                Z = method(i.Z),
            };
        }

        public static Vector3 ToEuler(this CitizenFX.Core.Quaternion q)
        {
            var pitchYawRoll = new Vector3();

            double sqw = q.W * q.W;
            double sqx = q.X * q.X;
            double sqy = q.Y * q.Y;
            double sqz = q.Z * q.Z;

            pitchYawRoll.Y = (float)Math.Atan2(2f * q.X * q.W + 2f * q.Y * q.Z, 1 - 2f * (sqz + sqw));     // Yaw
            pitchYawRoll.X = (float)Math.Asin(2f * (q.X * q.Z - q.W * q.Y));                             // Pitch
            pitchYawRoll.Z = (float)Math.Atan2(2f * q.X * q.Y + 2f * q.Z * q.W, 1 - 2f * (sqy + sqz));

            pitchYawRoll = pitchYawRoll.TransformVector(ToDegrees);

            pitchYawRoll = pitchYawRoll.Denormalize();

            pitchYawRoll = new Vector3()
            {
                Y = pitchYawRoll.Y * -1f + 180f,
                X = pitchYawRoll.X,
                Z = pitchYawRoll.Z,
            };

            return pitchYawRoll;
        }

        public static CitizenFX.Core.Quaternion ToQuaternion(this Vector3 vect)
        {
            vect = new Vector3()
            {
                X = vect.X.Denormalize() * -1f,
                Y = vect.Y.Denormalize() - 180f,
                Z = vect.Z.Denormalize() - 180f,
            };

            vect = vect.TransformVector(ToRadians);

            float rollOver2 = vect.Z * 0.5f;
            float sinRollOver2 = (float)Math.Sin((double)rollOver2);
            float cosRollOver2 = (float)Math.Cos((double)rollOver2);
            float pitchOver2 = vect.Y * 0.5f;
            float sinPitchOver2 = (float)Math.Sin((double)pitchOver2);
            float cosPitchOver2 = (float)Math.Cos((double)pitchOver2);
            float yawOver2 = vect.X * 0.5f; // pitch
            float sinYawOver2 = (float)Math.Sin((double)yawOver2);
            float cosYawOver2 = (float)Math.Cos((double)yawOver2);
            CitizenFX.Core.Quaternion result = new CitizenFX.Core.Quaternion();
            result.X = cosYawOver2 * cosPitchOver2 * cosRollOver2 + sinYawOver2 * sinPitchOver2 * sinRollOver2;
            result.Y = cosYawOver2 * cosPitchOver2 * sinRollOver2 - sinYawOver2 * sinPitchOver2 * cosRollOver2;
            result.Z = cosYawOver2 * sinPitchOver2 * cosRollOver2 + sinYawOver2 * cosPitchOver2 * sinRollOver2;
            result.W = sinYawOver2 * cosPitchOver2 * cosRollOver2 - cosYawOver2 * sinPitchOver2 * sinRollOver2;
            return result;
        }

        public static Vector3 ForwardVector(this Vector3 vector, float yaw)
        {
            Vector3 right = new Vector3();
            float cos = (float)Math.Cos(yaw + Math.PI/2.0f);
            right.X = (180f/(float)Math.PI)*cos;
            right.Y = 0f;
            float sin = (float) Math.Sin(yaw + Math.PI/2.0f);
            right.Z = (180f/(float) Math.PI)*sin;
            return CrossWith(vector, right);
        }

        public static Vector3 CrossWith(Vector3 left, Vector3 right)
        {
            Vector3 result = new Vector3();
            result.X = left.Y*right.Z - left.Z*right.Y;
            result.Y = left.Z*right.X - left.X*right.Z;
            result.Z = left.X*right.Y - left.Y*right.X;
            return result;
        }

        /// <summary>
        /// The one native the editor calls that hands results back through pointers.
        ///
        /// Not through OutputArgument, which is how every FiveM sample writes this: the type exists in the
        /// CitizenFX.Core.Client the project compiles against but not in the one the Enhanced client loads.
        /// A type that merely names a missing type in a method body cannot be loaded at all, so this took
        /// the whole of VectorExtensions down with it — every call threw TypeLoadException and the editor
        /// died on the first frame of freecam. tools/sandbox-audit checks for this.
        ///
        /// API is the generated wrapper layer over the same natives, present in both, and it takes the
        /// pointer arguments as ref parameters.
        /// </summary>
        public static bool WorldToScreenRel(Vector3 worldCoords, out Vector2 screenCoords)
        {
            float screenX = 0f, screenY = 0f;
            if (!API.GetScreenCoordFromWorldCoord(worldCoords.X, worldCoords.Y, worldCoords.Z, ref screenX, ref screenY))
            {
                screenCoords = new Vector2();
                return false;
            }
            screenCoords = new Vector2((screenX - 0.5f) * 2, (screenY - 0.5f) * 2);
            return true;
        }

        public static Vector3 ScreenRelToWorld(Vector3 camPos, Vector3 camRot, Vector2 coord)
        {
            var camForward = RotationToDirection(camRot);
            var rotUp = camRot + new Vector3(10, 0, 0);
            var rotDown = camRot + new Vector3(-10, 0, 0);
            var rotLeft = camRot + new Vector3(0, 0, -10);
            var rotRight = camRot + new Vector3(0, 0, 10);

            var camRight = RotationToDirection(rotRight) - RotationToDirection(rotLeft);
            var camUp = RotationToDirection(rotUp) - RotationToDirection(rotDown);

            var rollRad = -DegToRad(camRot.Y);

            var camRightRoll = camRight * (float)Math.Cos(rollRad) - camUp * (float)Math.Sin(rollRad);
            var camUpRoll = camRight * (float)Math.Sin(rollRad) + camUp * (float)Math.Cos(rollRad);

            var point3D = camPos + camForward * 10.0f + camRightRoll + camUpRoll;
            Vector2 point2D;
            if (!WorldToScreenRel(point3D, out point2D)) return camPos + camForward * 10.0f;
            var point3DZero = camPos + camForward * 10.0f;
            Vector2 point2DZero;
            if (!WorldToScreenRel(point3DZero, out point2DZero)) return camPos + camForward * 10.0f;

            const double eps = 0.001;
            if (Math.Abs(point2D.X - point2DZero.X) < eps || Math.Abs(point2D.Y - point2DZero.Y) < eps) return camPos + camForward * 10.0f;
            var scaleX = (coord.X - point2DZero.X) / (point2D.X - point2DZero.X);
            var scaleY = (coord.Y - point2DZero.Y) / (point2D.Y - point2DZero.Y);
            var point3Dret = camPos + camForward * 10.0f + camRightRoll * scaleX + camUpRoll * scaleY;
            return point3Dret;
        }

        public static Vector3 RotationToDirection(Vector3 rotation)
        {
            var z = DegToRad(rotation.Z);
            var x = DegToRad(rotation.X);
            var num = Math.Abs(Math.Cos(x));
            return new Vector3
            {
                X = (float)(-Math.Sin(z) * num),
                Y = (float)(Math.Cos(z) * num),
                Z = (float)Math.Sin(x)
            };
        }

        public static Vector3 DirectionToRotation(Vector3 direction)
        {
            direction.Normalize();

            var x = Math.Atan2(direction.Z, direction.Y);
            var y = 0;
            var z = -Math.Atan2(direction.X, direction.Y);

            return new Vector3
            {
                X = (float)RadToDeg(x),
                Y = (float)RadToDeg(y),
                Z = (float)RadToDeg(z)
            };
        }

        public static double DegToRad(double deg)
        {
            return deg * Math.PI / 180.0;
        }

        public static double RadToDeg(double deg)
        {
            return deg * 180.0 / Math.PI;
        }

        public static double BoundRotationDeg(double angleDeg)
        {
            var twoPi = (int)(angleDeg / 360);
            var res = angleDeg - twoPi * 360;
            if (res < 0) res += 360;
            return res;
        }

        public static Vector3 RaycastEverything(Vector2 screenCoord)
        {
            var camPos = GameplayCamera.Position;
            var camRot = GameplayCamera.Rotation;

            Entity ignoreEntity = Game.Player.Character;
            if (Game.Player.Character.IsInVehicle())
            {
                ignoreEntity = Game.Player.Character.CurrentVehicle;
            }

            return RaycastEverything(screenCoord, camPos, camRot, ignoreEntity);
        }

        /// <summary>
        /// Why not World.Raycast, which is what the SP build and the first draft of this port both used:
        /// on the Enhanced client it throws before it reaches the game. It reads its answer with
        /// GET_SHAPE_TEST_RESULT, whose out-parameters are Vector3 pointers, and the runtime that client
        /// ships cannot push a Vector3 as a native argument at all. <see cref="LuaBridge"/> has the whole
        /// story; the probe itself is the same native with the same arguments, so the answer is the one
        /// the editor has always worked from.
        /// </summary>
        public static Vector3 RaycastEverything(Vector2 screenCoord, Vector3 camPos, Vector3 camRot, Entity toIgnore)
        {
            const float raycastToDist = 100.0f;
            const float raycastFromDist = 1f;

            var target3D = ScreenRelToWorld(camPos, camRot, screenCoord);
            var source3D = camPos;

            var dir = (target3D - source3D);
            dir.Normalize();
            var hit = LuaBridge.Probe(source3D + dir * raycastFromDist,
                source3D + dir * raycastToDist,
                Compat.EditorIntersectFlags,
                toIgnore);

            if (hit.DidHit)
            {
                return hit.Position;
            }

            return camPos + dir * raycastToDist;
        }

        /// <summary>
        /// What the crosshair is on, by two passes that answer for two different halves of the world.
        ///
        /// The shape test is the real one and covers everything solid — see
        /// <see cref="RaycastEverything(Vector2, Vector3, Vector3, Entity)"/> for why it goes through Lua.
        /// It cannot see the dynamic objects of the map being edited, because those stand there with their
        /// collision switched off and a shape test is a question to the physics world (see
        /// <see cref="PropStreamer.HoldStill"/>). Those are answered for by
        /// <see cref="PropStreamer.PickIntangible"/>, which measures the ray against their model boxes
        /// instead.
        ///
        /// Both passes run every time, and the nearer answer wins. Whichever pass found it, the thing in
        /// front is the thing the player is pointing at: an intangible crate behind a wall must not be
        /// picked through it, and a crate in front of the ground must not lose to the ground.
        /// </summary>
        public static Entity RaycastEntity(Vector2 screenCoord, Vector3 camPos, Vector3 camRot)
        {
            const float raycastToDist = 100.0f;
            const float raycastFromDist = 1f;

            var target3D = ScreenRelToWorld(camPos, camRot, screenCoord);
            var source3D = camPos;

            Entity ignoreEntity = Game.Player.Character;

            var dir = (target3D - source3D);
            dir.Normalize();

            var from = source3D + dir * raycastFromDist;
            var to = source3D + dir * raycastToDist;

            var hit = LuaBridge.Probe(from, to, Compat.EditorIntersectFlags, ignoreEntity);

            // Zero is the map itself, and Entity.FromHandle of it would be an entity that does not exist.
            var solid = hit.EntityHandle == 0 ? null : Entity.FromHandle(hit.EntityHandle);

            // How far the ray got, whatever it met — the ground counts. A ray that met nothing at all, or
            // that could not be fired, is answered as its far end, so anything intangible along it wins.
            var solidDistance = hit.DidHit ? (hit.Position - from).Length() : raycastToDist;

            float intangibleDistance;
            var intangible = PropStreamer.PickIntangible(from, to, out intangibleDistance);

            return intangible != null && intangibleDistance < solidDistance ? intangible : solid;
        }

        /// <summary>
        /// Where a segment first enters an axis-aligned box, as a fraction of its own length, or -1 if it
        /// never does. Zero means it started inside.
        ///
        /// The slab test: a box is three pairs of parallel planes, and the segment is inside the box over
        /// the stretch it is inside all three pairs at once. Each pair narrows the stretch, and the moment
        /// the stretch closes there is nothing left to find.
        ///
        /// Axis-aligned is not a limitation here: the caller works in the entity's own frame, where the
        /// model box is axis-aligned by definition however the entity is turned in the world.
        /// </summary>
        public static float SegmentEntersBox(Vector3 from, Vector3 to, Vector3 min, Vector3 max)
        {
            var delta = to - from;
            var enter = 0f;
            var exit = 1f;

            if (!Slab(from.X, delta.X, min.X, max.X, ref enter, ref exit)) return -1f;
            if (!Slab(from.Y, delta.Y, min.Y, max.Y, ref enter, ref exit)) return -1f;
            if (!Slab(from.Z, delta.Z, min.Z, max.Z, ref enter, ref exit)) return -1f;

            return enter;
        }

        /// <summary>One pair of the box's faces. False once the surviving stretch of the segment is empty.</summary>
        private static bool Slab(float origin, float delta, float min, float max, ref float enter, ref float exit)
        {
            // Running parallel to this pair: the segment is either between the two faces for its whole
            // length or it misses the box outright, and dividing by the delta would say neither.
            const float parallel = 1e-6f;
            if (Math.Abs(delta) < parallel) return origin >= min && origin <= max;

            var t1 = (min - origin) / delta;
            var t2 = (max - origin) / delta;
            if (t1 > t2)
            {
                var swap = t1;
                t1 = t2;
                t2 = swap;
            }

            if (t1 > enter) enter = t1;
            if (t2 < exit) exit = t2;
            return enter <= exit;
        }
    }
}
