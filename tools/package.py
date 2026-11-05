#!/usr/bin/env python3
"""Package the mod folder as ModRemovalAuditor-<version>.zip.

Checks before packaging (exit code 1 on any problem):
* the requested version matches About/About.xml <modVersion> and the csproj <Version>;
* 1.7/Assemblies/ModRemovalAuditor.dll exists and carries that version;
* the only assembly in the package is ModRemovalAuditor.dll (no game or third-party assemblies);
* release notes exist at docs/releases/v<version>.md.

Usage: python3 tools/package.py <version> [output-dir]
Standard library only.
"""
import pathlib
import re
import sys
import xml.etree.ElementTree as ET
import zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
CONTENT = ["About", "1.7", "Defs", "Languages", "Textures", "README.md"]
OUR_ASSEMBLY = "1.7/Assemblies/ModRemovalAuditor.dll"


def main(argv):
    if len(argv) < 2 or not re.fullmatch(r"\d+\.\d+\.\d+", argv[1]):
        print("usage: package.py <major.minor.patch> [output-dir]")
        return 1
    version = argv[1]
    out_dir = pathlib.Path(argv[2]) if len(argv) > 2 else ROOT / "dist"
    problems = []

    about_version = ET.parse(ROOT / "About" / "About.xml").getroot().findtext("modVersion")
    if about_version != version:
        problems.append(f"About.xml modVersion is {about_version!r}, expected {version!r}")
    csproj = (ROOT / "Source" / "ModRemovalAuditor" / "ModRemovalAuditor.csproj").read_text(encoding="utf-8")
    m = re.search(r"<Version>([^<]+)</Version>", csproj)
    if not m or m.group(1) != version:
        problems.append(f"csproj Version is {m.group(1) if m else None!r}, expected {version!r}")

    dll = ROOT / OUR_ASSEMBLY
    if not dll.is_file():
        problems.append(f"missing {OUR_ASSEMBLY}")
    elif version.encode("ascii") not in dll.read_bytes():
        problems.append(f"{OUR_ASSEMBLY} does not carry version {version}; rebuild it against RimWorld 1.7")

    notes = ROOT / "docs" / "releases" / f"v{version}.md"
    if not notes.is_file():
        problems.append(f"missing release notes {notes.relative_to(ROOT)}")

    files = []
    for entry in CONTENT:
        path = ROOT / entry
        if path.is_file():
            files.append(path)
        elif path.is_dir():
            files.extend(p for p in sorted(path.rglob("*")) if p.is_file())
        else:
            problems.append(f"missing {entry}")
    for p in files:
        rel = p.relative_to(ROOT).as_posix()
        if p.suffix.lower() in (".dll", ".exe", ".so", ".dylib", ".pdb") and rel != OUR_ASSEMBLY:
            problems.append(f"refusing to package foreign binary {rel}")

    if problems:
        print("\n".join(problems))
        return 1

    out_dir.mkdir(parents=True, exist_ok=True)
    target = out_dir / f"ModRemovalAuditor-{version}.zip"
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as zf:
        for p in files:
            zf.write(p, "ModRemovalAuditor/" + p.relative_to(ROOT).as_posix())
    print(f"{target} ({len(files)} files)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
