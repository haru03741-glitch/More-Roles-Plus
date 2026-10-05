# 役職・設定の足し方

## 役職を 1 つ足す

`src/Roles/` の下 (クルーなら `Crew/`、インポスターなら `Impostor/`、第三陣営なら `Neutral/`) に、`RoleBase` を継承したクラスのファイルを 1 つ置くだけです。
登録の作業はありません。置いたクラスは起動時に自動で見つかり、次のものが自動で付きます。

- ゲーム設定の画面の「クルー」「インポスター」「第三陣営」タブに、その役職の見出しと「出現率」「人数」の行
- 下に書いた `static readonly` の設定項目の行
- 試合開始時の割り当て (ホストが同じ陣営の人から選んで全員に配る)
- イントロの役職名と説明、自分の名前の上の役職名、タスク欄の先頭の説明
- 会議の名札の役職名 (自分の分。設定で、死んだ後は全員分・インポスター同士の分も)

```csharp
using MoreRolesPlus.Options;

namespace MoreRolesPlus.Roles.Crew;

public sealed class Lighter : RoleBase
{
    public override Team Team => Team.Crew;          // Crew / Impostor / Neutral
    public override string Color => "#EEE5BE";       // 役職の色
    public override Text Name => new("ライター", "Lighter");
    public override Text Blurb => new("広い視界で船を見張ろう", "See further than the rest");

    // 設定項目 (全員共通の値なので static readonly)。設定画面に宣言の順で並ぶ
    public static readonly FloatOpt VisionMultiplier = new("視界の倍率", "Vision Multiplier", 1.5f, 1f, 3f, 0.25f, "x");

    // 試合中の処理
    public override void ModifyVision(ref float radius) => radius *= VisionMultiplier;
}
```

見本は `src/Roles/Crew/Lighter.cs` と `src/Roles/Impostor/Quickdraw.cs`、第三陣営は `src/Roles/Neutral/Jester.cs` (追放されたら一人勝ち)・`src/Roles/Neutral/Survivor.cs` (生きていれば一緒に勝つ)・`src/Roles/Neutral/Outlaw.cs` (キルして最後に残れば勝ち) です。

### 役職クラスで書けるもの

| 書くもの | 意味 |
|---|---|
| `Team` | 陣営。割り当てる相手と、設定画面のタブが決まる |
| `Color` | 役職の色 (`"#RRGGBB"`) |
| `Name` / `Blurb` | 役職名とイントロの一行説明。`new("日本語", "English")` |
| `Description` | タスク欄の先頭に出す説明 (既定は `Blurb`) |
| `IsKiller` | 第三陣営でキルする役職なら `true` (下の「第三陣営の勝ち方」) |
| `CanKill` | インポスター以外でキルボタンを使えるか (既定は `IsKiller` と同じ。クルーの役職でキルさせる時も `true`) |
| `BaseRole` | 本編のどの役職の上に乗るか (既定はクルー / インポスター。ベントを使うなら `RoleTypes.Engineer` など) |
| `MaxCount` | 設定画面の「人数」の上限 (既定 15) |
| `Id` | 保存と同期に使う名前 (既定はクラス名。変えると保存済みの設定値が引き継がれない) |

試合中の処理は、次のメソッドを上書きして書きます。

| メソッド | 呼ばれる時 |
|---|---|
| `OnAssigned()` | 役職が割り当てられた直後 (全員の端末) |
| `OnGameEnd()` | 試合が終わった時 (全員の端末) |
| `OnExiled()` | 会議で追放された時 (全員の端末) |
| `AlsoWins(GameResult result)` | 誰かの勝ちで試合が終わる時に、自分も一緒に勝つなら `true` を返す (ホストの端末) |
| `ModifyVision(ref float radius)` | 視界の広さを計算する時 |
| `ModifyKillCooldown(ref float seconds)` | キルの待ち時間を決める時 (自分の端末と、キルの依頼を確かめるホストの端末。設定値だけから決める) |

インスタンスは試合ごと・プレイヤーごとに作られます。`Player` / `PlayerId` / `IsLocal` で持ち主が分かり、残り回数のような状態は普通のフィールドに置けます。
ほかの役職の情報は `RoleState.Of(player)` (その人の役職、無ければ `null`) と `RoleState.Local` (自分の役職) で引けます。

### 第三陣営の勝ち方

第三陣営 (`Team.Neutral`) の役職が誰かに付いている試合では、勝ち負けを More Roles Plus が決めます (いない試合は本編のまま)。

- **一人勝ち**: 条件を満たした時に `GameEnd.Win(this)` を呼ぶと、その人だけの勝ちで試合が終わります。呼んで効くのはホストの端末だけなので、全員の端末で呼ばれるメソッド (`OnExiled` など) からそのまま呼んで構いません。
- **相乗り**: `AlsoWins` で `true` を返すと、誰が勝った時でも一緒に勝ちます (`result.Team` に勝った陣営、`result.Role` に勝った役職)。
- **キル役** (`IsKiller => true`): 生きている間は、インポスターの人数勝ちもクルーの全滅勝ちも起きません。インポスターが全滅し、キル役が 1 種類だけ残り、ほかの生存者がその人数以下になった時 (1 人なら最後の 1 対 1) に、その役職の全員の勝ちになります。
- 第三陣営のタスクは偽のタスクで、クルーのタスク勝利には数えません。

インポスター以外のキル (`CanKill => true`) は本編のキルボタンをそのまま使います。押すとホストに依頼が届き、ホストが生存・距離・待ち時間を確かめてから全員の画面で倒します。

足りない入口 (「会議が始まった時」など) が要る時は、`RoleBase` に仮想メソッドを足し、本編側から呼ぶパッチを `src/Roles/RoleHooks.cs` に書きます。

## 設定項目の種類

| 型 | 書き方 | 値 |
|---|---|---|
| `BoolOpt` | `new("名前", "Name", true)` | `bool` |
| `IntOpt` | `new("名前", "Name", 既定, 最小, 最大, 刻み = 1, "単位")` | `int` |
| `FloatOpt` | `new("名前", "Name", 既定, 最小, 最大, 刻み, "単位")` | `float` |
| `ChoiceOpt` | `new("名前", "Name", 既定の番号, new("選択肢A", "A"), new("選択肢B", "B"))` | 選ばれた番号 |

どれもそのまま値として使えます (`if (CanVent)` / `radius *= VisionMultiplier`)。
番号を振る必要はありません。名前は「役職の Id.フィールド名」で自動的に付きます。

## 役職以外の設定を足す

`static class` に `[Settings]` を付けると、その中の設定項目が見出し付きでタブに並びます。

```csharp
[Settings(Tab.General, "役職", "Roles")]
public static class RoleSettings
{
    public static readonly BoolOpt Enabled = new("More Roles Plus の役職を使う", "Use More Roles Plus roles", true);
}
```

## 設定値の保存と同期

- ホストが設定画面を閉じた時に、値を `BepInEx-MRP/config/MoreRolesPlus.options.txt` へ保存し、全員へ送ります。
- 部屋に入ってきた人には、版の確認が済んだ時点でホストが値を送ります。
- 全員が同じ版の More Roles Plus を入れていないと、ホストは試合を始められません (入っていない人・版が違う人の名前がチャットに出ます)。

## 端末どうしで情報を送る

役職や機能のクラスに `RemoteCall<T>` を `static readonly` で置くだけで使えます。起動時に集めて名前順に番号を振るので、番号表を書き換える必要はありません。

```csharp
private static readonly RemoteCall<byte> Mark = new("MyRole.Mark", Route.HostToAll,
    (w, playerId) => w.Write(playerId),   // 書き方
    r => r.ReadByte(),                     // 読み方
    (sender, playerId) => { /* 受け取った端末でする事 */ });

Mark.Send(target.PlayerId);   // 送る (自分の端末では実行されないので、手元に効かせる処理は送る側で呼ぶ)
```

- 名前は `役職Id.何をするか` のように、ほかと重ならないものにしてください。
- `Route` は誰から誰へ送るものか: `HostToAll` (ホストから客へ・ほかから来た物は捨てる) / `ToHost` (ホストへの依頼・ホスト自身が送るとその場で実行) / `Anyone`。
- 続けて送る物は `using (Remote.Batch()) { A.Send(..); B.Send(..); }` で 1 通にまとまり、順番どおりに届きます。
- 1 通は数百バイトまでに収めてください (公式サーバーでも遊べるように)。

## 試す

テスト用の遠隔操作 (config の `EnableTestBridge = true`) を有効にすると、次のコマンドが使えます。

- `giverole <役職Id> [番号]` — その人 (省略で自分) にその役職を付けて全員に配る。ホストかフリープレイで (例: `giverole Quickdraw`)
- `forcewin <crew|imp|役職Id>` / `winner` — その勝ちで試合を終わらせる / 最後の結果を見る
- `endcheck [番号...]` — 今の生き残りで勝者が決まるかを見るだけ (番号の人をキル役として数える)
- `roles` — 登録された役職・今の割り当て・自分の視界とキルの待ち時間
- `opt [Id.項目 [番号]]` — 設定項目を見る / 変える

## 他の mod から移植する時

ライセンスが GPL-3.0 と合うか確かめ、移植したファイルの先頭に `// Ported from <リポジトリの URL> <ファイル> (<ライセンス>)` を書き、README のクレジットに相手の名前と取ったものを足してください。画像・音の素材は他の mod の物を使いません。
