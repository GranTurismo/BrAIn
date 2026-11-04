#!/usr/bin/env python3
"""Makes the fake GitHub (fake-github.py) offer a release.

    set-release.py <fake-github dir> <tag> <archive.tar.gz>

The archive is served as BrAIn-<version>-linux-<arch>.tar.gz (the arch is the machine's), with the sha256 digest GitHub
computes for every asset. The other platforms' assets are listed too, but nothing is behind their URLs: an updater that
picks the wrong file fails its download.

The release is SIGNED the way scripts/sign-release.sh does it (key: $BRAIN_TEST_SIGN_KEY; the app trusts it through
$BRAIN_UPDATE_TEST_KEY), so the updater accepts it. $SIGN and $DIGEST make it deliberately wrong, for the refusals:
  SIGN=none      no .sig asset at all          SIGN=other   signed with a key the app does not trust
  SIGN=replay    a valid signature, but for another version (an old release passed off as this one)
  DIGEST=none    GitHub gave the asset no sha256 digest
"""
import hashlib, json, os, platform, shutil, subprocess, sys, tempfile

root, tag, archive = sys.argv[1:4]
version = tag.lstrip('v')
arch = 'arm64' if platform.machine() in ('aarch64', 'arm64') else 'x64'
other = 'x64' if arch == 'arm64' else 'arm64'
base = 'https://github.com/GranTurismo/BrAIn/releases/download/%s/' % tag


def asset(name, size, digest):
    return {'name': name, 'size': size, 'browser_download_url': base + name, 'digest': ('sha256:' + digest) if digest else None}


def sign(key, ver, name, sha):
    """base64 of the ECDSA/SHA-256 DER signature over the message UpdateTrust.Message builds."""
    with tempfile.NamedTemporaryFile('w', suffix='.msg', delete=False) as m:
        m.write('BrAIn-update-v1\n%s\n%s\n%s\n' % (ver, name, sha))
    sig = subprocess.run(['openssl', 'dgst', '-sha256', '-sign', key, m.name], capture_output=True, check=True).stdout
    os.unlink(m.name)
    import base64
    return base64.b64encode(sig).decode()


os.makedirs(os.path.join(root, 'files'), exist_ok=True)
mine = 'BrAIn-%s-linux-%s.tar.gz' % (version, arch)
shutil.copyfile(archive, os.path.join(root, 'files', mine))
data = open(archive, 'rb').read()
fake = hashlib.sha256(b'nothing here').hexdigest()
real = hashlib.sha256(data).hexdigest()
mode = os.environ.get('SIGN', 'ok')
sigfile = os.path.join(root, 'files', mine + '.sig')
if os.path.exists(sigfile):
    os.unlink(sigfile)
if mode != 'none':
    key = os.environ['BRAIN_TEST_SIGN_KEY']
    if mode == 'other':
        key = os.path.join(tempfile.mkdtemp(), 'other.pem')
        subprocess.run('openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out ' + key, shell=True, check=True, capture_output=True)
    open(sigfile, 'w').write(sign(key, '0.0.1' if mode == 'replay' else version, mine, real))
release = {
    'tag_name': tag,
    'name': 'BrAIn ' + version,
    'assets': [
        asset('BrAIn-%s-macOS-arm64.dmg' % version, 12345678, fake),
        asset('BrAIn-Setup-%s-x64.exe' % version, 12345678, fake),
        asset('BrAIn-%s-linux-%s.tar.gz' % (version, other), len(data), fake),
        asset(mine, len(data), None if os.environ.get('DIGEST') == 'none' else real),
    ] + ([asset(mine + '.sig', os.path.getsize(sigfile), hashlib.sha256(open(sigfile, 'rb').read()).hexdigest())] if mode != 'none' else []),
}
with open(os.path.join(root, 'latest.json'), 'w') as f:
    json.dump(release, f)
print('offering %s (%s, %d bytes)' % (tag, mine, len(data)))
