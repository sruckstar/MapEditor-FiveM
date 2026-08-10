--[[
    The natives the C# half of this resource is not able to call.

    Both of them hand their answer back through a Vector3 pointer. On the FiveM Enhanced client that is
    a shape the C# runtime cannot push. Its argument pusher, CitizenFX.Base.NativeApi.PushArg, accepts
    strings, byte arrays, ints, uints, floats and bools and nothing else, while the v1 API assemblies
    Enhanced ships (coreclr/fallback/CitizenFX.Core.Client.dll) push a Vector3 straight into it for
    every out-parameter of that type. So the runtime's own wrapper throws before the native is ever
    reached:

        System.Exception: Unsupported type Vector3
          at CitizenFX.Base.NativeApi.PushArg[T](T arg)
          at CitizenFX.Core.Native.API.GetShapeTestResult(...)
          at CitizenFX.Core.World.Raycast(...)

    There is no way round it on the C# side. Results of pointer arguments are read back through
    NativeApi.GetRes*, which belongs to the runtime and is not reachable from a script, so Function.Call
    cannot collect them either; and OutputArgument, which is how every FiveM sample writes this, does not
    exist in the Enhanced assemblies at all.

    Lua has no such trouble — its native invoker has always handled pointer arguments — so the affected
    calls live here and the C# side asks for them over an event. The other half is
    Client/Platform/LuaBridge.cs.

    Answers come back as one formatted string rather than as a table. A table crosses the runtime
    boundary as MessagePack and arrives on the other side as one of several shapes depending on which
    client is running; a string is a string everywhere. It costs nothing: %.9g is the shortest decimal
    that round-trips a float32 exactly.
]]

--- Fires a ray and reports what it hit.
--
-- The same native, and the same trailing 7, that CitizenFX's own World.Raycast used, so the editor
-- works from exactly the answer it has always worked from: StartShapeTestRay over there is an alias for
-- this one.
--
-- @return "<status> <hit> <x> <y> <z> <entity>". Status 2 means the probe answered at all; anything
--         else means there is no answer yet and the coordinates are meaningless.
local function probe(x1, y1, z1, x2, y2, z2, flags, ignoreEntity)
    local handle = StartExpensiveSynchronousShapeTestLosProbe(
        x1, y1, z1, x2, y2, z2, flags, ignoreEntity, 7)

    local status, hit, endCoords, _, entityHit = GetShapeTestResult(handle)

    -- `hit` is a BOOL written through a pointer, and Lua does not receive those as booleans: the
    -- generated natives push every BOOL out-parameter through PointerValueInt — the file says so
    -- itself, `_i --[[ actually bool ]]` — so what arrives is the number 0 or the number 1. In Lua
    -- 0 is true, so `hit and 1 or 0` answers "hit" to a ray that met nothing at all. It has to be
    -- compared rather than tested, and both shapes are allowed for in case a client ever pushes the
    -- boolean instead.
    local didHit = hit ~= nil and hit ~= false and hit ~= 0

    return ('%d %d %.9g %.9g %.9g %d'):format(
        status or 0,
        didHit and 1 or 0,
        endCoords.x, endCoords.y, endCoords.z,
        math.floor(entityHit or 0))
end

--- The bounding box of a model, in model space.
--
-- A model the game has not loaded answers with an empty box rather than with a failure, which is why
-- the caller checks the result before it keeps it.
--
-- @return "<minX> <minY> <minZ> <maxX> <maxY> <maxZ>"
local function modelDimensions(model)
    local min, max = GetModelDimensions(model)

    return ('%.9g %.9g %.9g %.9g %.9g %.9g'):format(min.x, min.y, min.z, max.x, max.y, max.z)
end

-- How the C# side gets hold of the two above.
--
-- This is the handshake `exports` performs underneath, with our own event name instead of the
-- __cfx_export_ one: the caller passes a setter, the setter is called with the functions, and both
-- arrive on the other side as callable references. Done directly because reaching a real export from C#
-- goes through `dynamic`, which drags in the C# runtime binder — machinery this resource has no other
-- use for and one more thing the Enhanced sandbox would have to be willing to allow.
--
-- Local events are dispatched straight to their handlers, so the setter has already run by the time the
-- other side's TriggerEvent returns. `exports` depends on the same thing.
AddEventHandler('mapeditor:internal:bindNatives', function(bind)
    bind(probe, modelDimensions)
end)

-- One line, once, at load. It is the only way to tell "this file never ran" from "it ran and the
-- handshake failed" — the two look identical from the C# side, and they are fixed in different places.
-- The C# half's own failure message points at this line by name.
print('[MapEditor] bridge.lua loaded; listening for mapeditor:internal:bindNatives.')
