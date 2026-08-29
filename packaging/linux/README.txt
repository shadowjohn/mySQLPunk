mySQLPunk @VERSION@ Linux @RUNTIME@

This package is self-contained and does not require a separate .NET runtime.

Install for the current user:

    ./install.sh

Then start it from the application menu or run:

    ~/.local/bin/mysqlpunk

Remove this version while preserving connection profiles:

    ./uninstall.sh

To save database passwords, install libsecret-tools and use GNOME Keyring or
another compatible Secret Service. The package never stores passwords in its
connections.json file.
