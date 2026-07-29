---
name: office-artifact-delivery
description: Turn user material into useful office artifacts such as correspondence, reports, tables, charts, Mermaid diagrams, documents, spreadsheets, presentations, and PDFs with an explicit quality check.
license: MIT
---

# Office artifact delivery

1. Identify the audience, decision, source material, and requested output format.
2. Inspect the live tool inventory and use only registered artifact and file capabilities.
3. Organize the deliverable around the answer or action, not around the creation process.
4. Preserve source facts and label assumptions, estimates, and inferred values.
5. Save new artifacts in the requested approved location, defaulting to Exports when none is given.
6. Validate structure, calculations, links, file existence, and visual readability using available tools.
7. Return the exact artifact path and a concise description of what was verified.

For office correspondence, match the user's relationship and tone, state the requested action early, preserve names and dates exactly, and never claim that a message was sent unless a registered email tool succeeded.

For Office or other binary documents, use binary-safe copy, metadata/hash, and archive tools. Never read or rewrite a binary file with a text-file operation. Use the indexed local-document tools for supported document interpretation and preserve the original file unless the user explicitly approves a change.

Never claim an artifact was created or visually reviewed without tool evidence. If a required renderer is unavailable, produce the best supported intermediate format and say exactly what remains.
