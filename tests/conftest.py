"""Pytest bootstrap.

Runs before test modules are imported. We blank env vars that would otherwise
cause `app.main` to try to open privileged paths (/data/beast.jsonl) or to
connect to a real BEAST source at import time.
"""

import os

os.environ["BEAST_OUTFILE"] = ""
os.environ["BEAST_STDOUT"] = "0"
