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

1. **インストーラーをダウンロード**  
   [Releases](../../releases) ページより、最新バージョンの `MoreRolesPlusInstaller.exe` をダウンロードします。
2. **インストール**  
   インストーラーを起動すると、Steam 版と Epic Games 版の Among Us を自動で探します。入れるゲームを選んで「インストール」を押してください。ほかの BepInEx 製 MOD が入っていた場合、その MOD はゲームのフォルダの `MRP_Backups` に退避され、あとでインストーラーから戻せます。  
   ※手で入れる場合は、同じ中身の ZIP（`MoreRolesPlus-vX.Y.Z.zip`）を、Among Us のインストールフォルダ（`Among Us.exe` があるフォルダ）にすべて展開します。ほかの BepInEx 製 MOD が入っているフォルダでは、そちらの BepInEx が上書きされるので、先に退避してください。
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
4. **itch.io へのログイン（推奨）**  
   アプリの「itch.io」の欄からログインすると、自分のアカウントで遊べます。ログインしない場合はゲスト扱いになり、公式サーバーではクイックチャットしか使えず、フリーチャットの部屋にも入れません。

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

## 🛠️ ビルド (Build)

<details>
<summary><b>クリックして開発者向けのビルド手順を表示</b></summary>
<br>

役職や設定の足し方は [CONTRIBUTING.md](CONTRIBUTING.md) にあります。

`local.props.example` を `local.props` にコピーし、`AmongUsPath` を自分のゲームの場所に合わせます。

```
dotnet build MoreRolesPlus.csproj -c Release                       # → <AmongUs>/BepInEx-MRP/plugins
dotnet build patcher/MoreRolesPlus.BootAccel.csproj -c Release     # → <AmongUs>/BepInEx-MRP/patchers (起動の高速化)
```

地形と水の重い計算は C++ の部品 (`native/src`) にあります。部品は `MoreRolesPlus.dll` の中に埋め込まれ、起動時に `BepInEx-MRP/cache` へ書き出して読み込まれます。本体のビルドは `native/src` が変わっていれば先に部品を組み直します (Visual Studio の C++ ビルドツールが要ります)。部品だけを組む時は次のとおりです。

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools/build-native.ps1 -SkipAndroid   # → build/native/win-x64/MrpNative.dll
```

配る時は `MoreRolesPlus.dll` を 1 つ plugins に入れるだけです。

</details>

---

## 🤝 クレジット & 謝辞 (Credits)

<details>
<summary><b>クリックしてサードパーティ製ライブラリおよび協力者のクレジット一覧を表示</b></summary>

<br>

本プロジェクトは、オープンソースコミュニティおよび先人たちの素晴らしい成果の上に成り立っています。開発に関わったすべての方々に心より感謝申し上げます。

### 🔹 移植・参考にした Mod

* **[Nebula on the Ship](https://github.com/Dolly1016/Nebula-Public) (Dolly1016、GPL-3.0)**  
  incremental GC を実行時に切る仕組み、演出中に GC を先に回す考え方、mod の通信を 1 つの番号にまとめて名前で振り分ける仕組み、試合の出来事を寿命付きで受け取る仕組み、ダミープレイヤーを出す手順、自分で作った色を人ごとの色の表で全員に見せる考え方、会議中の地図に自分だけのメモ (人の印と線) を書く機能、本編の地図の上に層を作って押し・なぞりを受ける仕組み (フォートレスメイカーの設計図)、スクリーンショットのキー、本編の絵で組む自前の画面の部品 (窓・スクロールする欄・ボタン)、役職のカードの一覧と細かい頁の設定画面、役職辞典の窓と配役の見込み、コメット・ドッペルゲンガー・スカーレット・妖狐の役職、ホスフォラスとユビキタスの役職 (SNR を経由して取り込み)
* **[ExtremeRoles](https://github.com/yukieiji/ExtremeRoles) (yukieiji、GPL-3.0)**  
  ダミープレイヤーを出す手順、設定の項目の名前が変わっても前の値を引き継ぐ仕組み
* **[Super New Roles](https://github.com/ykundesu/SuperNewRoles) (ykundesu、GPL-3.0)**  
  試合の出来事を配る仕組み (配っている最中の受け手の追加・削除の扱い)、公式サーバーに全員導入の mod として部屋を登録する方法、機器 (アドミン・カメラ・バイタル) を使えなくする方法、前の試合で外れた人ほど役職やインポスターに当たりやすくする配り方、Airship 以外のマップでも Airship の湧き場所選びの画面を出す方法、部屋を立てたらコードをコピーする機能、役職ごとに一緒に出ない役職を選ぶ設定、SNR から移した役職 (シェリフ・マッドメイト・メイヤー・霊媒師・ベイティング・ジャッカルとサイドキック・ヴァルチャー・アーソニスト・ナイスゲッサーとイビルゲッサー・カモフラージャー・突き落と師・ロケットランチャー・リモコン・ユビキタス・ホスフォラス・トリガーハッピー・マッシュルーマー・ラバーズ、ゲッサーの役職を選ぶ画面の作り)。移した役職の能力ボタンの絵 (`Resources/Icons/snr_*.png`) は MRP で作り直した物です
* **[Reactor](https://github.com/NuclearPowered/Reactor) (NuclearPowered、LGPL-3.0)**  
  公式サーバーかどうかの判定 (接続先のアドレスで見る)
* **[Town of Us Mira](https://github.com/AU-Avengers/TOU-Mira) (AU-Avengers、GPL-3.0)**  
  試合の途中で本編の役職を付け直す方法、試合の終わりから自動でロビーへ戻る機能 ([AutoRejoin](https://github.com/Maxi0fc/AutoRejoin) 由来)、危ない mod の一覧 (入ってきた人の mod を照らし合わせる)、会議のスキップの札を無くす設定、本編の保存ファイルを mod 用に分ける方法、クールダウンの残り 10 秒からの小数の表示、本編の細かい調整 (医務室のスキャンを同時にする・スキャンの前に台へ歩かない・作業中にクールダウンを止める・見えない所のベントの出入りを隠す・ほかの人のペットの見え方)
* **[Town of Us Reactivated](https://github.com/eDonnes124/Town-Of-Us-R) (eDonnes124、GPL-3.0)**  
  名前の横にタスクの進み具合を出す機能
* **[End K not](https://github.com/waffle-ful/Aeterna-End-K-not) (waffle-ful、GPL-3.0)**  
  起動の高速化 (`patcher/` のほぼ全体・パッチ適用の高速化)、GC の先回しの実装 (ゲーム側の GC も演出中に回す)、設定画面のフォントと検索の一致判定、起動直後のアカウント表示の停止、開発用の道具 (実機テストの操作口・重さの測り方・起動の時間ログ・AssetBundle の焼き方・Android で使えない API の照合)、Android の起動アプリへの改良 (mod と interop の同梱・落ちた時と固まった時の記録・ログの共有・itch.io でのログイン)、Android 版の実行基盤に手を入れる道具 (`tools/AndroidDotnetPatch`・`tools/InteropWbPatch`)、演出用の部品 (毎フレームの算術・プレイヤーの色で塗る手・シェーダの AssetBundle を読む仕組み)、ポータルの見た目 (行き先の景色を映す窓と床の装置)、更新ボタンのリリースの読み方 (プレリリースも含めて最新の 1 件を読む)、タグの付いたリリースに DLL を添付して配る流れ、コンソール窓を触ってもゲームが固まらないようにする仕組み (書き込みを別のスレッドへ移す・選択を自動で外す)、毎フレーム書き換える文字に、作り置きの文字列を渡す仕組み。次の機能は End K not を経由して取り込んでいます: 公式サーバーに mod の部屋として名乗る方法、公式サーバーかどうかの判定、マウスのホイールで視野を広げる機能、ホストのキー操作、チャットのコマンドの補完と貼り付け、端末ごとの設定を本編の設定画面に並べる方法、設定画面にタブを足す方法、設定を見る画面の並び
* **[Endless Host Roles](https://github.com/Gurge44/EndlessHostRoles) (Gurge44、GPL-3.0) / [TownOfHost-K](https://github.com/KYMario/TownOfHost-K) (KYMario、GPL-3.0) / [Town Of Host](https://github.com/tukasa0001/TownOfHost) (tukasa0001、GPL-3.0)**  
  ロゴ演出の短縮、設定画面にタブを足す方法、公式サーバーに mod の部屋として名乗る方法、BAN 一覧とホワイトリスト (入室時の照合・BAN した人の記録)、チャットの荒らし対策 (禁止語・開始の催促)、設定画面の検索欄 (チャットの入力欄を複製する方法)、設定を見る画面の並び、ホストのキー操作 (廃村・会議・開始の数えを飛ばす・ロビーで Enter で開始)、チャットコマンドの種類と名前、マウスのホイールで視野を広げる機能、チャットのコマンドの補完と貼り付け、チャットで前に送った文を呼び戻す機能、開始の数えの長さと取りやめ、VIP 一覧、端末ごとの設定を本編の設定画面に並べる方法、チャットの暗い色、試合の終わりの画面に全員の役職を並べる機能、ログをまとめて保存するコマンド、試合中の放置の検知、試合の終わりから自動でロビーへ戻る機能と切れた部屋へ入り直す仕組み ([AutoRejoin](https://github.com/Maxi0fc/AutoRejoin) 由来)、ゲッサーの役職を選ぶ画面 (会議の上に出す小さい端末・陣営のタブ・選んでからもう一度押して決める)、メインメニューの更新ボタン (リリースの DLL をダウンロードし、今の DLL を .bak に改名して置き換え、次の起動で消す方法・本編の確認窓に 2 つのボタンを並べる方法)、耳打ち、観戦、ポータルメーカーの動き (2 つ置くと近づいた人が行き来する)、文のファイル (言語ごとの .dat) を書き換えてゲームの文を変える仕組み、部屋の人数ごとにインポスターと第三陣営の数を決める機能、試合のルール (ランダムスポーンの部屋の座標・機器を使えなくする機能・タスクの無効化・ジップラインの制限・はしごからの落下・Airship の動く床を止める機能・会議の後のドアのリセット・Airship の電気室の壁・停電の配電盤の制限とスイッチの判定・サボタージュの無効化とクールダウン・ランダムマップ・マップごとのキル待ちと歩く速さ・会議の無効・タスク勝利の無効・全員ベント)
* **[CrowdedMod](https://github.com/CrowdedMods/CrowdedMod) (andry08 & CrowdedMods、MIT・ライセンス文は [licenses/CrowdedMod-MIT.txt](licenses/CrowdedMod-MIT.txt)) / [NikoCat233 の CrowdedMod](https://github.com/NikoCat233/CrowdedMod) (Reactor 無しで動く形、MIT) / [Endless Host Roles](https://github.com/Gurge44/EndlessHostRoles) (Gurge44、GPL-3.0)**  
  15 人を超える部屋 (部屋の作成画面の定員、会議の名札・変身の相手選び・バイタルのページ送り、Mira HQ のドアのログ)
* **[The Other Roles](https://github.com/TheOtherRolesAU/TheOtherRoles) (TheOtherRolesAU、GPL-3.0)**  
  試合の終わりの画面に全員の役職を並べる機能、試合中に F1 で設定を見る機能、会議の名札にボタンを足す方法、ゲッサーの役職を選ぶ画面 (会議の名札の部品で役職のボタンを並べる方法)、16 人以上の部屋で本編の色の表を伸ばす方法、ポータルメーカーの役職 (発想は K3ndo、作りは Gendelo と Mallöris)、会議でスキップできなくする設定と投票しなかった人を自分への票にする設定、幽霊の時に見る情報を端末ごとに切る設定、死んだインポスターのサボタージュを止める設定、前の試合の最初の犠牲者に付ける盾、停電中の監視カメラの暗視、ライターの役職。霊媒師・ジャッカルとサイドキック・ヴァルチャー・カモフラージャー・ゲッサー・ベイティングの役職は SNR を経由して取り込んでいます
* **[TheOtherRoles-GM](https://github.com/yukinogatari/TheOtherRoles-GM) (yukinogatari、GPL-3.0)**  
  ナイスウォッチャーとイビルウォッチャーの役職。ネコカボチャ・シリアルキラーの役職と、アドオンのウォッチャーは UchuAddon を経由して取り込んでいます
* **[Jester](https://github.com/Maartii/Jester) (Maartii)**  
  ジェスターの役職
* **[MiraAPI](https://github.com/All-Of-Us-Mods/MiraAPI) (All-Of-Us-Mods、LGPL-2.1)**  
  サボタージュの画面の点滅と警報音を端末ごとに切る機能、右下のボタンの大きさを端末ごとに変える機能、会議の票の重みを票 1 つずつ結果に並べて配る方法、会議の名札の ✓ と ✗ の並びに役職の能力のボタンを足す方法、本編の変身の画面を人を選ぶ画面に使う方法、能力の効果の残りをボタンに出す方法、本編のキーボードの割り当ての画面に mod の操作を足す方法
* **[Vanilla Enhancements](https://github.com/xChipseq/VanillaEnhancements) (xChipseq、GPL-3.0)**  
  会議の画面の暗い色
* **[TownOfNext](https://github.com/KARPED1EM/TownOfNext) (KARPED1EM、GPL-3.0)**  
  チャットの文字数の上限を広げる方法
* **[TownOfPlus](https://github.com/tugaru1975/TownOfPlus) (tugaru1975、GPL-3.0) / [TownOfHost_Y](https://github.com/Yumenopai/TownOfHost_Y) (Yumenopai、GPL-3.0)**  
  マウスのホイールで視野を広げる機能、オートプシーの役職 (TownOfHost_Y)
* **[MoreGamemodes](https://github.com/Rabek009/MoreGamemodes) (Rabek009、GPL-3.0)**  
  試合の途中で抜けた後の参加の待ち時間を消す方法
* **[TownOfHost-Pko](https://github.com/satokazoku/TownOfHost-Pko) (satokazoku ほか、GPL-3.0)**  
  前の試合にいた人を続けて入れない仕組み
* **[Town of Host: Enhanced](https://github.com/EnhancedNetwork/TownofHost-Enhanced) (The Enhanced Network、GPL-3.0)**  
  BAN 一覧・ホワイトリストで使うプレイヤー ID の要約の取り方、試合のルール (ジップラインと胞子の無効化・除染の時間・Airship の電気室の壁を変える設定・サボタージュの制限時間と無効化・マップの出やすさ・会議の無効)
* **[LotusContinued](https://github.com/Lotus-AU/LotusContinued) (Lotus-AU、GPL-3.0)**  
  入室ホワイトリスト、フォントをファイルから作る方法
* **[UchuAddon](https://github.com/TeamUchuaddon/UchuAddon) (TeamUchuaddon、GPL-3.0)**  
  アルケミスト・エクリプス・ノヴァの役職
* **[TownOfHost-hamo](https://github.com/rar006/TownOfHost-hamo) (rar006、GPL-3.0)**  
  共同制作者の haru の mod。守護者・フェイクジェスター・ヌルの役職
* **[Town of Us](https://github.com/slushiegoose/Town-Of-Us)** (slushiegoose)  
  メイヤーとアーソニストの役職 (SNR を経由して取り込み)、生存者の役職
* **[Sheriff Mod](https://github.com/Woodi-dev/Among-Us-Sheriff-Mod)** (Woodi-dev)  
  シェリフの役職 (SNR を経由して取り込み)
* **[au libhalt net](https://au.libhalt.net/)**  
  マッドメイトの役職 (SNR を経由して取り込み)
* **[Among-Us-Love-Couple-Mod](https://github.com/Woodi-dev/Among-Us-Love-Couple-Mod)** (Woodi-dev)  
  ラバーズの役職 (SNR を経由して取り込み)

### 🔹 主要オープンソースプロジェクト
* **BepInEx** ([BepInEx Team](https://github.com/BepInEx/BepInEx))  
  Unity/Il2Cpp ゲーム向けのプラグインフレームワークおよびローダー。
* **Il2CppInterop** ([BepInEx Team](https://github.com/BepInEx/Il2CppInterop))  
  Il2Cpp C++ コードと C# (.NET) 間の相互運用ライブラリ。
* **[FusionCore](https://github.com/All-Of-Us-Mods/FusionCore) (XtraCube / All-Of-Us-Mods、GPL-3.0)**  
  Android の起動アプリ (`Android/Launcher/`)。FusionCore の名前と見た目を変えて APK にしたもので、コードの大部分は FusionCore のものです。起動の入口の部品 (`fusionApp/src/main/jni/main/`) は [libmain.so](https://github.com/springmusk026/libmain.so) (springmusk026) が元です。同梱しているライブラリは [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) に載せています
* **[AndroidUtilities / AuthFix](https://github.com/All-Of-Us-Mods/AndroidUtilities) (All-Of-Us-Mods、Starlight 用)**  
  Android 版で itch.io のキーを使って EOS にログインする方法、広告の初期化を止める方法

### 🔹 システム・ランタイム・コンポーネント
* **.NET Runtime & Base Class Libraries** ([Microsoft Corporation](https://dotnet.microsoft.com/)) - MIT License
* **Opus** ([Xiph.Org Foundation ほか](https://github.com/xiph/opus)) - BSD-3-Clause License (ボイスチャットの声の圧縮)
* **OpenSSL** ([The OpenSSL Project](https://www.openssl.org/)) - Apache-2.0 / OpenSSL License
* **Dobby Instruction Relocation Engine** ([jmpews](https://github.com/jmpews/Dobby)) - Apache-2.0 License
* **Android Jetpack / AndroidX Libraries** ([Google LLC](https://developer.android.com/jetpack/androidx)) - Apache-2.0 License
* **Kotlin Standard Library** ([JetBrains s.r.o.](https://kotlinlang.org/)) - Apache-2.0 License
* **Material Components for Android** ([Google LLC](https://github.com/material-components/material-components-android)) - Apache-2.0 License

### 🔹 フォント・効果音

* **[Mochiy Pop One](https://github.com/fontdasu/Mochiypop) (The MochiyPop Project Authors、SIL Open Font License 1.1)**  
  設定画面の文字のフォント。使う字だけに削って同梱しています (ライセンス文は [Resources/Fonts/OFL.txt](Resources/Fonts/OFL.txt))
* **[OtoLogic](https://otologic.jp/) (CC BY 4.0)**  
  クロノリバースの演出で鳴る時計の秒針の音。「Clock-Second_Hand04」から 1 打ずつ切り出し、少し低くして低音と響きを重ねています (切り出した音は [tools/sounds/clock_tick_otologic.wav](tools/sounds/clock_tick_otologic.wav))
* **[Freesound](https://freesound.org/) の録音 (CC0)**  
  クロノリバースの演出で鳴る音。JakobGille・Jeln Pueskas (教会と塔の時計の機械)、Clagnar・weyhingj (歯車・ラチェット)、Crinkem・NachtmahrTV・slag777・PrrLl・bendavisdrumguy (金属のきしみ・唸り・弓で擦ったシンバル)、camel7695・JensZygar・ZoshP (タムタム・ゴング・鐘)、Breviceps (機械の足音)、daalvinz (合唱)、olver (ハープ)、Justjooning (羽ばたき) の録音を切り出して重ねています
* **[Freesound](https://freesound.org/) の録音 (CC0)**  
  サムライの刀の音。ethanchase7744・Scottrex05・Hano_van_Dalen・lucabia・Q.K. (刀を抜く・納める・鍔の音)、lmbubec・nekoninja・qubodup・Nomagician・Danjocross・velcronator (刃と風切り)、chungkury・d00121058・kibibu・KVV_Audio (刃と刃がぶつかる・擦れる)、perspektywa_tn・melle_teich・NearTheAtmoshphere・Joshua_van_Zyl・Johnny2810・brandondelehoy・JonasTisell・saturdaysoundguy (布が裂ける・服が擦れる)、j_p_higgins・getwecked・thatonecode・unfa・ElevatorFan2020・221Beimesche・bevibeldesign・ccen18・LukaCafuka・SpliceSound・Wigglesworth (氷とガラスが割れる・破片が落ちる)、michael_grinnell・JoelAudio・The_Chemical_Workshop・cb9382・gecop・weaveofkev・YouMightLikeThis (放電と火花・電気のうなり)、Robo9418 (金属のきしみ) の録音を切り出して重ねています
* **[Freesound](https://freesound.org/) の録音 (CC0)**  
  トリガーハッピーの機関銃とロケットランチャーの大砲の音。rob762x51 (機関銃の連射)、Legand569・Artninja・qubodup・SamsterBirdies (銃の金具と装填)、aust_paul・morganpurkis・Artmasterrich (跳弾)、GrayJoy (薬莢)、kyles・ThabzMalik (重い蓋と留め金)、SamsterBirdies (大砲の発射)、Nox_Sound・kmyers1316・JeffPearson (金属を叩く)、17nflores2・StevenJMUK・Robo9418 (金属が裂ける・きしむ)、craigsmith・xkeril (爆発と破片) の録音を切り出して重ねています
* **[Freesound](https://freesound.org/) の録音 (CC0)**  
  エクリプスの隕石とノヴァの光線の音。humanoide9000・Miulew (隕石が空を切る音)、NearTheAtmoshphere・LookIMadeAThing (火の玉と炎)、xkeril (落ちる瓦礫)、Artninja (金具)、cb9382・ironcross32 (放電のパチパチ)、SamsterBirdies・craigsmith (キャノンの破裂と爆発) の録音を切り出して重ねています

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

---

## ⚠️ 免責 (Disclaimer)

This mod is not affiliated with Among Us or Innersloth LLC, and the content contained therein is not endorsed or otherwise sponsored by Innersloth LLC. Portions of the materials contained herein are property of Innersloth LLC. © Innersloth LLC.
