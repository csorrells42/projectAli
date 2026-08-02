"""Provider-neutral local OpenAI-compatible LLM adapter for Mem0."""

import os

from mem0.llms.openai import OpenAILLM


class LocalOpenAICompatibleLLM(OpenAILLM):
    def generate_response(self, messages, response_format=None, tools=None, tool_choice="auto", **kwargs):
        messages = list(messages)
        params = self._get_supported_params(messages=messages, **kwargs)
        params.update({"model": self.config.model, "messages": messages})
        if response_format:
            params["response_format"] = response_format
        if tools:
            params["tools"] = tools
            params["tool_choice"] = tool_choice
        control = os.environ.get("ALI_MEM0_THINKING_CONTROL", "None")
        enabled = os.environ.get("ALI_MEM0_THINKING_ENABLED", "False").lower() == "true"
        if control == "GptOssReasoningEffort":
            params["extra_body"] = {
                "chat_template_kwargs": {
                    "reasoning_effort": os.environ.get("ALI_MEM0_REASONING_EFFORT", "")
                }
            }
        elif control == "QwenTemplateToggle":
            params["extra_body"] = {
                "chat_template_kwargs": {"enable_thinking": enabled}
            }
        elif control == "GemmaSystemPromptToken" and enabled:
            messages = [dict(message) for message in messages]
            system = next((message for message in messages if message.get("role") == "system"), None)
            if system is None:
                messages.insert(0, {"role": "system", "content": "<|think|>"})
            else:
                system["content"] = "<|think|>\n" + str(system.get("content", ""))
            params["messages"] = messages
        response = self.client.chat.completions.create(**params)
        return self._parse_response(response, tools)
