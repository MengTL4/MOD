"""Lists every Resources/ asset path in the game build with its Unity type.

Writes data/resources_index.tsv (path<TAB>type). The in-game exporter (SolverLogger) loads
these paths and serializes the ones the headless engine needs.
"""
import sys
from pathlib import Path

import UnityPy

game_data = Path(sys.argv[1] if len(sys.argv) > 1 else r"F:\SteamLibrary\steamapps\common\LONESTAR\LONESTAR_Data")
out = Path(__file__).resolve().parent.parent / "data" / "resources_index.tsv"

env = UnityPy.load(str(game_data / "globalgamemanagers"), str(game_data / "resources.assets"),
                   str(game_data / "sharedassets0.assets"))
files = {Path(name).name.lower(): f for name, f in env.files.items()}

manager = next(o for o in env.objects if o.type.name == "ResourceManager")
gg = manager.assets_file
container = manager.read_typetree()["m_Container"]

rows = []
for path, pptr in container:
    file_id, path_id = pptr["m_FileID"], pptr["m_PathID"]
    target = gg if file_id == 0 else files.get(Path(gg.externals[file_id - 1].path).name.lower())
    obj = target.objects.get(path_id) if target is not None else None
    rows.append((path, obj.type.name if obj is not None else "?"))

rows.sort()
out.write_text("".join(f"{p}\t{t}\n" for p, t in rows), encoding="utf-8")
print(f"{len(rows)} entries -> {out}")
