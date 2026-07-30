"""Start Aider with its pinned package group ahead of Ali's shared Python packages."""

from __future__ import annotations

import os
import runpy
import sys


packages = os.environ.get("ALI_AIDER_PACKAGES", "").strip()
if not packages:
    raise RuntimeError("Missing required Ali Aider setting: ALI_AIDER_PACKAGES")

sys.path.insert(0, packages)
runpy.run_module("aider", run_name="__main__")
