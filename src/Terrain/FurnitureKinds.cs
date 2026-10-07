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
        ["MiraShip/Storage/storage-top"] = FurnitureKind.Tip, // 倉庫の奥の棚 (箱の山・棚・緑の箱に分けて動かす = FurnitureSplit)
        ["MiraShip/Storage/storage-top-boxes"] = FurnitureKind.Shove, // 倉庫の奥の左の箱の山
        ["MiraShip/Storage/storage-top-shelf"] = FurnitureKind.Shove, // 倉庫の奥の棚 (壁際なので倒さずにずらす)
        ["MiraShip/Storage/storage-top-crates"] = FurnitureKind.Shove, // 倉庫の奥の右の緑の箱
        ["MiraShip/Storage/storageMid"] = FurnitureKind.Tip, // 倉庫の真ん中の棚 (当たり判定は子)
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
        ["SkeldShip/Ground/storage_Boxes"] = FurnitureKind.Shove, // 倉庫の箱の山 (床の箱・燃料缶・奥の塊に分けて動かす = FurnitureSplit)
        ["SkeldShip/Ground/storage_Boxes-left"] = FurnitureKind.Shove, // 倉庫の左の箱
        ["SkeldShip/Ground/storage_Boxes-front"] = FurnitureKind.Shove, // 倉庫の手前の箱
        ["SkeldShip/Ground/storage_Boxes-can"] = FurnitureKind.Shove, // 倉庫の燃料缶
        ["SkeldShip/Ground/storage_Boxes-stack"] = FurnitureKind.Shove, // 倉庫の台と積まれた箱 (燃料の端末・転がる箱ごと)
        ["SkeldShip/Ground/nav_chairfront"] = FurnitureKind.Tip, // 操縦室の椅子 (前)
        ["SkeldShip/Ground/nav_chairmid"] = FurnitureKind.Tip, // 操縦室の椅子 (真ん中)
        ["SkeldShip/Ground/nav_chairback"] = FurnitureKind.Tip, // 操縦室の椅子 (後ろ)
        ["PolusShip/Office/caftable"] = FurnitureKind.Shove, // 長机
        ["PolusShip/Office/projector"] = FurnitureKind.Tip, // プロジェクター
        ["PolusShip/Storage/storagebox"] = FurnitureKind.Shove, // 倉庫の箱
        ["PolusShip/Science/sciencetable"] = FurnitureKind.Shove, // 研究所の机
        ["PolusShip/Electrical/barrier"] = FurnitureKind.Tip, // 柵
        ["PolusShip/Electrical/transformer0001"] = FurnitureKind.Shove, // 変圧器
        ["PolusShip/RocksNBoxes/boxcluster"] = FurnitureKind.Shove, // 屋外の箱の山 1 (当たり判定と影は子)
        ["PolusShip/RocksNBoxes/boxclust2"] = FurnitureKind.Shove, // 屋外の箱の山 2 (同上)
        ["Airship/Vault/vault_dummie"] = FurnitureKind.Tip, // マネキン
        ["Airship/Storage/storage_cargo1"] = FurnitureKind.Shove, // 貨物室の荷車 (当たり判定と影は子)
        ["FungleShip/HighlandsObstacles/Obstacle1"] = FurnitureKind.Shove, // 高台の障害物 1
        ["FungleShip/HighlandsObstacles/Obstacle2"] = FurnitureKind.Shove, // 高台の障害物 2
        ["FungleShip/HighlandsObstacles/MetalPlate"] = FurnitureKind.Shove, // 高台の金属の板
        ["FungleShip/BeachObstacles/Debris"] = FurnitureKind.Shove, // 浜のがれき
        ["FungleShip/BeachObstacles/MetalPlate_BelowBonfire"] = FurnitureKind.Shove, // たき火の下の金属の板
        ["FungleShip/BeachObstacles/MetalPlate_AboveMeetingRoom"] = FurnitureKind.Shove, // 会議室の上の金属の板
        ["FungleShip/Bonfire/Chairs"] = FurnitureKind.Tip, // たき火の椅子の元の当たり判定 (椅子ごとに分けて動かす = FurnitureSplit)
        ["FungleShip/OutsideBeach/Bonfire-chair1"] = FurnitureKind.Tip, // たき火の椅子 1
        ["FungleShip/OutsideBeach/Bonfire-chair2"] = FurnitureKind.Tip, // たき火の椅子 2
        ["FungleShip/OutsideBeach/Bonfire-chair3"] = FurnitureKind.Tip, // たき火の椅子 3
    };

    // 船の名前 ("(Clone)" を除く)
    internal static string ShipName(ShipStatus ship) => ship.name.Replace("(Clone)", "");

    internal static bool TryGet(Transform tr, string shipName, out FurnitureKind kind) => TryGet(tr, shipName, out kind, out _);

    // owner = 絵を持つ家具。当たり判定が絵の子 (Mira の倉庫の真ん中の棚の "Collider") にある物は、親の名前で引く
    internal static bool TryGet(Transform tr, string shipName, out FurnitureKind kind, out Transform owner)
    {
        owner = tr;
        if (Table.TryGetValue(Key(tr, shipName), out kind)) return true;
        var parent = tr.parent;
        if (parent && Table.TryGetValue(Key(parent, shipName), out kind))
        {
            owner = parent;
            return true;
        }
        return false;
    }

    private static string Key(Transform tr, string shipName)
    {
        string name = tr.name;
        int paren = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (paren > 0 && name.EndsWith(")")) name = name.Substring(0, paren);
        return shipName + "/" + (tr.parent ? tr.parent.name : "") + "/" + name;
    }
}
