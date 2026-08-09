fx_version 'cerulean'
game 'gta5'

name 'my_sample_map'
description 'a map\nover two lines'
author 'Andrey & "co" <x>'
version '1.0.0'

client_script 'client.lua'

-- The map is read at runtime rather than compiled in, so it can be edited in place.
files {
    'map.json'
}
