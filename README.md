# More Roles Plus

Among Us に役職を足し、壁を壊せるようにするクライアント mod です (BepInEx 6 IL2CPP)。部屋の全員が同じ版を入れて遊びます。

- 役職: ゲーム設定の画面に「MRP 全般」「役職」のタブが増え、出現率と人数を決めると試合開始時に割り当てられます。役職のタブでは名前や設定で検索でき、陣営でも絞り込めます。
- 地形: 爆発やハンマーで壁が壊れ、通れる・見通せるようになります。
- 起動: ロゴ演出を待たずにメニューへ進み、2 回目以降の起動は読み込みの一部を省きます。

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
- [End K not](https://github.com/waffle-ful/Aeterna-End-K-not) (waffle-ful、GPL-3.0): 起動の高速化 (`patcher/`・パッチ適用時のデリゲート型の使い回し)、GC の先回し、設定画面で別のフォントを使う方法、Android 版の itch.io ログインと広告の止め方、起動直後のアカウント表示の停止、公式サーバーに mod の部屋として名乗る方法、設定画面の検索の一致判定 (全角半角・かなの違いをそろえる)、ホストの PC のキー操作、演出用の AssetBundle の焼き方、起動の区切りの時間ログ、実機テスト用のファイル経由の操作口とフレームの重さの測り方、Android 版で使えない API の照合ツール (`tools/AndroidIcallAudit`)
- [Endless Host Roles](https://github.com/Gurge44/EndlessHostRoles) (Gurge44、GPL-3.0) / [TownOfHost-K](https://github.com/KYMario/TownOfHost-K) (KYMario、GPL-3.0) / [Town Of Host](https://github.com/tukasa0001/TownOfHost) (tukasa0001、GPL-3.0): ロゴ演出の短縮、設定画面にタブを足す方法、公式サーバーに mod の部屋として名乗る方法、BAN 一覧とホワイトリスト (入室時の照合・BAN した人の記録)、チャットの荒らし対策 (禁止語・開始の催促)、設定画面の検索欄 (チャットの入力欄を複製する方法)、設定を見る画面の並び、ホストのキー操作 (廃村・会議・開始の数えを飛ばす・ロビーで Enter で開始)、チャットコマンドの種類と名前
- [TownOfHost-Pko](https://github.com/satokazoku/TownOfHost-Pko) (satokazoku ほか、GPL-3.0): 前の試合にいた人を続けて入れない仕組み
- [Town of Host: Enhanced](https://github.com/EnhancedNetwork/TownofHost-Enhanced) (The Enhanced Network、GPL-3.0): BAN 一覧・ホワイトリストで使うプレイヤー ID の要約の取り方
- [LotusContinued](https://github.com/Lotus-AU/LotusContinued) (Lotus-AU、GPL-3.0): 入室ホワイトリスト、フォントをファイルから作る方法
- [AndroidUtilities / AuthFix](https://github.com/All-Of-Us-Mods/AndroidUtilities) (All-Of-Us-Mods): Android 版で itch.io のキーを使って EOS にログインする方法、広告の初期化を止める方法
- [Mochiy Pop One](https://github.com/fontdasu/Mochiypop) (The MochiyPop Project Authors、SIL Open Font License 1.1): 設定画面の文字のフォント。使う字だけに削って同梱しています (ライセンス文は [Resources/Fonts/OFL.txt](Resources/Fonts/OFL.txt))
- [TownOfHost-hamo](https://github.com/rar006/TownOfHost-hamo) (rar006、GPL-3.0): 共同制作者の haru の mod

## ライセンス

GNU General Public License v3.0 です。詳しくは [LICENSE](LICENSE) を見てください。

## 免責

This mod is not affiliated with Among Us or Innersloth LLC, and the content contained therein is not endorsed or otherwise sponsored by Innersloth LLC. Portions of the materials contained herein are property of Innersloth LLC. © Innersloth LLC.
