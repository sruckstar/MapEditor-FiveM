using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CitizenFX.Core;
using MapEditor.Ui.Menus;
using MapEditor.Platform;

namespace MapEditor
{
    public partial class MapEditor
    {
        // --- Co-editing: the editor's half -----------------------------------------------------------
        //
        // Collab.cs holds the session and the wire; this turns it into a map several people are standing
        // in. Two directions, and they are not symmetrical:
        //
        //   out  — the map is *watched*, not instrumented. A rolling pass compares what PropStreamer is
        //          holding against what the session was last told, and the difference is what gets sent.
        //   in   — a change that arrives is applied to the world: an object spawned, moved or taken away.
        //
        // Watching rather than instrumenting is the one decision here worth defending, because the obvious
        // alternative is to send a message from each place that changes something. There are more than
        // fifty such places — dragging, the properties menu with its forty rows, stacking, looping,
        // multi-select, the entity menu, the ped wardrobe, copying, the world-object eraser — spread over
        // eight files, and every one of them is a place to forget. A pass that reads the map cannot forget:
        // whatever moved, moved. It costs a hundred-odd object comparisons a frame and it is right by
        // construction, which is the better trade for a feature whose failure mode is two people looking at
        // different maps and not knowing it.

        /// <summary>What the session has been told about one object, so the next pass can tell what moved.</summary>
        private sealed class CollabKnown
        {
            /// <summary>The map object as last sent or received. Null for a marker; see <see cref="Marker"/>.</summary>
            public MapObject Object;

            public Marker Marker;

            /// <summary>Which of the three tables this belongs to: "" for an object, "m", or "w".</summary>
            public string Table;

            /// <summary>The pass this was last seen in the map. An id not seen in a whole pass has gone.</summary>
            public int Generation;
        }

        private readonly Dictionary<int, CollabKnown> _collabKnown = new Dictionary<int, CollabKnown>();

        /// <summary>
        /// Ids whose object is on its way into the world from somebody else's change. They are in the
        /// session and not yet in the map, which is exactly what the sweep's removal test looks for, so it
        /// has to be told to wait.
        /// </summary>
        private readonly HashSet<int> _collabArriving = new HashSet<int>();

        /// <summary>Ids this client has asked the server to reserve — everything in the player's hands.</summary>
        private readonly HashSet<int> _collabReserved = new HashSet<int>();

        /// <summary>
        /// The subset of <see cref="_collabReserved"/> that is being dragged right now. These are left out
        /// of the sweep and streamed on their own channel instead: a drag changes a position on every frame,
        /// and committing each of those to the document would be sixty edits a second for one crate. The
        /// sweep picks the object up again the moment it is put down, and that is what everyone stores.
        /// </summary>
        private readonly HashSet<int> _collabDragging = new HashSet<int>();

        /// <summary>Changes waiting to go out, flushed on a timer or when they pile up.</summary>
        private Json _collabOutbox;
        private int _collabOutboxCount;
        private int _collabLastFlush;

        private int _collabGeneration;
        private int _collabCursor;

        /// <summary>Game-timer readings; see <see cref="Clock"/>.</summary>
        private int _collabLastHere;
        private int _collabLastDrag;

        /// <summary>Set while a batch of incoming changes is being applied, since applying one spawns models.</summary>
        private bool _collabApplying;

        /// <summary>Set while the whole document is being fetched and rebuilt, which takes many frames.</summary>
        private bool _collabRebuilding;

        /// <summary>How many objects the outgoing pass looks at per frame. A full sweep of a large map is
        /// under half a second at this rate, which is how long a deletion can go unnoticed.</summary>
        private const int CollabScanPerTick = 96;

        /// <summary>How often changes go out. Four times the drag rate: a placement should feel immediate.</summary>
        private const int CollabFlushMs = 120;

        /// <summary>How many changes are allowed to pile up before the timer is not waited for.</summary>
        private const int CollabFlushCount = 64;

        /// <summary>How often the transforms of whatever is being dragged go out.</summary>
        private const int CollabDragMs = 100;

        /// <summary>How often this client says where its camera is. Three a second is enough for a name tag.</summary>
        private const int CollabHereMs = 330;

        /// <summary>
        /// How far apart two positions have to be before the pass calls it a move.
        ///
        /// Not zero, and this matters: an object spawned from a description does not always report the
        /// position it was asked for — a ped answers from its middle and stands on its feet, a prop settles
        /// a millimetre — so two clients holding the same document read very slightly different numbers back
        /// out of the game. With an exact comparison each would see the other's object as moved and send a
        /// correction, and the two would push a crate back and forth forever at sixty frames a second.
        /// </summary>
        private const float CollabPositionEpsilon = 0.002f;

        private const float CollabAngleEpsilon = 0.05f;

        /// <summary>The row on the main menu that opens all of this. Declared in MapEditor.cs with the rest.</summary>
        private NativeItem _collabItem;

        /// <summary>Which participant <see cref="_collabPersonMenu"/> is about.</summary>
        private int _collabPersonSlot = -1;

        /// <summary>The room as this client last reacted to it. See <see cref="NoticeRoomChanges"/>.</summary>
        private int _collabRoomVersion;
        private readonly HashSet<int> _collabSeenPeers = new HashSet<int>();
        private bool _collabWasHost;

        // --- Menus -----------------------------------------------------------------------------------

        /// <summary>
        /// Builds the session menus. Called from the constructor, after the main menu exists — the row that
        /// opens this hangs off it.
        /// </summary>
        private void BuildCollabMenus()
        {
            _collabJoinMenu.Buttons.Visible = false;
            _collabJoinMenu.Parent = _collabMenu;
            _menuPool.Add(_collabJoinMenu);

            _collabPeopleMenu.Buttons.Visible = false;
            _collabPeopleMenu.Parent = _collabMenu;
            _menuPool.Add(_collabPeopleMenu);

            _collabPersonMenu.Buttons.Visible = false;
            _collabPersonMenu.Parent = _collabPeopleMenu;
            _menuPool.Add(_collabPersonMenu);

            _collabMenu.Buttons.Visible = false;
            _collabMenu.Parent = _mainMenu;
            _menuPool.Add(_collabMenu);

            // Not AddSubMenu: this menu's rows depend on whether there is a session and who is in it, so it
            // is rebuilt and then shown — the order every other menu here that has something to say uses.
            // A submenu row shows its menu itself, which would leave the rebuilding to happen inside the
            // Shown handler of a menu that is already on screen.
            _collabItem = new NativeItem(Translation.Translate("Build With Others"));
            _collabItem.Activated += (sender, args) =>
            {
                SetMenuVisible(_mainMenu, false);
                RedrawCollabMenu();
                SetMenuVisible(_collabMenu, true);
            };
            _mainMenu.Add(_collabItem);
        }

        private void RedrawCollabMenu()
        {
            _collabMenu.Clear();

            if (!Collab.Available)
            {
                _collabMenu.Add(new NativeItem(Translation.Translate("Not available here"),
                    Translation.Translate("This server is either running an older Map Editor, or its owner has not allowed players to edit maps together."))
                {
                    Enabled = false,
                    LeftBadgeSet = AlertBadge,
                });
                return;
            }

            if (!Collab.Active)
            {
                var host = new NativeItem(Translation.Translate("Open a Session"),
                    Translation.Translate("Put the map you have open now in front of the other players, so they can build it with you. Nothing is saved and nobody outside the session sees any of it."));
                host.Activated += (sender, args) => BeginHostSession();
                _collabMenu.Add(host);

                RedrawCollabJoinMenu();
                var join = _collabMenu.AddSubMenu(_collabJoinMenu);
                join.Title = Translation.Translate("Join a Session");
                join.Description = Translation.Translate("Take up somebody else's map where they have got to. What you have open now is put away — save it first if you want it.");

                AddCollabDisplayRows();
                _collabMenu.SelectedIndex = 0;
                return;
            }

            _collabMenu.Add(new NativeItem("~b~" + GetSafeShortString(Collab.SessionName, 34),
                Collab.IsHost
                    ? Translation.Translate("You are the host: the map's name, replacing it, and ending the session are yours.")
                    : Translation.Translate("The map's name and what map the session is about belong to its host."))
            {
                Enabled = false,
                AltTitle = (Collab.Peers.Count + 1).ToString(CultureInfo.InvariantCulture),
            });

            RedrawCollabPeopleMenu();
            var people = _collabMenu.AddSubMenu(_collabPeopleMenu);
            people.Title = Translation.Translate("Who Is Here");
            people.AltTitle = (Collab.Peers.Count + 1).ToString(CultureInfo.InvariantCulture);

            var resync = new NativeItem(Translation.Translate("Fetch the Map Again"),
                Translation.Translate("Throw away what you have and rebuild it from the session. For when your screen and somebody else's have plainly stopped agreeing."));
            resync.Activated += (sender, args) => BeginResync();
            _collabMenu.Add(resync);

            var leave = new NativeItem("~o~" + Translation.Translate("Leave the Session"),
                Translation.Translate("Stop sharing your edits. The map stays in your editor and you can save it as your own."));
            leave.Activated += (sender, args) => BeginLeaveSession();
            _collabMenu.Add(leave);

            if (Collab.IsHost)
            {
                var end = new NativeItem("~r~" + Translation.Translate("End It for Everyone"),
                    Translation.Translate("Close the session. Everyone keeps the map in their own editor; only the thread between you is cut."));
                end.Activated += (sender, args) => BeginEndSession(null);
                _collabMenu.Add(end);
            }

            AddCollabDisplayRows();
            _collabMenu.SelectedIndex = 0;
        }

        private void AddCollabDisplayRows()
        {
            var tags = new NativeCheckboxItem(Translation.Translate("Show Where People Are"),
                Translation.Translate("Names above the other builders' cameras, and their colour round anything they have picked up."),
                _settings.CollabTags);
            tags.CheckboxChanged += (sender, args) =>
            {
                _settings.CollabTags = tags.Checked;
                SaveSettings();
            };
            _collabMenu.Add(tags);

            var feed = new NativeCheckboxItem(Translation.Translate("Show What People Do"),
                Translation.Translate("A short list in the corner of the screen of what the others have just placed, moved or removed."),
                _settings.CollabFeed);
            feed.CheckboxChanged += (sender, args) =>
            {
                _settings.CollabFeed = feed.Checked;
                SaveSettings();
            };
            _collabMenu.Add(feed);
        }

        /// <summary>
        /// Fills the join menu from what was last known and goes to look for more behind it — the same
        /// arrangement the file picker uses, and for the same reason: the list is a cache, somebody may
        /// have opened a session since, and a menu that waited for the server before it appeared would
        /// look like a menu that had failed to open.
        /// </summary>
        private void RedrawCollabJoinMenu()
        {
            FillCollabJoinMenu();

            Log.Guard("Refresh sessions", async () =>
            {
                if (await Collab.Refresh() != null) return;
                if (!_collabJoinMenu.Visible) return;

                // Only the filling half, never this one: refreshing from inside the refresh handler is a
                // loop that asks the server again every time the answer comes back.
                FillCollabJoinMenu();
            });
        }

        private void FillCollabJoinMenu()
        {
            _collabJoinMenu.Clear();

            if (Collab.OpenSessions.Count == 0)
            {
                _collabJoinMenu.Add(new NativeItem(Translation.Translate("Nobody is building right now."),
                    Translation.Translate("Open one yourself and it appears here for everybody else."))
                {
                    Enabled = false,
                });
            }

            foreach (var open in Collab.OpenSessions)
            {
                var session = open;
                var item = new NativeItem(GetSafeShortString(session.Name, 34),
                    string.Format(CultureInfo.InvariantCulture, "~h~{0}:~h~ {1}\n~h~{2}:~h~ {3}\n~h~{4}:~h~ {5:N0}",
                        Translation.Translate("Host"), GetSafeShortString(session.Host, 24),
                        Translation.Translate("Builders"), session.Players,
                        Translation.Translate("Objects"), session.Objects))
                {
                    AltTitle = session.Players.ToString(CultureInfo.InvariantCulture),
                };

                item.Activated += (sender, args) => BeginJoinSession(session.Id);
                _collabJoinMenu.Add(item);
            }

            _collabJoinMenu.SelectedIndex = 0;
        }

        private void RedrawCollabPeopleMenu()
        {
            _collabPeopleMenu.Clear();

            if (!Collab.Active) return;

            var me = new NativeItem("~b~" + Translation.Translate("You"),
                Collab.IsHost
                    ? Translation.Translate("You are hosting this session.")
                    : Translation.Translate("You are building in this session."))
            {
                Enabled = false,
                AltTitle = CollabHoldCount(Collab.MySlot).ToString(CultureInfo.InvariantCulture),
            };
            _collabPeopleMenu.Add(me);

            foreach (var pair in Collab.Peers)
            {
                var peer = pair.Value;

                var where = !peer.HasPosition || Clock.Since(peer.Seen) > Collab.PresenceStaleMs
                    ? Translation.Translate("Not saying where they are.")
                    : peer.InEditor
                        ? Translation.Translate("In the editor.")
                        : Translation.Translate("On the ground, not editing.");

                var item = new NativeItem(GetSafeShortString(peer.Name, 30),
                    (peer.IsHost ? Translation.Translate("Hosting this session.") + "\n" : "") + where +
                    "\n" + Translation.Translate("Holding") + ": " + CollabHoldCount(peer.Slot))
                {
                    AltTitle = CollabHoldCount(peer.Slot).ToString(CultureInfo.InvariantCulture),
                };

                item.Activated += (sender, args) =>
                {
                    _collabPersonSlot = peer.Slot;
                    RedrawCollabPersonMenu();
                    SetMenuVisible(_collabPeopleMenu, false);
                    SetMenuVisible(_collabPersonMenu, true);
                };

                _collabPeopleMenu.Add(item);
            }

            _collabPeopleMenu.SelectedIndex = 0;
        }

        private void RedrawCollabPersonMenu()
        {
            _collabPersonMenu.Clear();

            CollabPeer peer;
            if (!Collab.Peers.TryGetValue(_collabPersonSlot, out peer)) return;

            _collabPersonMenu.Name = "~b~" + GetSafeShortString(peer.Name, 24).ToUpper();

            var goTo = new NativeItem(Translation.Translate("Fly to Them"),
                Translation.Translate("Puts your camera where theirs is. The editor has to be open."));
            goTo.Activated += (sender, args) => FlyToPeer(peer.Slot);
            goTo.Enabled = peer.HasPosition && IsInFreecam;
            if (!IsInFreecam)
                goTo.Description = Translation.Translate("Enter the map editor first.");
            _collabPersonMenu.Add(goTo);

            if (!Collab.IsHost) return;

            var free = new NativeItem(Translation.Translate("Let Go of What They Hold"),
                Translation.Translate("Frees the objects they have picked up, for when somebody has walked away holding half the map."))
            {
                AltTitle = CollabHoldCount(peer.Slot).ToString(CultureInfo.InvariantCulture),
                Enabled = CollabHoldCount(peer.Slot) > 0,
            };
            free.Activated += (sender, args) => Run("Free holds", async () =>
            {
                var error = await Collab.Free(peer.Slot);
                CollabNotify(error ?? Translation.Translate("Let go of everything they were holding."), error != null);
                RedrawCollabPersonMenu();
            });
            _collabPersonMenu.Add(free);

            var hand = new NativeItem("~o~" + Translation.Translate("Make Them the Host"),
                Translation.Translate("They get the map's name, replacing the map, and ending the session. You stop having them."));
            hand.Activated += (sender, args) => Run("Hand over session", async () =>
            {
                var error = await Collab.HandOver(peer.Slot);
                CollabNotify(error ?? Translation.Translate("They are the host now."), error != null);
                SetMenuVisible(_collabPersonMenu, false);
                RedrawCollabMenu();
                SetMenuVisible(_collabMenu, true);
            });
            _collabPersonMenu.Add(hand);
        }

        private static int CollabHoldCount(int slot)
        {
            var count = 0;
            foreach (var pair in Collab.Held)
            {
                if (pair.Value == slot) count++;
            }
            return count;
        }

        private void FlyToPeer(int slot)
        {
            CollabPeer peer;
            if (!Collab.Peers.TryGetValue(slot, out peer) || !peer.HasPosition) return;
            if (!IsInFreecam || _mainCamera == null) return;

            CloseAllMenus();
            _mainCamera.Position = peer.Position;

            // Not their rotation: only the heading travels, and dropping in with a camera pitched at
            // whatever theirs happened to be is disorienting. Level, facing the same way.
            _mainCamera.Rotation = VectorExtensions.ClampCameraRotation(new Vector3(0f, 0f, peer.Heading));
        }

        // --- Opening, joining, leaving ---------------------------------------------------------------

        private void BeginHostSession()
        {
            if (Collab.Active)
            {
                CollabNotify(Translation.Translate("You are already in a session."), true);
                return;
            }

            Run("Open session", async () =>
            {
                var suggested = string.IsNullOrWhiteSpace(PropStreamer.CurrentMapMetadata.Name)
                    ? ""
                    : PropStreamer.CurrentMapMetadata.Name;

                var name = await Compat.GetUserInput(suggested, MapNames.MaxLength);
                if (string.IsNullOrWhiteSpace(name)) return;

                var opened = await Collab.Open(name.Trim());
                if (opened.Error != null)
                {
                    CollabNotify(opened.Error, true);
                    return;
                }

                // The session is empty until this lands. Sent before anything else so that a player who
                // joins in the same second gets the map rather than a blank room.
                var error = await PushWholeSession();
                if (error != null)
                {
                    CollabNotify(error, true);
                    await Collab.Leave();
                    return;
                }

                CollabNotify(Translation.Translate("The session is open. Others can join it from their own editor."));
                CollabSay(Collab.MySlot, Translation.Translate("Session opened."));
                WarnAboutPickups();
                RefreshCollabMenus();
            });
        }

        private void BeginJoinSession(int id)
        {
            Run("Join session", async () =>
            {
                var joined = await Collab.Join(id);
                if (joined.Error != null)
                {
                    CollabNotify(joined.Error, true);
                    return;
                }

                CloseAllMenus();
                await ApplySessionDocument(joined.Body);

                CollabNotify(Translation.Translate("You are building with") + " ~h~" +
                             (Collab.Peers.Count + 1) + "~h~ " + Translation.Translate("people."));
                RefreshCollabMenus();
            });
        }

        /// <summary>
        /// Throws away what is standing and rebuilds it from the session. Both the button and what a client
        /// does when the host replaces the map underneath it — the same request, so the rare path is the
        /// well-trodden one. See Collab.Join.
        /// </summary>
        private void BeginResync()
        {
            if (!Collab.Active || _collabRebuilding) return;

            var id = Collab.SessionId;
            _collabRebuilding = true;

            Log.Guard("Resynchronise session", async () =>
            {
                try
                {
                    var joined = await Collab.Join(id);
                    if (joined.Error != null)
                    {
                        CollabNotify(joined.Error, true);
                        return;
                    }

                    await ApplySessionDocument(joined.Body);
                    CollabNotify(Translation.Translate("Fetched the session's map again."));
                    RefreshCollabMenus();
                }
                finally
                {
                    _collabRebuilding = false;
                }
            });
        }

        private void BeginLeaveSession()
        {
            Run("Leave session", async () =>
            {
                var error = await Collab.Leave();
                ForgetSession();

                CollabNotify(error ?? Translation.Translate("You have left the session. The map is still yours to save."),
                    error != null);
                RefreshCollabMenus();
            });
        }

        /// <summary>Closes the session for everyone. The host's row, and the first half of publishing.</summary>
        private void BeginEndSession(string why)
        {
            Run("End session", async () =>
            {
                var error = await Collab.End(why);
                ForgetSession();

                CollabNotify(error ?? Translation.Translate("The session is over. Everyone keeps their copy of the map."),
                    error != null);
                RefreshCollabMenus();
            });
        }

        /// <summary>
        /// Drops everything the editor was keeping for a session that has ended. The map itself is left
        /// exactly where it is: it is the player's work, and it is theirs whether or not anybody else is
        /// still looking at it.
        /// </summary>
        private void ForgetSession()
        {
            _collabKnown.Clear();
            _collabArriving.Clear();
            _collabReserved.Clear();
            _collabDragging.Clear();
            _collabOutbox = null;
            _collabOutboxCount = 0;
            _collabCursor = 0;
            _collabSeenPeers.Clear();
            _collabWasHost = false;
            _collabRoomVersion = Collab.RoomVersion;

            // The ids stay on the objects and simply stop meaning anything. Clearing them would be a second
            // pass over the whole map to no purpose: the next session mints its own, and PropStreamer drops
            // each one with the entity it belongs to.
        }

        private void RefreshCollabMenus()
        {
            if (_collabMenu.Visible) RedrawCollabMenu();
            if (_collabPeopleMenu.Visible) RedrawCollabPeopleMenu();
            if (_collabPersonMenu.Visible) RedrawCollabPersonMenu();

            // The filling half only: this is called whenever the room changes, and asking the server for
            // the session list every time somebody moves a crate is not what a redraw is for.
            if (_collabJoinMenu.Visible) FillCollabJoinMenu();
        }

        /// <summary>
        /// Notices that the people in the session have changed — somebody arrived, left, or became host —
        /// and both says so and rebuilds whichever menu is showing the old list.
        ///
        /// Done by comparing rather than by acting on the message, because the message carries the whole
        /// list rather than the change: that is what makes a client that missed one recover on the next,
        /// and it means the difference has to be worked out here.
        /// </summary>
        private void NoticeRoomChanges()
        {
            if (Collab.RoomVersion == _collabRoomVersion) return;
            _collabRoomVersion = Collab.RoomVersion;

            foreach (var pair in Collab.Peers)
            {
                if (_collabSeenPeers.Contains(pair.Key)) continue;
                _collabSeenPeers.Add(pair.Key);
                CollabSay(pair.Key, Translation.Translate("joined the session."));
            }

            List<int> left = null;
            foreach (var slot in _collabSeenPeers)
            {
                if (Collab.Peers.ContainsKey(slot)) continue;
                (left ?? (left = new List<int>())).Add(slot);
            }

            if (left != null)
            {
                foreach (var slot in left)
                {
                    _collabSeenPeers.Remove(slot);

                    // Said without a name: the peer record has already gone, which is how we know they have.
                    CollabSay(-1, Translation.Translate("Somebody left the session."));
                }
            }

            if (Collab.IsHost && !_collabWasHost)
                CollabSay(Collab.MySlot, Translation.Translate("are the session's host now."));

            _collabWasHost = Collab.IsHost;

            RefreshCollabMenus();
        }

        /// <summary>
        /// Said once when a session opens on a map that has pickups in it. A pickup is the one thing the
        /// editor places that is a networked entity in the game's own right, which is why a published map
        /// leaves them out too — and a session cannot carry what it cannot describe as a document.
        /// </summary>
        private void WarnAboutPickups()
        {
            if (PropStreamer.Pickups.Count == 0) return;

            CollabNotify(PropStreamer.Pickups.Count + " " + Translation.Translate(
                "pickup(s) are not part of the session: they are the game's own networked objects and only you can see yours."));
        }

        /// <summary>
        /// Whether the player may throw away the map that is open and put another one there. Outside a
        /// session, always. Inside one the map belongs to everybody in it, so it is the host's alone —
        /// and a member who tries is told that rather than quietly having their own screen emptied while
        /// everyone else's stays as it was.
        /// </summary>
        private bool MayReplaceTheMap()
        {
            if (!Collab.Active || Collab.IsHost) return true;

            CollabNotify(Translation.Translate(
                "This map belongs to the session. Leave it first, or ask the host to change the map."), true);
            return false;
        }

        /// <summary>
        /// Hands the session the map that has just replaced the one it was about. Called after "New Map"
        /// and after a load; it does nothing at all unless this client is hosting a session.
        /// </summary>
        private void AfterMapReplaced()
        {
            if (!Collab.Active || !Collab.IsHost) return;

            Log.Guard("Push the session's new map", async () =>
            {
                var error = await PushWholeSession();
                CollabNotify(error ?? Translation.Translate("Everyone in the session is being given the new map."),
                    error != null);
            });
        }

        // --- The whole document ----------------------------------------------------------------------

        /// <summary>
        /// Sends the session everything the editor is holding, and starts the record of what it has been
        /// told. Only the host does this, and only when the map has been replaced rather than edited.
        /// </summary>
        private async Task<string> PushWholeSession()
        {
            _collabKnown.Clear();
            _collabArriving.Clear();
            _collabReserved.Clear();
            _collabDragging.Clear();
            _collabOutbox = null;
            _collabOutboxCount = 0;
            _collabGeneration = 0;
            _collabCursor = 0;

            var objects = Json.Array();
            var markers = Json.Array();
            var removed = Json.Array();

            // Walked list by list rather than through GetAllEntities, because the id has to land where the
            // pass that watches for changes will look for it again: on the entity's handle for something
            // standing in the world, and on the record for something streaming has taken out. An id minted
            // onto a snapshot would be forgotten with the snapshot, and every object would be announced a
            // second time a fraction of a second later.
            foreach (var handle in PropStreamer.StreamedInHandles) AddToPush(objects, handle);
            foreach (var handle in PropStreamer.Vehicles) AddToPush(objects, handle);
            foreach (var handle in PropStreamer.Peds) AddToPush(objects, handle);

            foreach (var record in PropStreamer.StreamedOut)
            {
                var o = record.Object;
                if (o == null || o.Type == ObjectTypes.Pickup) continue;

                if (o.Uid == 0) o.Uid = Collab.NextUid();
                if (o.Uid == 0) continue;

                objects.Add(Json.Object().Set("u", o.Uid).Set("o", MapSerializer.ObjectJson(o)));
                Remember(o.Uid, "", CloneObjectValue(o), null);
            }

            foreach (var marker in PropStreamer.Markers)
            {
                if (marker.Uid == 0) marker.Uid = Collab.NextUid();
                if (marker.Uid == 0) continue;

                markers.Add(Json.Object().Set("u", marker.Uid).Set("o", MapSerializer.MarkerJson(marker)));
                Remember(marker.Uid, "m", null, CloneMarkerValue(marker));
            }

            foreach (var o in PropStreamer.RemovedObjects)
            {
                if (o.Uid == 0) o.Uid = Collab.NextUid();
                if (o.Uid == 0) continue;

                removed.Add(Json.Object().Set("u", o.Uid).Set("o", MapSerializer.ObjectJson(o)));
                Remember(o.Uid, "w", CloneObjectValue(o), null);
            }

            var document = Json.Object()
                .Set("objects", objects)
                .Set("markers", markers)
                .Set("removed", removed)
                .Set("meta", MapSerializer.MetadataJson(PropStreamer.CurrentMapMetadata));

            return await Collab.Push(document.ToJson());
        }

        /// <summary>One entity of the map on its way into the document the session is opened on.</summary>
        private void AddToPush(Json objects, int handle)
        {
            var uid = PropStreamer.UidOf(handle);
            if (uid == 0)
            {
                uid = Collab.NextUid();
                if (uid == 0) return;
                PropStreamer.Uids[handle] = uid;
            }

            var snapshot = PropStreamer.Snapshot(handle);
            if (snapshot == null) return;

            snapshot.Uid = uid;
            objects.Add(Json.Object().Set("u", uid).Set("o", MapSerializer.ObjectJson(snapshot)));
            Remember(uid, "", snapshot, null);
        }

        /// <summary>
        /// Replaces the editor's map with the session's. What joining does, and what a client does when the
        /// host swaps the map underneath it.
        ///
        /// It is the map loader with ids attached: the same choice about what to spawn and what to leave
        /// waiting out of range (see <see cref="LoadMap"/>), because a session's map is a map and a large
        /// one has to arrive the same way.
        /// </summary>
        private async Task ApplySessionDocument(Json body)
        {
            if (body == null) return;

            NewMap(quiet: true);

            _collabKnown.Clear();
            _collabArriving.Clear();
            _collabReserved.Clear();
            _collabDragging.Clear();
            _collabOutbox = null;
            _collabOutboxCount = 0;
            _collabGeneration = 0;
            _collabCursor = 0;

            var meta = MapSerializer.MetadataFrom(body["meta"]);
            if (meta != null) PropStreamer.CurrentMapMetadata = meta;

            SmartStreaming.BeginTick(CurrentStreamingOrigin);

            foreach (var item in body["objects"].Items)
            {
                var uid = item["u"].AsInt(0);
                var o = MapSerializer.ObjectFrom(item["o"]);
                if (uid == 0 || o == null || o.Type == ObjectTypes.Pickup) continue;

                o.Uid = uid;
                _loadedEntities++;

                Remember(uid, "", CloneObjectValue(o), null);
                await PlaceSessionObject(o);
            }

            foreach (var item in body["markers"].Items)
            {
                var uid = item["u"].AsInt(0);
                var marker = MapSerializer.MarkerFrom(item["o"]);
                if (uid == 0 || marker == null) continue;

                marker.Uid = uid;
                _markerCounter++;
                marker.Id = _markerCounter;

                PropStreamer.Markers.Add(marker);
                AddItemToEntityMenu(marker);
                Remember(uid, "m", null, CloneMarkerValue(marker));
            }

            foreach (var item in body["removed"].Items)
            {
                var uid = item["u"].AsInt(0);
                var o = MapSerializer.ObjectFrom(item["o"]);
                if (uid == 0 || o == null) continue;

                o.Uid = uid;
                o.Id = _mapObjCounter.ToString(CultureInfo.InvariantCulture);
                _mapObjCounter++;

                PropStreamer.RemoveWorldObject(o);
                AddItemToEntityMenu(o);
                Remember(uid, "w", CloneObjectValue(o), null);
            }

            _changesMade = 0;
        }

        /// <summary>
        /// Puts one of the session's objects into the world, or leaves it waiting out of range. The same
        /// decision <see cref="LoadMap"/> makes, for the same reason: most of a large map is nowhere near
        /// the player, and spawning all of it would spend the prop budget on scenery nobody is looking at.
        /// </summary>
        private async Task<Entity> PlaceSessionObject(MapObject o)
        {
            if (o.Id == null && !SmartStreaming.HasReturned(o.Position))
                return WaitOutOfRange(o);

            var entity = await SpawnMapObject(o);

            // A spawn that failed — a model this client does not have, or a streamer that would not part
            // with one — leaves the object in the map as something waiting to be placed, not out of it.
            //
            // Loading a map alone drops it silently and that is only a gap on one screen. In a session it
            // would be far worse: the pass that watches for changes would not find the object anywhere,
            // conclude that this player had deleted it, and take it out of everybody else's map. One client
            // missing a DLC model would quietly demolish the parts of the map made of it.
            if (entity == null) return WaitOutOfRange(o);

            PropStreamer.Uids[entity.Handle] = o.Uid;

            // What the game did to the position on the way in is written off here rather than becoming a
            // change: a ped is placed by its feet and answers about its middle, so reading it straight back
            // would give a number a metre from the one the session holds — and the pass that watches for
            // changes would report that as a move nobody made. See PropStreamer.Anchors.
            PropStreamer.Anchor(entity, o.Position);

            AddItemToEntityMenu(entity);
            return entity;
        }

        /// <summary>
        /// Keeps one of the session's objects in the map without putting it in the world. Streaming spawns
        /// it when the player goes to it, or tries again later if it could not be spawned this time.
        /// </summary>
        private Entity WaitOutOfRange(MapObject o)
        {
            var record = PropStreamer.AddStreamedOut(o);
            AddStreamedItemToEntityMenu(o, record.Uid);
            return null;
        }

        // --- The frame ------------------------------------------------------------------------------

        /// <summary>
        /// One frame of the session. Called from the tick whether or not the freecam is up: a participant
        /// who has stepped out of the editor for a moment is still in the session, still has the map
        /// standing around them, and still has to receive everybody else's work.
        /// </summary>
        private void ProcessCollab()
        {
            if (Collab.EndedReason != null)
            {
                var why = Collab.EndedReason;
                Collab.EndedReason = null;
                ForgetSession();
                CollabNotify(why, true);
                CollabSay(-1, why);
                RefreshCollabMenus();
            }

            if (!Collab.Active) return;

            if (Collab.ResetPending && !_collabRebuilding)
            {
                Collab.ResetPending = false;
                CollabNotify(Translation.Translate("The host has changed the map. Fetching it..."));
                BeginResync();
            }

            NoticeRoomChanges();

            if (_collabRebuilding) return;

            ApplyIncoming();
            ApplyIncomingDrags();
            UpdateReservations();
            SweepForChanges();
            FlushOutbox();
            SendDragFrame();
            SendPresence();
        }

        private void SendPresence()
        {
            if (!Clock.Elapsed(_collabLastHere, CollabHereMs)) return;
            _collabLastHere = Clock.Milliseconds;

            var origin = CurrentStreamingOrigin;
            var heading = IsInFreecam && _mainCamera != null && _mainCamera.Exists()
                ? _mainCamera.Rotation.Z
                : Game.Player.Character.Heading;

            Collab.SendHere(origin, heading, IsInFreecam);
        }

        // --- Reservations ---------------------------------------------------------------------------

        /// <summary>
        /// Keeps the server's idea of what this player has picked up in step with what they actually have,
        /// by looking at the editor rather than by being told.
        ///
        /// Everything that puts an object into the player's hands does so by assigning one of half a dozen
        /// fields — the snapped prop, the selected prop, the multi-selection, the stacking and looping
        /// bases, the two marker equivalents — and every one of them is read here. That is why picking a
        /// crate up reserves it without a single line being added to the code that picks crates up.
        /// </summary>
        private void UpdateReservations()
        {
            var wanted = new HashSet<int>();
            var dragging = new HashSet<int>();

            AddReservation(wanted, dragging, _snappedProp, true);
            AddReservation(wanted, dragging, _selectedProp, false);
            AddReservation(wanted, dragging, _stackingBase, false);
            AddReservation(wanted, dragging, _loopingBase, false);

            foreach (var selected in _multiSelection)
                AddReservation(wanted, dragging, selected, _multiSelectionSnapped);

            if (_snappedMarker != null && _snappedMarker.Uid != 0)
            {
                wanted.Add(_snappedMarker.Uid);
                dragging.Add(_snappedMarker.Uid);
            }

            if (_selectedMarker != null && _selectedMarker.Uid != 0) wanted.Add(_selectedMarker.Uid);

            _collabDragging.Clear();
            foreach (var uid in dragging) _collabDragging.Add(uid);

            List<int> taken = null;
            foreach (var uid in wanted)
            {
                if (_collabReserved.Contains(uid)) continue;
                (taken ?? (taken = new List<int>())).Add(uid);
            }

            List<int> given = null;
            foreach (var uid in _collabReserved)
            {
                if (wanted.Contains(uid)) continue;
                (given ?? (given = new List<int>())).Add(uid);
            }

            if (given != null)
            {
                foreach (var uid in given) _collabReserved.Remove(uid);
                Collab.Drop(given);
            }

            if (taken == null) return;

            foreach (var uid in taken) _collabReserved.Add(uid);

            // Asked for after the player has already got hold of it, never before: the round trip is a
            // tenth of a second and a crate that only starts moving after it is not a crate the player
            // believes they are dragging. If the answer is no it is put down again, below.
            var asked = taken;
            Log.Guard("Reserve objects", async () =>
            {
                var error = await Collab.Grab(asked);
                if (error == null) return;

                foreach (var uid in asked) _collabReserved.Remove(uid);
                LetGoOf(asked);
                RestoreFromSession(asked);
                CollabNotify(error, true);
            });
        }

        private void AddReservation(HashSet<int> wanted, HashSet<int> dragging, Entity entity, bool isDrag)
        {
            if (entity == null || !entity.Exists()) return;

            var uid = PropStreamer.UidOf(entity.Handle);
            if (uid == 0) return;

            wanted.Add(uid);
            if (isDrag) dragging.Add(uid);
        }

        /// <summary>
        /// Takes objects out of the player's hands because somebody else got them first. Whatever the
        /// editor was doing with them stops; the server's own copy of where they are follows a moment later
        /// and slides them back.
        /// </summary>
        private void LetGoOf(List<int> uids)
        {
            foreach (var uid in uids)
            {
                var handle = PropStreamer.HandleOf(uid);

                if (handle != 0)
                {
                    if (_snappedProp != null && _snappedProp.Handle == handle) _snappedProp = null;
                    if (_selectedProp != null && _selectedProp.Handle == handle)
                    {
                        _selectedProp = null;
                        SetMenuVisible(_objectInfoMenu, false);
                    }

                    var index = _multiSelection.FindIndex(e => e != null && e.Handle == handle);
                    if (index != -1)
                    {
                        _multiSelection.RemoveAt(index);
                        _multiSelectionOffsets.RemoveAt(index);
                    }
                }

                if (_snappedMarker != null && _snappedMarker.Uid == uid) _snappedMarker = null;
                if (_selectedMarker != null && _selectedMarker.Uid == uid)
                {
                    _selectedMarker = null;
                    SetMenuVisible(_objectInfoMenu, false);
                }
            }

            if (_multiSelection.Count == 0) _multiSelectionSnapped = false;
        }

        /// <summary>
        /// Puts objects back where the session says they are, after a reservation was refused.
        ///
        /// Needed because the tenth of a second before the refusal arrives is a tenth of a second of real
        /// dragging, and it is not reported to anyone: an object in the player's hands is left out of the
        /// pass that watches for changes. So nothing on the server knows the object moved, nothing will
        /// correct it, and without this it would sit where the loser of the race dragged it — until they
        /// eventually announced that position as the truth.
        /// </summary>
        private void RestoreFromSession(List<int> uids)
        {
            foreach (var uid in uids)
            {
                CollabKnown known;
                if (!_collabKnown.TryGetValue(uid, out known)) continue;

                if (known.Marker != null)
                {
                    var marker = FindMarker(uid);
                    if (marker == null) continue;

                    marker.Position = known.Marker.Position;
                    marker.Rotation = known.Marker.Rotation;
                    continue;
                }

                if (known.Object == null) continue;

                var handle = PropStreamer.HandleOf(uid);
                var entity = handle == 0 ? null : Compat.Ent(handle);
                if (entity == null) continue;

                MoveEntityTo(entity, known.Object);
            }
        }

        /// <summary>
        /// Whether the player may take hold of this entity. False when somebody else in the session has it,
        /// and it says who — the one refusal in the editor that is about another person rather than about
        /// the map.
        /// </summary>
        private bool CanTouch(Entity entity)
        {
            if (entity == null || !Collab.Active) return true;
            return CanTouchUid(PropStreamer.UidOf(entity.Handle));
        }

        private bool CanTouch(Marker marker)
        {
            if (marker == null || !Collab.Active) return true;
            return CanTouchUid(marker.Uid);
        }

        private bool CanTouchUid(int uid)
        {
            if (uid == 0 || !Collab.HeldByOther(uid)) return true;

            CollabNotify("~h~" + Collab.HolderName(uid) + "~h~ " + Translation.Translate("has hold of that."), true);
            return false;
        }

        // --- Outgoing: what changed ------------------------------------------------------------------

        /// <summary>
        /// Walks a slice of the map and sends whatever no longer matches what the session was told.
        ///
        /// The cursor runs over the map's four parts laid end to end and wraps; a whole pass takes a few
        /// frames on a large map. The lists shift underneath it as objects are placed and deleted, exactly
        /// as they do for the streaming scan next door, and with the same consequence: an object stepped
        /// over is looked at on the following pass a fraction of a second later.
        ///
        /// Removals can only be found by a whole pass — an id is gone when the map has been walked from end
        /// to end without it turning up — so they are dealt with when the cursor wraps.
        /// </summary>
        private void SweepForChanges()
        {
            var total = CollabItemCount;
            if (total == 0)
            {
                // An empty map is a pass that finished the moment it started, and the generation has to move
                // on with it — otherwise everything the player has just deleted stays at the generation of
                // the last pass that saw it, is never found to be missing, and is never reported gone.
                CollectRemovals();
                _collabGeneration++;
                return;
            }

            var budget = Math.Min(total, CollabScanPerTick);

            for (var i = 0; i < budget; i++)
            {
                if (_collabCursor >= CollabItemCount)
                {
                    _collabCursor = 0;
                    CollectRemovals();
                    _collabGeneration++;
                }

                InspectCollabItem(_collabCursor);
                _collabCursor++;
            }
        }

        private static int CollabItemCount
        {
            get
            {
                return PropStreamer.StreamedInHandles.Count + PropStreamer.Vehicles.Count +
                       PropStreamer.Peds.Count + PropStreamer.StreamedOut.Count +
                       PropStreamer.Markers.Count + PropStreamer.RemovedObjects.Count;
            }
        }

        /// <summary>
        /// Looks at one object of the map and queues whatever has to be said about it. Position in the
        /// combined index the sweep walks: the three live lists, then what streaming has taken out, then
        /// the markers, then the game's own objects the map hides.
        /// </summary>
        private void InspectCollabItem(int index)
        {
            var props = PropStreamer.StreamedInHandles;
            if (index < props.Count)
            {
                InspectLiveEntity(props[index]);
                return;
            }
            index -= props.Count;

            var vehicles = PropStreamer.Vehicles;
            if (index < vehicles.Count)
            {
                InspectLiveEntity(vehicles[index]);
                return;
            }
            index -= vehicles.Count;

            var peds = PropStreamer.Peds;
            if (index < peds.Count)
            {
                InspectLiveEntity(peds[index]);
                return;
            }
            index -= peds.Count;

            var streamed = PropStreamer.StreamedOut;
            if (index < streamed.Count)
            {
                // Out of the world but still in the map. Its description cannot have changed while nothing
                // could reach it, so this only mints an id and marks it as still present.
                var record = streamed[index];
                var uid = EnsureObjectUid(record.Object);
                if (uid != 0) InspectObject(uid, "", record.Object);
                return;
            }
            index -= streamed.Count;

            var markers = PropStreamer.Markers;
            if (index < markers.Count)
            {
                InspectMarker(markers[index]);
                return;
            }
            index -= markers.Count;

            var removed = PropStreamer.RemovedObjects;
            if (index >= removed.Count) return;

            var world = removed[index];
            if (world.Uid == 0) world.Uid = Collab.NextUid();
            if (world.Uid != 0) InspectObject(world.Uid, "w", world);
        }

        private void InspectLiveEntity(int handle)
        {
            var uid = PropStreamer.UidOf(handle);

            if (uid == 0)
            {
                uid = Collab.NextUid();
                if (uid == 0) return;
                PropStreamer.Uids[handle] = uid;
            }

            // Being dragged: its position is going out on the drag channel every tenth of a second and is
            // not the document's business until it is put down.
            if (_collabDragging.Contains(uid))
            {
                Seen(uid);
                return;
            }

            var snapshot = PropStreamer.Snapshot(handle);
            if (snapshot == null) return;

            snapshot.Uid = uid;
            InspectObject(uid, "", snapshot);
        }

        private void InspectObject(int uid, string table, MapObject current)
        {
            Seen(uid);

            CollabKnown known;
            if (_collabKnown.TryGetValue(uid, out known) && known.Object != null)
            {
                if (SameObject(known.Object, current)) return;

                // Somebody else has it. Their copy is what everyone will end up with, so nothing is sent —
                // and nothing is corrected here either: their next message says where it really is.
                if (Collab.HeldByOther(uid)) return;

                known.Object = CloneObjectValue(current);
                Queue(table + "=", uid, MapSerializer.ObjectJson(current));
                return;
            }

            Remember(uid, table, CloneObjectValue(current), null);
            Queue(table + "+", uid, MapSerializer.ObjectJson(current));
        }

        private void InspectMarker(Marker marker)
        {
            if (marker.Uid == 0) marker.Uid = Collab.NextUid();
            if (marker.Uid == 0) return;

            Seen(marker.Uid);

            if (_collabDragging.Contains(marker.Uid)) return;

            CollabKnown known;
            if (_collabKnown.TryGetValue(marker.Uid, out known) && known.Marker != null)
            {
                if (SameMarker(known.Marker, marker)) return;
                if (Collab.HeldByOther(marker.Uid)) return;

                known.Marker = CloneMarkerValue(marker);
                Queue("m=", marker.Uid, MapSerializer.MarkerJson(marker));
                return;
            }

            Remember(marker.Uid, "m", null, CloneMarkerValue(marker));
            Queue("m+", marker.Uid, MapSerializer.MarkerJson(marker));
        }

        /// <summary>
        /// Says goodbye to every id the pass just finished never came across. An id in the record and
        /// nowhere in the map is an object the player deleted — which is the only way one leaves.
        /// </summary>
        private void CollectRemovals()
        {
            List<int> gone = null;

            foreach (var pair in _collabKnown)
            {
                if (pair.Value.Generation == _collabGeneration) continue;

                // On its way in from somebody else's change: in the session, not yet in the world, and the
                // sweep has no way to tell that apart from deleted.
                if (_collabArriving.Contains(pair.Key)) continue;

                (gone ?? (gone = new List<int>())).Add(pair.Key);
            }

            if (gone == null) return;

            foreach (var uid in gone)
            {
                var table = _collabKnown[uid].Table;
                _collabKnown.Remove(uid);
                Queue(table + "-", uid, null);
            }
        }

        private void Seen(int uid)
        {
            CollabKnown known;
            if (_collabKnown.TryGetValue(uid, out known)) known.Generation = _collabGeneration;
        }

        private void Remember(int uid, string table, MapObject o, Marker marker)
        {
            _collabKnown[uid] = new CollabKnown
            {
                Object = o,
                Marker = marker,
                Table = table,
                Generation = _collabGeneration,
            };
        }

        private void Queue(string kind, int uid, Json body)
        {
            if (_collabOutbox == null) _collabOutbox = Json.Array();

            var op = Json.Object().Set("k", kind).Set("u", uid);
            if (body != null) op.Set("o", body);

            _collabOutbox.Add(op);
            _collabOutboxCount++;
        }

        private void FlushOutbox()
        {
            if (_collabOutboxCount == 0) return;
            if (_collabOutboxCount < CollabFlushCount && !Clock.Elapsed(_collabLastFlush, CollabFlushMs)) return;

            Collab.SendOps(_collabOutbox);
            _collabOutbox = null;
            _collabOutboxCount = 0;
            _collabLastFlush = Clock.Milliseconds;
        }

        /// <summary>
        /// Sends where the things in the player's hands are at this instant. Not a change to the document:
        /// it is what the others need to see the crate travelling rather than teleporting when it lands.
        /// </summary>
        private void SendDragFrame()
        {
            if (_collabDragging.Count == 0) return;
            if (!Clock.Elapsed(_collabLastDrag, CollabDragMs)) return;
            _collabLastDrag = Clock.Milliseconds;

            var items = Json.Array();

            foreach (var uid in _collabDragging)
            {
                var handle = PropStreamer.HandleOf(uid);
                if (handle != 0)
                {
                    var entity = Compat.Ent(handle);
                    if (entity == null) continue;

                    items.Add(Json.Object()
                        .Set("u", uid)
                        .Set("p", VectorJson(entity.Position))
                        .Set("r", VectorJson(entity.Rotation)));
                    continue;
                }

                if (_snappedMarker != null && _snappedMarker.Uid == uid)
                    items.Add(Json.Object()
                        .Set("u", uid)
                        .Set("p", VectorJson(_snappedMarker.Position))
                        .Set("r", VectorJson(_snappedMarker.Rotation)));
            }

            if (items.Count == 0) return;

            Collab.SendDrag(Json.Object().Set("i", items));
        }

        private static Json VectorJson(Vector3 v)
        {
            return Json.Array().Add(Json.Of(v.X)).Add(Json.Of(v.Y)).Add(Json.Of(v.Z));
        }

        private static Vector3 VectorFrom(Json json)
        {
            if (json.Kind != JsonKind.Array || json.Count < 3) return Vector3.Zero;
            return new Vector3(json[0].AsFloat(0f), json[1].AsFloat(0f), json[2].AsFloat(0f));
        }

        // --- Incoming: what everyone else changed ----------------------------------------------------

        /// <summary>
        /// Puts somebody else's drag on the screen. Applied straight to the entity and not to the record of
        /// what the session holds: a drag is not a change to the document, and the object's real position
        /// arrives as one when it is put down.
        /// </summary>
        private void ApplyIncomingDrags()
        {
            if (Collab.Drags.Count == 0) return;

            foreach (var pair in Collab.Drags)
            {
                foreach (var item in pair.Value["i"].Items)
                {
                    var uid = item["u"].AsInt(0);
                    if (uid == 0) continue;

                    var position = VectorFrom(item["p"]);
                    var rotation = VectorFrom(item["r"]);

                    var marker = FindMarker(uid);
                    if (marker != null)
                    {
                        marker.Position = position;
                        marker.Rotation = rotation;
                        continue;
                    }

                    var handle = PropStreamer.HandleOf(uid);
                    if (handle == 0) continue;

                    var entity = Compat.Ent(handle);
                    if (entity == null) continue;

                    entity.Position = position;
                    entity.Rotation = rotation;
                }
            }

            // Each one is where something is now; a message that has been read is not worth keeping, and
            // one that has been overtaken was never applied at all.
            Collab.Drags.Clear();
        }

        /// <summary>
        /// Takes one batch of changes off the queue and puts it into the world.
        ///
        /// One batch per frame at most, and it is not awaited: spawning waits for models, so a batch that
        /// arrives with two hundred objects in it lands over the following few seconds while the editor
        /// carries on. <see cref="_collabApplying"/> is what keeps the next batch behind it, so they are
        /// applied in the order they were sent — which is the order they were made in.
        /// </summary>
        private void ApplyIncoming()
        {
            if (_collabApplying || Collab.Inbox.Count == 0) return;

            var batch = Collab.Inbox.Dequeue();
            _collabApplying = true;

            Log.Guard("Apply session changes", async () =>
            {
                try
                {
                    await ApplyBatch(batch);
                }
                finally
                {
                    _collabApplying = false;
                }
            });
        }

        private async Task ApplyBatch(Json batch)
        {
            var slot = batch["s"].AsInt(-1);
            int added = 0, changed = 0, gone = 0;

            foreach (var op in batch["ops"].Items)
            {
                var kind = op["k"].AsString("");

                if (kind == "meta")
                {
                    var meta = MapSerializer.MetadataFrom(op["o"]);
                    if (meta != null) PropStreamer.CurrentMapMetadata = meta;
                    continue;
                }

                var uid = op["u"].AsInt(0);
                if (uid == 0) continue;

                switch (kind)
                {
                    case "+":
                    case "=":
                    {
                        var o = MapSerializer.ObjectFrom(op["o"]);
                        if (o == null) continue;
                        o.Uid = uid;

                        if (await ApplyObject(uid, o)) added++;
                        else changed++;
                        break;
                    }
                    case "-":
                        if (ApplyRemoval(uid)) gone++;
                        break;

                    case "m+":
                    case "m=":
                    {
                        var marker = MapSerializer.MarkerFrom(op["o"]);
                        if (marker == null) continue;
                        marker.Uid = uid;

                        if (ApplyMarker(uid, marker)) added++;
                        else changed++;
                        break;
                    }
                    case "m-":
                        if (ApplyRemoval(uid)) gone++;
                        break;

                    case "w+":
                    case "w=":
                    {
                        var o = MapSerializer.ObjectFrom(op["o"]);
                        if (o == null) continue;
                        o.Uid = uid;

                        if (ApplyWorldRemoval(uid, o)) added++;
                        else changed++;
                        break;
                    }
                    case "w-":
                        if (ApplyRemoval(uid)) gone++;
                        break;
                }
            }

            // A correction is the server putting back something this client was not allowed to change. It
            // is applied like anything else and reported like nothing at all: the player has already been
            // told whose object it was, and "you moved 1" would be a line about a move that did not happen.
            if (!batch["c"].AsBool(false)) ReportActivity(slot, added, changed, gone);
        }

        /// <summary>
        /// Puts one object of somebody else's into the world, or moves the one that is already there.
        /// Returns true when it was new here.
        ///
        /// Moving is the fast path and it has to be: a drag ends in one of these, and a hundred props
        /// respawning because somebody nudged them would be a hundred models fetched from the streamer for
        /// a change of two centimetres. So an object that differs only in where it is gets moved, and
        /// anything else — a different model, a different colour, a ped in different clothes — is built
        /// again from the description, which is the only way to be sure the result is what the description
        /// says.
        /// </summary>
        private async Task<bool> ApplyObject(int uid, MapObject o)
        {
            CollabKnown known;
            var isKnown = _collabKnown.TryGetValue(uid, out known) && known.Object != null;

            if (isKnown && SameObjectExceptPlacement(known.Object, o))
            {
                known.Object = CloneObjectValue(o);
                known.Generation = _collabGeneration;

                var streamedRecord = FindStreamedOut(uid);
                if (streamedRecord != null)
                {
                    // Not in the world here at all. The description it will be spawned from is what moves.
                    streamedRecord.Object.Position = o.Position;
                    streamedRecord.Object.Rotation = o.Rotation;
                    streamedRecord.Object.Quaternion = o.Quaternion;
                    return false;
                }

                var handle = PropStreamer.HandleOf(uid);
                var entity = handle == 0 ? null : Compat.Ent(handle);
                if (entity != null)
                {
                    MoveEntityTo(entity, o);
                    return false;
                }
            }

            var wasHere = RemoveByUid(uid);

            Remember(uid, "", CloneObjectValue(o), null);
            _collabArriving.Add(uid);

            try
            {
                await PlaceSessionObject(o);
            }
            finally
            {
                _collabArriving.Remove(uid);
            }

            return !wasHere;
        }

        /// <summary>
        /// Moves an entity to where the description says, without rebuilding it. Props are placed by their
        /// model origin rather than their centre, exactly as everywhere else in the editor.
        /// </summary>
        private static void MoveEntityTo(Entity entity, MapObject o)
        {
            if (IsProp(entity)) entity.PositionNoOffset = o.Position;
            else if (o.Type == ObjectTypes.Ped) entity.Position = o.Position - new Vector3(0f, 0f, 1f);
            else entity.Position = o.Position;

            if (o.Quaternion != null && !IsUnsetQuaternion(o.Quaternion) && o.Type != ObjectTypes.Ped)
                Quaternion.SetEntityQuaternion(entity, o.Quaternion);
            else
                entity.Rotation = o.Rotation;

            if (o.Type == ObjectTypes.Ped) entity.Heading = o.Rotation.Z;

            PropStreamer.Anchor(entity, o.Position);
            PropStreamer.HoldStill(entity);
        }

        private bool ApplyMarker(int uid, Marker marker)
        {
            var existing = FindMarker(uid);

            if (existing != null)
            {
                // Written into the marker that is already there rather than swapped for the new one: the
                // editor holds markers by reference — the snapped one, the selected one, the row in the
                // entity menu — and replacing the instance would leave every one of those pointing at a
                // marker that is no longer in the map.
                existing.Type = marker.Type;
                existing.Position = marker.Position;
                existing.Rotation = marker.Rotation;
                existing.Scale = marker.Scale;
                existing.TeleportTarget = marker.TeleportTarget;
                existing.Red = marker.Red;
                existing.Green = marker.Green;
                existing.Blue = marker.Blue;
                existing.Alpha = marker.Alpha;
                existing.BobUpAndDown = marker.BobUpAndDown;
                existing.RotateToCamera = marker.RotateToCamera;
                existing.OnlyVisibleInEditor = marker.OnlyVisibleInEditor;

                Remember(uid, "m", null, CloneMarkerValue(existing));
                return false;
            }

            _markerCounter++;
            marker.Id = _markerCounter;

            PropStreamer.Markers.Add(marker);
            AddItemToEntityMenu(marker);
            Remember(uid, "m", null, CloneMarkerValue(marker));
            return true;
        }

        private bool ApplyWorldRemoval(int uid, MapObject o)
        {
            var existing = FindWorldRemoval(uid);
            if (existing != null)
            {
                // The description of a hidden object cannot really change — it is a model and a spot — so
                // this only ever arrives as the object being hidden somewhere else. Put back, hidden anew.
                PropStreamer.RestoreWorldObject(existing);
                RemoveItemFromEntityMenu(existing.Id);
            }

            o.Id = _mapObjCounter.ToString(CultureInfo.InvariantCulture);
            _mapObjCounter++;

            PropStreamer.RemoveWorldObject(o);
            AddItemToEntityMenu(o);
            Remember(uid, "w", CloneObjectValue(o), null);
            return existing == null;
        }

        private bool ApplyRemoval(int uid)
        {
            _collabKnown.Remove(uid);
            return RemoveByUid(uid);
        }

        /// <summary>
        /// Takes whatever carries this id out of the map, wherever it is: standing in the world, waiting out
        /// of range, a marker, or one of the game's objects the map was hiding.
        /// </summary>
        private bool RemoveByUid(int uid)
        {
            var handle = PropStreamer.HandleOf(uid);
            if (handle != 0)
            {
                var entity = Compat.Ent(handle);
                if (entity != null) LetGoOf(new List<int> { uid });
                DeleteEditorEntityByHandle(handle);
                return true;
            }

            var record = FindStreamedOut(uid);
            if (record != null)
            {
                RemoveFromEntityMenu(tag => tag.Kind == EntityMenuKind.Streamed && tag.Id == record.Uid);
                PropStreamer.StreamedOut.Remove(record);
                return true;
            }

            var marker = FindMarker(uid);
            if (marker != null)
            {
                if (_snappedMarker == marker) _snappedMarker = null;
                if (_selectedMarker == marker)
                {
                    _selectedMarker = null;
                    SetMenuVisible(_objectInfoMenu, false);
                }

                PropStreamer.Markers.Remove(marker);
                RemoveMarkerFromEntityMenu(marker.Id);
                return true;
            }

            var world = FindWorldRemoval(uid);
            if (world != null)
            {
                PropStreamer.RestoreWorldObject(world);
                RemoveItemFromEntityMenu(world.Id);
                return true;
            }

            return false;
        }

        /// <summary>
        /// <see cref="DeleteEditorEntity"/> by handle, for the paths that have an id rather than an entity.
        /// </summary>
        private void DeleteEditorEntityByHandle(int handle)
        {
            var entity = Compat.Ent(handle);
            if (entity != null)
            {
                DeleteEditorEntity(entity);
                return;
            }

            // The entity is already gone but the bookkeeping under its handle is not.
            RemoveFromEntityMenu(tag => tag.Kind == EntityMenuKind.Entity && tag.Id == handle);
            PropStreamer.RemoveEntity(handle);
        }

        private static Marker FindMarker(int uid)
        {
            if (uid == 0) return null;

            foreach (var marker in PropStreamer.Markers)
            {
                if (marker.Uid == uid) return marker;
            }
            return null;
        }

        private static MapObject FindWorldRemoval(int uid)
        {
            if (uid == 0) return null;

            foreach (var o in PropStreamer.RemovedObjects)
            {
                if (o.Uid == uid) return o;
            }
            return null;
        }

        private static PropStreamer.StreamedObject FindStreamedOut(int uid)
        {
            if (uid == 0) return null;

            foreach (var record in PropStreamer.StreamedOut)
            {
                if (record.Object != null && record.Object.Uid == uid) return record;
            }
            return null;
        }

        /// <summary>Mints an id for a map object that has none, and writes it back where it will be found again.</summary>
        private static int EnsureObjectUid(MapObject o)
        {
            if (o.Uid != 0) return o.Uid;

            o.Uid = Collab.NextUid();
            return o.Uid;
        }

        // --- Comparing ------------------------------------------------------------------------------

        /// <summary>
        /// Whether two descriptions of the same object say the same thing, allowing for the millimetre the
        /// game moves things by on the way in. See <see cref="CollabPositionEpsilon"/> for why that
        /// allowance is the difference between a working session and a crate oscillating forever.
        /// </summary>
        private static bool SameObject(MapObject a, MapObject b)
        {
            return SameObjectExceptPlacement(a, b) && SamePlacement(a, b);
        }

        private static bool SamePlacement(MapObject a, MapObject b)
        {
            return NearlyEqual(a.Position, b.Position, CollabPositionEpsilon) &&
                   NearlyEqual(a.Rotation, b.Rotation, CollabAngleEpsilon) &&
                   SameQuaternion(a.Quaternion, b.Quaternion);
        }

        /// <summary>
        /// Everything about an object except where it is standing. What decides whether a change can be
        /// applied by moving the entity or has to rebuild it.
        /// </summary>
        private static bool SameObjectExceptPlacement(MapObject a, MapObject b)
        {
            if (a.Type != b.Type || a.Hash != b.Hash) return false;
            if (a.Dynamic != b.Dynamic || a.Door != b.Door) return false;
            if (a.Id != b.Id) return false;
            if (a.Shared != b.Shared) return false;

            if (a.Type == ObjectTypes.Vehicle)
            {
                if (a.SirensActive != b.SirensActive) return false;
                if (a.PrimaryColor != b.PrimaryColor || a.SecondaryColor != b.SecondaryColor) return false;
                if (a.Livery != b.Livery) return false;
            }

            if (a.Type == ObjectTypes.Ped)
            {
                if (a.Action != b.Action || a.Relationship != b.Relationship) return false;
                if (a.Weapon != b.Weapon) return false;
                if (!SameInts(a.Drawables, b.Drawables) || !SameInts(a.Textures, b.Textures)) return false;
            }

            return true;
        }

        private static bool SameMarker(Marker a, Marker b)
        {
            return a.Type == b.Type &&
                   NearlyEqual(a.Position, b.Position, CollabPositionEpsilon) &&
                   NearlyEqual(a.Rotation, b.Rotation, CollabAngleEpsilon) &&
                   NearlyEqual(a.Scale, b.Scale, CollabPositionEpsilon) &&
                   a.Red == b.Red && a.Green == b.Green && a.Blue == b.Blue && a.Alpha == b.Alpha &&
                   a.BobUpAndDown == b.BobUpAndDown && a.RotateToCamera == b.RotateToCamera &&
                   a.OnlyVisibleInEditor == b.OnlyVisibleInEditor &&
                   a.TeleportTarget.HasValue == b.TeleportTarget.HasValue &&
                   (!a.TeleportTarget.HasValue ||
                    NearlyEqual(a.TeleportTarget.Value, b.TeleportTarget.Value, CollabPositionEpsilon));
        }

        private static bool SameQuaternion(Quaternion a, Quaternion b)
        {
            if (a == null || b == null) return a == null && b == null;

            return Math.Abs(a.X - b.X) <= CollabPositionEpsilon &&
                   Math.Abs(a.Y - b.Y) <= CollabPositionEpsilon &&
                   Math.Abs(a.Z - b.Z) <= CollabPositionEpsilon &&
                   Math.Abs(a.W - b.W) <= CollabPositionEpsilon;
        }

        private static bool NearlyEqual(Vector3 a, Vector3 b, float epsilon)
        {
            return Math.Abs(a.X - b.X) <= epsilon && Math.Abs(a.Y - b.Y) <= epsilon &&
                   Math.Abs(a.Z - b.Z) <= epsilon;
        }

        private static bool SameInts(int[] a, int[] b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a.Length != b.Length) return false;

            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// A copy of what the session has been told, kept apart from the object the editor is holding.
        /// Without the copy the record would be the same instance the map is, and every comparison would
        /// find them identical no matter what the player had done.
        /// </summary>
        private static MapObject CloneObjectValue(MapObject o)
        {
            return new MapObject
            {
                Type = o.Type,
                Position = o.Position,
                Rotation = o.Rotation,
                Hash = o.Hash,
                Dynamic = o.Dynamic,
                Quaternion = o.Quaternion == null
                    ? null
                    : new Quaternion { X = o.Quaternion.X, Y = o.Quaternion.Y, Z = o.Quaternion.Z, W = o.Quaternion.W },
                Door = o.Door,
                Action = o.Action,
                Relationship = o.Relationship,
                Weapon = o.Weapon,
                Drawables = o.Drawables == null ? null : (int[])o.Drawables.Clone(),
                Textures = o.Textures == null ? null : (int[])o.Textures.Clone(),
                SirensActive = o.SirensActive,
                PrimaryColor = o.PrimaryColor,
                SecondaryColor = o.SecondaryColor,
                Livery = o.Livery,
                Amount = o.Amount,
                RespawnTimer = o.RespawnTimer,
                Flag = o.Flag,
                Id = o.Id,
                Shared = o.Shared,
                Uid = o.Uid,
            };
        }

        private static Marker CloneMarkerValue(Marker m)
        {
            return new Marker
            {
                Type = m.Type,
                Position = m.Position,
                Rotation = m.Rotation,
                Scale = m.Scale,
                TeleportTarget = m.TeleportTarget,
                Red = m.Red,
                Green = m.Green,
                Blue = m.Blue,
                Alpha = m.Alpha,
                BobUpAndDown = m.BobUpAndDown,
                RotateToCamera = m.RotateToCamera,
                OnlyVisibleInEditor = m.OnlyVisibleInEditor,
                Id = m.Id,
                Uid = m.Uid,
            };
        }

        // --- Saying things --------------------------------------------------------------------------

        private static void CollabNotify(string message, bool bad = false)
        {
            Compat.Notify((bad ? "~r~~h~Map Editor~h~~w~~n~" : "~b~~h~Map Editor~h~~w~~n~") + message);
        }

        /// <summary>
        /// Turns one applied batch into one line of the activity list. Counted rather than listed: a
        /// stacking run is two hundred changes, and two hundred lines would be a wall rather than news.
        /// </summary>
        private void ReportActivity(int slot, int added, int changed, int gone)
        {
            if (added == 0 && changed == 0 && gone == 0) return;

            var parts = new List<string>();
            if (added > 0) parts.Add(Translation.Translate("placed") + " " + added);
            if (changed > 0) parts.Add(Translation.Translate("moved") + " " + changed);
            if (gone > 0) parts.Add(Translation.Translate("removed") + " " + gone);

            CollabSay(slot, string.Join(", ", parts.ToArray()));
        }
    }
}
