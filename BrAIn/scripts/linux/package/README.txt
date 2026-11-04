BrAIn for Linux
===============

A desktop browser with an MCP server built in: an AI agent can drive it, and you can rewind it,
pause it and audit what it did. https://github.com/GranTurismo/BrAIn

Install
-------
    ./install.sh                  for you only, no root (program in ~/.local/share/brain-browser,
                                  command `brain` in ~/.local/bin, entry in the application menu).
                                  This kind of install updates itself from the toolbar menu.
    sudo ./install.sh --system    for everybody (/opt/brain-browser). Update by running install.sh
                                  from a newer archive.
    ./uninstall.sh                remove it again (your data in ~/.local/share/BrAIn stays;
                                  uninstall.sh --purge deletes that too)

Or just run it from here, nothing to install:  ./BrAIn

Needs (Debian / Ubuntu names; install.sh prints the right ones for your distribution)
    sudo apt install libwebkit2gtk-4.1-0 libgtk-3-0 libnotify4 libsecret-1-0
    Fedora: sudo dnf install webkit2gtk4.1 gtk3 libnotify libsecret openssl3-libs   (OpenSSL 3 is its own package since Fedora 45)

Let an AI agent drive it (any MCP client, e.g. Claude Desktop / Claude Code):
    { "mcpServers": { "brain": { "command": "/home/YOU/.local/share/brain-browser/BrAIn" } } }

Files
-----
    BrAIn                the program (self-contained: no .NET needed)
    Photino.Native.so    the window layer; must stay next to BrAIn
    Assets/              window icon
    icons/               menu icons, copied to ~/.local/share/icons by install.sh
    brain.desktop        menu entry template
    install.sh, uninstall.sh, release.txt (lists the files above for the self-updater)

If something is off
-------------------
    Blank or black window (some NVIDIA / driver setups):   WEBKIT_DISABLE_DMABUF_RENDERER=1 brain
    Odd window behaviour on a Wayland session:             GDK_BACKEND=x11 brain
    Help and tool list: press F1 inside BrAIn, or see the README on GitHub.
