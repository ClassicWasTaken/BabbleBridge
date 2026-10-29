# Copyright (c) 2026 Frame Bridge contributors.
# SPDX-License-Identifier: Apache-2.0

"""Verify collected notices and exclusions before publishing a source/build package."""
import argparse
import hashlib
import json
from pathlib import Path

root = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--publish-dir', type=Path)
args = parser.parse_args()

required = ['LICENSE', 'NOTICE.txt', 'THIRD_PARTY_NOTICES.md',
            'licenses/VRCFaceTracking-LICENSE.txt', 'licenses/LICENSE-SOURCES.txt',
            'licenses/LICENSE-SOURCES.json', 'licenses/DEPENDENCY-METADATA.json']
for relative in required:
    assert (root / relative).is_file(), f'Missing source notice: {relative}'
receipts = json.loads((root / 'licenses/LICENSE-SOURCES.json').read_text(encoding='utf-8-sig'))
for receipt in receipts:
    path = root / 'licenses' / receipt['file']
    assert path.is_file(), f'Missing collected license: {receipt["file"]}'
    assert hashlib.sha256(path.read_bytes()).hexdigest() == receipt['sha256'], f'Changed license text: {receipt["file"]}'
font = Path('src/BabbleFrameBridge.App/Assets/Brand/IBM-Plex-OFL.txt')
assert '2017 IBM Corp.' in (root / font).read_text(encoding='utf-8-sig')
assert hashlib.sha256((root / 'vendor/fti_osc.dll').read_bytes()).hexdigest() == '668628bcaf181617839663fe3624f42eb7d36ae3d16e3434ace40a9545aa982e'

excluded = {'vcruntime140.dll', 'baballonia-module.zip', 'vrcfacetracking.baballonia.dll',
            'babble-brand-license.txt', 'babblelogos.xaml', 'babblebanner.png',
            'logo-white.svg', 'logo_silhouette.svg'}
for path in root.rglob('*'):
    if any(part in {'bin', 'obj', 'dist', '.git', 'work', '__pycache__'} for part in path.relative_to(root).parts):
        continue
    assert not path.is_file() or path.name.lower() not in excluded, f'Excluded source component: {path.relative_to(root)}'
assert not (root / 'src/BabbleFrameBridge.App/Assets/Brand/logo.png').exists()

if args.publish_dir:
    payload = args.publish_dir.resolve()
    metadata = json.loads((root / 'licenses/DEPENDENCY-METADATA.json').read_text(encoding='utf-8-sig'))
    recorded_runtimes = {item['package'].split('/')[0].lower(): item['package'].split('/')[1]
                         for item in metadata if '.runtime.win-x64/' in item['package'].lower()}
    runtime_config = json.loads((payload / 'BabbleFrameBridge.runtimeconfig.json').read_text(encoding='utf-8-sig'))
    for framework in runtime_config['runtimeOptions']['includedFrameworks']:
        key = framework['name'].lower() + '.runtime.win-x64'
        assert recorded_runtimes.get(key) == framework['version'], 'Update runtime license inventory for ' + framework['name']
    for relative in required:
        assert (payload / relative).is_file(), f'Missing distribution notice: {relative}'
    for receipt in receipts:
        path = payload / 'licenses' / receipt['file']
        assert path.is_file(), f'Missing distribution license: {receipt["file"]}'
        assert hashlib.sha256(path.read_bytes()).hexdigest() == receipt['sha256'], f'Changed distribution license: {receipt["file"]}'
    assert (payload / 'Assets/Brand/IBM-Plex-OFL.txt').read_bytes() == (root / font).read_bytes()
    for path in payload.rglob('*'):
        assert not path.is_file() or path.name.lower() not in excluded, f'Excluded distribution component: {path.relative_to(payload)}'
    assert not (payload / 'Assets/Brand/logo.png').exists()

print(f'PASS license/copyright notices, {len(receipts)} exact license receipts, original branding, and excluded components.')
