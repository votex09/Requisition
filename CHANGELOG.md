# Requisition Changelog
## [0.5.2]

### Added
- **Lock Recipe toggle** — checkbox in the crafting panel pins crafting to the exact selected recipe variant instead of auto-picking one, so it won't switch recipes mid mass-craft
- **androLib (Vacuum Bags) support** — optional "Deposit to Requisition" button on vacuum bag UIs that empties the bag into a Terminal's network in range (only appears when androLib is installed)
- **`/tsdump` command** — diagnostic chat command that dumps the recipe graph and current storage to a file for performance analysis
- **Grid Scroll Rows setting** — client config option (1-255, default 3) for how many rows one mouse wheel notch scrolls in the item, recipe, NPC and disk panes; the default is tuned to match Terraria's own scroll speed

### Changed
- Held items can now be dropped anywhere in the Storage tab, including empty grid slots
- Ingredients you lack but can sub-craft now show in **orange** as `12/12` instead of a misleading red `0/12`

### Fixed
- **Withdrawing a stack no longer strips enchantments and modifiers off it.** When a cell held the same item under two different per-instance states — an enchanted copy and a plain one, or a reforged and an unreforged one — taking the lot handed back items carrying neither, so every enchantment and modifier in that cell was gone. A withdrawal now stops where the state changes and hands over that run intact. **The trade-off is clicks:** such a cell now comes out a run at a time, so emptying it can take several clicks instead of one, and how much the first click gives you depends on how the cell happens to be laid out. Crafting is unaffected — a recipe still draws everything it needs in one go
- **Multiplayer: another player can no longer empty your storage from across the world.** Every storage action used to tell the server which disks to take from, and the server took its word for it — disk identifiers are sent to every client, so a modified client could name any disk anywhere and drain it without ever leaving its base. Withdrawing, depositing, crafting, defragmenting and disk upgrades now name the Terminal you are standing at (or holding a Remote Terminal for), and the server works out the disks itself
- **Multiplayer: Defragment now works through a Remote Terminal.** It was the one action already restricted by distance, so pressing Defragment from a Remote Terminal did nothing at all, without saying so
- **Multiplayer: a modified client can no longer craft with stations it does not have.** Crafting and disk upgrades used to send the list of available crafting stations along with the request; the server now reads them from the Crafting Cores around the Terminal
- **Multiplayer: Drive Bay slots can no longer be emptied or filled from a distance.** A forged packet could clear any Drive Bay slot in the world, destroying the disk in it. Taking a disk out now waits for the server to hand it over, so a refusal can never leave you holding a copy of a disk that is also still in the bay
- **Multiplayer: disk management now tells you why it refused.** Disk upgrades, disk recovery, defragment, archiving, and inserting or removing a disk in a Drive Bay all used to fail in complete silence — twenty separate cases. Each now names its reason in chat
- **A deposit that the server refuses now hands the item back** instead of leaving it nowhere
- Being just inside the range the Terminal and Drive Bay panels allow no longer means the server refuses everything you do. The panel measured from the block's corner and the server from its centre, so on two sides there was a band where the window stayed open and every action silently failed
- Empty disk entries no longer accumulate in the world file each time a disk is taken out of a Drive Bay and put back — in singleplayer as well as on a server
- Every deposit, withdrawal, craft and quick-stack used to copy the contents of **every** disk in the world before running, so servers got slower as more disks existed anywhere on the map. Each operation now copies only the disks of the Terminal it came from
- **An aborted craft no longer destroys an item you owned.** When a multi-step craft could not be paid for and rolled back, it worked out which units it had created by their position in the ledger — so if the item it made landed on an earlier disk than a copy you already owned, the rollback kept its own copy and destroyed yours. Affected items carrying per-instance state (enchantments, mod data); the count always balanced, which is why it went unnoticed
- Clicking the selected recipe a second time and then CRAFT no longer throws (single player) or does nothing (multiplayer) — deselecting now clears the plan the craft button reads
- **Multiplayer: a refused storage action now tells you why.** Crafting, withdrawing, depositing or quick-stacking on a server used to fail in total silence — the server sent a yes/no the client threw away. Every refusal now names its reason in chat, in the same words singleplayer already used, and a bulk action that is refused forty times says so once instead of forty times
- Quick-stacking into a full storage network no longer reports success and does nothing
- Depositing with no Terminal in range, or with no disks connected, now says so instead of silently returning the item
- Recipes no longer show as craftable when shared materials were double-counted across ingredients
- Loop recipes (e.g. blue mushrooms) now show a red **"Nothing to Craft"** button instead of a craft button that did nothing
- Recipe list now refreshes correctly when storage changes — no more stale or disappearing recipes after crafting or moving items
- Fixed false "missing station" errors on recipes that were actually craftable (e.g. demonite → crimtane) — a craftable variant is now preferred over a station-missing one
- Ingredient counts are no longer over-counted after a partial craft
- Eliminated Terminal hitching every couple seconds while open, and heavy lag on each craft in large modpacks (allocation-free feasibility, worklist-based reachability, targeted post-craft revalidation, and removal of a spurious storage-version bump that also starved disk backups)
- The click that opens the Terminal no longer bleeds into the item grid as a grab (left and right click)
- Drive Bay: a max-tier disk's contents no longer scroll past the last row into blank space, and the last disk in the bay is always reachable in the list
- Storage, crafting and Drive Bay scroll positions now stay correct after resizing the Terminal window instead of stranding the view in blank space, and the scrollbar thumb resizes with the window
- Crafting: the recipe scrollbar thumb no longer pins early or dead-zones at the bottom while the grid scrolls one row further
- Defragment no longer freezes the game on a large drive bay — a full 40-disk bay went from about a third of a second to roughly one frame, and a bay packed with bulk materials from about seven tenths of a second to the same
- Defragment: a malformed multiplayer defrag request can no longer make a disk donate to itself, exhaust server memory, or stall the server on a repeated disk

## [0.5.1]

### Added
- **Craft to Inventory toggle** — checkbox next to Clean Craft lets you send crafted items directly to your inventory instead of storage
- **Remote Terminal** — can now be opened with left-click use (hold in hotbar and use like a normal item)

### Fixed
- Thorium Radiant/Symphonic weapons no longer incorrectly appear under "Other Weapons" filter

## [0.5.0]

### Added
- **Quick Stack** — quick stack now works with the storage network
- **Thorium mod filter support** — damage class filters now catch Thorium's custom classes (Bard, Healer, etc.)

### Changed
- Rebranded mod display name to **Requisition**
- Source code now available on [GitHub](https://github.com/votex09/Requisition)

### Fixed
- Vanilla crafting UI no longer reappears when storage panels are open
- Packet handling for oversized messages is now more robust
- Shift+click behavior in the Terminal no longer causes unexpected results
- UI appearance fixes for various buttons and padding
- Station tooltips and disk visual state fixes
- MP Drive Bay lights now update correctly
- Prefixed item withdrawal now works as expected
- You can now deselect recipes in the Crafting Tab to display available crafting stations

## [0.2.10]

### Fixed
- Fixed a bug where the leftovers of a stack could vanish when inserting items that went over capacity.
- In SP, sometimes items dont merge due to some old branch of code left behind from the original fullsync method.  Modernized using new method
- Fixed a dupe with items that have a use time

## [0.2.9]
Just a version bump for imaging stuff on workshop.

## [0.2.8] - Released

### Added
- **Sprite update**
    - All sprites have gotten a facelift. The CraftingCore and DriveBay are now different sizes, please break and replace your tiles to get the new form factor.
- **Drive Bay visual overlays** — small status lights on each disk slot show fill state (offline, online, 80% full, 100% full). Bay-level status light shows overall capacity across all disks at a glance.
- Vanilla hold-rightclick behavior in the storage terminal works as expected now
- Remote Terminal keybind.  You still have to have the item bound the normal way however

### Changed
- Smaller craft button to help prevent misclicks

### Removed
- **Predictive Mode Toggle**
    - In MP, the server will always use the new Predictive networking method.  The old way has been completely depreciated as it has too many issues to continue maintaining, and has been removed entirely.  SP Remains unaffected and will continue using Fullsync since packet size is a non-issue.

### Fixed
- Removed some unused code.
- Fixed search bars stealing text input permanently until the game was restarted

## [0.2.7] - Released

### Added
- Cyrillic language support
  - NOTE: I've verified that you can type cyrillic characters in the search bar. I've added Russian language support though machine translation, but it's likely terrible.  Not all of the UX elements are translated or have Localization entries however, but this is a good start.

### Fixed
- Protect the floor/platform where a Crafting Core/Drive Bay contains items.
- Added simulated vanilla crafting mode (experimental, there will be bugs at the moment)
  - Brings support for mods that add data to items on craft
- Made the output slot size in the crafting tab larger
- Recipes that allow groups of items now display that ingredient as a group
- ItemGrid in crafting tab no longer jumps around when crafting

## [0.2.6] - Released

### Fixed
- Texture smearing on tiles (Terminal, CraftingCore, DriveBay)
- Fix stack merging issues caused by the previous patch.
- The collapsed browse pane in Encyclopedia no longer allows queries on an invisible item grid.

## [0.2.5] - Released

### Fixed
- Items with per-instance data from other mods (e.g. enchantments from Entropy) are no longer stripped on deposit into storage.
- Disk tier upgrades now preserve per-instance data (enchantments, GlobalItem state).

### Changed
- Network packets for unique items are now more compact — eliminated redundant ModData when FullItemTag is already present.

### Notes
- In MP, items with extra ItemTags do not load for the client unless the terminal is reopened/full sync occurs.  Small bug, it's only visual so impact is negligible.


## [0.2.4] - Released
**Small Height fix for Terminal**

### Changed
- The terminal max height is now able to be expanded to 80% of the screen height (Up from 60%)

## [0.2.3] - Released
**Massive UI Update**

### Added
- **Favorites toggle button** — a ★ button in the player inventory UI to open/close the Favorites panel from anywhere. Middle-click and drag to reposition it.
- **Defragment tooltip** — hovering the Defragment button in the Disks tab now shows an explanation of what it does.
- **Encyclopedia browse pane** — a collapsible item browser that slides in from the left edge of the Encyclopedia window, covering the detail panel. Contains the filter bar, sort bar, and item grid. Toggled by a permanent strip button on the left edge, or automatically when the search bar is focused. Clicking an item collapses it and shows the detail view.
- **Tooltip bleed prevention** — open windows (Encyclopedia, Terminal, Drive Bay, Crafting Core, Crafting Tree, Favorites) now block item tooltips from showing through them.

### Changed
- **Unified UI style** — all close buttons, tabs, and action buttons (Deposit All, Upgrade, Defragment) now share a consistent visual style.
- **Resize handle** — the corner resize handle on all resizable windows is now a diagonal-striped square instead of a solid block.
- **Deposit All** button moved above the item grid, aligned with the sort bar, to reduce wasted footer space.
- **Encyclopedia minimum size** reduced to allow much smaller window sizes.
- Reduced unused gap between the item grid and scrollbar in the Storage tab.
- Reduced excess right-side padding in the Crafting tab.

### Fixed
- **Smooth scrolling** — all scrollable lists and panels (Storage, Crafting, Encyclopedia, Disks) now scroll smoothly.
- **Disk tab FPS drop** — Improved rendering cost of viewing the Disks Tab.
- UI window positions and sizes (Terminal, Encyclopedia, Crafting Tree, Favorites panel) now correctly persist across game sessions.
- Drive Bay and Crafting Core windows now always open centered rather than restoring an off-screen saved position.

## [0.2.2] - Released

### Added
- Alt+click any item in the Encyclopedia or Crafting Tree to add/remove its recipe from Favorites.
- Alt+click a recipe in the Favorites panel to remove it.

### Fixed
- Crafting with a full storage network no longer silently destroys the crafted item — it goes to the player's inventory instead. If both storage and inventory are full, crafting is blocked.

## [0.2.1] - Released

### Added
- **Item Encyclopedia** (rebindable keybind)
  - Browse all items. Type `!` to switch to NPC browsing.
  - Detail panel shows crafting recipes, drop sources, shop sources, shimmer, and used-in recipes.
  - Recipes cycle with `<` / `>`. All icons are interactive: left-click to navigate, right-click for Crafting Tree, middle-click to send to Terminal.
  - Click an NPC to view their drops and shop inventory.

### Fixed
- Crafting Tree: selecting certain nodes (e.g. Lens) caused the mouse cursor to vanish and the Info Panel to not display.

## [0.2.0] - Released

### Added
- **Crafting Tree** — visual, pannable, zoomable graph explorer for item relationships. Hover any inventory item and press a configurable hotkey to open.
  - **Bidirectional exploration**: right side shows what an item crafts INTO, left side shows ingredients needed to CREATE it. Right-click nodes to expand/collapse.
  - **Info Panel**: left-click a node to select it and reveal a sidebar showing all non-crafting sources — NPC drops (with percentage and stack range), NPC shop availability, and Shimmer transmutations. Each entry has an icon slot with vanilla hover tooltips.
  - **Animated transitions**: nodes slide in/out with lerp animations on expand/collapse. The info panel slides in from the left edge.
  - **Minimap**: corner overview with bracket-style connection lines matching the main view. Click and drag the minimap to navigate.
  - **Middle-click integration**: middle-click any node while a Terminal is open to jump to that recipe in the crafting tab. Optional auto-minimize toggle (per character).
  - Nodes are color-coded by item category. Cycle detection prevents infinite loops. Draggable, resizable, minimizable window with saved position.
- **In Storage count** — item tooltips now show "In Storage: X" based on the last opened Terminal's network.
- **Debug Tooltips** — client config option. Hold Alt while hovering any item to see its classification, damage type, and internal properties.

### Fixed
- `#` tooltip search now includes dynamic item properties (bait, damage, defense, pickaxe, axe, hammer, accessory, vanity, material, potion, ammo) — e.g. `#bait` now finds fishing bait.
- Bait items now appear under the Ammo filter instead of Consumables.
- Modded boss summoners have an increased likelyhood of being correctly classified as Boss Summoners.
- Modded weapons should now be filtered correctly depending on how they were implemented.

## [0.1.12] - Released

### Added
- **Delta Sync** — server config toggle (`Predictive Sync Mode`). Replaces full disk broadcasts with small item-level change requests. Per-disk sequence numbers with automatic full resync on gap detection. Classic full-sync mode remains as fallback.
- **Recursive Crafting toggle** — new checkbox in the Crafting Tab header. Shows recipes whose ingredients can be crafted from other recipes in storage. Right-click drag to set recursion depth.
- Tooltips on Show Uncraftable and Recursive checkboxes.

### Changed
- Terminal crafting no longer considers player inventory — only storage contents are used for crafting resolution and material consumption.

### Fixed
- **Crafting Tab hitching** — eliminated multiple sources of frame drops in both networking methods
  - Ingredient changes only re-check affected recipes.
  - Filter/sort/search no longer re-scan all disks — uses cached item counts.
  - Station tile hover no longer hitches on first use

## [0.1.11] - Released

### Added
- **Defragment Disks** — new button in the Terminal Disks Tab. Consolidates partially-filled disks by moving items from later disks into earlier ones in Drive Bay order. Fully MP-compatible (server-authoritative).

### Fixed
- Disk panel item grid now renders animated items correctly and shows tooltips on hover.
- Disk Recovery panel now refreshes in real-time when world storage changes — no longer requires closing and reopening the Drive Bay.
- Disk placed in the Disk Recovery slot is now returned if the Recovery window is closed before restoring.

## [0.1.10] - Released

### Added
- **Disk Backups** — storage is automatically backed up as you play. Up to 3 rolling backups are kept per world (current session, previous session, oldest). Backups are written lazily ~10 seconds after a change and flushed on world exit.
- **Backup & Restore UI** — accessible from the client config page. Shows a world dropdown and per-slot timestamps; click Restore to queue a wholesale restore that takes effect on next world load.
- **`tsrestore` server command** — for dedicated server admins. `tsrestore list` shows available backups; `tsrestore <0|1|2>` applies a restore immediately without a world reload and pushes updated state to all connected clients.
- Version number now displayed in both config pages (Server and Client).

### Fixed
- In MP, inserting an unarchived disk into a Drive Bay now correctly restores its items.
- In MP, archiving a disk now broadcasts the GUID removal to all clients — the old GUID no longer lingers in Disk Recovery for other players.
- Disk Recovery (Remap) and Terminal disk upgrade are now server-authoritative in MP; previously both operations ran client-side only and had no effect on the actual world storage.
- Disk Recovery no longer allows duplicating items — recovering a disk now invalidates the original GUID so any surviving copy of the original disk becomes empty.

## [0.1.9] - Released
### Fixed
- Inserting an archived disk into a Drive Bay no longer deletes it — the disk is rejected and stays on the cursor/inventory instead.

## [0.1.8] - Released

### Added
- Placeable Demon Altar and Crimson Altar for Crafting Core use
- Remote Terminal — bind to a Crafting Terminal, open it from anywhere

### Fixed
- Packets sent in MP now use a compact binary format and are sent one disk at a time, staying within Terraria's packet size limit.
- Modded crafting stations appearing as blank slots in the Terminal
- Crafting tab FPS spikes
- Favoriting a recipe no longer affects all variants of that item

### Changed
- Crafting amount field: left-click to type, right-drag to adjust, middle-click to reset
- Crafting conditions are now vanilla items placed in the Crafting Core (Bottomless water/lava/honey buckets, Ice Machine, tombstones), Added crafting recipes for them
- Condition icons shown in crafting panel instead of text tags

### Removed
- Custom source items and tiles (Water Source, Lava Source, Honey Source, Snow Globe, Ectomist Emitter, Shimmer Source)

### TODO
- Terminal does not refresh its connected disk list when another player inserts or removes a disk while it is open. Other players' terminals go stale until reopened. Fix: broadcast an updated disk list to all open terminals on insert/remove, and refresh `_connectedDiskIds` in `TerminalUIState` on receipt.

## [0.1.7] - Prior release

*(No detailed log — see Steam Workshop changelog)*
