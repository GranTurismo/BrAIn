# Linux test rig for BrAIn: a headless X session with the same libraries a desktop user needs.
#
#   docker build -t brain-linux-test -f BrAIn/scripts/linux/Dockerfile BrAIn/scripts/linux
#
# scripts/linux/test.sh builds it when it is missing and runs the checks in it (README.md next to it says how).
#
# A container has no GPU, so Mesa draws with its software renderer (LIBGL_ALWAYS_SOFTWARE=1) and WebKit runs its
# normal, accelerated code path on top of that. A real desktop takes the same path with hardware GL underneath.
FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
        # --- what BrAIn needs at runtime ---
        libwebkit2gtk-4.1-0 libgtk-3-0t64 libnotify4 libsecret-1-0 libx11-6 \
        gstreamer1.0-plugins-base gstreamer1.0-plugins-good gstreamer1.0-libav \
        # --- a minimal desktop session to test against ---
        xvfb xauth x11-utils x11-xserver-utils xdotool x11-apps openbox picom \
        dbus dbus-x11 gnome-keyring libglib2.0-bin \
        # --- what install.sh looks for, and the tools that check what it installed ---
        desktop-file-utils libgtk-3-bin hicolor-icon-theme shared-mime-info xdg-utils \
        # --- tooling for the tests themselves ---
        imagemagick python3 python3-gi python3-gi-cairo python3-cairo gir1.2-gtk-3.0 gir1.2-webkit2-4.1 gir1.2-secret-1 libsecret-tools curl ca-certificates openssl procps strace \
        fonts-dejavu-core fonts-liberation fonts-noto-color-emoji \
    && rm -rf /var/lib/apt/lists/*

# WebKit's bubblewrap sandbox needs user namespaces, which an unprivileged container lacks.
# GDK_BACKEND=x11: the rig has an X server and no Wayland compositor.
# WEBKIT_DISABLE_DMABUF_RENDERER is deliberately NOT set: nothing in BrAIn depends on it (the toolbar is an opaque
# view painted over a snapshot of the page), so the rig runs the renderer a user gets by default. It remains the first
# thing to try on a real machine that shows a blank window (see the README's troubleshooting section).
ENV WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=1 \
    LIBGL_ALWAYS_SOFTWARE=1 \
    GDK_BACKEND=x11 \
    NO_AT_BRIDGE=1 \
    DISPLAY=:99

RUN useradd -m -s /bin/bash tester
USER tester
WORKDIR /home/tester

# test.sh rebuilds the image when this differs from its own RIG number (i.e. when what is installed above changed)
LABEL brain.rig="3"
