"""Build output folder: <checkout>/artifacts unless OPENFSSO_ARTIFACTS names another."""
import os
from pathlib import Path

ARTIFACTS = Path(os.environ.get(
    'OPENFSSO_ARTIFACTS', Path(__file__).resolve().parent.parent / 'artifacts'
)).expanduser().resolve()
