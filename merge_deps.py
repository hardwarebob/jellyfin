import json, sys
base = json.load(open('/base-deps.json'))
new  = json.load(open('/out/jellyfin.deps.json'))
# Use runtimeTarget.name to find the active target, not positional index
target = base['runtimeTarget']['name']
nt     = list(new['targets'].keys())[0]
for k, v in new['targets'].get(nt, {}).items():
    if 'opentelemetry' in k.lower():
        base['targets'][target][k] = v
for k, v in new.get('libraries', {}).items():
    if 'opentelemetry' in k.lower():
        base['libraries'][k] = v
json.dump(base, open('/out/jellyfin.merged.deps.json', 'w'))
print(f"Merged {sum(1 for k in base['libraries'] if 'opentelemetry' in k.lower())} OTel entries")
