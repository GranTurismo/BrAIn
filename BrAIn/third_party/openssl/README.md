# Bundled OpenSSL 3 (Linux fallback)

BrAIn's .NET runtime needs OpenSSL 3 (`libssl.so.3`) for TLS and crypto. Some systems no longer provide it — Fedora 45 and
newer ship OpenSSL 4 and keep 3 in an optional package — and without it the runtime aborts the process. The Linux archives
therefore carry these libraries in `openssl/`; `Browser/Linux/LinuxPreflight.cs` loads them **only when the system has no OpenSSL it
can use** (a system copy always wins: it gets the distribution's security updates).

| | |
|---|---|
| Origin | Fedora 45 package `openssl3-libs-3.5.9-1.fc45` (OpenSSL 3.5.9, an LTS series), unpacked with `rpm2cpio`; the files are the RPM's `libssl.so.3.5.9` / `libcrypto.so.3.5.9` copied to their SONAMEs |
| RPM sha256 | x86_64 `60fdefdada231053ca4da86e69ecd2706fd09bab7690cf978c7f0b24d41c17fc`, aarch64 `a00f6a0d75d50ad0e03cc11d081e5c01efc08e2872cff201c69df43bf8c88f79` |
| Needs | glibc 2.38 or newer, `libz.so.1` (every system that lacks OpenSSL 3 today is newer than that) |
| Licence | Apache-2.0, see `LICENSE.txt` (shipped next to the libraries in the archive) |

File checksums (sha256):

```
a10387fd055e5bf3cf7b2d1703c61c68434732c923c1bf343b3df36f24990521  linux-x64/libcrypto.so.3
b403808b41dc94160cfa4ccce5346eb24b3802b5630a17d8ec37c68f2e27e8a4  linux-x64/libssl.so.3
08ad0b0ab5680aab693856c0ce7a57518e150af7393de0ef2e9562578dbe4199  linux-arm64/libcrypto.so.3
aec8ccd8fa7529e5f4226d95c6d868b56ceb015188ef5c656e9a4ab45c1c3a28  linux-arm64/libssl.so.3
```

## Updating

OpenSSL 3.5 is supported until April 2030 and Fedora keeps patching `openssl3-libs`. To refresh: `dnf download --forcearch=<aarch64|x86_64> openssl3-libs`
on a Fedora system, `rpm2cpio <rpm> | cpio -idm`, copy `usr/lib64/libssl.so.3.x.y` and `libcrypto.so.3.x.y` over the two files per architecture
(name them `libssl.so.3` and `libcrypto.so.3`), refresh the sha256 values and the version above, and rebuild the Linux archives.
