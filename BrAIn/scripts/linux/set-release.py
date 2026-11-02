#!/usr/bin/env python3
"""Makes the fake GitHub (fake-github.py) offer a release.

    set-release.py <fake-github dir> <tag> <archive.tar.gz>

The archive is served as BrAIn-<version>-linux-<arch>.tar.gz (the arch is the machine's), with the sha256 digest GitHub
computes for every asset. The other platforms' assets are listed too, but nothing is behind their URLs: an updater that
picks the wrong file fails its download.
"""
import hashlib, json, os, platform, shutil, sys

root, tag, archive = sys.argv[1:4]
version = tag.lstrip('v')
arch = 'arm64' if platform.machine() in ('aarch64', 'arm64') else 'x64'
other = 'x64' if arch == 'arm64' else 'arm64'
base = 'https://github.com/GranTurismo/BrAIn/releases/download/%s/' % tag


def asset(name, size, digest):
    return {'name': name, 'size': size, 'browser_download_url': base + name, 'digest': 'sha256:' + digest}


os.makedirs(os.path.join(root, 'files'), exist_ok=True)
mine = 'BrAIn-%s-linux-%s.tar.gz' % (version, arch)
shutil.copyfile(archive, os.path.join(root, 'files', mine))
data = open(archive, 'rb').read()
fake = hashlib.sha256(b'nothing here').hexdigest()
release = {
    'tag_name': tag,
    'name': 'BrAIn ' + version,
    'assets': [
        asset('BrAIn-%s-macOS-arm64.dmg' % version, 12345678, fake),
        asset('BrAIn-Setup-%s-x64.exe' % version, 12345678, fake),
        asset('BrAIn-%s-linux-%s.tar.gz' % (version, other), len(data), fake),
        asset(mine, len(data), hashlib.sha256(data).hexdigest()),
    ],
}
with open(os.path.join(root, 'latest.json'), 'w') as f:
    json.dump(release, f)
print('offering %s (%s, %d bytes)' % (tag, mine, len(data)))
