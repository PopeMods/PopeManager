# Bundled components

- FusionCore, GPL-3.0: https://github.com/All-Of-Us-Mods/FusionCore
  Source base bf4c69d9d4724e4eff058664a5be0a1d9cfd4e61 plus the local Quest patch.
  Corresponding patch/source instructions: `E:\PopeMods\FusionCore-Contribution`.
- BepInExFusion, LGPL-2.1: https://github.com/All-Of-Us-Mods/BepInExFusion
  Generator references are extracted from the bundled FusionCore APK, not downloaded as
  unrelated binaries. Its payload contains Cpp2IL, Il2CppInterop, MonoMod, AsmResolver and
  other dependencies; retain their respective licenses when redistributing.
- Il2CppInterop fork: https://github.com/All-Of-Us-Mods/Il2CppInterop
- Cpp2IL: https://github.com/SamboyCoding/Cpp2IL
- Qt Bridge: https://github.com/qt/qtbridge-csharp and the sample's NuGet package license.
- ADB: the supplied sample's binaries; Android SDK platform-tools notices apply.

This local development bundle is not a complete redistribution/license package. Before
publishing binaries, include all applicable dependency notices and corresponding source
or source offers required by their licenses. The local patch and build instructions are
included to make the modified FusionCore source reviewable and reproducible.


## DepotDownloader and Steam login

Bundled Windows and Linux DepotDownloader binaries: https://github.com/SteamRE/DepotDownloader
(GPL-2.0; licenses shipped alongside each binary). Steam Frame downloads use app 1533390,
depot 1533392, and the current public manifest resolved by DepotDownloader.
The Steam login layout and QR PNG renderer are adapted from the user's GTCLauncher project.
GTCDistLib is not included in that source folder, so PopeManager reads DepotDownloader's
QR output directly. QR challenges are held only in memory and excluded from logs.
