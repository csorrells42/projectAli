"""Mem0 LLM provider that preserves Lemonade's GPT-OSS reasoning mapping."""

import os

from mem0.llms.openai import OpenAILLM


class LemonadeLLM(OpenAILLM):
    """OpenAI-compatible Mem0 provider with Lemonade chat-template arguments."""

    def generate_response(
        self,
        messages,
        response_format=None,
        tools=None,
        tool_choice="auto",
        **kwargs,
    ):
        params = self._get_supported_params(messages=messages, **kwargs)
        params.update({"model": self.config.model, "messages": messages})
        if response_format:
            params["response_format"] = response_format
        if tools:
            params["tools"] = tools
            params["tool_choice"] = tool_choice

        effort = self.config.reasoning_effort or "low"
        params["extra_body"] = {
            "chat_template_kwargs": {"reasoning_effort": effort}
        }
        response = self.client.chat.completions.create(**params)
        parsed = self._parse_response(response, tools)
        if self.config.response_callback:
            self.config.response_callback(self, response, params)
        return parsed


def assert_local_configuration(config):
    """Reject any configuration that could silently use a remote provider."""
    allowed = ("http://127.0.0.1:", "http://localhost:")
    endpoints = [
        config["llm"]["config"]["openai_base_url"],
        config["embedder"]["config"]["openai_base_url"],
    ]
    if not all(str(endpoint).lower().startswith(allowed) for endpoint in endpoints):
        raise RuntimeError("Mem0 LLM and embedding endpoints must be loopback HTTP endpoints.")
    vector = config["vector_store"]["config"]
    if str(vector.get("host", "")).lower() not in {"127.0.0.1", "localhost"}:
        raise RuntimeError("Mem0 Qdrant must use a loopback host.")
    if os.environ.get("MEM0_TELEMETRY", "").lower() not in {"false", "0", "no"}:
        raise RuntimeError("MEM0_TELEMETRY must be disabled before importing Mem0.")
