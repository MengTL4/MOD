Terminus: Zombie Survivors - AI Takeover for your main character (unofficial)
==========================================================================

Let the game's own companion AI play your main character for a turn.

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
to <game>\ai_takeover and checks the game version first - on a mismatch it changes nothing.

USE
A robot button appears in the action panel (bottom right):
  Left-click / F6         AI plays this turn, then ends it
  Shift+F6                continuous takeover (every turn)
  click / F6 while on     stop
  Right-click             menu: this turn / continuous / AI behavior settings
The AI uses the companion logic: medicine, safe sleep, fighting zombies and hostile NPCs,
vehicle repair, debris, searching and looting, crafting/upgrading, reading, music, mending,
repairs, farming, eating and drinking. It never changes maps, drives or trades.
"AI behavior settings" opens the game's companion automation panel for your character
(the master switch at the top of that panel does nothing for the main character).
Continuous mode stops below 35% HP and pauses while a popup is open.

AFTER A MOD OR GAME UPDATE
Run install.bat again. If it reports a Python runtime mismatch, wait for a mod update.

UNINSTALL
Double-click uninstall.bat (removes <game>\ai_takeover and restores the game unless another
mod still uses the loader), then unsubscribe. Alternatively: Steam > Verify integrity of game files, then delete <game>\ai_takeover.

HOW IT WORKS
The installer changes one file: the Python stdlib module linecache.pyc inside
<game>\_internal\base_library.zip, replaced by a version that runs the original code and
then a small mod loader shared with other mods (e.g. Shared Party Inventory), so they can be
installed together. The package ships the original file to restore it on uninstall.
Single-player only; saves are unaffected. Errors go to <game>\ai_takeover\ai_takeover.log.
