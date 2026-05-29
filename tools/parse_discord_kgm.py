# Parses a DiscordChatExporter "ReGaMa - Kogama Archive / games" text export
# into docs/kgm.json. Each KogamaBot message announces one game with a direct
# .kgm download link on Discord's CDN, e.g.:
#
#   [27.05.2026 04:23] KogamaBot#6438
#   🆕 | `>WAR 4<` (ID: `2593313`) created by `opnfeniks**`
#
#   {Attachments}
#   https://cdn.discordapp.com/attachments/.../2593313.kgm?ex=...
#
# Usage: python tools/parse_discord_kgm.py <input.txt> [outPath]
#   outPath defaults to docs/kgm.json relative to repo root.

import json
import os
import re
import sys
from datetime import datetime, timezone

DATE_RE = re.compile(r'^\[(\d{2})\.(\d{2})\.(\d{4}) (\d{2}):(\d{2})\]')
MSG_RE = re.compile(r'\|\s*`(?P<title>.*)`\s*\(ID:\s*`(?P<id>[^`]+)`\)\s*created by\s*`(?P<owner>.*)`\s*$')
URL_RE = re.compile(r'(https://cdn\.discordapp\.com/attachments/\S+\.kgm)\S*')


def find_repo_root(start):
    d = os.path.abspath(start)
    while d:
        if os.path.isdir(os.path.join(d, '.git')):
            return d
        parent = os.path.dirname(d)
        if parent == d:
            break
        d = parent
    return os.path.abspath(start)


def clean_owner(owner):
    # KogamaBot appends "**" (bold marker leftover) to most usernames.
    return owner.rstrip('*').strip()


def main():
    if len(sys.argv) < 2:
        print('Usage: python tools/parse_discord_kgm.py <input.txt> [outPath]', file=sys.stderr)
        return 1

    in_path = sys.argv[1]
    repo_root = find_repo_root(os.path.dirname(__file__))
    out_path = sys.argv[2] if len(sys.argv) > 2 else os.path.join(repo_root, 'docs', 'kgm.json')

    with open(in_path, 'r', encoding='utf-8') as f:
        lines = f.read().splitlines()

    entries = {}
    last_dt = None
    pending = None  # (title, id, owner, savedAt)

    for line in lines:
        m = DATE_RE.match(line)
        if m:
            dd, mm, yyyy, hh, mi = m.groups()
            last_dt = datetime(int(yyyy), int(mm), int(dd), int(hh), int(mi), tzinfo=timezone.utc)
            continue

        m = MSG_RE.search(line)
        if m:
            pending = (
                m.group('title').strip(),
                m.group('id').strip(),
                clean_owner(m.group('owner')),
                last_dt.isoformat().replace('+00:00', 'Z') if last_dt else None,
            )
            continue

        m = URL_RE.search(line)
        if m and pending:
            title, gid, owner, saved = pending
            entry = {
                'Name': gid + '.kgm',
                'GameId': gid,
                'GameTitle': title,
                'OwnerUsername': owner,
                'SavedAt': saved,
                'Url': m.group(1),
                'Type': 'kgm',
            }
            # Later messages override earlier ones for the same game id.
            entries[gid] = entry
            pending = None

    maps = sorted(entries.values(), key=lambda e: (e['SavedAt'] or ''), reverse=True)

    output = {
        'generatedAt': datetime.now(timezone.utc).isoformat().replace('+00:00', 'Z'),
        'source': 'ReGaMa Kogama Archive (Discord export)',
        'count': len(maps),
        'maps': maps,
    }

    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    with open(out_path, 'w', encoding='utf-8') as f:
        json.dump(output, f, ensure_ascii=False, separators=(',', ':'))

    print(f'Wrote {len(maps)} kgm entries to {out_path}', file=sys.stderr)
    return 0


if __name__ == '__main__':
    sys.exit(main())
