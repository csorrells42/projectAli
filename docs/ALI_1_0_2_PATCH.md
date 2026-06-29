# Ali 1.0.2 Patch

Ali 1.0.2 is a focused field patch for installed machines where voice was repaired but the curated Sources & Topics catalog stayed incomplete.

## Fixed

- Setup repair now explicitly repairs the installed `BootstrapData\Sources\curated_sources.json` catalog.
- Missing built-in starter sources are merged into an existing catalog without deleting owner-added sources.
- If the existing source catalog is invalid JSON, setup backs it up and reseeds the starter catalog.
- Fresh install exports and repair patch exports now carry the 1.0.2 setup executable.

## Not Changed

- Runtime model defaults and owner-selected model settings are not changed by this patch.
- Chats, memories, reminders, app settings, installed voices, and user-added valid sources are preserved.
