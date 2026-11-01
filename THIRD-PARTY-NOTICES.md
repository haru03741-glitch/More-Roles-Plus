# Third-Party Notices / 同梱している第三者ソフトウェア

More Roles Plus itself is licensed under the GNU General Public License v3.0 (see [`LICENSE`](./LICENSE)).
The components listed below are the work of other authors and keep their own licenses.
Source ported from other mods is credited in the [README credits](./README.md#クレジット).

More Roles Plus 本体は GPL-3.0 です ([`LICENSE`](./LICENSE))。以下は別の作者による著作物で、
それぞれのライセンスに従います。他の mod から移植したコードは [README のクレジット](./README.md#クレジット) に載せています。

| Component | Author / Project | License | Where |
|---|---|---|---|
| FusionCore | XtraCube / All-Of-Us-Mods — [FusionCore](https://github.com/All-Of-Us-Mods/FusionCore) | GPL-3.0 | source, `Android/Launcher/` (git subtree, rebranded Android launcher) |
| BepInEx 6.0.0-be.735 | BepInEx team — [BepInEx](https://github.com/BepInEx/BepInEx) | LGPL-2.1 | PC release zip (`BepInEx-MRP/core/`) |
| Unity Doorstop 4.5.1 | NeighTools — [UnityDoorstop](https://github.com/NeighTools/UnityDoorstop) | LGPL-2.1 | PC release zip (`winhttp.dll`, `doorstop_config.ini`) |
| Il2CppInterop 1.5.3 | BepInEx team, knah et al. — [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) | LGPL-3.0 | PC release zip (`BepInEx-MRP/core/`; `Il2CppInterop.Runtime.dll` modified) |
| BepInEx Fusion | All-Of-Us-Mods — [BepInExFusion](https://github.com/All-Of-Us-Mods/BepInExFusion) | LGPL-2.1 | bundled in the launcher APK (`Android/Launcher/fusionApp/src/main/assets/BepInEx-arm64.zip`) |
| Il2CppInterop Fusion | All-Of-Us-Mods — [Il2CppInteropFusion](https://github.com/All-Of-Us-Mods/Il2CppInteropFusion) | LGPL-3.0 | bundled in the launcher APK (same `BepInEx-arm64.zip`; `Il2CppInterop.Runtime.dll` modified) |
| BepInEx core libraries (HarmonyX, MonoMod, Mono.Cecil, Cpp2IL / LibCpp2IL / StableNameDotNet / WasmDisassembler, Disarm, AsmResolver, AssetRipper.CIL / AssetRipper.Primitives, Iced, Capstone.NET, SemanticVersioning, Microsoft.Extensions.*) | BepInEx & Andreas Pardeike — [HarmonyX](https://github.com/BepInEx/HarmonyX); 0x0ade — [MonoMod](https://github.com/MonoMod/MonoMod); Jb Evain — [Cecil](https://github.com/jbevain/cecil); Sam Byass — [Cpp2IL](https://github.com/SamboyCoding/Cpp2IL), [Disarm](https://github.com/SamboyCoding/Disarm); Washi — [AsmResolver](https://github.com/Washi1337/AsmResolver); ds5678 — [AssetRipper.CIL](https://github.com/AssetRipper/AssetRipper.CIL), [AssetRipper.Primitives](https://github.com/AssetRipper/AssetRipper.Primitives); [iced](https://github.com/icedland/iced); Ahmed Garhy — [Capstone.NET](https://github.com/9ee1/Capstone.NET); Adam Reeve — [semver.net](https://github.com/adamreeve/semver.net); Microsoft — [dotnet/runtime](https://github.com/dotnet/runtime) | MIT | PC release zip (`BepInEx-MRP/core/`); bundled in the launcher APK (`core/` inside `BepInEx-arm64.zip`) |
| .NET runtime | Microsoft & .NET Foundation — [dotnet/runtime](https://github.com/dotnet/runtime) | MIT | PC release zip (`dotnet/`); bundled in the launcher APK (`assets/dotnet-arm64.zip`, `jniLibs/arm64-v8a/`; `System.Diagnostics.Process.dll` patched) |
| Dobby (modified) | jmpews — [Dobby](https://github.com/jmpews/Dobby) | Apache-2.0 | PC release zip (`BepInEx-MRP/core/dobby.dll`, unmodified); bundled in the launcher APK (`jniLibs/arm64-v8a/libdobby.so`); changes in [`Android/Launcher/third_party/dobby/`](./Android/Launcher/third_party/dobby/) |
| stb_image | Sean Barrett — [stb](https://github.com/nothings/stb) | MIT or Public Domain (Unlicense) | compiled into the mod's native component (`native/src/third_party/stb_image.h`; the license text is at the end of that file) |
| OpenSSL | The OpenSSL Project Authors — [OpenSSL](https://github.com/openssl/openssl) | Apache-2.0 | bundled in the launcher APK (`jniLibs/arm64-v8a/libcrypto.so`, `libssl.so`) |
| LLVM libunwind | The LLVM Project — [llvm-project](https://github.com/llvm/llvm-project) | Apache-2.0 WITH LLVM-exception | bundled in the launcher APK (`libfusionunwind.so`, built from the Android NDK's `libunwind.a`) |
| AndroidX (annotation, appcompat, browser, coordinatorlayout, core) / Material Components | The Android Open Source Project / Google — [AndroidX](https://github.com/androidx/androidx), [material-components-android](https://github.com/material-components/material-components-android) | Apache-2.0 | bundled in the launcher APK |
| Kotlin standard library | JetBrains — [Kotlin](https://github.com/JetBrains/kotlin) | Apache-2.0 | bundled in the launcher APK |
| Protocol Buffers (protobuf-javalite) | Google — [protobuf](https://github.com/protocolbuffers/protobuf) | BSD-3-Clause | bundled in the launcher APK |

## FusionCore (Android launcher)

The Android launcher in `Android/Launcher/` is a `git subtree` of
[FusionCore](https://github.com/All-Of-Us-Mods/FusionCore) by XtraCube / All-Of-Us-Mods (GPL-3.0), carrying the
launcher changes made in [End K not](https://github.com/waffle-ful/Aeterna-End-K-not) (waffle-ful, GPL-3.0),
with branding and package name changed for More Roles Plus. It stays under GPL-3.0; the full text is in
[`Android/Launcher/LICENSE`](./Android/Launcher/LICENSE) and the tracked upstream commit is recorded in
[`Android/Launcher/UPSTREAM.md`](./Android/Launcher/UPSTREAM.md).

`Android/Launcher/` の Android ランチャーは XtraCube / All-Of-Us-Mods の [FusionCore](https://github.com/All-Of-Us-Mods/FusionCore) (GPL-3.0) を
git subtree として取り込み、End K not で加えられた変更を重ねて、名前とパッケージ名を More Roles Plus 向けに変えたものです。

## BepInEx / Unity Doorstop (LGPL-2.1) / Il2CppInterop (LGPL-3.0) — PC release zip

The PC release zip contains [BepInEx](https://github.com/BepInEx/BepInEx) 6.0.0-be.735 (the official
`BepInEx-Unity.IL2CPP-win-x64` build, placed in `BepInEx-MRP/` instead of `BepInEx/` so that it does not share
plugins with other mods), [Unity Doorstop](https://github.com/NeighTools/UnityDoorstop) 4.5.1 and
[Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) 1.5.3. BepInEx and Unity Doorstop are unmodified
(`doorstop_config.ini` and `BepInEx.cfg` are configuration files adjusted for More Roles Plus).
`Il2CppInterop.Runtime.dll` / `Il2CppInterop.Common.dll` are the 1.5.3 build distributed with
[Endless Host Roles](https://github.com/Gurge44/EndlessHostRoles/commit/c2e0b297e), kept unmodified in
[`packaging/il2cppinterop/`](./packaging/il2cppinterop/). `Il2CppInterop.Runtime.dll` is then modified so that its
reference stores go through the game's GC write barrier (`IL2CPP.FieldWriteWbarrierStub`,
`Il2CppReferenceArray<T>.StoreValue` and the `Il2CppStringArray` indexer setter; adds `IL2CPP.WbStore`).
The modified file is reproduced from the unmodified one by [`tools/InteropWbPatch`](./tools/InteropWbPatch/).
The full license texts are in [`licenses/LGPL-2.1.txt`](./licenses/LGPL-2.1.txt) and
[`licenses/LGPL-3.0.txt`](./licenses/LGPL-3.0.txt), and are included in the release zip.

PC 版の配布 zip には BepInEx 6.0.0-be.735 (公式のビルド。ほかの mod と plugins を共有しないよう `BepInEx/` でなく
`BepInEx-MRP/` に置いています)・Unity Doorstop 4.5.1・Il2CppInterop 1.5.3 が入っています。BepInEx と Unity Doorstop は
無改変です (`doorstop_config.ini` と `BepInEx.cfg` は設定ファイルのため More Roles Plus 向けに調整しています)。
Il2CppInterop は Endless Host Roles の配布物に含まれる 1.5.3 のビルド (`packaging/il2cppinterop/` に無改変のまま置いています) を使い、
`Il2CppInterop.Runtime.dll` だけ参照の書き込みが GC の書き込みバリアを通るように書き換えています
(元のファイルから `tools/InteropWbPatch` で作り直せます)。ライセンス全文は `licenses/` にあり、配布 zip にも入っています。

## BepInEx Fusion (LGPL-2.1) / Il2CppInterop Fusion (LGPL-3.0)

[BepInExFusion](https://github.com/All-Of-Us-Mods/BepInExFusion) and
[Il2CppInteropFusion](https://github.com/All-Of-Us-Mods/Il2CppInteropFusion) (All-Of-Us-Mods) are bundled
inside `BepInEx-arm64.zip` in the launcher APK (`BepInEx.cfg` settings are adjusted) and
extracted on the device at first start. `core/Il2CppInterop.Runtime.dll` is modified so that its reference
stores go through the game's GC write barrier (`IL2CPP.FieldWriteWbarrierStub`,
`Il2CppReferenceArray<T>.StoreValue`, the `Il2CppStringArray` indexer setter and the `Set` methods of
`Il2CppReferenceField<T>` / `Il2CppStringField`; adds `IL2CPP.WbStore`). The modification is reproduced from the
unmodified file by [`tools/InteropWbPatch`](./tools/InteropWbPatch/). The other files are unmodified.
The MonoMod libraries in the same zip are covered below. The full license texts are in [`licenses/`](./licenses/).

ランチャーの APK に同梱し、初回起動時に端末へ展開しています。`Il2CppInterop.Runtime.dll` だけは参照の書き込みが GC の書き込みバリアを通るように書き換えています (元のファイルから `tools/InteropWbPatch` で作り直せます)。ほかは無改変です (同じ zip の MonoMod は下の MIT 節を参照)。

## MIT components (.NET runtime, BepInEx core libraries)

The .NET runtime and the BepInEx core libraries listed above are included in the PC release zip (unmodified) and
bundled inside the launcher APK under the MIT License, with these copyright notices. In the APK they are unmodified except that `System.Diagnostics.Process.dll`
is patched, and `MonoMod.Core`, `MonoMod.Utils`, `MonoMod.RuntimeDetour` and `MonoMod.Iced` are the builds
shipped in FusionCore with `MonoMod.Core` patched (see [`Android/Launcher/UPSTREAM.md`](./Android/Launcher/UPSTREAM.md)).

```
.NET runtime / Microsoft.Extensions
                    Copyright (c) .NET Foundation and Contributors
HarmonyX            Copyright (c) 2020 BepInEx
                    Copyright (c) 2017 Andreas Pardeike (Harmony)
MonoMod             Copyright (c) 2015 - 2020 0x0ade
Mono.Cecil          Copyright (c) 2008 - 2015 Jb Evain
                    Copyright (c) 2008 - 2011 Novell, Inc.
AsmResolver         Copyright (c) 2016-2026 Washi
Cpp2IL / LibCpp2IL / StableNameDotNet / WasmDisassembler
                    Copyright (c) 2020 Sam Byass
Disarm              Copyright (c) 2025 Sam Byass
AssetRipper.CIL     Copyright (c) 2024 ds5678
AssetRipper.Primitives
                    Copyright (c) 2022 ds5678
Iced                Copyright (C) 2018-present iced project and contributors
Capstone.NET        Copyright (c) Ahmed Garhy
SemanticVersioning  Copyright (c) Adam Reeve
```

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Apache-2.0 components (Dobby, OpenSSL, Android libraries)

`libdobby.so`, `libcrypto.so`, `libssl.so`, `libfusionunwind.so`, AndroidX, Material Components and the Kotlin standard library
are licensed under the Apache License 2.0. The full text is in [`licenses/Apache-2.0.txt`](./licenses/Apache-2.0.txt).

- Dobby — Copyright jmpews. `libdobby.so` is built from commit `f4643b8d` with
  [`code-patch-end-page.patch`](./Android/Launcher/third_party/dobby/code-patch-end-page.patch) applied;
  build steps are in [`Android/Launcher/third_party/dobby/README.md`](./Android/Launcher/third_party/dobby/README.md).
- OpenSSL — Copyright (c) 1998-2025 The OpenSSL Project Authors.
- LLVM libunwind (`libfusionunwind.so`) — Copyright (c) LLVM Project contributors, Apache-2.0 with LLVM
  Exceptions. Built from the unwinder archive in the Android NDK (r28) with its `_Unwind_*` symbols exported
  (`Android/Launcher/fusionApp/src/main/jni/unwind/`).
- AndroidX / Material Components — Copyright The Android Open Source Project / Google LLC.
- Kotlin — Copyright JetBrains s.r.o. and Kotlin Programming Language contributors.

## Protocol Buffers (BSD-3-Clause)

```
Copyright 2008 Google Inc.  All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

    * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
    * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
    * Neither the name of Google Inc. nor the names of its
contributors may be used to endorse or promote products derived from
this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```
