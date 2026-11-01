# MRP サーバー用プラグイン (MrpServer)

MRP のサーバー (Impostor) に入れるプラグインです。次のことをします。

- **バグ報告の受付**: ゲーム内の「バグ報告」から送られた報告を、Discord の報告チャンネルにスレッドとして投稿します。ログは zip で添付されます。画像・動画も付けられます。
- **ゲーム内チャット**: スタッフがそのスレッドに書いた返事 (画像・動画も) がゲームに届きます。プレイヤーがゲームで書いた事や送った画像・動画もスレッドに出ます。
- **管理のボタン**: 報告ごとに「既読にする・閉じる・停止」のボタンを置きます。押せるのはスタッフだけです。
- **マッチメイキング**: MRP の部屋が「公開」になると、Discord のマッチメイキングチャンネルに投稿します。人数や状態が変わると書き換え、非公開になったり部屋が終わったりすると消します。「参加する」ボタンを押すとインストーラー経由でゲームが開き、その部屋に入ります。
- **ロビーでのモードの切り替え**: ロビーでクラシックとかくれんぼを切り替えても、ホストが切断されないようにします (下の「モードの切り替えの直し」)。

## 動かし方 (はじめから)

ここでは Windows の PC でサーバーを動かす場合を書きます。Linux も同じ流れで、違う所だけ別に書きます。

### 0. 全体の形

```
遊ぶ人のゲーム ──(https 443: 部屋の作成・一覧・バグ報告)──▶ Cloudflare ──▶ サーバーの PC の 22023 番 (HTTP)
遊ぶ人のゲーム ──(UDP 22023: 試合の通信)──────────────────────────────▶ サーバーの PC の 22023 番 (UDP)
                                                   Impostor ──(Bot)──▶ Discord
```

- **Impostor**: Among Us のサーバーです。MRP のサーバーはこれに MrpServer (このプラグイン) を入れた物です。
- **HTTP の口** (部屋の作成・部屋の一覧・バグ報告・参加リンク) は Cloudflare を通して `https://<ドメイン>` で出します。
- **UDP の口** (試合の通信) は Cloudflare を通りません。ルーターで 22023 番を開けて、直接受けます。

### 1. 用意する物

| 物 | どこで | 備考 |
|---|---|---|
| .NET 8 の **ASP.NET Core Runtime** | https://dotnet.microsoft.com/download/dotnet/8.0 | 「ASP.NET Core Runtime 8.x」の Windows x64 の Installer を入れます。SDK でも動きます |
| **Impostor 1.10.9** 以上 | https://github.com/Impostor/Impostor/releases | `Impostor-Server_1.10.9_win-x64.zip` (Linux なら `linux-x64.tar.gz`) |
| MrpServer のファイル | このリポジトリをビルドするか、配った `MrpServer` フォルダー | `MrpServer.dll`・`0Harmony.dll`・`config_mrp.example.json` |
| ドメインと Cloudflare | すでに使っている `amongus.nkzk.win` など | https でゲームと話すため |
| Discord の Bot | 下の「Discord の準備」 | バグ報告とマッチメイキングに使います |

ビルドする場合は、リポジトリの一番上で次を打ちます (.NET 8 SDK が要ります)。

```
dotnet build server/MrpServer -c Release
```

できた物は `server/MrpServer/bin/Release/net8.0/` にあります。

### 2. Impostor を置く

1. Impostor の zip を好きな所に展開します。例: `C:\Impostor`
2. 次のように置きます。

```
C:\Impostor\
  Impostor.Server.exe
  config.json              ← Impostor の設定 (3 で書き換える)
  plugins\
    MrpServer.dll          ← ここに置く
  libraries\
    0Harmony.dll           ← ここに置く (無いと「モードの切り替えの直し」が動きません)
```

`plugins` と `libraries` のフォルダーが無ければ作ってください。`MrpServer.pdb` と `MrpServer.deps.json` は置かなくても動きます。

### 3. Impostor の config.json を書く

`C:\Impostor\config.json` を開いて、`Server` と `AntiCheat` を次のようにします。

```json
{
  "Server": {
    "PublicIp": "<サーバーのグローバル IP>",
    "PublicPort": 22023,
    "ListenIp": "0.0.0.0",
    "ListenPort": 22023
  },
  "HttpServer": {
    "Enabled": true,
    "ListenIp": "0.0.0.0",
    "ListenPort": 22023
  },
  "AntiCheat": {
    "Enabled": true,
    "ForbidProtocolExtensions": false,
    "EnableRoleChecks": false,
    "EnableGameFlowChecks": false,
    "AllowHostOnlyExtensions": "Always"
  }
}
```

- **PublicIp** は、遊ぶ人から見たサーバーの住所です。家のルーターの外側の IP (「IP 確認」などで調べられます) を書きます。ここが `127.0.0.1` のままだと、部屋は作れても試合の通信が届かず、入れません。
- `AntiCheat` の理由は下の「Impostor の設定 (大事)」にあります。ほかの項目は初めから入っている値のままで構いません。

### 4. ルーターと Windows のファイアウォール

- ルーターで **UDP の 22023 番** を、サーバーの PC へ転送 (ポート開放) します。
- Windows の「セキュリティが強化された Windows Defender ファイアウォール」→「受信の規則」→「新しい規則」で、**UDP 22023** と **TCP 22023** を許可します。
- HTTP (TCP 22023) は Cloudflare Tunnel を使うなら外へ開けなくて構いません (6 を参照)。

### 5. 初めて起動して、config_mrp.json を書く

1. `C:\Impostor\Impostor.Server.exe` をダブルクリックします (Linux は `./Impostor.Server`)。黒い窓にログが流れます。
2. ログに次のように出れば、プラグインが読み込まれています。
   - プラグインを読み込んだ行 (`win.nkzk.mrp` か `MrpServer` を含む行)
   - `game mode fix installed`
   - Discord をまだ書いていないので「Discord.BotToken が空なので…」の警告 (今は気にしなくて大丈夫です)
3. 起動したフォルダーに `config_mrp.json` ができます。窓を閉じて (Ctrl+C)、`config_mrp.json` を開きます。
4. `PublicUrl` に `https://<ドメイン>` を、`Discord` に下の「Discord の準備」で取った値を書きます。
5. もう一度 `Impostor.Server.exe` を起動します。ログに「Discord の Gateway につなぎました」と出て、Discord の Bot が「オンライン」になれば準備ができています。

`config_mrp.json` の項目の意味は `config_mrp.example.json` と下の表にあります。

### 6. Cloudflare で https の口を作る

ゲームは `https://<ドメイン>` (443 番) で部屋の作成や一覧を頼みます。Cloudflare から Impostor の HTTP (22023 番) へ流してください。どちらか片方で構いません。

- **Cloudflare Tunnel (おすすめ・ポート開放が要らない)**: Cloudflare のダッシュボード → Zero Trust → Networks → Tunnels でトンネルを作り、サーバーの PC に `cloudflared` を入れます。Public Hostname に `<ドメイン>` を足し、Service を `http://localhost:22023` にします。
- **ふつうのプロキシ (オレンジの雲)**: DNS の A レコードをサーバーの IP にし、Cloudflare の Origin Rules で宛先のポートを 22023 に書き換えます。この場合は TCP 22023 もルーターで開けます。

どちらでも、`/api/…` (部屋の作成・一覧) と `/mrp/…` (バグ報告・参加リンク) の両方が Impostor へ届くようにしてください。ブラウザで `https://<ドメイン>/mrp/status` を開き、`{"ok":true,...}` が出れば通っています。

### 7. ゲームの側

MRP は初めから `amongus.nkzk.win` の 443 番を「MRP Server」として地域の一覧に足します。別のドメインで動かす時は、ゲームのフォルダーの `BepInEx\config\86aa5c38-7092-4d6f-ae72-7e9301070806.cfg` の `[Server]` の `Address` と `Port` を書き換えます (443 番なら https で話します)。

### 8. 動いているかの確かめ

1. `https://<ドメイン>/mrp/status` が `{"ok":true,...}` を返す。
2. ゲームで地域を「MRP Server」にして、部屋を作れる。もう 1 人入って試合を始められる。
3. ロビーでクラシック ⇔ かくれんぼを切り替えても切断されない (サーバーのログに `options switched to HideNSeek` と出る)。
4. ロビーのバグ報告のボタンから送ると、Discord の報告チャンネルにスレッドができる。
5. 部屋を「公開」にすると、マッチメイキングのチャンネルに投稿が出る。

### 9. ずっと動かしておく

- **Windows**: 「タスク スケジューラ」で「タスクの作成」→ トリガー「スタートアップ時」→ 操作「プログラムの開始」に `C:\Impostor\Impostor.Server.exe`、「開始 (オプション)」に `C:\Impostor` を書きます。「ユーザーがログオンしているかどうかにかかわらず実行する」を選ぶと、ログインしなくても動きます。
- **Linux**: systemd のサービスにします。例 (`/etc/systemd/system/impostor.service`):

```ini
[Unit]
Description=Impostor (MRP)
After=network-online.target

[Service]
WorkingDirectory=/opt/impostor
ExecStart=/opt/impostor/Impostor.Server
Restart=always
User=impostor

[Install]
WantedBy=multi-user.target
```

`sudo systemctl enable --now impostor` で起動し、`journalctl -u impostor -f` でログを見られます。

### 10. 更新する時

1. Impostor を止めます。
2. 新しい `MrpServer.dll` を `plugins` に、`0Harmony.dll` を `libraries` に上書きします。
3. Impostor を起動します。`config_mrp.json` と `mrp-data` (報告の記録) はそのまま使われます。

### 困った時

| 様子 | 見る所 |
|---|---|
| 部屋は作れるが入れない・すぐ切れる | `config.json` の `PublicIp` がグローバル IP か。ルーターで UDP 22023 を開けたか |
| 部屋を作れない・地域が赤い | `https://<ドメイン>/api/games` が開けるか (Cloudflare の向き先とポート) |
| ゲームを開始できない | `AntiCheat.ForbidProtocolExtensions` が `false` か (起動時にエラーが出ます) |
| 試合の途中で追い出される | `AntiCheat.EnableRoleChecks` と `EnableGameFlowChecks` が `false` か |
| ロビーでモードを替えると切断される | `libraries\0Harmony.dll` があるか。ログに `game mode fix installed` が出ているか |
| バグ報告が送れない | `/mrp/status` が開けるか。ログの Discord のエラー (トークン・チャンネル ID・Bot の権限) |
| スタッフの返事がゲームに届かない | Bot の Message Content Intent が ON か |

## モードの切り替えの直し

MRP はロビーでクラシックとかくれんぼを切り替えられます (ホストが GameManager を出し直し、新しいモードの設定を送ります)。Impostor は部屋を作った時のモードの設定の入れ物を持ち続けるので、別のモードの設定が届くと読めずに例外になり、ホストが「サーバー接続が切れました」で切断されます。MrpServer は Impostor の設定を読む所 (`GameOptionsFactory.DeserializeInto`) に Harmony で割り込み、モードが違う時は部屋の入れ物を届いたモードの物に差し替えます。このため `0Harmony.dll` が要ります。

## Impostor の設定 (大事)

Impostor の不正対策は MOD の無い本編に合わせてあります。初期設定のままだと MRP の通信が止められます。

- **ゲームを開始できない**: 入ってきた人から MRP の版の知らせがホストに届きません。MRP は「MRP が入っていない人がいる」とみなして開始を止めます。
- **途中で追い出される**: 役職の能力が不正とみなされます。

Impostor の `config.json` の `AntiCheat` を次のようにしてから、Impostor を起動し直してください。

```json
"AntiCheat": {
  "Enabled": true,
  "ForbidProtocolExtensions": false,
  "EnableRoleChecks": false,
  "EnableGameFlowChecks": false,
  "AllowHostOnlyExtensions": "Always"
}
```

設定が合っていないと、Impostor の起動の時に「MRP: config.json の AntiCheat.〜」というエラーか警告がログに出ます。

## Discord の準備

1. https://discord.com/developers/applications でアプリを作り、Bot を作ってトークンを `Discord.BotToken` に書きます。
2. Bot の設定で **Message Content Intent を ON** にします。これが OFF だと、スタッフの返事の本文を読めません。
3. Bot をサーバーに招待します。必要な権限は次の通りです。
   - チャンネルを見る
   - メッセージを送信
   - スレッドでメッセージを送信
   - 公開スレッドの作成
   - ファイルを添付
   - 埋め込みリンク
   - メッセージ履歴を読む
4. チャンネルを用意し、それぞれの ID を書きます。ID は Discord の開発者モードで右クリック →「ID をコピー」で取れます。

| 設定 | 中身 |
|---|---|
| `GuildId` | サーバーの ID (スタッフの役職を確かめる時に使う) |
| `ReportChannelId` | バグ報告のチャンネル。**フォーラムがおすすめ**ですが、普通のテキストチャンネルでも動きます。スタッフだけが見られるようにしてください (ログが付くため) |
| `MatchmakingChannelId` | マッチメイキングのチャンネル (みんなが見られる所) |
| `StaffRoleId` | 管理のボタンを押せる役職。空なら、サーバーの管理者 (Administrator の権限を持つ人) だけが押せます。管理者は役職が無くてもいつでも押せます |
| `StaffChannelId` | 管理のボタンを置くチャンネル (スタッフだけが見られる所)。空なら、報告のスレッドの中に置きます |

5. Impostor を起動し直して Bot が「オンライン」になっていれば、ボタンを受け付けています (ログに「Discord の Gateway につなぎました」と出ます)。

## 管理のボタン (スタッフだけ)

報告が届くと、`StaffChannelId` のチャンネル (空なら報告のスレッドの中) に、その報告の状態とボタンが出ます。

| ボタン | 動き |
|---|---|
| 既読にする | スタッフが読んだ印。その報告は「1 時間に送れる数」に数えなくなり、その人はまた送れるようになります。ゲームには「スタッフが報告を読みました」と出ます |
| 閉じる / 開け直す | 報告を閉じる (ゲームからは書けなくなる) / 開け直す |
| 停止 / 停止を外す | 理由を聞いてから、その人 (フレンドコードと IP) からの報告とチャットを止めます / 外します |

スタッフでない人が押すと、その人にだけ「スタッフだけが押せます」と出て、何も変わりません。スレッドで `//` から始まる書き込みは、スタッフ同士のメモとしてゲームには出しません。

## 悪用を防ぐ仕組み

- **本人確認**: 報告は「最近このサーバーにつないだフレンドコード」からだけ受けます。既定は 72 時間以内です。報告には、そのフレンドコードがつないだ時の IP と同じかどうかを「確認」欄に出します。
- **回数の制限**: 1 人あたり 1 時間に 3 件、同じ IP からは 1 時間に 6 件までです。スタッフが「既読にする」を押した報告は数えないので、読まれればまた送れます。1 日の上限はありません。チャットは 3 秒に 1 回・1 時間に 60 回までです。値は `Reports` で変えられます。
- **大きさの制限**: 題 90 字・本文 3500 字・チャット 600 字・ログ 6MB まで、画像・動画は 1 回に 4 つ・1 つ 9.5MB までです (Discord の無料の上限が 10MB のため)。
- **画像・動画の確認**: ファイルの名前ではなく中身の頭を見て、画像 (png・jpg・gif・webp) と動画 (mp4・mov・webm) だけを受けます。名前は英数字だけに直します。
- **通知させない**: 報告やチャットに `@everyone` などが入っていても、誰にも通知が飛ばないようにしています。
- **合言葉**: 報告ごとに合言葉を発行します。それを持つゲームだけが、その報告のやり取りを読み書きできます。サーバーには合言葉のハッシュだけを残します。
- **停止**: 「停止」のボタンで、フレンドコードと IP の両方を止めます。

記録は `mrp-data/store.json` に残ります。つないだ人の記録は 30 日、報告は 180 日で消えます。

## 参加リンク

マッチメイキングの「参加する」は `https://<PublicUrl>/mrp/join/<部屋コード>` を開きます。このページが `mrp://join/<部屋コード>` を開き、MRP のインストーラーが受け取ります。

- インストーラーは起動するたびに `mrp://` を自分に結び付けます。
- インストーラーは、MRP の入ったゲームに部屋コードを渡します。ゲームが閉じていれば起動します。
- ゲームは MRP のサーバーへ切り替えて、その部屋に入ります。
