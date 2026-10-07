using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Terrain;

// 自分の絵と当たり判定を持つ家具 (層 12 と、船の層 9 に入っている物) を、押された時にどう動かすか。
// キー = 船の名前/親の名前/家具の名前 (末尾の " (番号)" を除く)。載っていない家具は動かさない (揺れもしない)。
// 載っている層 9 の家具は歩ける所の地図 (SolidMap) に入れず、壁としても壊さない (動いた後の場所と食い違うため)
internal enum FurnitureKind : byte { Shove, Tip }

internal static class FurnitureKinds
{
    internal static readonly Dictionary<string, FurnitureKind> Table = new()
    {
        ["MiraShip/Admin/admin-tablelower"] = FurnitureKind.Shove, // 下の机
        ["MiraShip/Storage/storage-top"] = FurnitureKind.Tip, // 倉庫の上の棚
        ["MiraShip/Storage/storage-mop"] = FurnitureKind.Tip, // モップ
        ["MiraShip/Storage/storage-box2"] = FurnitureKind.Shove, // 倉庫の木箱
        ["MiraShip/Storage/storage-boxes1"] = FurnitureKind.Shove, // 倉庫の箱の山
        ["MiraShip/Office/computerTableB"] = FurnitureKind.Shove, // パソコン机
        ["MiraShip/Office/computerTableM"] = FurnitureKind.Shove, // 細い机
        ["MiraShip/Office/office-ball"] = FurnitureKind.Shove, // ボール
        ["MiraShip/Office/office-mid"] = FurnitureKind.Shove, // 真ん中の机 (通信の修理の端末ごと)
        ["MiraShip/Comms/comms-top"] = FurnitureKind.Shove, // 通信室の上の机 (端末ごと)
        ["MiraShip/Balcony/weatherPanel"] = FurnitureKind.Shove, // 天気の掲示板
        ["MiraShip/Reactor/reactor-area-upper"] = FurnitureKind.Shove, // 原子炉の上の机 (端末ごと)
        ["MiraShip/Reactor/reactor-desk-lower"] = FurnitureKind.Shove, // 原子炉の下の机
        ["MiraShip/Reactor/reactor-desk-elec"] = FurnitureKind.Shove, // 原子炉の電気の机 (端末ごと)
        ["MiraShip/MedBay/medBayBed"] = FurnitureKind.Shove, // ベッド (縦)
        ["MiraShip/MedBay/medBayBedHort"] = FurnitureKind.Shove, // ベッド (横)
        ["MiraShip/MedBay/medBayBedBottomLeft"] = FurnitureKind.Shove, // ベッド (左下)
        ["MiraShip/MedBay/medBayBedBottom"] = FurnitureKind.Shove, // ベッド (下)
        ["MiraShip/Locker/lockersBench"] = FurnitureKind.Shove, // 更衣室のベンチ
        ["MiraShip/Laboratory/labTable"] = FurnitureKind.Shove, // 研究室の机 (端末ごと)
        ["MiraShip/Laboratory/lab-right"] = FurnitureKind.Shove, // 研究室の右の台
        ["MiraShip/Cafe/Table"] = FurnitureKind.Shove, // 食堂のテーブル (緊急ボタンごと)
        ["MiraShip/Cafe/cafVendingMachRight"] = FurnitureKind.Tip, // 自販機 (右)
        ["MiraShip/Cafe/cafShelf"] = FurnitureKind.Tip, // 食堂の棚
        ["MiraShip/Cafe/cafVendMachbottom"] = FurnitureKind.Tip, // 自販機 (下)
        ["SkeldShip/Ground/nav_chairfront"] = FurnitureKind.Tip, // 操縦室の椅子 (前)
        ["SkeldShip/Ground/nav_chairmid"] = FurnitureKind.Tip, // 操縦室の椅子 (真ん中)
        ["SkeldShip/Ground/nav_chairback"] = FurnitureKind.Tip, // 操縦室の椅子 (後ろ)
        ["PolusShip/Office/caftable"] = FurnitureKind.Shove, // 長机
        ["PolusShip/Office/projector"] = FurnitureKind.Tip, // プロジェクター
        ["PolusShip/Storage/storagebox"] = FurnitureKind.Shove, // 倉庫の箱
        ["PolusShip/Science/sciencetable"] = FurnitureKind.Shove, // 研究所の机
        ["PolusShip/Electrical/barrier"] = FurnitureKind.Tip, // 柵
        ["PolusShip/Electrical/transformer0001"] = FurnitureKind.Shove, // 変圧器
        ["Airship/Vault/vault_dummie"] = FurnitureKind.Tip, // マネキン
        ["FungleShip/HighlandsObstacles/Obstacle1"] = FurnitureKind.Shove, // 高台の障害物 1
        ["FungleShip/HighlandsObstacles/Obstacle2"] = FurnitureKind.Shove, // 高台の障害物 2
        ["FungleShip/HighlandsObstacles/MetalPlate"] = FurnitureKind.Shove, // 高台の金属の板
        ["FungleShip/BeachObstacles/Debris"] = FurnitureKind.Shove, // 浜のがれき
        ["FungleShip/BeachObstacles/MetalPlate_BelowBonfire"] = FurnitureKind.Shove, // たき火の下の金属の板
        ["FungleShip/BeachObstacles/MetalPlate_AboveMeetingRoom"] = FurnitureKind.Shove, // 会議室の上の金属の板
    };

    // 船の名前 ("(Clone)" を除く)
    internal static string ShipName(ShipStatus ship) => ship.name.Replace("(Clone)", "");

    internal static bool TryGet(Transform tr, string shipName, out FurnitureKind kind)
    {
        string name = tr.name;
        int paren = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (paren > 0 && name.EndsWith(")")) name = name.Substring(0, paren);
        return Table.TryGetValue(shipName + "/" + (tr.parent ? tr.parent.name : "") + "/" + name, out kind);
    }
}
