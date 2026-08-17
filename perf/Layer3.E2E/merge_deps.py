import json

base = json.load(open("/base-deps.json"))
new = json.load(open("/out/jellyfin.deps.json"))

# Use runtimeTarget.name to find the active target, not positional index.
target = base["runtimeTarget"]["name"]
new_target_name = next(iter(new["targets"]))

# Overlay every entry from our build's target/libraries onto base's — not just a specific
# package's entries — so this doesn't silently miss whatever this repo happens to add later.
# Base's own self-contained-only entries (native runtime libs our framework-dependent build never
# lists) are left untouched since we only update/add keys, never remove.
base["targets"][target].update(new["targets"][new_target_name])
base["libraries"].update(new.get("libraries", {}))

json.dump(base, open("/out/jellyfin.merged.deps.json", "w"))
print(f"Merged {len(new['targets'][new_target_name])} target entries, {len(new.get('libraries', {}))} library entries")
