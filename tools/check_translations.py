#!/usr/bin/env python3
"""Static check of the Keyed/DefInjected translations.

* English and Japanese define exactly the same keys.
* Every key referenced from C# exists (literal keys and the dynamic "prefix + enum value" keys).
* Each key uses the same {n} placeholders in both languages.
* All XML files under About/, Defs/ and Languages/ are well-formed.

Exit code 0 on success, 1 on any problem. Standard library only.
"""
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parent.parent
LANGS = ["English", "Japanese"]

# Dynamic keys: prefix -> values appended at runtime (must mirror the C# enums / scope ids).
DYNAMIC = {
    "MRA_Category_": ["Items", "Buildings", "Terrain", "ContainedEquipped", "WorldCaravan", "Material", "Other"],
    "MRA_Confidence_": ["Confirmed", "Uncertain", "Unknown"],
    "MRA_Reason_": ["Declared", "Shadowed", "NotInOwnerDefs", "NoOwner"],
    "MRA_Status_": ["Pending", "Checked", "Partial", "Failed", "Cancelled", "NotCovered"],
    "MRA_Verdict_": ["Detected", "NoDetectedInstances", "Incomplete"],
    "MRA_VerdictTip_": ["Detected", "NoDetectedInstances", "Incomplete"],
    "MRA_Scope_": None,      # filled from scope ids below
    "MRA_ScopeTip_": None,
    "MRA_Holder_": ["Inventory", "Apparel", "Equipment", "Carried", "TradeStock", "Container", "None"],
    "MRA_TerrainLayer_": ["top", "under", "foundation", "temp"],
}


def load_keyed(lang):
    keys = {}
    for path in sorted((ROOT / "Languages" / lang / "Keyed").glob("*.xml")):
        tree = ET.parse(path)
        for el in tree.getroot():
            if el.tag in keys:
                raise SystemExit(f"duplicate key {el.tag} in {path}")
            keys[el.tag] = el.text or ""
    return keys


def scope_ids(source_text):
    ids = set(re.findall(r'public const string Scope\w+ = "([a-z]+)"', source_text))
    block = re.search(r"NotCoveredScopes\s*=\s*\{(.*?)\};", source_text, re.S)
    if block:
        ids.update(re.findall(r'"([a-z]+)"', block.group(1)))
    return sorted(ids)


def main():
    problems = []
    for folder in ("About", "Defs", "Languages"):
        for path in (ROOT / folder).rglob("*.xml"):
            try:
                ET.parse(path)
            except ET.ParseError as exc:
                problems.append(f"malformed XML {path.relative_to(ROOT)}: {exc}")

    keyed = {lang: load_keyed(lang) for lang in LANGS}
    en, ja = keyed["English"], keyed["Japanese"]
    for missing in sorted(set(en) - set(ja)):
        problems.append(f"missing in Japanese: {missing}")
    for missing in sorted(set(ja) - set(en)):
        problems.append(f"missing in English: {missing}")
    for key in sorted(set(en) & set(ja)):
        pe = sorted(set(re.findall(r"\{\d+\}", en[key])))
        pj = sorted(set(re.findall(r"\{\d+\}", ja[key])))
        if pe != pj:
            problems.append(f"placeholder mismatch {key}: en={pe} ja={pj}")
        if not ja[key].strip() or not en[key].strip():
            problems.append(f"empty text: {key}")

    source = "\n".join(p.read_text(encoding="utf-8") for p in (ROOT / "Source").rglob("*.cs"))
    scopes = scope_ids(source)
    DYNAMIC["MRA_Scope_"] = scopes
    DYNAMIC["MRA_ScopeTip_"] = scopes
    referenced = set(re.findall(r'"(MRA_[A-Za-z_]+)"', source))
    for key in sorted(referenced):
        if key in DYNAMIC:
            for value in DYNAMIC[key]:
                if key + value not in en:
                    problems.append(f"dynamic key not defined: {key + value}")
        elif key not in en:
            problems.append(f"key used in code but not defined: {key}")

    used = set(k for k in referenced if k not in DYNAMIC)
    for prefix, values in DYNAMIC.items():
        if prefix in referenced:
            used.update(prefix + v for v in values)
    for key in sorted(set(en) - used):
        problems.append(f"unused key: {key}")

    if problems:
        print("\n".join(problems))
        return 1
    print(f"translations ok: {len(en)} keys in {', '.join(LANGS)}; {len(referenced)} referenced from code; scopes={scopes}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
