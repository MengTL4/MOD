Terminus: Zombie Survivors - Smarter Companion AI (unofficial)
==============================================================

Replaces a few decision points of the built-in companion AI and keeps everything else vanilla.
Respects the game's own companion automation settings (attack, use guns, use medical supplies,
pick up items, movement). Also applies to the player when the AI Takeover mod is in control.

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
to <game>\companion_ai and checks the game version first - on a mismatch it changes nothing.

WHAT IT DOES
Combat
- Retreat: below 40% HP companions stop meleeing and move where zombies can't reach next turn,
  close to you.
- Body part: picked by expected damage using the game's own accuracy/damage/crit formulas
  (vanilla always aims at the head). Crits only happen on the head (x5); a likely killing blow
  goes to the head; when retreating, legs are preferred to slow pursuers.
- Weapon: the one with the most expected damage this turn (accuracy, attack AP, walking first).
- Target: zombies threatening you or another companion first, one-hit kills first,
  then the weakest nearby one (focus fire), then distance.
- Quiet combat: a gunshot pulls up to 40 nearby zombies to the shooter and costs ammo.
  Melee or bows are used when they do at least half the damage.
- Melee approach: out of reach, they wait just outside the zombie's attack range (vanilla
  stands still when the path is long; this walks part of the way). They charge instead when the
  zombie threatens the party or one hit will probably kill it.
- Ranged: they pick a firing position the zombies can't reach.
- End of turn: if still inside a zombie's attack range with AP left, they step out.
Survival and teamwork
- Medicine below 50% HP, staying under the headache limit; above it only when critical,
  never reaching the fainting limit (vanilla waits until HP < 5 and can pop a faint warning).
- They bandage and treat you and each other; doctors use First Aid. Never walking into danger for it.
- Guard: when zombies come within 8 tiles of you, they drop looting, upgrading, clearing,
  repairs and gardening and come back to you.
- Pickup: vanilla only picks up "high" value items by default, so ammo, bandages and medicine
  were ignored. Now they also pick up ammo/arrows for party weapons, medicine and clearly better
  melee weapons; never items you dropped; far items are approached over several turns;
  never inside a zombie's attack range.

SETTINGS
Edit the constants at the top of <game>\companion_ai\companion_ai.py and restart the game,
e.g. RETREAT_HP_RATIO, HEAL_HP_RATIO, GUARD, QUIET_COMBAT, PICK_UP_NEEDED, KEEP_DROPPED.
Each one is explained in the file. Every decision is logged to <game>\companion_ai\companion_ai.log.

AFTER A MOD OR GAME UPDATE
Run install.bat again. If it reports a Python runtime mismatch, wait for a mod update.

UNINSTALL
Double-click uninstall.bat (removes <game>\companion_ai and restores the game unless
another mod still uses the loader), then unsubscribe. To recognise items you dropped, the mod
marks items your party has carried; the mark is saved with the game, does nothing without the mod,
and saves load normally.

HOW IT WORKS
The installer changes one file: the Python stdlib module linecache.pyc inside
<game>\_internal\base_library.zip, replaced by a version that runs the original code and
then a small mod loader shared with other mods (AI Takeover, Shared Party Inventory),
so they can be installed together. The package ships the original file to restore it on uninstall.
Every replaced function falls back to vanilla on error. Single-player only.
