"""Run the SDK's canonical C# formatter in Docker."""
import argparse
import shutil
import subprocess
import tempfile
from pathlib import Path

from paths import ARTIFACTS


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true', help='Fail if formatting would change source')
    options = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    if options.check:
        subprocess.run(['docker', 'build', '--target', 'format-check', str(root)], cwd=root.anchor, check=True)
        return
    with tempfile.TemporaryDirectory(prefix='openfsso-format-') as temporary:
        subprocess.run(['docker', 'build', '--target', 'formatted', '--output',
                        'type=local,dest=' + temporary, str(root)], cwd=root.anchor, check=True)
        changed = 0
        for source in Path(temporary).rglob('*.cs'):
            relative = source.relative_to(temporary)
            if relative.parts[0] not in ('src', 'tools', 'tests'):
                raise RuntimeError('Unexpected formatter output')
            target = root / relative
            if not target.is_file():
                raise RuntimeError('Formatter must not create application source')
            if source.read_bytes() != target.read_bytes():
                shutil.copyfile(source, target)
                changed += 1
        artifacts = ARTIFACTS
        artifacts.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(Path(temporary) / 'format-report.json', artifacts / 'format-report.json')
        print(f'Formatted {changed} C# files with dotnet format; verification passed in Docker.')


if __name__ == '__main__':
    main()
