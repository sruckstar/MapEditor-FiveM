fx_version 'cerulean'
game 'gta5'

name 'mapeditor'
author 'Guadmaz and andre500'
description 'Map Editor for FiveM'
version '1.0.0'

client_scripts {
    -- The handful of natives the C# runtime cannot call, because they answer through a Vector3 pointer
    -- and the client the Enhanced build ships cannot push one. See the file itself, and
    -- Client/Platform/LuaBridge.cs for the other half. Listed first so the event it listens on is
    -- registered before anything can ask for it.
    'client/bridge.lua',

    -- Mono v1. The ".net" suffix on the assembly name is what makes the runtime pick this up.
    'client/MapEditor.Client.net.dll',
}

server_script 'server/MapEditor.Server.net.dll'

files {
    -- Read at runtime with LoadResourceFile. A file missing from this list reads back as nil.
    'data/ObjectList.ini',
    'data/categories.json',
    'data/translations/*.xml',
}
