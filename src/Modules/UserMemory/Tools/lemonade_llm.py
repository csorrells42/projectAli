"""Lemonade provider adapter for Mem0 without modifying the pinned package."""

from mem0.llms.openai import OpenAILLM


class LemonadeLLM(OpenAILLM):
    def generate_response(self, messages, response_format=None, tools=None, tool_choice="auto", **kwargs):
        params = self._get_supported_params(messages=messages, **kwargs)
        params.update({"model": self.config.model, "messages": messages})
        if response_format:
            params["response_format"] = response_format
        if tools:
            params["tools"] = tools
            params["tool_choice"] = tool_choice
        params["extra_body"] = {
            "chat_template_kwargs": {
                "reasoning_effort": self.config.reasoning_effort or "low"
            }
        }
        response = self.client.chat.completions.create(**params)
        return self._parse_response(response, tools)
