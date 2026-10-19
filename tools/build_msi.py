"""Package Docker outputs as MSIs, from signed payloads unless --unsigned. All packaging tools run in Docker."""
import argparse
import os
import re
import shutil
import subprocess
import tempfile
import uuid
import zipfile
from pathlib import Path
from xml.etree import ElementTree as ET

from paths import ARTIFACTS


def extract(path, payload):
    payload.mkdir()
    with zipfile.ZipFile(path) as archive:
        seen = set()
        for entry in archive.infolist():
            name = entry.filename.replace('\\', '/')
            if not name.startswith('payload/') or entry.is_dir():
                continue
            relative = Path(name).relative_to('payload')
            if '..' in relative.parts or ':' in name or str(relative).casefold() in seen:
                raise ValueError('Unsafe signed-package path')
            seen.add(str(relative).casefold())
            destination = payload / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            with archive.open(entry) as source, destination.open('wb') as target:
                shutil.copyfileobj(source, target)


def main():
    root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--role', choices=('Client', 'Receiver', 'both'), default='both')
    parser.add_argument('--signed-packages', type=Path, default=ARTIFACTS / 'signed')
    parser.add_argument('--unsigned', action='store_true', help='Package the unsigned build outputs in the artifacts folder')
    parser.add_argument('--output', type=Path, default=ARTIFACTS / 'msi')
    parser.add_argument('--service', help='Build-time identity for a separate receiver instance MSI family')
    parser.add_argument('--test-version', help='Package-only version override for upgrade tests')
    args = parser.parse_args()
    version = args.test_version or ET.parse(root / 'Directory.Build.props').findtext('.//Version')
    if not re.fullmatch(r'\d+\.\d+\.\d+', version) or any(
            int(part) > limit for part, limit in zip(version.split('.'), (255, 255, 65535))):
        parser.error('Use a three-part MSI version within Windows Installer limits.')
    if args.service and (args.role != 'Receiver' or not re.fullmatch(r'OpenFsso[A-Za-z0-9_-]{1,64}', args.service)):
        parser.error('--service requires --role Receiver and an OpenFsso service identity.')
    namespace = uuid.UUID('1d52bba2-57b2-47af-9860-b13853e50590')
    output = (root / args.output).resolve()
    signed_packages = (root / args.signed_packages).resolve()
    output.mkdir(parents=True, exist_ok=True)
    # Long native Windows file operations can invalidate a DrvFS cwd alias.
    # Keep the process on a stable root and use absolute paths throughout.
    os.chdir(root.anchor)
    subprocess.run(['docker', 'build', '-t', 'openfsso-msi-builder:local', str(root / 'deploy/msi')],
                   cwd=root.anchor, check=True)
    roles = ('Client', 'Receiver') if args.role == 'both' else (args.role,)
    for role in roles:
        with tempfile.TemporaryDirectory(prefix='openfsso-msi-') as temp:
            input_directory = Path(temp)
            payload = input_directory / 'payload'
            if args.unsigned:
                shutil.copytree(ARTIFACTS / role.lower(), payload)
            else:
                extract(signed_packages / (role.lower() + '-signed.zip'), payload)
            # Single-file executables embed their dependency manifest; the version
            # resource still carries the build version as UTF-16 text.
            expected = ET.parse(root / 'Directory.Build.props').findtext('.//Version')
            if expected.encode('utf-16-le') not in (payload / ('OpenFsso.' + role + '.exe')).read_bytes():
                raise ValueError('Build and sign the current application version before packaging MSI.')
            service = args.service or ('OpenFssoClient' if role == 'Client' else 'OpenFssoReceiver')
            identity = 'Client' if role == 'Client' else 'Server'
            if service not in ('OpenFssoClient', 'OpenFssoReceiver'):
                identity += '-' + service
            filename = 'OpenFSSO.' + identity + '-' + version + '.msi'
            # Keep the existing product families stable.
            definitions = {
                'Version': version, 'Role': role, 'ServiceName': service, 'Identity': identity,
                'ProductCode': str(uuid.uuid5(namespace, identity + ':' + version)),
                'UpgradeCode': str(uuid.uuid5(namespace, identity)),
            }
            command = ['docker', 'run', '--rm', '--mount', f'type=bind,source={input_directory},target=/input,readonly',
                       '--mount', f'type=bind,source={output},target=/out', 'openfsso-msi-builder:local',
                       filename.removesuffix('.msi')]
            for name, value in definitions.items():
                command.extend(['-d', name + '=' + value])
            subprocess.run(command, check=True)


if __name__ == '__main__':
    main()
