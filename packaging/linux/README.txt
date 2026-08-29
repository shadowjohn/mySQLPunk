mySQLPunk @VERSION@ Linux @RUNTIME@

This package is self-contained and does not require a separate .NET runtime.
Installation replaces the versioned app, launcher and desktop entry as one
transaction. If any step fails, the previous installation is restored.

Install for the current user:

    ./install.sh

Then start it from the application menu or run:

    ~/.local/bin/mysqlpunk

Installed builds can download, verify and apply later Linux releases from the
Check Updates button. The updater waits for the current process to exit,
rechecks the archive, and relaunches the previous version if startup fails.

Remove this version while preserving connection profiles:

    ./uninstall.sh

To save database passwords, install libsecret-tools and use GNOME Keyring or
another compatible Secret Service. The package never stores passwords in its
connections.json file.
