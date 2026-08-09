using System;

namespace MapEditor.Platform
{
    /// <summary>
    /// Which objects of a published map the server has to own, and which every client may keep a copy of.
    ///
    /// <b>The criterion is one sentence.</b> An object needs synchronising exactly when its state stops
    /// following from the document. While it does follow, every client computing the same function of the
    /// same file <em>is</em> the synchronisation — exact, free, uncapped and with nobody owning it. Only
    /// what somebody touches can drift: the game (physics, AI) or a player.
    ///
    /// So the split is not by <c>ObjectTypes</c>. It is by the fields the map already carries:
    ///
    /// <list type="bullet">
    /// <item>A frozen prop is a function of the file. Ninety-five percent of a map is this, and it stays
    ///       local on every client.</item>
    /// <item>A prop with its physics on, or a door, is not: each client integrates its own physics from the
    ///       first frame it exists.</item>
    /// <item>A ped that stands still unarmed and friendly is a prop with a face. One that walks, or carries
    ///       a weapon, or hates the player, is a different game for every player who has their own copy of
    ///       it — see the port status document for what that looks like.</item>
    /// <item>A vehicle is a decoration until somebody opens the door. Getting in is a <em>ped</em> task and
    ///       it replicates; the car does not, so everyone else watches a player sit down in mid-air. This is
    ///       why the default is the server, and why the author who unchecks it gets a locked, undriveable
    ///       car rather than an open one.</item>
    /// <item>A laser is a marker with arithmetic behind it: every client draws the same beams from the same
    ///       numbers off the same clock, and the damage each of them deals is dealt to their own player,
    ///       which is the only place that answer could come from. Local, always.</item>
    /// </list>
    ///
    /// <b>Why the author gets a say.</b> The rule below cannot know that two hundred crates were made
    /// dynamic for the look of them rather than to be pushed around, and two hundred server entities is not
    /// what that map should cost. So <c>shared</c> in the document overrides the rule in both directions,
    /// and it is absent unless the author actually said something — a map written before this existed reads
    /// as "decide by the rule", which is the answer it would have wanted.
    ///
    /// Compiled into both halves of the resource, like <see cref="MapNames"/>: the client to know what not
    /// to spawn, the server to know what to create. Deliberately written against strings and bools rather
    /// than against MapObject, because the server never builds one — it reads the same decision straight out
    /// of the JSON.
    /// </summary>
    public static class SharedObjects
    {
        /// <summary>
        /// Whether this object of a published map belongs to the server.
        ///
        /// <paramref name="shared"/> is the author's answer, and null when they never gave one.
        /// </summary>
        public static bool NeedsServer(string type, bool? shared, bool dynamic, bool door,
            string action, string weapon, string relationship)
        {
            if (shared.HasValue) return shared.Value;
            return ByRule(type, dynamic, door, action, weapon, relationship);
        }

        /// <summary>
        /// What the document says about the object, with nobody's opinion on top. This is what the editor
        /// shows in the checkbox before the author touches it, and what applies to every map written before
        /// the checkbox existed.
        /// </summary>
        public static bool ByRule(string type, bool dynamic, bool door, string action, string weapon,
            string relationship)
        {
            switch (Normalize(type))
            {
                case "marker":
                case "laser":
                    // Drawn from the document every frame; there is no entity for anyone to disagree about.
                    return false;

                case "vehicle":
                    return true;

                case "prop":
                    return dynamic || door;

                case "ped":
                    return dynamic || HasAction(action) || IsArmed(weapon) || IsHostile(relationship);
            }

            // An object type this build does not know is left to the client, which is where every object was
            // before this file existed. Wrong quietly beats absent.
            return false;
        }

        /// <summary>
        /// Whether the ped does anything after it is spawned. "None" is the editor's own word for standing
        /// still, and a null is a map that never said.
        ///
        /// A scenario in place is the one action that stays local on purpose: two clients running the same
        /// idle animation drift in phase and in nothing else, which is a cosmetic disagreement about which
        /// frame of a loop somebody is on.
        /// </summary>
        private static bool HasAction(string action)
        {
            if (string.IsNullOrEmpty(action)) return false;

            action = action.Trim();
            if (action.Length == 0) return false;
            if (string.Equals(action, "None", StringComparison.OrdinalIgnoreCase)) return false;

            return string.Equals(action, "Wander", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(action, "Any", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(action, "Any - Walk", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(action, "Any - Warp", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether the ped is carrying anything. The editor writes the weapon by the name of the enum member
        /// or, for one it has no name for, as the raw hash — so anything that is neither absent nor
        /// "Unarmed" counts, including a number.
        /// </summary>
        private static bool IsArmed(string weapon)
        {
            if (string.IsNullOrEmpty(weapon)) return false;

            weapon = weapon.Trim();
            if (weapon.Length == 0) return false;

            return !string.Equals(weapon, "Unarmed", StringComparison.OrdinalIgnoreCase)
                   && weapon != "0";
        }

        /// <summary>
        /// Whether the ped will fight somebody. The two gang groups are the editor's own, and they exist to
        /// be set against each other; the rest are the game's Relationship enum, where only these two mean
        /// hostility. "Companion" and a null are the ordinary case and stay local.
        /// </summary>
        private static bool IsHostile(string relationship)
        {
            if (string.IsNullOrEmpty(relationship)) return false;

            relationship = relationship.Trim();
            return string.Equals(relationship, "Hate", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(relationship, "Dislike", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(relationship, "Ballas", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(relationship, "Grove", StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string type)
        {
            return string.IsNullOrEmpty(type) ? "" : type.Trim().ToLowerInvariant();
        }
    }
}
