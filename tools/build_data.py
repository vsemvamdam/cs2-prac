#!/usr/bin/env python3
"""Builds the plugin's data files.

  lineups/<map>.json  from CS2-annotations (MIT, ReneRebsdorf/CS2-annotations): local/<map>/<map>.txt
  skins.json          from cs2-WeaponPaints website/data (GPL-3.0): skins_en.json + skins_ru.json
  agents.json         from cs2-WeaponPaints website/data: agents_en.json + agents_ru.json

Usage: build_data.py <annotations_repo> <weaponpaints_repo> <out_dir>
"""
import json
import re
import sys
from pathlib import Path

# ---------------------------------------------------------------- KV3 (text) parser
TOKEN = re.compile(r'\s*(?:(//[^\n]*)|(<!--.*?-->)|("(?:[^"\\]|\\.)*")|([{}\[\]=,])|([^\s{}\[\]=,"]+))', re.S)


def tokenize(text):
    pos = 0
    out = []
    while pos < len(text):
        m = TOKEN.match(text, pos)
        if not m:
            if text[pos:].strip() == "":
                break
            raise ValueError(f"bad token at {pos}: {text[pos:pos+40]!r}")
        pos = m.end()
        if m.group(1) or m.group(2):
            continue
        if m.group(3) is not None:
            out.append(("str", json.loads(m.group(3))))
        elif m.group(4):
            out.append(("sym", m.group(4)))
        elif m.group(5):
            out.append(("atom", m.group(5)))
    return out


def parse_value(toks, i):
    kind, val = toks[i]
    if kind == "sym" and val == "{":
        obj = {}
        i += 1
        while not (toks[i] == ("sym", "}")):
            key = toks[i][1]
            assert toks[i + 1] == ("sym", "="), toks[i:i + 3]
            v, i = parse_value(toks, i + 2)
            obj[key] = v
        return obj, i + 1
    if kind == "sym" and val == "[":
        arr = []
        i += 1
        while not (toks[i] == ("sym", "]")):
            if toks[i] == ("sym", ","):
                i += 1
                continue
            v, i = parse_value(toks, i)
            arr.append(v)
        return arr, i + 1
    if kind == "str":
        return val, i + 1
    if val in ("true", "false"):
        return val == "true", i + 1
    try:
        return (float(val) if any(c in val for c in ".eE") else int(val)), i + 1
    except ValueError:
        return val, i + 1


def parse_kv3(text):
    toks = tokenize(text)
    v, _ = parse_value(toks, 0)
    return v


# ---------------------------------------------------------------- lineups
def clean(s):
    s = (s or "").replace("\\n", "\n")
    return " · ".join(p.strip() for p in s.split("\n") if p.strip())


def side_of(color):
    if not color:
        return ""
    r, g, b = color[:3]
    if b > r:
        return "CT"
    if r > b:
        return "T"
    return ""


TYPE_MAP = {"smoke": "smoke", "flash": "flash", "he": "he", "fire": "molotov", "molotov": "molotov", "decoy": "decoy"}


def build_lineups(ann_repo, out_dir):
    out = out_dir / "lineups"
    out.mkdir(parents=True, exist_ok=True)
    summary = {}
    for f in sorted((ann_repo / "local").glob("*/*.txt")):
        data = parse_kv3(f.read_text(encoding="utf-8"))
        nodes = [v for k, v in data.items() if k.startswith("MapAnnotationNode")]
        mains = {n["Id"]: n for n in nodes if n.get("Type") == "grenade" and n.get("SubType") == "main"}
        aims, dests = {}, {}
        for n in nodes:
            if n.get("Type") != "grenade":
                continue
            if n.get("SubType") == "aim_target":
                aims[n.get("MasterNodeId")] = n
            elif n.get("SubType") == "destination":
                dests[n.get("MasterNodeId")] = n
        lineups = []
        for mid, m in mains.items():
            a = aims.get(mid)
            d = dests.get(mid)
            if not a or not m.get("Enabled", True):
                continue
            title = clean(m.get("Title", {}).get("Text")) or clean(a.get("Title", {}).get("Text"))
            desc = clean(a.get("Desc", {}).get("Text")) or clean(m.get("Desc", {}).get("Text"))
            if desc.lower().startswith("setpos"):
                desc = ""  # служебная координата автора, не подсказка
            gtype = TYPE_MAP.get(str(m.get("GrenadeType", "smoke")).lower(), "smoke")
            jump = bool(m.get("JumpThrow", False))
            if not jump and re.search(r"jump\s*-?\s*throw|jumpthrow", (title + " " + desc).lower()):
                jump = True  # флаг в разметке не стоит, а в подсказке написано «jump throw»
            low = title.lower()
            if gtype == "smoke" and (low.startswith("molo") or " molo" in low or "molotov" in low or "incend" in low):
                gtype = "molotov"  # в исходных данных встречается молотов с типом smoke
            lineups.append({
                "Id": mid,
                "Title": title or "Lineup",
                "Desc": desc,
                "Type": gtype,
                "Side": side_of(m.get("Color")),
                "JumpThrow": jump,
                "Stand": [round(x, 3) for x in m["Position"]],
                "Aim": [round(x, 3) for x in a["Angles"]],
                "AimPoint": [round(x, 3) for x in a["Position"]],
                "Dest": [round(x, 3) for x in d["Position"]] if d else None,
                "Threshold": float(d.get("DistanceThreshold", 80.0)) if d else 80.0,
            })
        order = {"smoke": 0, "molotov": 1, "flash": 2, "he": 3, "decoy": 4}
        lineups.sort(key=lambda x: (order.get(x["Type"], 9), x["Side"], x["Title"].lower()))
        mapname = data.get("MapName") or f.stem
        (out / f"{mapname}.json").write_text(json.dumps(lineups, ensure_ascii=False, indent=1), encoding="utf-8")
        summary[mapname] = {t: sum(1 for x in lineups if x["Type"] == t) for t in order}
    return summary


# ---------------------------------------------------------------- skins / agents
CATS = {
    "rifle": [7, 16, 60, 10, 13, 8, 39],
    "sniper": [9, 40, 38, 11],
    "pistol": [4, 61, 32, 36, 3, 30, 63, 1, 2, 64],
    "smg": [17, 34, 33, 23, 24, 19, 26],
    "heavy": [35, 25, 27, 29, 14, 28],
}
KNIVES = [500, 503, 505, 506, 507, 508, 509, 512, 514, 515, 516, 517, 518, 519, 520, 521, 522, 523, 525, 526]

POPULAR = {
    9: ["Dragon Lore", "Gungnir", "Medusa", "Prince", "Asiimov", "Lightning Strike", "Fade", "Printstream", "Neo-Noir",
        "Containment Breach", "Wildfire", "Hyper Beast", "Desert Hydra", "The Prince"],
    7: ["Fire Serpent", "Wild Lotus", "Gold Arabesque", "Vulcan", "Asiimov", "Redline", "Neon Rider", "Bloodsport",
        "The Empress", "Case Hardened", "Fuel Injector", "Head Shot", "Hydroponic", "X-Ray"],
    16: ["Howl", "Poseidon", "The Emperor", "Asiimov", "Neo-Noir", "Desolate Space", "Temukau", "Buzz Kill", "Royal Paladin"],
    60: ["Welcome to the Jungle", "Hot Rod", "Knight", "Printstream", "Hyper Beast", "Golden Coil", "Chantico's Fire",
         "Player Two", "Imminent Danger", "Icarus Fell", "Blue Phosphor"],
    1: ["Blaze", "Printstream", "Code Red", "Fennec Fox", "Kumicho Dragon", "Golden Koi", "Hypnotic"],
    61: ["Kill Confirmed", "The Traitor", "Printstream", "Neo-Noir", "Orion", "Whiteout", "Cortex"],
    4: ["Fade", "Gamma Doppler", "Water Elemental", "Neo-Noir", "Bullet Queen", "Dragon Tattoo"],
    40: ["Dragonfire", "Blood in the Water", "Death Strike", "Fever Dream"],
}
KNIFE_POPULAR = ["Fade", "Doppler", "Gamma Doppler", "Marble Fade", "Tiger Tooth", "Crimson Web", "Slaughter",
                 "Case Hardened", "Lore", "Autotronic", "Black Laminate", "Damascus Steel", "Ultraviolet", "Vanilla"]
DOPPLER_PHASES = {
    415: "Ruby", 416: "Sapphire", 417: "Black Pearl", 418: "Phase 1", 419: "Phase 2", 420: "Phase 3", 421: "Phase 4",
    568: "Emerald", 569: "Phase 1", 570: "Phase 2", 571: "Phase 3", 572: "Phase 4",
    617: "Black Pearl", 618: "Phase 2", 619: "Sapphire",
    852: "Phase 1", 853: "Phase 2", 854: "Phase 3", 855: "Phase 4",
    1119: "Emerald", 1120: "Phase 1", 1121: "Phase 2", 1122: "Phase 3", 1123: "Phase 4",
}


def split_name(s):
    parts = [p.strip() for p in s.split("|", 1)]
    return (parts[0], parts[1] if len(parts) > 1 else "")


def build_skins(wp_repo, out_dir):
    data = wp_repo / "website" / "data"
    en = json.loads((data / "skins_en.json").read_text(encoding="utf-8"))
    ru = {(x["weapon_defindex"], str(x["paint"])): x["paint_name"] for x in
          json.loads((data / "skins_ru.json").read_text(encoding="utf-8"))}
    weapons = {}
    for x in en:
        d = x["weapon_defindex"]
        paint = int(x["paint"])
        wname_en, sname_en = split_name(x["paint_name"])
        wname_ru, sname_ru = split_name(ru.get((d, str(x["paint"])), x["paint_name"]))
        w = weapons.setdefault(d, {"Def": d, "Class": x["weapon_name"], "Name": wname_en.replace("★", "").strip(),
                                   "NameRu": wname_ru.replace("★", "").strip(), "Skins": []})
        if paint == 0:
            continue
        phase = DOPPLER_PHASES.get(paint) if "Doppler" in sname_en else None
        if phase:
            sname_en = f"{sname_en} ({phase})"
            sname_ru = f"{sname_ru} ({phase})"
        w["Skins"].append({"Paint": paint, "En": sname_en, "Ru": sname_ru, "Legacy": bool(x.get("legacy_model"))})

    def sort_skins(w):
        pop = KNIFE_POPULAR if w["Def"] in KNIVES else POPULAR.get(w["Def"], [])
        def key(s):
            base = s["En"].split(" (")[0]
            return (pop.index(base) if base in pop else 999, s["Ru"].lower())
        w["Skins"].sort(key=key)
        for s in w["Skins"]:
            s["Popular"] = s["En"].split(" (")[0] in pop

    out = {"Categories": {}, "Knives": []}
    for cat, defs in CATS.items():
        out["Categories"][cat] = []
        for d in defs:
            if d in weapons:
                sort_skins(weapons[d])
                out["Categories"][cat].append(weapons[d])
    for d in KNIVES:
        if d in weapons:
            sort_skins(weapons[d])
            out["Knives"].append(weapons[d])
    (out_dir / "skins.json").write_text(json.dumps(out, ensure_ascii=False), encoding="utf-8")

    a_en = json.loads((data / "agents_en.json").read_text(encoding="utf-8"))
    a_ru = {x["model"]: x["agent_name"] for x in json.loads((data / "agents_ru.json").read_text(encoding="utf-8"))}
    agents = []
    for x in a_en:
        if not x.get("model") or x["model"] == "null":
            continue
        agents.append({"Team": x["team"], "Model": x["model"], "En": x["agent_name"], "Ru": a_ru.get(x["model"], x["agent_name"])})
    (out_dir / "agents.json").write_text(json.dumps(agents, ensure_ascii=False), encoding="utf-8")
    return {"weapons": sum(len(v) for v in out["Categories"].values()), "knives": len(out["Knives"]),
            "skins": sum(len(w["Skins"]) for w in weapons.values()), "agents": len(agents)}


if __name__ == "__main__":
    ann, wp, out = map(Path, sys.argv[1:4])
    out.mkdir(parents=True, exist_ok=True)
    print("lineups:", json.dumps(build_lineups(ann, out)))
    print("skins:", json.dumps(build_skins(wp, out)))
