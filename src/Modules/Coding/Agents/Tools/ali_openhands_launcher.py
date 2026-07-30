"""Configure OpenHands from Ali's active local runtime, then start its CLI."""

from __future__ import annotations

import os
import sys
from pathlib import Path


def required(name: str) -> str:
    value = os.environ.get(name, "").strip()
    if not value:
        raise RuntimeError(f"Missing required Ali OpenHands setting: {name}")
    return value


persistence_root = Path.home() / ".local" / "state" / "ali-openhands"
persistence_root.mkdir(parents=True, exist_ok=True)
os.environ["OPENHANDS_PERSISTENCE_DIR"] = str(persistence_root)
os.environ.setdefault("OPENHANDS_SUPPRESS_BANNER", "1")

from openhands.sdk import LLM  # noqa: E402
from openhands_cli.entrypoint import main  # noqa: E402
from openhands_cli.stores.agent_store import AgentStore  # noqa: E402
from openhands_cli.utils import get_default_cli_agent  # noqa: E402


llm_settings: dict[str, object] = {
    "model": required("ALI_OPENHANDS_MODEL"),
    "api_key": required("ALI_OPENHANDS_API_KEY"),
    "base_url": required("ALI_OPENHANDS_BASE_URL"),
    "usage_id": "agent",
    "max_input_tokens": int(required("ALI_OPENHANDS_CONTEXT_TOKENS")),
    "max_output_tokens": int(required("ALI_OPENHANDS_MAX_OUTPUT_TOKENS")),
    "temperature": float(required("ALI_OPENHANDS_TEMPERATURE")),
    "reasoning_effort": None,
    "litellm_extra_body": {
        "chat_template_kwargs": {
            "reasoning_effort": required("ALI_OPENHANDS_REASONING_EFFORT")
        }
    },
    "timeout": None,
}

top_p = os.environ.get("ALI_OPENHANDS_TOP_P", "").strip()
if top_p:
    llm_settings["top_p"] = float(top_p)

agent = get_default_cli_agent(LLM(**llm_settings))
AgentStore().save(agent)

sys.argv[0] = "openhands"
raise SystemExit(main())
