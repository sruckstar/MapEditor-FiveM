using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CitizenFX.Core.Native;

namespace MapEditor.Platform
{
    /// <summary>
    /// The client's only persistence. Replaces every File.ReadAllText / File.WriteAllText the SP build did
    /// for settings, favorites, the autosave, session restore and locally saved maps.
    ///
    /// Values above <see cref="ChunkSize"/> are split across several keys: a single KVP entry holding a
    /// multi-megabyte map has been unreliable in practice, and a map of a couple of thousand objects is
    /// exactly that size. Chunking is transparent to callers.
    /// </summary>
    public static class Kvp
    {
        private const int ChunkSize = 128 * 1024;

        /// <summary>
        /// Marks a key whose real value lives in "<key>#0".."<key>#N-1". The leading control character
        /// cannot occur in the JSON/XML/INI payloads the editor stores, so it can never collide with a
        /// genuine value.
        /// </summary>
        private const string ChunkedMarker = "chunked:";

        public static string GetString(string key)
        {
            string raw = API.GetResourceKvpString(key);
            if (raw == null) return null;
            if (!raw.StartsWith(ChunkedMarker, StringComparison.Ordinal)) return raw;

            int count;
            if (!int.TryParse(raw.Substring(ChunkedMarker.Length), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out count) || count <= 0)
            {
                Log.Info("KVP entry '{0}' has a damaged chunk header; treating it as missing.", key);
                return null;
            }

            var sb = new StringBuilder(count * ChunkSize);
            for (int i = 0; i < count; i++)
            {
                string part = API.GetResourceKvpString(ChunkKey(key, i));
                if (part == null)
                {
                    Log.Info("KVP entry '{0}' is missing chunk {1} of {2}; treating it as missing.", key, i, count);
                    return null;
                }
                sb.Append(part);
            }
            return sb.ToString();
        }

        public static void SetString(string key, string value)
        {
            if (value == null)
            {
                Delete(key);
                return;
            }

            // Always clear the old shape first: a value shrinking below the chunk threshold would leave
            // orphaned "<key>#N" entries, and a stale chunk read back into a later value corrupts it.
            Delete(key);

            if (value.Length <= ChunkSize)
            {
                API.SetResourceKvp(key, value);
                return;
            }

            int count = (value.Length + ChunkSize - 1) / ChunkSize;
            for (int i = 0; i < count; i++)
            {
                int offset = i * ChunkSize;
                int length = Math.Min(ChunkSize, value.Length - offset);
                API.SetResourceKvp(ChunkKey(key, i), value.Substring(offset, length));
            }
            API.SetResourceKvp(key, ChunkedMarker + count.ToString(CultureInfo.InvariantCulture));
        }

        public static void Delete(string key)
        {
            string raw = API.GetResourceKvpString(key);
            if (raw != null && raw.StartsWith(ChunkedMarker, StringComparison.Ordinal))
            {
                int count;
                if (int.TryParse(raw.Substring(ChunkedMarker.Length), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out count))
                {
                    for (int i = 0; i < count; i++)
                        API.DeleteResourceKvp(ChunkKey(key, i));
                }
            }
            API.DeleteResourceKvp(key);
        }

        public static bool Exists(string key)
        {
            return API.GetResourceKvpString(key) != null;
        }

        /// <summary>
        /// How many keys one listing walks before it stops and says so.
        ///
        /// The KVP iterator is the one loop in this file with no bound of its own: it ends when FIND_KVP
        /// says there is nothing left, and it says so by handing back nothing. A build that ends the walk
        /// with an empty string rather than with a null, or that hands the same key back forever, turns this
        /// into a loop that never returns — and this runs inside a frame, on a client, where a loop that
        /// never returns is a game frozen hard with no console left to read the reason in.
        ///
        /// The editor's own keys are a handful of settings and one entry per saved map, plus the chunks of
        /// the larger ones, so a real listing is nowhere near this. Anything past it is the iterator
        /// misbehaving, not a player with a lot of maps.
        /// </summary>
        private const int MaxKeysPerListing = 4096;

        /// <summary>
        /// Every key under <paramref name="prefix"/>, with the chunk parts of chunked values filtered out
        /// so a caller listing saved maps sees one entry per map.
        /// </summary>
        public static List<string> Keys(string prefix)
        {
            var result = new List<string>();
            int handle = API.StartFindKvp(prefix);
            if (handle == -1) return result;

            try
            {
                // Counted separately from the result, because a listing made entirely of chunk parts adds
                // nothing to it and would slip past a bound measured on what came out.
                int walked = 0;
                while (walked++ < MaxKeysPerListing)
                {
                    string key = API.FindKvp(handle);

                    // Null is how the walk is documented to end; empty is treated the same, since no caller
                    // writes an empty key and a runtime that ends the walk that way would never let go of
                    // the frame.
                    if (string.IsNullOrEmpty(key)) break;

                    if (key.IndexOf('#') >= 0 && IsChunkPart(key)) continue;
                    result.Add(key);
                }

                if (walked > MaxKeysPerListing)
                    Log.Info("KVP listing for '{0}' was cut off after {1} keys — the iterator is not ending. " +
                             "Whatever is being listed will look incomplete.", prefix, MaxKeysPerListing);
            }
            finally
            {
                API.EndFindKvp(handle);
            }
            return result;
        }

        private static string ChunkKey(string key, int index)
        {
            return key + "#" + index.ToString(CultureInfo.InvariantCulture);
        }

        private static bool IsChunkPart(string key)
        {
            int hash = key.LastIndexOf('#');
            if (hash < 0 || hash == key.Length - 1) return false;
            for (int i = hash + 1; i < key.Length; i++)
            {
                if (key[i] < '0' || key[i] > '9') return false;
            }
            // "map:foo#3" is only a chunk part if "map:foo" is itself a chunk header.
            string raw = API.GetResourceKvpString(key.Substring(0, hash));
            return raw != null && raw.StartsWith(ChunkedMarker, StringComparison.Ordinal);
        }

        // --- Typed convenience wrappers, replacing the SP build's INI parsing. ---

        public static bool GetBool(string key, bool fallback)
        {
            string raw = API.GetResourceKvpString(key);
            if (raw == null) return fallback;
            return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }

        public static void SetBool(string key, bool value)
        {
            API.SetResourceKvp(key, value ? "1" : "0");
        }

        public static int GetInt(string key, int fallback)
        {
            string raw = API.GetResourceKvpString(key);
            int value;
            if (raw != null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return value;
            return fallback;
        }

        public static void SetInt(string key, int value)
        {
            API.SetResourceKvp(key, value.ToString(CultureInfo.InvariantCulture));
        }

        public static float GetFloat(string key, float fallback)
        {
            string raw = API.GetResourceKvpString(key);
            float value;
            if (raw != null && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return value;
            return fallback;
        }

        public static void SetFloat(string key, float value)
        {
            API.SetResourceKvp(key, value.ToString("R", CultureInfo.InvariantCulture));
        }
    }
}
