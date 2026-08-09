using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CitizenFX.Core.Native;
using MapEditor.Platform;

// ServerMaps.Directory is a const of its own, and the nearer name wins inside the class: without this
// alias, System.IO.Directory is unreachable from there by its short name.
using IoDirectory = System.IO.Directory;
using IoFile = System.IO.File;

namespace MapEditor.Server
{
    /// <summary>
    /// Which of the two storages a map is in. Both are folders on the server owner's disk; what separates
    /// them is who can see the map and who is allowed to put one there.
    /// </summary>
    public enum MapScope
    {
        /// <summary>
        /// The player's own folder, <c>maps/players/&lt;their name&gt;/</c>. Listed only to them, loadable
        /// only by them, and writable without any ACE — this is where "Save Local" goes, and where the
        /// autosave goes, because a FiveM client has no disk of its own to write to.
        /// </summary>
        Personal,

        /// <summary>
        /// <c>maps/</c> itself: everyone's. Listed in every player's picker, and — while it is
        /// <see cref="ServerMapEntry.Live"/> — standing in every player's world. Writing needs the
        /// mapeditor.publish ACE, and taking one back down needs mapeditor.unload.
        /// </summary>
        Shared,
    }

    /// <summary>One map in the server's storage, as the index records it.</summary>
    public sealed class ServerMapEntry
    {
        /// <summary>What the player called it. Spaces, punctuation and non-Latin letters all survive here.</summary>
        public string Name;

        /// <summary>
        /// The file it lives in, under <see cref="ServerMaps.Directory"/>. Derived from <see cref="Name"/>
        /// by <see cref="ServerMaps.MakeFileName"/> and never taken from the client — which is what makes
        /// path traversal through SaveResourceFile impossible rather than merely filtered.
        /// </summary>
        public string File;

        /// <summary>Characters of content, matching what the client's own picker shows.</summary>
        public int Size;

        /// <summary>Round-trip UTC, or null on an index written by hand.</summary>
        public string SavedAt;

        /// <summary>Whoever saved it, by the name they were connected under. Display only.</summary>
        public string Author;

        /// <summary>
        /// Whether this map is standing in the world of every player on the server right now.
        ///
        /// Publishing a map sets it; unloading one clears it; a player joining spawns everything that has
        /// it. It is deliberately the same bit for all three, because "published" and "in the world" are
        /// one state and not two — a shared map that is up is up for everybody, and one that is down is
        /// only a file waiting to be edited or published again.
        ///
        /// Personal maps never carry it: those are drafts, and only their owner ever sees them.
        /// </summary>
        public bool Live;

        /// <summary>
        /// Whose map this is: the identifier from <see cref="ServerMaps.OwnerKey"/>, or null for a shared
        /// one. A null here is what makes a v1 index — written before personal storage existed — read as a
        /// server full of shared maps, which is exactly what it was.
        /// </summary>
        public string Owner;

        public MapScope Scope
        {
            get { return Owner == null ? MapScope.Shared : MapScope.Personal; }
        }

        public Json ToJson()
        {
            return Json.Object()
                .Set("name", Name)
                .Set("file", File)
                .Set("size", Size)
                .Set("savedAt", SavedAt)
                .Set("author", Author)
                .Set("live", Live)
                .Set("owner", Owner);
        }

        /// <summary>The row as the client's file picker wants it. The owner key is the server's business.</summary>
        public Json ToClientJson()
        {
            return Json.Object()
                .Set("name", Name)
                .Set("scope", Owner == null ? "shared" : "personal")
                .Set("size", Size)
                .Set("savedAt", SavedAt)
                .Set("author", Author)
                .Set("live", Live);
        }
    }

    /// <summary>A player who has saved at least one map, and the folder their maps are kept in.</summary>
    public sealed class ServerMapOwner
    {
        /// <summary>See <see cref="ServerMaps.OwnerKey"/>. Stable across renames.</summary>
        public string Key;

        /// <summary>Where their maps live, relative to the resource: <c>maps/players/&lt;slug&gt;/</c>.</summary>
        public string Folder;

        /// <summary>The name they were last connected under, so the folder is recognisable on disk.</summary>
        public string Name;

        public Json ToJson()
        {
            return Json.Object()
                .Set("key", Key)
                .Set("folder", Folder)
                .Set("name", Name);
        }
    }

    /// <summary>
    /// The server's map storage.
    ///
    /// Maps are files in the resource's own <c>maps/</c> folder, written with SaveResourceFile and read
    /// back with LoadResourceFile. System.IO is deliberately untouched for storage — the same reason
    /// Newtonsoft and XmlSerializer are not used anywhere in this port: what the Mono sandbox permits is
    /// the server owner's business, and a resource that only ever calls natives has no opinion to be wrong
    /// about. (Making a folder is the one exception, because there is no native for it; see
    /// <see cref="EnsureDirectory"/>, which is written so that failing is survivable.)
    ///
    /// <b>Two scopes.</b> <see cref="MapScope.Shared"/> is <c>maps/</c> itself, as it always was.
    /// <see cref="MapScope.Personal"/> is <c>maps/players/&lt;name&gt;/</c>, one folder per player, listed
    /// and loadable only by its owner. The personal half exists because a FiveM client cannot write a file
    /// anywhere: "save locally" has to land somewhere, and the only disk in the conversation is this one.
    ///
    /// The two costs of never listing a directory, both visible below:
    ///
    /// * <b>No directory listing.</b> Hence <c>maps/index.json</c>, which is the catalogue for both scopes
    ///   at once, and the files beside it are only content. An index that disagrees with the folders is the
    ///   index's fault to fix.
    /// * <b>No delete.</b> There is no native that removes a resource file. <see cref="Delete"/> drops the
    ///   entry and blanks the file; the empty file stays on disk until the owner removes it, and the name
    ///   is free to be used again.
    ///
    /// <b>The folder has to exist first.</b> SaveResourceFile opens the file for writing and does not
    /// create missing directories, so a save into an absent <c>maps/</c> returns false and nothing else —
    /// which is how the first live run failed, with this class blaming folder permissions. The resource
    /// therefore ships <c>maps/README.txt</c>, because that is the only way a folder survives git and zip,
    /// and <see cref="EnsureStorage"/> checks at startup rather than at the player's first save.
    ///
    /// The catalogue itself is deliberately <em>not</em> shipped: an owner who updates the resource by
    /// pulling or unpacking over the top would have their index replaced by an empty one, and every map on
    /// the server would drop out of the listing at once.
    ///
    /// Only the native JSON format is accepted, in both scopes. A map the server stores is one the server
    /// has to be able to size up and count the objects in before it writes it, and doing that for five
    /// formats means shipping the whole serializer here. See <see cref="Validate"/>.
    /// </summary>
    public static class ServerMaps
    {
        public const string Directory = "maps/";

        /// <summary>The parent of every player's own folder. Nothing is ever written here directly.</summary>
        public const string PlayerDirectory = Directory + "players/";

        private const string IndexFile = Directory + "index.json";

        /// <summary>Would collide with the index itself.</summary>
        private const string ReservedFileStem = "index";

        /// <summary>
        /// 2 added <c>owner</c> to a map and the <c>owners</c> list beside it. A version 1 index still
        /// reads: every map in it is a shared one, which is all there was.
        ///
        /// 3 renamed <c>autoload</c> to <c>live</c>. The old key is still read, and it means the same thing
        /// it always did for a shared map — spawn this for everyone — so an owner's published maps come
        /// back up after the upgrade rather than quietly staying down. On a personal map it is dropped:
        /// personal maps are drafts now and nothing spawns them.
        /// </summary>
        private const int IndexVersion = 3;

        /// <summary>
        /// The catalogue, read once on the first call and kept in memory afterwards. The server is the only
        /// writer, so the copy on disk cannot drift underneath us within one run.
        /// </summary>
        private static List<ServerMapEntry> _entries;

        private static List<ServerMapOwner> _owners;

        private static List<ServerMapEntry> Entries
        {
            get
            {
                if (_entries == null) ReadIndex();
                return _entries;
            }
        }

        private static List<ServerMapOwner> Owners
        {
            get
            {
                if (_owners == null) ReadIndex();
                return _owners;
            }
        }

        /// <summary>Shared maps only — what the server as a whole is holding, for the log line and the cap.</summary>
        public static int Count
        {
            get
            {
                var count = 0;
                foreach (var entry in Entries)
                {
                    if (entry.Owner == null) count++;
                }
                return count;
            }
        }

        /// <summary>Shared maps standing in everyone's world right now. For the startup line.</summary>
        public static int LiveCount
        {
            get
            {
                var count = 0;
                foreach (var entry in Entries)
                {
                    if (entry.Live) count++;
                }
                return count;
            }
        }

        /// <summary>How many maps this player is keeping in their own folder. Their own cap is on this.</summary>
        public static int CountFor(string owner)
        {
            var count = 0;
            foreach (var entry in Entries)
            {
                if (SameOwner(entry.Owner, owner)) count++;
            }
            return count;
        }

        public static IList<ServerMapEntry> All
        {
            get { return Entries; }
        }

        /// <summary>
        /// Reads the catalogue and makes sure a save will actually land, at startup rather than under the
        /// player who is trying to save. Returns whether storage is usable; the log line says which.
        ///
        /// The check is a real write — of the catalogue, so it costs nothing and leaves nothing behind —
        /// because there is no native that asks whether a folder exists or is writable.
        /// </summary>
        public static bool EnsureStorage()
        {
            var count = Count;   // reads the index, if there is one

            if (WriteIndex())
            {
                Log.Info("Storage is ready at {0} ({1} shared map(s), {2} player folder(s)).",
                    FullPath(Directory), count, Owners.Count);
                return true;
            }

            // Overwhelmingly the missing folder rather than permissions: the first thing a fresh install
            // lacks is the directory, and that is exactly what SaveResourceFile will not create for us.
            if (EnsureDirectory(Directory) && WriteIndex())
            {
                Log.Info("Created the missing {0}. Storage is ready ({1} shared map(s)).", FullPath(Directory), count);
                return true;
            }

            Log.Info("STORAGE IS NOT WRITABLE: {0} could not be written. Saving maps on the server will " +
                     "fail until this is fixed. {1} See DEPLOY.md.", FullPath(IndexFile), Diagnose());
            return false;
        }

        /// <summary>
        /// The map by that name in that scope, or null. <paramref name="owner"/> is ignored for
        /// <see cref="MapScope.Shared"/> and required for the other — a personal map belongs to exactly one
        /// player, and asking for someone else's by name must come back empty rather than come back.
        /// </summary>
        public static ServerMapEntry Find(MapScope scope, string owner, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (scope == MapScope.Personal && string.IsNullOrEmpty(owner)) return null;

            foreach (var entry in Entries)
            {
                if (entry.Scope != scope) continue;
                if (scope == MapScope.Personal && !SameOwner(entry.Owner, owner)) continue;
                if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)) return entry;
            }
            return null;
        }

        /// <summary>
        /// The catalogue as one player's file picker wants it: every shared map, plus that player's own,
        /// and nothing of anybody else's. Names and metadata only, never content.
        /// </summary>
        public static string ListJson(string owner)
        {
            var maps = Json.Array();
            foreach (var entry in Entries)
            {
                if (entry.Owner != null && !SameOwner(entry.Owner, owner)) continue;
                maps.Add(entry.ToClientJson());
            }

            return Json.Object()
                .Set("maps", maps)
                .ToJson();
        }

        /// <summary>The map's content, or null if there is no such map or the index names a file that is gone.</summary>
        public static string Read(MapScope scope, string owner, string name)
        {
            var entry = Find(scope, owner, name);
            if (entry == null) return null;

            var content = API.LoadResourceFile(API.GetCurrentResourceName(), entry.File);
            if (content != null) return content;

            // The owner deleted the file by hand, or the write that created it failed. Either way the
            // index is now lying, and the entry goes rather than staying to fail the same way tomorrow.
            Log.Info("Map '{0}' is in the index but '{1}' could not be read; dropping the entry.", entry.Name, entry.File);
            Entries.Remove(entry);
            SaveIndex();
            return null;
        }

        /// <summary>
        /// Writes the map, creating or replacing it, and returns the entry as it now stands.
        ///
        /// Content is expected to have passed <see cref="Validate"/> already — this does the storing, not
        /// the deciding.
        ///
        /// A shared map comes out <see cref="ServerMapEntry.Live"/>: publishing <em>is</em> putting the map
        /// into the world, and there is no separate step and no checkbox. A personal one never is.
        /// </summary>
        public static ServerMapEntry Write(MapScope scope, string owner, string ownerName, string name,
            string content, string author)
        {
            if (scope == MapScope.Personal && string.IsNullOrEmpty(owner))
                throw new ArgumentException("A personal map needs an owner.", "owner");

            var directory = scope == MapScope.Shared ? Directory : FolderFor(owner, ownerName);

            var entry = Find(scope, owner, name);
            if (entry == null)
            {
                entry = new ServerMapEntry
                {
                    Name = name,
                    Owner = scope == MapScope.Shared ? null : owner,
                    File = MakeFileName(directory, name),
                };
                Entries.Add(entry);
            }

            // The folder can go missing between restarts — an update unpacked over the top, a deploy script
            // that mirrors the repository — so the repair is tried here too, not only at startup. Once per
            // folder per server run either way.
            if (!SaveFile(entry.File, content) && !(EnsureDirectory(DirectoryOf(entry.File)) && SaveFile(entry.File, content)))
            {
                // Nothing reached the disk, so the index must not claim otherwise.
                if (entry.Size == 0 && entry.SavedAt == null) Entries.Remove(entry);

                Log.Info("Could not write {0}. {1} See DEPLOY.md.", FullPath(entry.File), Diagnose());
                throw new InvalidOperationException(
                    "The server could not store the map: its maps folder is missing or not writable. " +
                    "The server console has the path.");
            }

            entry.Size = content.Length;
            entry.SavedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            entry.Author = author;
            entry.Live = scope == MapScope.Shared;

            SaveIndex();
            return entry;
        }

        /// <summary>
        /// Takes a published map out of the world, or puts it back. Returns the entry it acted on, or null
        /// when there is no such shared map.
        ///
        /// This is the whole of "unload": the map stays in storage and stays in everyone's picker, it simply
        /// stops standing anywhere. Which is also what makes it editable again — see the load path in
        /// MapEditorServer.
        /// </summary>
        public static ServerMapEntry SetLive(string name, bool live)
        {
            var entry = Find(MapScope.Shared, null, name);
            if (entry == null) return null;

            if (entry.Live == live) return entry;

            entry.Live = live;
            SaveIndex();
            return entry;
        }

        /// <summary>
        /// Takes the map out of the catalogue and blanks its file. Returns false when there was no such map.
        ///
        /// The file itself stays: FiveM has no native that deletes one. It is left empty rather than intact
        /// so that a map removed on purpose is not still readable to anyone who guesses the filename.
        /// </summary>
        public static bool Delete(MapScope scope, string owner, string name)
        {
            var entry = Find(scope, owner, name);
            if (entry == null) return false;

            Entries.Remove(entry);
            SaveFile(entry.File, "");
            SaveIndex();
            return true;
        }

        /// <summary>
        /// Whether the document is something this server is willing to store, and why not when it is not.
        ///
        /// The client is an untrusted party — the whole point of §5.9 — so nothing here believes anything
        /// it was told about the content: the size is measured, the format is read out of the document and
        /// the objects are counted.
        /// </summary>
        public static bool Validate(string content, int maxObjects, out string error)
        {
            error = null;

            if (string.IsNullOrEmpty(content))
            {
                error = "The map is empty.";
                return false;
            }

            var document = Json.TryParse(content);
            if (document == null || document.Kind != JsonKind.Object)
            {
                error = "The server only stores maps in the native Map Editor format.";
                return false;
            }

            if (document["format"].AsString("") != "mapeditor-map")
            {
                error = "The server only stores maps in the native Map Editor format.";
                return false;
            }

            var objects = document["objects"].Count + document["removeFromWorld"].Count;
            if (objects > maxObjects)
            {
                error = string.Format(CultureInfo.InvariantCulture,
                    "The map has {0} objects; this server allows {1}.", objects, maxObjects);
                return false;
            }

            return true;
        }

        // --- Owners ---------------------------------------------------------------------------------

        /// <summary>
        /// Who a player is, for the purpose of owning maps.
        ///
        /// Their <c>license</c> identifier, which is the one FiveM derives from the Rockstar account and
        /// the one that does not change when they change their name. The name itself is only what the
        /// folder is called, so that the owner of the server can tell whose is whose on disk — keying on it
        /// would mean a player loses every map the day they rename, and gains someone else's the day they
        /// take their name.
        ///
        /// The fallback down to the name exists for servers with no identifiers at all (sv_lan), where
        /// there is nothing else to go on.
        /// </summary>
        public static string OwnerKey(CitizenFX.Core.Player player)
        {
            if (player == null) return null;

            var license = Identifier(player, "license:");
            if (license != null) return license;

            var license2 = Identifier(player, "license2:");
            if (license2 != null) return license2;

            var fivem = Identifier(player, "fivem:");
            if (fivem != null) return fivem;

            return string.IsNullOrEmpty(player.Name) ? null : "name:" + player.Name;
        }

        private static string Identifier(CitizenFX.Core.Player player, string prefix)
        {
            // player.Identifiers is an indexer over the same natives; walking the list is what works on
            // both the v1 wrappers and the raw API, and there are half a dozen of them at most.
            var count = API.GetNumPlayerIdentifiers(player.Handle);
            for (var i = 0; i < count; i++)
            {
                var identifier = API.GetPlayerIdentifier(player.Handle, i);
                if (!string.IsNullOrEmpty(identifier) &&
                    identifier.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return identifier;
            }
            return null;
        }

        private static bool SameOwner(string a, string b)
        {
            return a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// This player's folder, made the first time they save and remembered in the index afterwards.
        ///
        /// Remembered rather than recomputed because the name it is built from can change under it: the
        /// folder is named after the player for the server owner's benefit, and the maps in it belong to
        /// the key. A player who renames keeps the folder they had, and the display name beside it is
        /// updated so the index still says who they are now.
        /// </summary>
        private static string FolderFor(string owner, string ownerName)
        {
            foreach (var known in Owners)
            {
                if (!SameOwner(known.Key, owner)) continue;

                if (!string.IsNullOrEmpty(ownerName) && known.Name != ownerName)
                {
                    known.Name = ownerName;
                    SaveIndex();
                }
                return known.Folder;
            }

            var record = new ServerMapOwner
            {
                Key = owner,
                Name = ownerName,
                Folder = PlayerDirectory + UniqueFolderName(ownerName) + "/",
            };

            Owners.Add(record);
            return record.Folder;
        }

        private static string UniqueFolderName(string playerName)
        {
            var stem = SafeStem(playerName);
            if (stem.Length == 0) stem = "player";

            var candidate = stem;
            var suffix = 2;
            while (IsFolderTaken(PlayerDirectory + candidate + "/"))
            {
                candidate = stem + "_" + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }
            return candidate;
        }

        private static bool IsFolderTaken(string folder)
        {
            foreach (var known in Owners)
            {
                if (string.Equals(known.Folder, folder, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // --- Names and paths ------------------------------------------------------------------------

        /// <summary>
        /// A name a player chose, turned into a file name the server chose, inside the folder the server
        /// chose.
        ///
        /// Every character outside the safe set becomes an underscore, which disposes of <c>..</c>, of both
        /// slashes, and of the drive-letter colon in one stroke — the traversal in SaveResourceFile that
        /// §5.9 warns about is not filtered here so much as made unreachable, since the client's string
        /// never becomes a path in the first place. Collisions after that (two different names mapping onto
        /// the same file) are broken by a counter.
        /// </summary>
        public static string MakeFileName(string directory, string name)
        {
            var baseName = SafeStem(name);
            if (baseName.Length > 48) baseName = baseName.Substring(0, 48);

            // A name made entirely of characters we replaced — "Центр", "★" — leaves nothing to work with.
            if (baseName.Length == 0 || string.Equals(baseName, ReservedFileStem, StringComparison.OrdinalIgnoreCase))
                baseName = "map";

            var candidate = directory + baseName + ".json";
            var suffix = 2;
            while (IsFileTaken(candidate))
            {
                candidate = directory + baseName + "_" + suffix.ToString(CultureInfo.InvariantCulture) + ".json";
                suffix++;
            }

            return candidate;
        }

        private static string SafeStem(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var stem = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                var safe = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                           || c == '-' || c == '_';
                stem.Append(safe ? c : '_');
            }
            return stem.ToString().Trim('_');
        }

        private static bool IsFileTaken(string file)
        {
            foreach (var entry in Entries)
            {
                if (string.Equals(entry.File, file, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>The folder part of a resource-relative file, trailing slash and all.</summary>
        private static string DirectoryOf(string file)
        {
            var cut = file.LastIndexOf('/');
            return cut < 0 ? Directory : file.Substring(0, cut + 1);
        }

        /// <summary>
        /// Whether a file named in the index is somewhere this resource is willing to read from or write
        /// to. An index edited by hand could name a file outside the folder, and that file would then be
        /// handed to any client that asked for the map: the catalogue is trusted for what it lists, not for
        /// where it points.
        /// </summary>
        private static bool IsInsideStorage(string file)
        {
            return file.StartsWith(Directory, StringComparison.Ordinal)
                   && file.IndexOf("..", StringComparison.Ordinal) < 0
                   && file.IndexOf('\\') < 0;
        }

        // --- The index ------------------------------------------------------------------------------

        private static void ReadIndex()
        {
            _entries = new List<ServerMapEntry>();
            _owners = new List<ServerMapOwner>();

            var raw = API.LoadResourceFile(API.GetCurrentResourceName(), IndexFile);
            if (string.IsNullOrEmpty(raw)) return;

            var document = Json.TryParse(raw);
            if (document == null)
            {
                // Kept, not overwritten: the owner may have edited it by hand, and the next save would
                // otherwise take every map in it with them.
                Log.Info("{0} is not valid JSON and was ignored. No server maps will be listed until it is fixed.", IndexFile);
                return;
            }

            foreach (var item in document["owners"].Items)
            {
                var key = item["key"].AsString(null);
                var folder = item["folder"].AsString(null);
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(folder)) continue;
                if (!IsInsideStorage(folder)) continue;

                _owners.Add(new ServerMapOwner
                {
                    Key = key,
                    Folder = folder,
                    Name = item["name"].AsString(null),
                });
            }

            foreach (var item in document["maps"].Items)
            {
                var name = item["name"].AsString(null);
                var file = item["file"].AsString(null);
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(file)) continue;

                if (!IsInsideStorage(file))
                {
                    Log.Info("Map '{0}' names the file '{1}', which is outside {2}; the entry was ignored.", name, file, Directory);
                    continue;
                }

                // Absent in a version 1 index, where every map was everyone's.
                var owner = item["owner"].AsString(null);

                _entries.Add(new ServerMapEntry
                {
                    Name = name,
                    File = file,
                    Size = item["size"].AsInt(0),
                    SavedAt = item["savedAt"].AsString(null),
                    Author = item["author"].AsString(null),

                    // "autoload" is what a version 2 index called this. On a shared map the meaning carried
                    // over exactly; on a personal one there is nothing left for it to mean.
                    Live = owner == null && item["live"].AsBool(item["autoload"].AsBool(false)),

                    Owner = owner,
                });
            }
        }

        private static void SaveIndex()
        {
            if (!WriteIndex())
                Log.Info("Could not write {0}. Maps saved in this session will be gone when the server restarts.",
                    FullPath(IndexFile));
        }

        private static bool WriteIndex()
        {
            var maps = Json.Array();
            foreach (var entry in Entries) maps.Add(entry.ToJson());

            var owners = Json.Array();
            foreach (var owner in Owners) owners.Add(owner.ToJson());

            var document = Json.Object()
                .Set("format", "mapeditor-index")
                .Set("version", IndexVersion)
                .Set("owners", owners)
                .Set("maps", maps);

            return SaveFile(IndexFile, document.ToJson());
        }

        private static bool SaveFile(string file, string content)
        {
            return API.SaveResourceFile(API.GetCurrentResourceName(), file, content, -1);
        }

        // --- Folders --------------------------------------------------------------------------------

        /// <summary>Where a resource-relative name actually is, for a log line the owner can act on.</summary>
        private static string FullPath(string file)
        {
            var root = API.GetResourcePath(API.GetCurrentResourceName());
            return string.IsNullOrEmpty(root) ? file : Combine(root, file);
        }

        /// <summary>
        /// Joins the two halves without doubling the separator: GetResourcePath returns a trailing slash on
        /// some builds and not on others, and a path printed as <c>mapeditor//maps</c> reads like a bug in
        /// the very message that is asking the owner to go and look at it.
        /// </summary>
        private static string Combine(string root, string tail)
        {
            return root.TrimEnd('/', '\\') + "/" + tail;
        }

        /// <summary>
        /// Why the write failed, in the operating system's own words.
        ///
        /// SaveResourceFile reports a bare false — no errno, no message — so without this the owner is left
        /// choosing between a folder that is not there and one they may not write to, which are different
        /// jobs to fix and only one of which is usually the case. System.IO is asked the same question
        /// purely to have the answer printed: nothing is stored through it, and a runtime that refuses the
        /// call lands in the catch, leaving the message no worse than it was before.
        /// </summary>
        private static string Diagnose()
        {
            var root = API.GetResourcePath(API.GetCurrentResourceName());
            if (string.IsNullOrEmpty(root))
                return "The server reports no path for this resource at all, which is a problem of its own.";

            var folder = Combine(root, Directory);

            try
            {
                if (!IoDirectory.Exists(folder))
                    return "The folder " + folder + " does not exist and could not be created. It ships with "
                           + "the resource (as maps/README.txt, since an empty folder survives neither git "
                           + "nor zip), so it was most likely left behind when the resource was uploaded.";

                var probe = Combine(folder, ".mapeditor-write-test");
                IoFile.WriteAllText(probe, "");
                IoFile.Delete(probe);

                return "The folder " + folder + " exists and takes an ordinary file write, so it is FXServer "
                       + "refusing the resource-file write rather than the filesystem.";
            }
            catch (Exception e)
            {
                return "Writing into " + folder + " failed: " + e.Message;
            }
        }

        /// <summary>Folders already tried this run, so a repair that cannot work is not retried per save.</summary>
        private static readonly HashSet<string> _attemptedDirectories =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Creates a folder under the resource when it is missing. Tried once per folder per server run,
        /// after a write has already failed — so this is a repair, not the normal path. It is also the only
        /// path by which a player's own folder ever comes into existence, since nothing ships one.
        ///
        /// This is the one place the server half touches System.IO, and it does so knowing it might not be
        /// allowed to: there is no native that makes a folder, and the alternative is telling the owner to
        /// go and mkdir one per player by hand. The failure is caught and reported, and everything above it
        /// works the same whether this succeeds or not — a personal save on a server that refuses this
        /// fails with the same message a read-only maps folder gives.
        /// </summary>
        private static bool EnsureDirectory(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return false;
            if (!_attemptedDirectories.Add(relative)) return false;

            var root = API.GetResourcePath(API.GetCurrentResourceName());
            if (string.IsNullOrEmpty(root)) return false;

            try
            {
                IoDirectory.CreateDirectory(Combine(root, relative));
                return true;
            }
            catch (Exception e)
            {
                Log.Info("Could not create {0}: {1}", Combine(root, relative), e.Message);
                return false;
            }
        }
    }
}
