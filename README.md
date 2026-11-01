# More Roles Plus

**Among Us をもっと深く、もっとエキサイティングに。**

新たなカスタム役職と戦術を追加する、PC・Android 両対応の多機能MODです。

---

## 🌟 概要 (Overview)

**More Roles Plus** は、人気ゲーム『Among Us』に独自のカスタム役職やルールを追加し、より高度な駆け引きと新しいゲーム体験を提供するMODです。

※こちらのMODはAmongus公式(Innersloth)は全く関係ないのでバグなどの報告をAmongUs公式にしないでください

PC環境（Windows）はもちろんのこと、**Android端末向けの専用ランチャーアプリ**も同梱・サポートしているため、スマートフォンからでも簡単に導入してマルチプレイをお楽しみいただけます。

---

## ✨ 主な特徴 (Features)

* **🎭 多彩なオリジナル役職**
  クルーメイトやインポスターに加えて、独自の勝利条件を持つ第三陣営などの魅力的なカスタム役職を多数追加。
* **📱 PC & Android 完全対応**
  PC（Steam / Epic Games）だけでなく、専用ランチャーアプリを使用することでAndroid端末でも快適に動作します。
* **🚀 簡単セットアップ**
  初心者でも迷うことなくスムーズに導入できるよう、シンプルな手順で設計されています。
* **🔓 オープンソース**
  GPL-3.0 ライセンスに基づき、透明性が高くコミュニティに開かれた開発を行っています。

---

## 📱 動作環境 (Requirements)

| プラットフォーム | 動作要件 / 備考 |
| :--- | :--- |
| **PC (Windows)** | 64-bit Windows / Steam版またはEpic Games版 Among Us |
| **Android** | Android 8.0 以上（専用ランチャー APK を使用） |

---

## 📥 導入手順 (Installation)

### 1. PC (Windows) の場合

1. **ファイルをダウンロード**  
   [Releases](../../releases) ページより、最新バージョンの ZIP ファイルをダウンロードします。
2. **ゲームフォルダに配置**  
   『Among Us』のインストールフォルダ（`Among Us.exe` が存在するディレクトリ）を開き、ダウンロードした ZIP ファイルの中身をすべてそのまま展開・配置します。
3. **ゲームの起動**  
   `Among Us.exe` を起動します。初回起動時はMODの初期化処理のため、立ち上がりまでに少し時間がかかる場合があります。  
   ※タイトル画面に「More Roles Plus」のロゴまたはバージョン表記が表示されていれば導入完了です。

### 2. Android 端末の場合

1. **APK ファイルをダウンロード**  
   [Releases](../../releases) ページより、Android専用ランチャーアプリ（`.apk`）をダウンロードします。
2. **アプリのインストール**  
   ダウンロードした `.apk` ファイルを実行し、端末にインストールします。  
   ※「不明なソースからのアプリ」のインストール許可を求められた場合は、画面の指示に従って許可してください。
3. **起動とセットアップ**  
   インストールされた「More Roles Plus」アプリを起動します。必要なデータやランタイムの展開が自動的に行われますので、完了後ゲームを開始してください。

---

## ❓ よくある質問 (FAQ)

<details>
<summary><b>Q. MODを入れていないプレイヤーと一緒に遊べますか？</b></summary>

> **A.** いいえ。本MODで追加された役職や機能を正しく動作させるため、ロビーに参加するプレイヤー全員が同じバージョンの **More Roles Plus** を導入している必要があります。
</details>

<details>
<summary><b>Q. ゲームが起動しない、またはMODが反映されません。</b></summary>

> **A.** ゲーム本体のバージョンとMODの対応バージョンが一致しているかご確認ください。また、一度ゲームフォルダ内のMOD関連ファイルを削除し、再インストールをお試しください。
</details>

---

## 🤝 クレジット & 謝辞 (Credits)

<details>
<summary><b>クリックしてサードパーティ製ライブラリおよび協力者のクレジット一覧を表示</b></summary>

<br>

本プロジェクトは、オープンソースコミュニティおよび先人たちの素晴らしい成果の上に成り立っています。開発に関わったすべての方々に心より感謝申し上げます。

### 🔹 主要オープンソースプロジェクト
* **FusionCore** ([All-Of-Us-Mods](https://github.com/All-Of-Us-Mods/FusionCore))  
  Android向けランチャーアプリの基盤として利用しています。
* **BepInEx** ([BepInEx Team](https://github.com/BepInEx/BepInEx))  
  Unity/Il2Cpp ゲーム向けのプラグラグインフレームワークおよびローダー。
* **Il2CppInterop** ([BepInEx Team](https://github.com/BepInEx/Il2CppInterop))  
  Il2Cpp C++ コードと C# (.NET) 間の相互運用ライブラリ。

### 🔹 システム・ランタイム・コンポーネント
* **.NET Runtime & Base Class Libraries** ([Microsoft Corporation](https://dotnet.microsoft.com/)) - MIT License
* **OpenSSL** ([The OpenSSL Project](https://www.openssl.org/)) - Apache-2.0 / OpenSSL License
* **Dobby Instruction Relocation Engine** ([jmpews](https://github.com/jmpews/Dobby)) - Apache-2.0 License
* **Android Jetpack / AndroidX Libraries** ([Google LLC](https://developer.android.com/jetpack/androidx)) - Apache-2.0 License
* **Kotlin Standard Library** ([JetBrains s.r.o.](https://kotlinlang.org/)) - Apache-2.0 License
* **Material Components for Android** ([Google LLC](https://github.com/material-components/material-components-android)) - Apache-2.0 License

### 🔹 特別な謝辞
* **End K not**  
  Androidランチャーのカスタマイズおよび挙動実装において非常に参考にさせていただきました。

※ライセンスの全文および各著作権通知の詳細は [THIRD-PARTY-NOTICES.md](./THIRD-PARTY-NOTICES.md) をご覧ください。

</details>

---

## 📄 ライセンス (License)

<details>
<summary><b>クリックしてライセンス情報を表示 (GPL-3.0)</b></summary>

<br>

本プロジェクトは **GNU General Public License v3.0 (GPL-3.0)** の下で公開されています。

```text
More Roles Plus
Copyright (C) 2024 haru03741-glitch

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.
```

詳細なライセンス全文につきましては、リポジトリ内の [LICENSE](./LICENSE) ファイルをご確認ください。

</details>
