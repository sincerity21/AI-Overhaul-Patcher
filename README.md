# Personal fork of Excinerus's AI-Overhaul-Patcher
A fork of said patcher with personal tweaks and fixes with the intention of it being used alongside my AI Overhaul SSE SkyPatcher Patch: https://www.nexusmods.com/skyrimspecialedition/mods/138722

You can download the .synth file from the modpage linked above.

The changes include:
- Will now forward 3 AI Datas covered by AI Overhaul.esp; Confidence, Aggression, Assistance
- Will now forward Object Bounds of NPCs from AI Overhaul.esp
- Will now forward Combat Style of NPCs from AI Overhaul.esp
- Updated to support AI Overhaul SSE's latest version (1.9.5), the split plugins update (USSEP Patch, Fishing Addon)
- Quest reference-alias ALPC patching (`QuestAlpcPatch`): `Mg01Only` restores MG01 alias 11 packages (Faralda / College conflict); `All` forwards every AIO quest ALPC delta vs master using the same package merge rules as NPCs

(New) Settings:


- QuestAlpcPatch

		Default = Off

		`Off` — no quest ALPC patching. 
		`Mg01Only` — patch MG01 alias 11 only (College Of Winterhold conflict). 
		`All` — patch every quest reference alias where AIO changed ALPC vs master.


Vibe-coded using Cursor.




# THE ORIGINAL DESCRIPTION:
# AI-Overhaul-Patcher
A Synthethis patcher for AI Overhaul SE https://www.nexusmods.com/skyrimspecialedition/mods/21654
- Forwards Packages from AIO, keeps packages added by later loaded mods that were not removed by USEEP or AIO
- Forwards observe and combat package lists from AIO
- Adds AIO added factions to the latest winning override set of factions
- Forwards the maximum Essential/Protected status present at AIO or later loaded plugins
- Forwards minimum confidence level present at AIO or later loaded plugins
- Uses the latest loaded outfits that were not overwritten or removed by AIO or USEEP

Get Synthesis https://github.com/Mutagen-Modding/Synthesis/wiki/Installation

Settings :
- Ignore Identical To LastOverride

		Default = false
	
		When enabled the patcher will not override NPCs that are already patched.
- IgnorePlayerRecord

		Default = true

		When enabled the patcher will ignore the player record (00000007).
- MaintainHighestProtectionLevel

		Default = true
	
		When enabled NPCs that are set to Essential or Protected by other mods will maintain the highest protection level.
