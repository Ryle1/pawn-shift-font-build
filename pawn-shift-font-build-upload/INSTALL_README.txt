PAWN SHIFT Demo - Korean Font Fallback
======================================

Target:
- Unity 6000.3.10f1
- IL2CPP
- BepInEx 6 IL2CPP
- TextMesh Pro

Install:
1. Install BepInEx 6 IL2CPP into PAWN SHIFT and run the game once.
2. Copy:
   BepInEx\plugins\PAWNShift.KoreanFontFallback\
   from the artifact ZIP into the game folder.
3. Put YOUR OWN NanumGothic.ttf in:
   BepInEx\plugins\PAWNShift.KoreanFontFallback\
4. Start the game.

The plugin does NOT edit resources.assets.

Default mode:
- Keeps the game's original font.
- Adds NanumGothic as TMP fallback for Korean glyphs.

If Korean still shows as squares:
Open:
BepInEx\config\com.ryle1.pawnshift.koreanfontfallback.cfg
and set:
ForceReplaceAll = true

Check BepInEx\LogOutput.log for:
- PAWN SHIFT Korean Font Fallback
- Created NanumGothic dynamic TMP runtime font.
- Font pass:
