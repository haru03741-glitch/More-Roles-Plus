# More Roles Plus Launcher (Android)

Android launcher that starts Among Us with the More Roles Plus mod loaded through BepInEx Fusion.
It is a rebranded build of [FusionCore](https://github.com/All-Of-Us-Mods/FusionCore) by
All-Of-Us-Mods, used under the GNU General Public License v3.0 (see [`LICENSE`](./LICENSE) and
[`UPSTREAM.md`](./UPSTREAM.md) for how upstream is tracked), and carries the launcher changes made in
[End K not](https://github.com/waffle-ful/Aeterna-End-K-not) (waffle-ful, GPL-3.0).

This mod is not affiliated with Among Us or Innersloth LLC, and the content contained therein is not endorsed or otherwise sponsored by Innersloth LLC. Portions of the materials contained herein are property of Innersloth LLC. © Innersloth LLC.
Use at your own risk; Among Us must already be installed on the device from an official store. This launcher does not ship the game.

## What it does

- Finds the installed Among Us package (`com.innersloth.spacemafia`) and launches it with the
  modding runtime injected. If it is installed, launch starts automatically after a short delay;
  tap the card to launch immediately, or open the game settings first (shows the launch
  activity in use). The game's own Unity runtime is used as-is.
- Stores BepInEx files in the launcher's private storage under `files/<package name>/`.

## Requirements

- Android 8.1 (API level 27) or newer, arm64-v8a.
- "All files access" permission (requested on first start) so BepInEx files can be written.

## Build

Requirements: JDK 17, Android SDK with platform 37 and NDK `28.2.13676358`, CMake 3.22.1.

1. Create `local.properties` next to this file (it is ignored by git):

   ```properties
   sdk.dir=C:/path/to/Android/Sdk
   ```

2. Build the debug APK with the Gradle wrapper from this directory:

   ```powershell
   .\gradlew.bat :fusionApp:assembleDebug
   ```

   The APK is written to `fusionApp/build/outputs/apk/debug/`.

The mod DLL is bundled from `build-android/apk-assets/` at the repository root, which
`dotnet build MoreRolesPlus.csproj -c Android` fills. Without it the APK ships without a bundled plugin.

Release builds are signed only when `MRP_KEYSTORE_PATH`, `MRP_KEYSTORE_PASSWORD`, `MRP_KEY_ALIAS` and
`MRP_KEY_PASSWORD` are set in the environment; otherwise the release APK is left unsigned.

## Credits

- [End K not](https://github.com/waffle-ful/Aeterna-End-K-not) (waffle-ful, GPL-3.0): the launcher changes on top of
  FusionCore (bundled mod and interop install, crash and hang records, log sharing, itch.io sign-in, launcher UI).
- [FusionCore](https://github.com/All-Of-Us-Mods/FusionCore), [BepInExFusion](https://github.com/All-Of-Us-Mods/BepInExFusion)
  and [Il2CppInteropFusion](https://github.com/All-Of-Us-Mods/Il2CppInteropFusion) by All-Of-Us-Mods.
- Third-party licenses are listed in the repository root [`THIRD-PARTY-NOTICES.md`](../../THIRD-PARTY-NOTICES.md).
