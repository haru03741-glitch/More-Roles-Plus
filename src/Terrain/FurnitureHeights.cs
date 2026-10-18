using System;
using System.Collections.Generic;

namespace MoreRolesPlus.Terrain;

// 家具の高さと中身。高さは水の深さと同じ物差し (単位・クルーの腰 = 0.35)。
// Solid = 中まで詰まった物 (足元の床が天板の高さまで上がり、水を押しのける。水面が天板を越えると上にも水が乗る)。
// Legs = 脚のある物 (水は下を通る。天板の高さは水に沈んだ見た目だけに使う)。
// Floats = 深い水に浮いて流れに乗る。
// Upright = 真横から見た板 (手すり): 絵の高さがそのまま本当の高さ (壁の面やクルーと同じ。家具は斜めに見下ろして縮んで描かれる)。
// Deep = 奥行きのある低い机: 絵の縦の広がりはほとんど奥行きなので、水面の板は絵の下から水を這い上がらせず、天板の高さを越えた時に家具ごと沈める。
// キー = 船の名前/動く物の名前 (末尾の " (番号)" と " copy 番号" を除く)。"*/名前" はどの船にもある物
internal enum FurnitureFill : byte { Solid, Legs }

internal static class FurnitureHeights
{
    internal readonly struct Entry
    {
        public readonly float Top;
        public readonly FurnitureFill Fill;
        public readonly bool Floats, Upright, Deep;
        public Entry(float top, FurnitureFill fill, bool floats = false, bool upright = false, bool deep = false) { Top = top; Fill = fill; Floats = floats; Upright = upright; Deep = deep; }
    }

    private const FurnitureFill S = FurnitureFill.Solid, L = FurnitureFill.Legs;

    private static readonly Dictionary<string, Entry> Table = new()
    {
        // どの船にもある季節の飾り (かぼちゃ・ろうそく・お菓子の器)
        ["*/pumpkin1"] = new(0.10f, L),
        ["*/pumpkin2"] = new(0.15f, L),
        ["*/pumpkin3"] = new(0.15f, L),
        ["*/pumpkin4"] = new(0.20f, L),
        ["*/pumpkin5"] = new(0.10f, L),
        ["*/candle"] = new(0.45f, L),
        ["*/candyBowl"] = new(0.10f, L),
        // Skeld
        ["SkeldShip/Table"] = new(0.35f, L, deep: true),                    // 食堂の丸い机
        ["SkeldShip/medBay_bed1"] = new(0.30f, S),              // 医務室のベッド
        ["SkeldShip/storage_Boxes-left"] = new(0.35f, S, true), // 倉庫の左の箱
        ["SkeldShip/storage_Boxes-front"] = new(0.35f, S, true),// 倉庫の手前の箱
        ["SkeldShip/storage_Boxes-can"] = new(0.30f, S, true),  // 倉庫の燃料缶
        ["SkeldShip/storage_Boxes-stack"] = new(0.70f, S),      // 倉庫の台と積まれた箱
        ["SkeldShip/nav_chairfront"] = new(0.25f, L, true),     // 操縦室の椅子
        ["SkeldShip/nav_chairmid"] = new(0.25f, L, true),
        ["SkeldShip/nav_chairback"] = new(0.25f, L, true),
        ["SkeldShip/security_chair"] = new(0.25f, L, true),
        // Mira
        ["MiraShip/caf_table"] = new(0.35f, L, deep: true),                 // 食堂のテーブル
        ["MiraShip/cafe-vending2"] = new(1.10f, S),             // 自販機
        ["MiraShip/cafe-vending3"] = new(1.10f, S),
        ["MiraShip/cafe-sidebar"] = new(1.00f, S),              // 食堂の棚
        ["MiraShip/storage-shelves1"] = new(1.00f, S),          // 倉庫の真ん中の棚
        ["MiraShip/storage-box2"] = new(0.35f, S, true),        // 倉庫の木箱
        ["MiraShip/storage-boxes1"] = new(0.70f, S),            // 倉庫の箱の山
        ["MiraShip/storage-backwall-boxes"] = new(0.70f, S),    // 倉庫の奥の箱の山
        ["MiraShip/storage-backwall-shelf"] = new(1.00f, S),    // 倉庫の奥の棚
        ["MiraShip/storage-backwall-crates"] = new(0.70f, S),   // 倉庫の奥の緑の箱
        ["MiraShip/reactor-console-upper"] = new(0.40f, S),     // 原子炉の上の机 (端末)
        ["MiraShip/reactor-console-elec"] = new(0.40f, S),      // 原子炉の電気の机 (端末)
        ["MiraShip/reactor-console-lower"] = new(0.35f, L),     // 原子炉の下の机
        ["MiraShip/lab-cent"] = new(0.40f, S),                  // 研究室の机 (端末)
        ["MiraShip/lab-right"] = new(0.40f, S),                 // 研究室の右の台
        ["MiraShip/locker-bench"] = new(0.20f, L),              // 更衣室のベンチ
        ["MiraShip/admin-tablelower"] = new(0.35f, L),          // 管理室の下の机
        ["MiraShip/office-bot"] = new(0.35f, L),                // パソコン机
        ["MiraShip/office-right"] = new(0.35f, L),              // 細い机
        ["MiraShip/office-mid"] = new(0.40f, S),                // 真ん中の机 (端末)
        ["MiraShip/comms-top"] = new(0.40f, S),                 // 通信室の上の机 (端末)
        ["MiraShip/balcony_Weather"] = new(0.80f, L),           // 天気の掲示板
        ["MiraShip/med-bed-bottom"] = new(0.30f, S),            // 医務室のベッド
        ["MiraShip/med-bed-hort"] = new(0.30f, S),
        ["MiraShip/medbaybottom1"] = new(0.30f, S),
        ["MiraShip/medbaybottom2"] = new(0.30f, S),
        ["MiraShip/garden-plant1"] = new(0.25f, S),             // 温室のプランター
        ["MiraShip/garden-plant2"] = new(0.25f, S),
        ["MiraShip/storage-mop"] = new(0.50f, L),               // 倉庫のモップ
        ["MiraShip/office-ball"] = new(0.35f, S, true),         // バランスボール
        // Polus
        ["PolusShip/barrier"] = new(0.40f, L),                  // 電気室の柵
        ["PolusShip/transformer"] = new(0.60f, S),              // 電気室の変圧器 (コマ送りの絵で名前の番号が変わるので番号は見ない)
        ["PolusShip/caftable"] = new(0.35f, L, deep: true),                 // 長机
        ["PolusShip/projector"] = new(0.60f, L),                // プロジェクター
        ["PolusShip/sciencetable"] = new(0.35f, L, deep: true),             // 研究所の机
        ["PolusShip/storagebox"] = new(0.35f, S, true),         // 倉庫の箱
        ["PolusShip/outsideboxes1-front"] = new(0.35f, S, true),// 屋外の箱
        ["PolusShip/outsideboxes1-right"] = new(0.35f, S, true),
        ["PolusShip/outsideboxes1-back"] = new(0.35f, S, true),
        ["PolusShip/outsideboxes2-box1"] = new(0.35f, S, true),
        ["PolusShip/outsideboxes2-box2"] = new(0.35f, S, true),
        ["PolusShip/outsideboxes2-box3"] = new(0.35f, S, true),
        ["PolusShip/outsideboxes2-canL"] = new(0.30f, S, true), // 屋外の缶
        ["PolusShip/outsideboxes2-canR"] = new(0.30f, S, true),
        ["PolusShip/rock1"] = new(0.35f, S),                    // 屋外の岩
        ["PolusShip/rock2"] = new(0.25f, S),
        ["PolusShip/rock3"] = new(0.25f, S),
        ["PolusShip/snowman"] = new(0.40f, S),                  // 雪だるま
        // Airship
        ["Airship/vault_dummie"] = new(0.80f, S),               // マネキン
        ["Airship/storage_cargo1"] = new(0.40f, L),             // 貨物室の荷車
        ["Airship/cockpit_chair"] = new(0.30f, L, true),        // 操縦席の椅子
        ["Airship/records_chair"] = new(0.25f, L, true),        // 記録室の椅子
        ["Airship/lounge_stool"] = new(0.25f, L, true),         // ラウンジの丸椅子
        ["Airship/shower_bench1"] = new(0.25f, L),              // シャワー室のベンチ
        ["Airship/shower_bench2"] = new(0.25f, L),
        ["Airship/showers_bench3"] = new(0.25f, L),
        ["Airship/medbay_scale"] = new(0.50f, L),               // 医務室の体重計
        ["Airship/vault_sword"] = new(0.40f, L),                // 金庫の剣・銃・ルビーの台
        ["Airship/vault_gun"] = new(0.30f, L),
        ["Airship/vault_rubyglow"] = new(0.90f, S),
        // Fungle
        ["FungleShip/jungle_metalPlate"] = new(0.05f, S),       // 金属の板
        ["FungleShip/jungle_railing"] = new(0.40f, L),          // 高台の柵
        ["FungleShip/jungle_medScanner"] = new(0.50f, S),       // 高台の障害物
        ["FungleShip/jungle_o2plant"] = new(0.50f, S),
        ["FungleShip/bonfireArea-chair1"] = new(0.25f, S, true),// たき火の丸太の椅子
        ["FungleShip/bonfireArea-chair2"] = new(0.25f, S, true),
        ["FungleShip/bonfireArea-chair3"] = new(0.25f, S, true),
        ["FungleShip/crate"] = new(0.45f, S, true),             // 木箱
        ["FungleShip/croppedBoxes"] = new(0.35f, S, true),      // 箱
        ["FungleShip/croppedCoffeetable"] = new(0.50f, L),      // コーヒーの台
        ["FungleShip/croppedBARRELS"] = new(0.35f, S, true),    // 樽
        ["FungleShip/croppedWatercooler"] = new(0.60f, S),      // ウォータークーラー
        ["FungleShip/obj_recRoom_floatieHead"] = new(0.30f, S, true), // 浮き輪の頭
    };

    // 動かないが水には沈む家具 (自分の絵を持つ物は絵の名前・部屋の絵に描き込まれた物は当たり判定の名前)。
    // 背の高い物 (絵の上の方まで天板の高さで頭打ちになる物) は入れない
    private static readonly Dictionary<string, Entry> Still = new()
    {
        // Mira
        ["MiraShip/cafe-vending1"] = new(1.10f, S),             // 食堂の自販機 (端末)
        ["MiraShip/balconyFenceHorz"] = new(0.30f, L, upright: true), // 発射台とバルコニーのガラスの手すり (横向き・水は手すりの縁で溜まる)
        ["MiraShip/admin-map"] = new(0.40f, S),                 // 管理室の地図の台
        ["MiraShip/reactor-center"] = new(0.40f, S),            // 原子炉の真ん中の台
        // Polus
        ["PolusShip/commstable"] = new(0.35f, L, deep: true),               // 通信室の机
        ["PolusShip/admintable"] = new(0.40f, S),               // 管理室の地図の台
        ["PolusShip/fencebot"] = new(0.40f, L),                 // 電気室の手前の柵
        ["PolusShip/panel_security"] = new(0.40f, S),           // 電気室の監視の机
        ["PolusShip/rockblock1"] = new(0.60f, S),               // 屋外の岩の塊
        ["PolusShip/blockrock"] = new(0.70f, S),
        // Airship
        ["Airship/armory_table"] = new(0.35f, L),               // 武器庫の机
        ["Airship/kitchen_table1"] = new(0.35f, L),             // キッチンの机
        ["Airship/kitchen_table2"] = new(0.35f, L),
        ["Airship/records_top"] = new(1.00f, S),                // 記録室の棚 (左右とも同じ絵)
        ["Airship/lounge_pooltable1"] = new(0.40f, L),          // ビリヤード台
        ["Airship/lounge_pooltable2"] = new(0.40f, L),
        ["Airship/meeting_table"] = new(0.40f, L),              // 会議室の机
        // Fungle
        ["FungleShip/storage_middleshelves"] = new(1.00f, S),      // 倉庫の真ん中の棚
        ["FungleShip/labratory_center"] = new(0.40f, S),        // 研究所の真ん中の台
    };

    internal static bool TryGetStill(string shipName, string name, out Entry e) =>
        Still.TryGetValue(shipName + "/" + Strip(name), out e);

    internal static bool TryGet(string shipName, string name, out Entry e)
    {
        string n = Strip(name);
        return Table.TryGetValue(shipName + "/" + n, out e) || Table.TryGetValue("*/" + n, out e);
    }

    // 船の名前に載っている表の名前 (確認用・どの船にもある飾りは除く)
    internal static IEnumerable<string> NamesFor(string shipName)
    {
        string head = shipName + "/";
        foreach (var k in Table.Keys)
            if (k.StartsWith(head, StringComparison.Ordinal)) yield return k.Substring(head.Length);
        foreach (var k in Still.Keys)
            if (k.StartsWith(head, StringComparison.Ordinal)) yield return k.Substring(head.Length);
    }

    internal static string Strip(string name)
    {
        int i = name.LastIndexOf(" copy", StringComparison.Ordinal);
        if (i > 0) name = name.Substring(0, i);
        int paren = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (paren > 0 && name.EndsWith(")")) name = name.Substring(0, paren);
        // コマ送りで絵が替わる家具は、拾った時の絵の名前の番号が物ごとに違う
        if (name.StartsWith("transformer", StringComparison.Ordinal)) return "transformer";
        return name;
    }
}
