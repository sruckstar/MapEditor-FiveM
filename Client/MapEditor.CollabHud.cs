using System;
using System.Collections.Generic;
using System.Drawing;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using CitizenFX.Core.UI;
using MapEditor.Ui.Elements;
using MapEditor.Ui.Tools;
using MapEditor.Platform;
using Font = CitizenFX.Core.UI.Font;
using SizeF = MapEditor.Ui.SizeF;

namespace MapEditor
{
    public partial class MapEditor
    {
        // --- Co-editing: what it looks like ----------------------------------------------------------
        //
        // The problem this half solves is particular to this editor and worth stating, because without it
        // co-editing would technically work and be unusable: in the freecam a player is frozen, invisible,
        // and eight metres behind a camera that exists on their machine only. Two people building the same
        // street would have no way whatever of knowing where the other one was, whether the crate that just
        // slid across the road was a person or a bug, or why the wall they are reaching for will not move.
        //
        // So three things are drawn, and each answers one of those questions:
        //
        //   * a name where each of the others is, in their own colour — where is everybody;
        //   * that colour round anything somebody is holding, with their name on it — this is theirs;
        //   * a short list in the corner of what has just happened — what changed while I was facing away.
        //
        // All three are off in one setting each, because a session of two people who can see each other's
        // screens wants none of them.

        /// <summary>One line of the activity list.</summary>
        private sealed class CollabLine
        {
            public string Text;
            public int Slot;

            /// <summary>Game-timer reading of when it was said; see <see cref="Clock"/>.</summary>
            public int At;
        }

        private readonly List<CollabLine> _collabFeed = new List<CollabLine>();

        /// <summary>How many lines of the activity list are kept. The rest is scrollback nobody can scroll.</summary>
        private const int CollabFeedLines = 5;

        /// <summary>How long a line stays on screen.</summary>
        private const int CollabFeedLifeMs = 9000;

        /// <summary>Over the last second of its life a line fades out rather than blinking away.</summary>
        private const int CollabFeedFadeMs = 1000;

        /// <summary>How far away another builder is still named. Beyond it the tag would be a pixel of text.</summary>
        private const float CollabTagRange = 600f;

        /// <summary>How many held objects are outlined at once, so a session-wide grab costs a bounded number of draws.</summary>
        private const int MaxCollabHighlights = 32;

        private const float CollabTagScale = 0.4f;
        private const float MinCollabTagScale = 0.22f;

        /// <summary>Adds a line to the activity list. <paramref name="slot"/> is -1 for the session itself.</summary>
        private void CollabSay(int slot, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            string who = null;
            if (slot >= 0 && slot != Collab.MySlot)
            {
                CollabPeer peer;
                who = Collab.Peers.TryGetValue(slot, out peer) && !string.IsNullOrEmpty(peer.Name)
                    ? peer.Name
                    : Translation.Translate("Someone");
            }
            else if (slot == Collab.MySlot)
            {
                who = Translation.Translate("You");
            }

            _collabFeed.Add(new CollabLine
            {
                Text = who == null ? text : who + " " + text,
                Slot = slot,
                At = Clock.Milliseconds,
            });

            while (_collabFeed.Count > CollabFeedLines) _collabFeed.RemoveAt(0);
        }

        /// <summary>
        /// The part of a session that is drawn in the world: where everybody is, and what they are holding.
        ///
        /// Called from inside the freecam half of the tick, after the object picker has had its chance to
        /// take the frame — a name floating over the model preview would be drawn on top of a scene that is
        /// nowhere near the map. The activity list is drawn separately and earlier, because it is worth
        /// having with the editor closed. Nothing here draws outside a session.
        /// </summary>
        private void DrawCollabWorld()
        {
            if (!Collab.Active || !IsInFreecam || _mainCamera == null || !_mainCamera.Exists()) return;

            DrawCollabStrip();

            if (!_settings.CollabTags) return;

            DrawCollabPeers();
            DrawCollabHolds();
        }

        /// <summary>
        /// The reminder that this map is not private: the session's name and how many people are in it.
        /// Hidden while a menu is up, which is where it would otherwise sit.
        /// </summary>
        private void DrawCollabStrip()
        {
            if (_menuPool.AreAnyVisible) return;

            var topLeft = SafeZone.TopLeft;
            var color = Colors.Peer(Collab.MySlot);

            new ScaledTexture(new PointF(topLeft.X + 10, topLeft.Y + 10), new SizeF(300, 30), "timerbars", "all_black_bg")
            {
                Color = Color.FromArgb(150, 255, 255, 255),
            }.Draw();

            new ScaledText(new PointF(topLeft.X + 20, topLeft.Y + 13), "●", 0.32f, Font.ChaletLondon)
            {
                Color = color,
            }.Draw();

            var people = Collab.Peers.Count + 1;
            new ScaledText(new PointF(topLeft.X + 38, topLeft.Y + 14),
                GetSafeShortString(Collab.SessionName, 24) + "  —  " + people + " " +
                Translation.Translate(people == 1 ? "builder" : "builders"), 0.3f, Font.ChaletLondon)
            {
                Color = Colors.White,
            }.Draw();
        }

        /// <summary>
        /// A name and a chevron where each of the other builders is. The chevron is what carries across a
        /// street — text at four hundred metres is a smudge, a marker is still a marker.
        /// </summary>
        private void DrawCollabPeers()
        {
            var cameraPosition = _mainCamera.Position;
            var cameraDirection = VectorExtensions.RotationToDirection(_mainCamera.Rotation);

            foreach (var pair in Collab.Peers)
            {
                var peer = pair.Value;
                if (!peer.HasPosition || Clock.Since(peer.Seen) > Collab.PresenceStaleMs) continue;

                var toPeer = peer.Position - cameraPosition;
                var distance = toPeer.Length();
                if (distance > CollabTagRange || distance < 1f) continue;

                // SET_DRAW_ORIGIN projects a point behind the camera back onto the screen as though it were
                // in front of it, so anything behind has to be dropped before it is handed over. The same
                // guard the world-object names use, and for the same reason.
                if (Vector3.Dot(cameraDirection, Vector3.Normalize(toPeer)) <= 0f) continue;

                var color = Colors.Peer(peer.Slot);

                // Dimmer for somebody who is in the session but not in the editor: they are around, they
                // are simply not the person who just moved that wall.
                if (!peer.InEditor) color = Color.FromArgb(120, color.R, color.G, color.B);

                Function.Call(Hash.DRAW_MARKER, 0, peer.Position.X, peer.Position.Y, peer.Position.Z + 1.4f,
                    0f, 0f, 0f, 0f, 0f, 0f, 0.7f, 0.7f, 0.7f, color.R, color.G, color.B, color.A,
                    true, true, 2, false, false, false, false);

                var scale = Math.Max(MinCollabTagScale, CollabTagScale * (1f - (distance / CollabTagRange)));
                var label = string.IsNullOrEmpty(peer.Name) ? Translation.Translate("Someone") : peer.Name;
                if (peer.IsHost) label = "★ " + label;

                DrawText3D(peer.Position + new Vector3(0f, 0f, 2.2f), label, color, scale);
            }
        }

        /// <summary>
        /// A box round anything somebody else has picked up, in their colour, with their name on it. This
        /// is what turns "that will not move" from a bug into a fact about another person.
        /// </summary>
        private void DrawCollabHolds()
        {
            if (Collab.Held.Count == 0) return;

            var cameraPosition = _mainCamera.Position;
            var cameraDirection = VectorExtensions.RotationToDirection(_mainCamera.Rotation);
            var drawn = 0;

            foreach (var pair in Collab.Held)
            {
                if (drawn >= MaxCollabHighlights) return;
                if (pair.Value == Collab.MySlot) continue;

                var handle = PropStreamer.HandleOf(pair.Key);
                Vector3 at;
                Entity entity = null;

                if (handle != 0)
                {
                    entity = Compat.Ent(handle);
                    if (entity == null) continue;
                    at = entity.Position;
                }
                else
                {
                    var marker = FindMarker(pair.Key);
                    if (marker == null) continue;
                    at = marker.Position;
                }

                var toObject = at - cameraPosition;
                var distance = toObject.Length();
                if (distance > CollabTagRange || distance < 0.5f) continue;
                if (Vector3.Dot(cameraDirection, Vector3.Normalize(toObject)) <= 0f) continue;

                drawn++;

                var color = Colors.Peer(pair.Value);
                if (entity != null) DrawEntityBox(entity, color);

                CollabPeer peer;
                var name = Collab.Peers.TryGetValue(pair.Value, out peer) && !string.IsNullOrEmpty(peer.Name)
                    ? peer.Name
                    : Translation.Translate("Someone");

                var scale = Math.Max(MinCollabTagScale, CollabTagScale * (1f - (distance / CollabTagRange)));
                DrawText3D(at + new Vector3(0f, 0f, 1.2f), name, color, scale);
            }
        }

        /// <summary>
        /// The last few things that happened, bottom left, above the instructional buttons and fading out
        /// on their own. Drawn outside the freecam too: a change can land while the player is standing on
        /// the ground with the editor closed, and it is still worth knowing about.
        /// </summary>
        private void DrawCollabFeed()
        {
            if (!_settings.CollabFeed || _collabFeed.Count == 0) return;

            var bottomLeft = SafeZone.BottomLeft;

            for (var i = _collabFeed.Count - 1; i >= 0; i--)
            {
                var line = _collabFeed[i];
                var age = Clock.Since(line.At);

                if (age > CollabFeedLifeMs)
                {
                    _collabFeed.RemoveAt(i);
                    continue;
                }

                var remaining = CollabFeedLifeMs - age;
                var alpha = remaining >= CollabFeedFadeMs ? 255 : (int)(255f * remaining / CollabFeedFadeMs);

                var color = line.Slot < 0 ? Colors.White : Colors.Peer(line.Slot);
                var slotFromBottom = _collabFeed.Count - 1 - i;
                var y = bottomLeft.Y - 120 - (slotFromBottom * 26);

                new ScaledText(new PointF(bottomLeft.X + 20, y), line.Text, 0.28f, Font.ChaletLondon)
                {
                    Color = Color.FromArgb(alpha, color.R, color.G, color.B),
                    Outline = true,
                }.Draw();
            }
        }
    }
}
