using CitizenFX.Core;
using CitizenFX.Core.Native;

namespace MapEditor.Server
{
    /// <summary>
    /// Who may open the editor, who may keep maps, and who may put one in front of everybody else.
    ///
    /// Four permissions, one per thing a player can do that the owner of a server might want to say no to:
    ///
    /// * <c>mapeditor.use</c> — opening the editor at all (the F7 command). The freecam is a vision cheat,
    ///   so a public roleplay server wants this shut; a build server wants it open.
    /// * <c>mapeditor.save</c> — keeping maps in one's own folder. That is the replacement for the disk a
    ///   FiveM client does not have, so it is open by default; a server that does not want players filling
    ///   its disk can shut it and leave the editor itself usable.
    /// * <c>mapeditor.publish</c> — writing to, and deleting from, the shared storage. A published map is a
    ///   file on the owner's disk <em>and</em> a map that spawns for every player on the server, so this is
    ///   never open by default.
    /// * <c>mapeditor.unload</c> — taking a published map back out of the world, for everyone at once. It is
    ///   separate from publishing because it acts on somebody else's map as readily as on one's own.
    ///
    /// <b>Why two of them default to allowed rather than to an ACE.</b> A resource starts after server.cfg
    /// has been executed, so an <c>add_ace builtin.everyone mapeditor.use allow</c> issued from here would
    /// silently overrule an owner who had written the matching <c>remove_ace</c> into their config — the
    /// last writer wins, and it would be us. A convar is read at the moment of the check and says the same
    /// thing in one line without the ordering question:
    ///
    /// <code>
    /// set mapeditor_restrict_use  true             # the editor now needs the ACE below
    /// set mapeditor_restrict_save true             # so does keeping maps
    /// add_ace group.admin mapeditor.use     allow
    /// add_ace group.admin mapeditor.save    allow
    /// add_ace group.admin mapeditor.publish allow  # always needed, restricted or not
    /// add_ace group.admin mapeditor.unload  allow  # likewise
    /// </code>
    ///
    /// <b>What this can and cannot enforce.</b> Everything the editor does to the world is client-side, so
    /// <see cref="CanUse"/> is a request the client honours, not a wall: a player running a modified
    /// client can spawn props whatever this says, exactly as they could with any menu. The other three are
    /// different — they guard events the server acts on, and each is checked inside its handler rather than
    /// trusted from the client's own copy of the answer.
    /// </summary>
    public static class Access
    {
        public const string UsePermission = "mapeditor.use";
        public const string SavePermission = "mapeditor.save";
        public const string PublishPermission = "mapeditor.publish";
        public const string UnloadPermission = "mapeditor.unload";

        /// <summary>Whether <see cref="UsePermission"/> is enforced at all. See the class remarks.</summary>
        public static bool RestrictUse
        {
            get { return IsTruthy(API.GetConvar("mapeditor_restrict_use", "false")); }
        }

        /// <summary>Whether <see cref="SavePermission"/> is enforced at all. See the class remarks.</summary>
        public static bool RestrictSave
        {
            get { return IsTruthy(API.GetConvar("mapeditor_restrict_save", "false")); }
        }

        public static bool CanUse(Player player)
        {
            if (player == null) return false;
            if (!RestrictUse) return true;
            return API.IsPlayerAceAllowed(player.Handle, UsePermission);
        }

        /// <summary>
        /// Whether this player may keep maps in their own folder. Open unless the owner has restricted it:
        /// personal storage is what stands in for the client's missing disk, and shutting it by default
        /// would leave a player with the editor open and nowhere to put anything.
        /// </summary>
        public static bool CanSave(Player player)
        {
            if (player == null) return false;
            if (!RestrictSave) return true;
            return API.IsPlayerAceAllowed(player.Handle, SavePermission);
        }

        public static bool CanPublish(Player player)
        {
            if (player == null) return false;
            return API.IsPlayerAceAllowed(player.Handle, PublishPermission);
        }

        /// <summary>
        /// Whether this player may take a published map out of the world for everyone on the server.
        ///
        /// Kept apart from <see cref="CanPublish"/> on purpose, because the two are not the same power:
        /// publishing puts up a map of one's own, while unloading takes down whichever map happens to be up,
        /// including somebody else's. An owner who wants the pair together grants both ACEs — and usually
        /// should, since a player who can publish but not unload cannot edit their own map again.
        /// </summary>
        public static bool CanUnload(Player player)
        {
            if (player == null) return false;
            return API.IsPlayerAceAllowed(player.Handle, UnloadPermission);
        }

        /// <summary>
        /// Convars are strings, and an owner writing one will write whichever of these they are used to.
        /// Anything unrecognised is false, so a typo leaves the editor open rather than shutting it with no
        /// explanation.
        /// </summary>
        private static bool IsTruthy(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            value = value.Trim();
            return value == "1" || string.Equals(value, "true", System.StringComparison.OrdinalIgnoreCase)
                                || string.Equals(value, "yes", System.StringComparison.OrdinalIgnoreCase)
                                || string.Equals(value, "on", System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
