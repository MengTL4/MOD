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
Combat: turn planning
- Companions do the maths with the game's real rules: standing on a given tile at the end of the turn,
  how much damage will the whole party take in the next zombie turn, and how likely is someone to die
  (zombies chase the nearest person, how many swings they get, AP spent bashing doors, dodge, damage minus
  armor, and the bite rule: bleeding with 0 AP means a fatal bite). Your losses count 5x - if you fall, it's game over.
- Every step compares target, weapon, body part, whether to walk up first, and hit-and-run vs. fighting it out,
  with gains and risks in HP. They stop when it isn't worth it and end on the safest tile.
- That yields retreating, waiting for zombies to walk up and striking first, hit-and-run, focus fire and rescues;
  a healthy, armored companion steps between a zombie and you, a wounded one doesn't.
- Body part: head = chance to kill; body = attack power drops proportionally (0 = can't claw);
  legs = action power drops proportionally.
- Crippled zombies (no body left, can't claw, but still bite the bleeding) get finished off,
  so the party doesn't stand guard over one forever.
- Weapon wear is a cost (each melee hit takes 6-8 condition, more on the head): no wrecking
  a weapon for a tiny gain.
- A companion with no usable melee weapon takes a spare one from you or another companion:
  everyone keeps at least one, and never your best one or your favorite weapon.
- A gunshot pulls up to 40 nearby zombies; that cost is part of the maths.
- They close a door next to them when it clearly helps; companions closest to zombies act first.
- No reading, crafting or repairs while a zombie can reach them; they put on armor they carry.
- Fixes a vanilla bug where a companion with nowhere to move crashes the whole companion turn.

Tested on a real save against the vanilla AI (same scenarios, 30 turns each with zombies
arriving all the time; 40 scenarios not used for tuning):
- you standing still: vanilla lost 9 companions, 285 companion HP and 8.7 of your HP;
  this mod lost 0 companions, 6 companion HP and none of yours.
- you under AI Takeover: vanilla lost 1 companion, 76 companion HP and 21.7 of your HP;
  this mod lost 0, 1.8 and 0.
Kills were about the same as vanilla.

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
- Loot whole location (optional, off by default): the game's Companion AI Settings get a new
  "Loot Whole Location" line under Search, per companion. Vanilla companions only search near you
  (within 20 tiles when following, 5 when staying close). With it on they search every unsearched
  container (and corpses if Search Corpses is on) and pick up items on every floor of the location,
  splitting up between companions. They still come back to guard you when zombies get close, and stop
  heading to new places once their load costs 0.5 extra AP per tile (about 65 kg at Strength 10).
  Loot stays in the companion's own bag. They never travel to another location on their own.
  Tested on a military base with 103 containers: with no zombies, 80 searched without it vs. all 103 with it;
  with zombies arriving all the time, 77 vs. 86 on average, costing 2.8 companion HP over 6 runs and no deaths.
  Small places are fully searched either way.

SETTINGS
Edit the constants at the top of <game>\companion_ai\companion_ai.py and restart the game,
e.g. PROTECT_PLAYER_WEIGHT, RISK_AVERSION, HEAL_HP_RATIO, GUARD, CLOSE_DOORS, ARM_FROM_PARTY, PICK_UP_NEEDED, KEEP_DROPPED,
SWEEP_MAX_SLOWDOWN.
Each one is explained in the file. Every decision is logged to <game>\companion_ai\companion_ai.log.

AFTER A MOD OR GAME UPDATE
Run install.bat again. If it reports a Python runtime mismatch, wait for a mod update.

UNINSTALL
Double-click uninstall.bat (removes <game>\companion_ai and restores the game unless
another mod still uses the loader), then unsubscribe. To recognise items you dropped, the mod
marks items your party has carried; the mark is saved with the game, does nothing without the mod,
and saves load normally. The Loot Whole Location setting is dropped by the game on the next load.

HOW IT WORKS
The installer changes one file: the Python stdlib module linecache.pyc inside
<game>\_internal\base_library.zip, replaced by a version that runs the original code and
then a small mod loader shared with other mods (AI Takeover, Shared Party Inventory),
so they can be installed together. The package ships the original file to restore it on uninstall.
Every replaced function falls back to vanilla on error. Single-player only.
