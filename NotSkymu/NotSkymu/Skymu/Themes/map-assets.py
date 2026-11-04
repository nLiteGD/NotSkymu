import sys
import hashlib
from pathlib import Path
from PIL import Image


def pixel_hash(path):
    with Image.open(path) as img:
        data = img.convert("RGBA").tobytes()
    return hashlib.md5(data).hexdigest()


def index_dir(directory, recursive, label):
    pattern = "**/*.png" if recursive else "*.png"
    files = sorted(directory.glob(pattern))
    total = len(files)
    index = {}
    for i, p in enumerate(files, 1):
        print(f"\r  [{label}] {i}/{total}", end="", flush=True)
        try:
            h = pixel_hash(p)
            index.setdefault(h, []).append(p)
        except Exception as e:
            print(f"\n  [WARN] skipping {p}: {e}")
    print(f"  — {total} PNG(s)")
    return index


def main():
    cwd = Path.cwd()
    ref_dir    = cwd / "0409"
    assets_dir = cwd / "Assets"

    for d in (ref_dir, assets_dir):
        if not d.is_dir():
            sys.exit(f"ERROR: '{d.name}' directory not found in {cwd}")

    print("Indexing 0409/ ...")
    ref_index = index_dir(ref_dir, recursive=False, label="0409")

    print("\nIndexing Assets/ ...")
    asset_index = index_dir(assets_dir, recursive=True, label="Assets")

    print("\nMatching ...")
    matched = {}
    unmatched = []

    for h, asset_paths in asset_index.items():
        ref_paths = ref_index.get(h)
        if ref_paths is None:
            for p in asset_paths:
                unmatched.append(str(p.relative_to(cwd)).replace("\\", "/"))
        else:
            for ref_path in ref_paths:
                for asset_path in asset_paths:
                    asset_rel = str(asset_path.relative_to(cwd)).replace("\\", "/")
                    matched[ref_path.name] = asset_rel

    out_path = cwd / "mappings.txt"
    with out_path.open("w", encoding="utf-8") as f:
        f.write("# Match found\n\n")
        for ref_name, asset_rel in sorted(matched.items()):
            f.write(f"{ref_name}={asset_rel}\n")
        f.write("\n# No match found\n\n")
        for asset_rel in sorted(unmatched):
            f.write(f"{asset_rel}\n")

    print(f"\nWrote {len(matched)} matched, {len(unmatched)} unmatched → {out_path}")


if __name__ == "__main__":
    main()