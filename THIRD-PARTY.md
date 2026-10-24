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
