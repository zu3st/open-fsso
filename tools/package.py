"""Package the Docker build outputs for signing."""
import zipfile

from paths import ARTIFACTS


destination = ARTIFACTS / 'release-unsigned.zip'
with zipfile.ZipFile(destination, 'w', zipfile.ZIP_DEFLATED, compresslevel=1) as archive:
    for role in ('client', 'receiver'):
        payload = ARTIFACTS / role
        files = list(payload.rglob('*'))
        assert any(file.name == f'OpenFsso.{role.title()}.exe' for file in files), 'Build both services first'
        for file in files:
            if file.is_file():
                archive.write(file, role + '/payload/' + file.relative_to(payload).as_posix())
print('Unsigned payloads packaged for guest/CI signing; not installable until signed.')
