using System.Threading.Tasks;
using CitizenFX.Core;
using CitizenFX.Core.UI;
using MapEditor.Platform;

namespace MapEditor
{
    public static class Extensions
    {
        public static string Limit(this string s, int limit)
        {
            if (s == null) return null;
            if (s.Length > limit) return s.Substring(0, limit);
            return s;
        }

        /// <summary>
        /// Replaces NativeUI's MiscExtensions.LinearVectorLerp.
        /// </summary>
        public static Vector3 LinearVectorLerp(Vector3 start, Vector3 end, int currentTime, int duration)
        {
            return new Vector3
            {
                X = LinearFloatLerp(start.X, end.X, currentTime, duration),
                Y = LinearFloatLerp(start.Y, end.Y, currentTime, duration),
                Z = LinearFloatLerp(start.Z, end.Z, currentTime, duration),
            };
        }

        public static float LinearFloatLerp(float start, float end, int currentTime, int duration)
        {
            float change = end - start;
            return change * currentTime / duration + start;
        }
    }

    /// <summary>
    /// The SP build's shims for the ScriptHookVDotNet 2 APIs that version 3 removed. CitizenFX.Core is
    /// modelled on SHVDN 2, so most of them are no longer needed — the class stays so that call sites
    /// read the same in both ports, and so the FiveM-only substitutions have somewhere to live.
    /// </summary>
    public static class Compat
    {
        public static void Notify(string message)
        {
            Notifier.Notify(message);
        }

        /// <summary>
        /// CitizenFX keeps the SHVDN 2 public constructors, so wrapping a handle no longer needs
        /// Entity.FromHandle — but FromHandle is what returns null for a handle whose entity is gone,
        /// which is the behaviour every call site here relies on.
        /// </summary>
        public static Entity Ent(int handle)
        {
            return Entity.FromHandle(handle);
        }

        public static Prop PropFrom(int handle)
        {
            return Entity.FromHandle(handle) as Prop;
        }

        public static Ped PedFrom(int handle)
        {
            return Entity.FromHandle(handle) as Ped;
        }

        public static Vehicle VehicleFrom(int handle)
        {
            return Entity.FromHandle(handle) as Vehicle;
        }

        /// <summary>
        /// The on-screen keyboard. Unlike the SP build this is asynchronous, because FiveM has no fibers
        /// to block on — see <see cref="Platform.Frame"/>. Ctrl+V pasting is gone with it; see
        /// <see cref="Platform.TextInput"/>.
        /// </summary>
        public static Task<string> GetUserInput(int maxLength)
        {
            return TextInput.Get(string.Empty, maxLength);
        }

        public static Task<string> GetUserInput(string defaultText, int maxLength)
        {
            return TextInput.Get(defaultText, maxLength);
        }

        /// <summary>
        /// The raycast filter the editor has always used: map + mission entities/vehicles + ped capsules
        /// and ragdolls + objects + foliage. CitizenFX exposes SHVDN 2's IntersectOptions, where the two
        /// ped bits are folded into a single Peds1 (4|8) member.
        /// </summary>
        public const IntersectOptions EditorIntersectFlags =
            IntersectOptions.Map | IntersectOptions.MissionEntities | IntersectOptions.Peds1 |
            IntersectOptions.Objects | IntersectOptions.Vegetation;
    }
}
