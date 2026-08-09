using CitizenFX.Core;
using CitizenFX.Core.Native;

namespace MapEditor.Platform
{
    /// <summary>
    /// FiveM-only natives with no equivalent in the SP build.
    /// </summary>
    public static class Natives
    {
        // --- Entity creation ----------------------------------------------------------------------

        /// <summary>
        /// Editor objects are local by default: on a server, a networked entity is replicated to every
        /// player and counts against the OneSync entity budget, and dragging one object with the mouse
        /// would push hundreds of creations through the network. Published maps are shared as data and
        /// re-spawned locally by each client instead.
        /// </summary>
        public static int CreateObjectNoOffset(int modelHash, Vector3 position, bool dynamic, bool networked)
        {
            return Function.Call<int>(Hash.CREATE_OBJECT_NO_OFFSET, modelHash,
                position.X, position.Y, position.Z, networked, networked, dynamic);
        }

        /// <summary>
        /// The ped type CitizenFX's own World.CreatePed passes, kept so that a ped the editor spawns is the
        /// same kind of ped as one spawned by any other script.
        /// </summary>
        private const int MissionPedType = 26;

        /// <summary>
        /// Why not World.CreatePed: it spawns networked, with no way to ask for anything else. Everything
        /// above about objects goes double for peds — they are the most expensive thing on the network
        /// budget the editor can put down.
        /// </summary>
        public static int CreatePed(int modelHash, Vector3 position, float heading, bool networked)
        {
            return Function.Call<int>(Hash.CREATE_PED, MissionPedType, modelHash,
                position.X, position.Y, position.Z, heading, networked, networked);
        }

        /// <summary>Local by default for the same reason as everything else here. See <see cref="CreateObjectNoOffset"/>.</summary>
        public static int CreateVehicle(int modelHash, Vector3 position, float heading, bool networked)
        {
            return Function.Call<int>(Hash.CREATE_VEHICLE, modelHash,
                position.X, position.Y, position.Z, heading, networked, networked);
        }

        public static void SetEntityQuaternion(Entity entity, Quaternion q)
        {
            Function.Call(Hash.SET_ENTITY_QUATERNION, entity.Handle, q.X, q.Y, q.Z, q.W);
        }

        /// <summary>
        /// A point of the world in an entity's own frame, which is where a model's bounding box is
        /// axis-aligned however the entity is turned. The inverse of the offset call the bounding boxes
        /// are drawn from.
        ///
        /// The typed wrapper rather than <c>Function.Call&lt;Vector3&gt;</c>: the generic call cannot hand
        /// back a vector on Enhanced. The native takes its arguments by value, so unlike the shape test it
        /// needs no help from Lua — see <see cref="LuaBridge"/>.
        /// </summary>
        public static Vector3 OffsetFromEntity(int handle, Vector3 world)
        {
            return API.GetOffsetFromEntityGivenWorldCoords(handle, world.X, world.Y, world.Z);
        }

        // --- Collision ----------------------------------------------------------------------------
        //
        // A model's drawable and its collision stream in separately, and REQUEST_MODEL is satisfied by the
        // drawable alone. A prop spawned on that looks finished but cannot be touched: the player walks
        // through it, the placement raycast passes over it, and stacking has nothing to land on. These are
        // the two natives the game's own scripts use to wait for the other half.

        public static void RequestCollisionForModel(int modelHash)
        {
            Function.Call(Hash.REQUEST_COLLISION_FOR_MODEL, modelHash);
        }

        public static bool HasCollisionForModelLoaded(int modelHash)
        {
            return Function.Call<bool>(Hash.HAS_COLLISION_FOR_MODEL_LOADED, modelHash);
        }

        // --- Liveries -----------------------------------------------------------------------------
        //
        // A vehicle's livery lives in one of two places, and which one depends on the model. The older
        // vehicles have a livery of their own (SET_VEHICLE_LIVERY, counted by GET_VEHICLE_LIVERY_COUNT);
        // everything added since keeps its liveries as mods in slot 48, where the livery natives cannot
        // see them at all — SET_VEHICLE_LIVERY does nothing and GET_VEHICLE_LIVERY answers -1 for ever.
        //
        // These three exist so that the one rule for choosing between them is written down once. It used
        // to be CitizenFX's VehicleModCollection.Livery, and that is not safe to use: its getter returns
        // the number of liveries rather than the one being worn whenever the vehicle is of the second
        // kind (the property's IL is a copy of LiveryCount's). A map saved through it records a livery
        // index one past the end of the list, and nothing can put that back on afterwards.

        /// <summary>The mod slot the newer vehicles keep their liveries in. VehicleModType.Livery.</summary>
        private const int LiveryModType = 48;

        /// <summary>
        /// Installs the mod kit, without which the game reports no mods and no liveries on any vehicle,
        /// including the ones that have them. Cheap and idempotent, so every reader below starts with it
        /// rather than relying on somebody having done it earlier.
        /// </summary>
        private static void InstallModKit(int handle)
        {
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, handle, 0);
        }

        /// <summary>How many liveries the vehicle's model carries. Zero or -1 when it carries none.</summary>
        public static int LiveryCount(int handle)
        {
            InstallModKit(handle);

            var mods = Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, handle, LiveryModType);
            return mods > 0 ? mods : Function.Call<int>(Hash.GET_VEHICLE_LIVERY_COUNT, handle);
        }

        /// <summary>The livery the vehicle is wearing, or -1 for none.</summary>
        public static int GetLivery(int handle)
        {
            InstallModKit(handle);

            return Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, handle, LiveryModType) > 0
                ? Function.Call<int>(Hash.GET_VEHICLE_MOD, handle, LiveryModType)
                : Function.Call<int>(Hash.GET_VEHICLE_LIVERY, handle);
        }

        /// <summary>Puts a livery on, or takes the current one off with -1.</summary>
        public static void SetLivery(int handle, int index)
        {
            InstallModKit(handle);

            if (Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, handle, LiveryModType) <= 0)
            {
                Function.Call(Hash.SET_VEHICLE_LIVERY, handle, index);
                return;
            }

            // A mod is taken off by its own native; SET_VEHICLE_MOD with -1 is not how it is spelled.
            if (index < 0)
                Function.Call(Hash.REMOVE_VEHICLE_MOD, handle, LiveryModType);
            else
                Function.Call(Hash.SET_VEHICLE_MOD, handle, LiveryModType, index, false);
        }

        // --- Hiding map objects -------------------------------------------------------------------
        //
        // Entity.Delete on a map prop only lasts until the area streams back in, and on a published map
        // every client applies the removal itself. CREATE_MODEL_HIDE survives re-streaming.

        public static void HideModel(Vector3 position, float radius, int modelHash, bool alsoHideBrokenGlass)
        {
            Function.Call(Hash.CREATE_MODEL_HIDE, position.X, position.Y, position.Z, radius, modelHash,
                alsoHideBrokenGlass);
        }

        public static void UnhideModel(Vector3 position, float radius, int modelHash)
        {
            Function.Call(Hash.REMOVE_MODEL_HIDE, position.X, position.Y, position.Z, radius, modelHash, false);
        }
    }
}
