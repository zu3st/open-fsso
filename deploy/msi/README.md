# Windows installer authoring

The installer is ordinary WiX 5 source:

- `Package.wxs`: package identity, directories, upgrades, launch conditions and action scheduling.
- `Components.wxs`: service, harvested payload, protected folders and configuration copy.
- `Client.wxs`: tray executable, logon startup and one non-advertised Start menu entry.

`tools/build_msi.py` extracts the already signed payload, preserves stable product
family identifiers, and passes WiX preprocessor definitions. `build.sh` invokes
`wix build` and copies the completed MSI and WiX debugging symbols to the output
folder. WiX harvests the payload with `Files`, reads PE versions, and authors the native tables
from `CopyFile`, `PermissionEx`, `ServiceInstall`, `ServiceControl` and
`MajorUpgrade` elements. The client uses WiX's standard `CloseApplication` utility
extension to close old tray instances before maintenance replaces files. The
utility extension is pinned in the Docker image. The client service uses Windows session tokens to start trays for existing users after installation or
upgrade, including SYSTEM deployments.

## Container build

From the repository root:

```bash
python3 tools/build_msi.py --signed-packages <signed zips> --output <msi folder>
```

The output folder receives the MSIs and their WiX debugging symbols.

The Dockerfile pins the [WiX/Wine build image](https://github.com/jkroepke/docker-wixtoolset)
by digest, including WiX **5.0.2**, Wine and the Windows .NET runtime, so it runs
on the Docker Desktop Linux engine. The `.wxs` authoring also compiles with native
Windows WiX.

The build uses MSZIP cabinet compression and assembles the MSI on the container
filesystem, which is faster than writing it to a Windows bind mount.

Use `--role Receiver --service <name>` for an independent receiver product
family, and keep that name stable between versions.

## Service recovery

Service recovery (restart
after 15 seconds, counter reset daily) uses the utility extension's
`util:ServiceConfig`, because
[MSI's native recovery table is unreliable](https://learn.microsoft.com/en-us/windows/win32/msi/msiconfigureservices-action).
