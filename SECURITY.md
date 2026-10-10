# Security policy

Please report vulnerabilities privately through GitHub's
[private vulnerability reporting](https://github.com/balsoft-gmbh/Balsoft.Hive.Installer.Windows/security/advisories/new),
not in a public issue. You will get an answer within five working days.

Released packages are author-signed with the code signing certificate of Balsoft GmbH
(SHA-256 fingerprint `B761F905A340CD6FC0FB3DB1BB06E768E199A4E2B5919ACDB273DE474530AFD5`).
The public certificate is in [`eng/BalsoftGmbH.cer`](eng/BalsoftGmbH.cer).
Verify a package with `dotnet nuget verify --all <package>.nupkg`.

Setup runs elevated. It never writes the service account password to its log, and it only
deletes files it installed itself. Setup executables built with this package should be
Authenticode-signed by the product's publisher.
