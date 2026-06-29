# Ali Audio Setup Source Index

Purpose: give Ali source-backed reference material for the audio kit Chris plans to ship with the AI computer. This is not a new feature; it is a curated source set for the existing approved source system.

## Gear

- Focusrite Scarlett Solo or Scarlett 2i2 audio interface.
- Audio-Technica AT2040 dynamic XLR microphone.
- FetHead inline microphone preamp.
- Shure low-profile boom arm.
- One XLR cable.

## Curated Source Catalog

Repo catalog:

```text
docs\source-catalogs\ali_audio_setup_sources.json
```

Live Ali catalog location:

```text
%LOCALAPPDATA%\Ali\BootstrapData\Sources\curated_sources.json
```

Installer/dependency pass should merge or copy the catalog entries into the live curated source catalog so Ali can retrieve them when Chris asks audio setup questions.

## Official Sources Included

- Focusrite Scarlett Solo 4th Gen downloads and user guide:
  `https://downloads.focusrite.com/focusrite/scarlett-4th-gen/scarlett-solo-4th-gen`
- Focusrite Scarlett 2i2 4th Gen downloads and user guide:
  `https://downloads.focusrite.com/focusrite/scarlett-4th-gen/scarlett-2i2-4th-gen`
- Audio-Technica AT2040 official product page:
  `https://www.audio-technica.com/en-us/at2040`
- TritonAudio FetHead official product page:
  `https://tritonaudio.com/product/fethead/`
- Shure Gator Low Profile Boom Arm SH-BROADCAST2 official product page:
  `https://www.shure.com/en-US/products/accessories/gator-broadcast2-boom?variant=SH-BROADCAST2`

## Setup Guidance Boundary

Ali may use these sources for general setup questions such as:

- Which interface input to use.
- Whether phantom power is needed.
- What the FetHead does in the chain.
- Basic gain-staging concepts.
- Why clipping, low level, noise, or no signal may happen.
- Where to get the Focusrite driver/control software.

Ali should not claim one universal gain knob position. Good gain depends on voice level, mic distance, room noise, interface model/generation, Windows input settings, and whether the FetHead is in the chain.

Recommended answer style:

1. Identify the exact hardware model and generation.
2. Check the official source if the question depends on a manual/spec.
3. Explain the signal chain: microphone -> XLR -> FetHead if used -> interface input -> USB -> Windows/Ali.
4. Give a safe setup range or procedure, not a magic number.
5. Ask Chris to speak at real use volume and watch for clipping/noise.
6. For the Shure boom arm, use the SH-BROADCAST2 source before giving mounting, clearance, or compatibility guidance.

## Important Notes

- The AT2040 is a dynamic XLR microphone. It does not use phantom power for the microphone itself.
- The FetHead uses phantom power from the interface to power the inline preamp while passing signal from a dynamic microphone.
- If the FetHead is used, phantom power is normally enabled on the interface for the FetHead. If the FetHead is not used, phantom power is normally not needed for the AT2040.
- Scarlett Solo and Scarlett 2i2 setup details can differ by generation. Prefer the exact generation source before giving port/button/software instructions.
- The Shure boom arm model is Gator Low Profile Boom Arm SH-BROADCAST2. Official source-backed notes: desk clamp supports surfaces up to 2.17 in / 55 mm, a direct drill mount is included, fully extended reach is up to 33 in / 838 mm, rated capacity is up to 4.4 lb / 2 kg, horizontal adjustment is 360 degrees, upper vertical adjustment is 90 degrees, and magnetic cable channels support cable management.
