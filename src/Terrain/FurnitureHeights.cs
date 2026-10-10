using System;
using System.Collections.Generic;

namespace MoreRolesPlus.Terrain;

// 家具の高さと中身。高さは水の深さと同じ物差し (単位・クルーの腰 = 0.35)。
// Solid = 中まで詰まった物 (足元の床が天板の高さまで上がり、水を押しのける。水面が天板を越えると上にも水が乗る)。
// Legs = 脚のある物 (水は下を通る。天板の高さは水に沈んだ見た目だけに使う)。
// Floats = 深い水に浮いて流れに乗る。
// キー = 船の名前/動く物の名前 (末尾の " (番号)" と " copy 番号" を除く)
internal enum FurnitureFill : byte { Solid, Legs }

internal static class FurnitureHeights
{
    internal readonly struct Entry
    {
        public readonly float Top;
        public readonly FurnitureFill Fill;
        public readonly bool Floats;
        public Entry(float top, FurnitureFill fill, bool floats = false) { Top = top; Fill = fill; Floats = floats; }
    }

    private const FurnitureFill S = FurnitureFill.Solid, L = FurnitureFill.Legs;

    private static readonly Dictionary<string, Entry> Table = new()
    {
        // Skeld
        ["SkeldShip/Table"] = new(0.35f, L),                    // 食堂の丸い机
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
        ["MiraShip/caf_table"] = new(0.35f, L),                 // 食堂のテーブル
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
        // Polus
        ["PolusShip/barrier"] = new(0.40f, L),                  // 電気室の柵
        ["PolusShip/transformer0002"] = new(0.60f, S),          // 変圧器
        ["PolusShip/caftable"] = new(0.35f, L),                 // 長机
        ["PolusShip/projector"] = new(0.60f, L),                // プロジェクター
        ["PolusShip/sciencetable"] = new(0.35f, L),             // 研究所の机
        ["PolusShip/storagebox"] = new(0.35f, S, true),         // 倉庫の箱
        ["PolusShip/outsideboxes1-front"] = new(0.35f, S, true),// 屋外の箱
        ["PolusShip/outsideboxes1-right"] = new(0.35f, S, true),
        ["PolusShip/outsideboxes1-back"] = new(0.35f, S, true),
        ["PolusShip/outsideboxes2-box1"] = new(0.35f, S, true),
        ["PolusShip/outsideboxes2-box2"] = new(0.35f, S, true),
        ["PolusShip/outsideboxes2-box3"] = new(0.35f, S, true),
        ["PolusShip/outsideboxes2-canL"] = new(0.30f, S, true), // 屋外の缶
        ["PolusShip/outsideboxes2-canR"] = new(0.30f, S, true),
        // Airship
        ["Airship/vault_dummie"] = new(0.80f, S),               // マネキン
        ["Airship/storage_cargo1"] = new(0.40f, L),             // 貨物室の荷車
        // Fungle
        ["FungleShip/jungle_metalPlate"] = new(0.05f, S),       // 金属の板
        ["FungleShip/jungle_railing"] = new(0.40f, L),          // 高台の柵
        ["FungleShip/jungle_medScanner"] = new(0.50f, S),       // 高台の障害物
        ["FungleShip/jungle_o2plant"] = new(0.50f, S),
        ["FungleShip/bonfireArea-chair1"] = new(0.25f, S, true),// たき火の丸太の椅子
        ["FungleShip/bonfireArea-chair2"] = new(0.25f, S, true),
        ["FungleShip/bonfireArea-chair3"] = new(0.25f, S, true),
    };

    internal static bool TryGet(string shipName, string name, out Entry e) => Table.TryGetValue(shipName + "/" + Strip(name), out e);

    private static string Strip(string name)
    {
        int i = name.LastIndexOf(" copy", StringComparison.Ordinal);
        if (i > 0) name = name.Substring(0, i);
        int paren = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (paren > 0 && name.EndsWith(")")) name = name.Substring(0, paren);
        return name;
    }
}
