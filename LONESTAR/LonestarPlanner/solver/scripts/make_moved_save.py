"""Pre-battle file with the units moved on the ship, as the game would save it (tests).

usage: python make_moved_save.py IN.jsonl REPLY.json OUT.jsonl

REPLY.json is a loadout reply from the service: its Entries (x, y, unit, id, lv, source x,
source y, bag) say where each unit goes. Only moves between ship cells are supported (bag = -1):
the unit's save data moves with it from its source cell to its target cell, the other cells are
emptied. The save is the game's own ship.json, decrypted and encrypted again with its key
(EncryptionTool: TripleDES-ECB, PKCS7, key MD5("777")); everything else is copied unchanged.
"""
import base64
import json
import subprocess
import sys

CRYPT = ("$k=[Security.Cryptography.MD5]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes('777'));"
         "$t=[Security.Cryptography.TripleDES]::Create();$t.Key=$k;$t.Mode='ECB';$t.Padding='PKCS7';"
         "$b=[Convert]::FromBase64String([Console]::In.ReadToEnd().Trim());"
         "[Console]::Out.Write([Convert]::ToBase64String($t.{0}().TransformFinalBlock($b,0,$b.Length)))")


def run(op, data_b64):
    out = subprocess.run(["pwsh", "-NoProfile", "-Command", CRYPT.format(op)], input=data_b64,
                         capture_output=True, text=True, check=True).stdout
    return out.strip()


def decrypt(enc):
    return base64.b64decode(run("CreateDecryptor", enc)).decode("utf-8")


def encrypt(text):
    return run("CreateEncryptor", base64.b64encode(text.encode("utf-8")).decode("ascii"))


src, reply_path, dst = sys.argv[1:4]
with open(src, encoding="utf-8") as f:
    lines = f.read().splitlines()
begin = json.loads(lines[0])
files = begin["save_files"]
name = next(k for k in files if k.endswith("/ship.json"))
ship = json.loads(decrypt(files[name]))

reply = json.loads(open(reply_path, encoding="utf-8").read())
cells = {(c["coor"]["x"], c["coor"]["y"]): c for c in ship["grid"]["gridArray"]}
moved = {}
for e in reply["Entries"]:
    x, y, sx, sy, bag = e[0], e[1], e[5], e[6], e[7]
    if bag != -1:
        sys.exit("only moves between ship cells are supported (entry %r comes from the bag)" % (e,))
    unit = cells[(sx, sy)]["unitData"]
    if unit is None or unit["id"] != e[3] or unit["lv"] != e[4]:
        sys.exit("entry %r does not match the save" % (e,))
    moved[(x, y)] = unit
for key, cell in cells.items():
    cell["unitData"] = moved.get(key)

# the game writes compact JSON (Unity's JsonUtility style); the solver reads either
files[name] = encrypt(json.dumps(ship, ensure_ascii=False, separators=(",", ":")))
lines[0] = json.dumps(begin, ensure_ascii=False, separators=(",", ":"))
with open(dst, "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(lines) + "\n")
print("wrote", dst, "-", len(moved), "units placed")
