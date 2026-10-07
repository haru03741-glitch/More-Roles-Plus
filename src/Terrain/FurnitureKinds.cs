using System.Collections.Generic;

namespace MoreRolesPlus.Terrain;

// 自分の絵と当たり判定を持つ家具 (層 12) を、押された時にどう動かすか。
// キー = 船の名前/親の名前/家具の名前 (末尾の " (番号)" を除く)。載っていない家具は動かさない (揺れもしない)
internal enum FurnitureKind : byte { Shove, Tip }

internal static class FurnitureKinds
{
    internal static readonly Dictionary<string, FurnitureKind> Table = new()
    {
        ["MiraShip/Admin/admin-tablelower"] = FurnitureKind.Shove, // 下の机
        ["MiraShip/Storage/storage-mop"] = FurnitureKind.Tip, // モップ
        ["MiraShip/Storage/storage-boxes1"] = FurnitureKind.Shove, // 倉庫の箱の山
        ["MiraShip/MedBay/medBayBed"] = FurnitureKind.Shove, // ベッド (縦)
        ["MiraShip/MedBay/medBayBedHort"] = FurnitureKind.Shove, // ベッド (横)
        ["MiraShip/MedBay/medBayBedBottomLeft"] = FurnitureKind.Shove, // ベッド (左下)
        ["MiraShip/MedBay/medBayBedBottom"] = FurnitureKind.Shove, // ベッド (下)
        ["MiraShip/Locker/lockersBench"] = FurnitureKind.Shove, // 更衣室のベンチ
        ["MiraShip/Laboratory/labTable"] = FurnitureKind.Shove, // 研究室の机
        ["MiraShip/Laboratory/lab-right"] = FurnitureKind.Shove, // 研究室の右の台
        ["MiraShip/Cafe/Table"] = FurnitureKind.Shove, // 食堂のテーブル
        ["MiraShip/Cafe/cafVendingMachRight"] = FurnitureKind.Tip, // 自販機 (右)
        ["MiraShip/Cafe/cafShelf"] = FurnitureKind.Tip, // 食堂の棚
        ["MiraShip/Cafe/cafVendMachbottom"] = FurnitureKind.Tip, // 自販機 (下)
        ["PolusShip/Office/caftable"] = FurnitureKind.Shove, // 長机
        ["PolusShip/Office/projector"] = FurnitureKind.Tip, // プロジェクター
        ["PolusShip/Storage/storagebox"] = FurnitureKind.Shove, // 倉庫の箱
        ["PolusShip/Science/sciencetable"] = FurnitureKind.Shove, // 研究所の机
        ["PolusShip/Electrical/barrier"] = FurnitureKind.Tip, // 柵
        ["PolusShip/Electrical/transformer0001"] = FurnitureKind.Shove, // 変圧器
        ["Airship/Vault/vault_dummie"] = FurnitureKind.Tip, // マネキン
    };
}
