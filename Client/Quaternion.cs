using System;
using CitizenFX.Core;
using CitizenFX.Core.Native;

namespace MapEditor
{
	public class Quaternion
	{
		public float X;
		public float Y;
		public float Z;
		public float W;

		public static void SetEntityQuaternion(Entity ent, Quaternion q)
		{
			Function.Call(Hash.SET_ENTITY_QUATERNION, ent.Handle, q.X, q.Y, q.Z, q.W);
		}

		public static void SetEntityQuaternion(Entity ent, CitizenFX.Core.Quaternion q)
		{
			Function.Call(Hash.SET_ENTITY_QUATERNION, ent.Handle, q.X, q.Y, q.Z, q.W);
		}

		/// <summary>
		/// Through API rather than through Function.Call with OutputArgument, which is what the SP build used:
		/// OutputArgument is missing from the CitizenFX.Core.Client the Enhanced client loads, and naming a
		/// missing type anywhere in a class stops that whole class from loading. See
		/// <see cref="VectorExtensions.WorldToScreenRel"/>, where the same thing killed the freecam.
		/// </summary>
		public static Quaternion GetEntityQuaternion(Entity e)
		{
			float x = 0f, y = 0f, z = 0f, w = 0f;
			API.GetEntityQuaternion(e.Handle, ref x, ref y, ref z, ref w);
			return new Quaternion()
			{
				X = x,
				Y = y,
				Z = z,
				W = w
			};
		}

		public static Quaternion RotationYawPitchRoll(float pitch, float roll, float yaw)
		{
			Quaternion result = new Quaternion();

			pitch = (float)VectorExtensions.DegToRad(pitch);
			roll = (float)VectorExtensions.DegToRad(roll);
			yaw = (float)VectorExtensions.DegToRad(yaw);

			float halfRoll = roll*0.5f;
			float sinRoll = (float) Math.Sin((double) halfRoll);
			float cosRoll = (float) Math.Cos((double) halfRoll);

			float halfPitch = pitch*0.5f;
			float sinPitch = (float) Math.Sin((double) halfPitch);
			float cosPitch = (float) Math.Cos((double) halfPitch);

			float halfYaw = yaw*0.5f;
			float sinYaw = (float) Math.Sin((double) halfYaw);
			float cosYaw = (float) Math.Cos((double) halfYaw);

			result.X = (cosYaw*sinPitch*cosRoll) + (sinYaw*cosPitch*sinRoll);
			result.Y = (sinYaw*cosPitch*cosRoll) - (cosYaw*sinPitch*sinRoll);
			result.Z = (cosYaw*cosPitch*sinRoll) - (sinYaw*sinPitch*cosRoll);
			result.W = (cosYaw*cosPitch*cosRoll) + (sinYaw*sinPitch*sinRoll);

			return result;
		}
	}
}