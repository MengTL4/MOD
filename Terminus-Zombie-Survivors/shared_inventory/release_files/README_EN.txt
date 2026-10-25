Terminus: Zombie Survivors - Shared Party Inventory (unofficial)
================================================================

Your character's and all companions' inventories become one shared pool.

WHY A MANUAL INSTALL
Workshop mods for this game can only contain data (JSON/images); the game never runs
code from them. This mod needs its installer run once. The on/off switch in the in-game
mod manager has no effect on it - use install / uninstall below.

INSTALL (no Python needed)
- Workshop: subscribe, wait for the download, quit the game, then open
    <Steam library>\steamapps\workshop\content\1534980\__ITEM_ID__
  and double-click install.bat.
- Zip download: extract anywhere and double-click install.bat.
The installer finds the game automatically (or asks you to pick the folder), copies the mod
to <game>\shared_inventory and checks the game version first - on a mismatch it changes nothing.

WHAT IT DOES
- Every party member's inventory screen shows the whole party's items (own items first).
- Crafting, cooking, repairs, medicine, fire starting, reinforcing, quests and so on can
  use any party member's items.
- The companion AI (and the AI Takeover mod) eats, heals, crafts, reads and repairs with
  the shared pool too.
- Used, dropped or handed-over items are taken from whoever actually carries them.
  Nothing is ever duplicated.
- Items carried by a companion show the carrier's initial in the slot's top-left corner
  (one color per character; "+" for a stack split between several companions).
  Hovering shows "Carried by <name>", or "Carried by: you x2 - <name> x1" for mixed stacks.
- A "Showing: party / mine only" toggle below the inventory panel (hotkey V, remembered).
  "Mine only" lists just what you carry, matching your weight; crafting, cooking and the
  companion AI still use the whole party's items.
- When trading items with a companion (companion on the right side of the inventory),
  both sides show their own real inventory so you can rebalance weight.

NOT SHARED
- Clothes and bags a companion is wearing, the weapon in their hands, bedding in use.
  Putting on a companion's clothing moves it to you first.
- Passive "carried" effects: flashlight light, umbrella/rain gear, armed level.
  Using a companion's flashlight from your inventory moves it to you first.
- Weight: everyone carries their own; picked-up items go to whoever picks them up.

Note: "Drop all" drops everything currently listed - in party view that includes
companions' items, in "mine only" view just your own.

Turn-hang guard: if anything goes wrong while ending a turn, the game normally keeps spinning on
"other survivors' turn" forever. This mod logs the error and hands control back to you.

SETTINGS
Edit the top of <game>\shared_inventory\shared_inventory.py, then restart the game:
  SAME_LOCATION_ONLY = True   share only with companions on the same map (default False)
  OWNER_TOOLTIP = False       hide the "Carried by" tooltip line
  OWNER_BADGE = False         hide the carrier badge on item slots
  VIEW_HOTKEY = "v"           hotkey for the party / mine-only toggle

AFTER A MOD OR GAME UPDATE
Run install.bat again. If it reports a Python runtime mismatch, wait for a mod update.

UNINSTALL
Double-click uninstall.bat (removes <game>\shared_inventory and restores the game unless
another mod still uses the loader), then unsubscribe. Saves keep each character's own
inventory and load normally without the mod.

HOW IT WORKS
The installer changes one file: the Python stdlib module linecache.pyc inside
<game>\_internal\base_library.zip, replaced by a version that runs the original code and
then a small mod loader shared with other mods (e.g. AI Takeover), so they can be
installed together. The package ships the original file to restore it on uninstall.
Single-player only. Errors go to <game>\shared_inventory\shared_inventory.log.
