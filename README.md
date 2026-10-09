# More Roles Plus

役職や設定の足し方は [CONTRIBUTING.md](CONTRIBUTING.md) にあります。

## ビルド

`local.props.example` を `local.props` にコピーし、`AmongUsPath` を自分のゲームの場所に合わせます。

```
dotnet build MoreRolesPlus.csproj -c Release                       # → <AmongUs>/BepInEx-MRP/plugins
dotnet build patcher/MoreRolesPlus.BootAccel.csproj -c Release     # → <AmongUs>/BepInEx-MRP/patchers (起動の高速化)
```

## クレジット

- [Nebula on the Ship](https://github.com/Dolly1016/Nebula-Public) (Dolly1016、GPL-3.0): incremental GC を実行時に切る仕組み、演出中に GC を先に回す考え方、mod の通信を 1 つの番号にまとめて名前で振り分ける仕組み、試合の出来事を寿命付きで受け取る仕組み、ダミープレイヤーを出す手順
- [ExtremeRoles](https://github.com/yukieiji/ExtremeRoles) (yukieiji、GPL-3.0): ダミープレイヤーを出す手順
- [Super New Roles](https://github.com/ykundesu/SuperNewRoles) (ykundesu、GPL-3.0): 試合の出来事を配る仕組み (配っている最中の受け手の追加・削除の扱い)、公式サーバーに全員導入の mod として部屋を登録する方法
- [Reactor](https://github.com/NuclearPowered/Reactor) (NuclearPowered、LGPL-3.0): 公式サーバーかどうかの判定 (接続先のアドレスで見る)
- [Town of Us Mira](https://github.com/AU-Avengers/TOU-Mira) (AU-Avengers、GPL-3.0): 試合の途中で本編の役職を付け直す方法
- [End K not](https://github.com/waffle-ful/Aeterna-End-K-not) (waffle-ful、GPL-3.0): 起動の高速化 (`patcher/`・パッチ適用の高速化)、GC の先回しの実装 (ゲーム側の GC も演出中に回す)、設定画面のフォントと検索の一致判定、起動直後のアカウント表示の停止、開発用の道具 (実機テストの操作口・重さの測り方・起動の時間ログ・AssetBundle の焼き方・Android で使えない API の照合)
- [Endless Host Roles](https://github.com/Gurge44/EndlessHostRoles) (Gurge44、GPL-3.0) / [TownOfHost-K](https://github.com/KYMario/TownOfHost-K) (KYMario、GPL-3.0) / [Town Of Host](https://github.com/tukasa0001/TownOfHost) (tukasa0001、GPL-3.0): ロゴ演出の短縮、設定画面にタブを足す方法、公式サーバーに mod の部屋として名乗る方法、BAN 一覧とホワイトリスト (入室時の照合・BAN した人の記録)、チャットの荒らし対策 (禁止語・開始の催促)、設定画面の検索欄 (チャットの入力欄を複製する方法)、設定を見る画面の並び、ホストのキー操作 (廃村・会議・開始の数えを飛ばす・ロビーで Enter で開始)、チャットコマンドの種類と名前、マウスのホイールで視野を広げる機能、チャットのコマンドの補完と貼り付け
- [TownOfNext](https://github.com/KARPED1EM/TownOfNext) (KARPED1EM、GPL-3.0): チャットの文字数の上限を広げる方法
- [TownOfPlus](https://github.com/tugaru1975/TownOfPlus) (tugaru1975、GPL-3.0) / [TownOfHost_Y](https://github.com/Yumenopai/TownOfHost_Y) (Yumenopai、GPL-3.0): マウスのホイールで視野を広げる機能
- [MoreGamemodes](https://github.com/Rabek009/MoreGamemodes) (Rabek009、GPL-3.0): 試合の途中で抜けた後の参加の待ち時間を消す方法
- [TownOfHost-Pko](https://github.com/satokazoku/TownOfHost-Pko) (satokazoku ほか、GPL-3.0): 前の試合にいた人を続けて入れない仕組み
- [Town of Host: Enhanced](https://github.com/EnhancedNetwork/TownofHost-Enhanced) (The Enhanced Network、GPL-3.0): BAN 一覧・ホワイトリストで使うプレイヤー ID の要約の取り方
- [LotusContinued](https://github.com/Lotus-AU/LotusContinued) (Lotus-AU、GPL-3.0): 入室ホワイトリスト、フォントをファイルから作る方法
- [AndroidUtilities / AuthFix](https://github.com/All-Of-Us-Mods/AndroidUtilities) (All-Of-Us-Mods、Starlight 用): Android 版で itch.io のキーを使って EOS にログインする方法、広告の初期化を止める方法
- [Mochiy Pop One](https://github.com/fontdasu/Mochiypop) (The MochiyPop Project Authors、SIL Open Font License 1.1): 設定画面の文字のフォント。使う字だけに削って同梱しています (ライセンス文は [Resources/Fonts/OFL.txt](Resources/Fonts/OFL.txt))
- [TownOfHost-hamo](https://github.com/rar006/TownOfHost-hamo) (rar006、GPL-3.0): 共同制作者の haru の mod

## ライセンス

GNU General Public License v3.0 です。詳しくは [LICENSE](LICENSE) を見てください。

## 免責

This mod is not affiliated with Among Us or Innersloth LLC, and the content contained therein is not endorsed or otherwise sponsored by Innersloth LLC. Portions of the materials contained herein are property of Innersloth LLC. © Innersloth LLC.
