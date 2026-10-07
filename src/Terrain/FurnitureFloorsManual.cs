using System.Collections.Generic;

namespace MoreRolesPlus.Terrain;

// 部屋の絵に描き込まれた家具のうち、tools/measure-room-floors.py が測れなかった物の床 (手で測った値)。
// 道具の表 (FurnitureFloors.Table) より先に引く。道具が書き直すのは Table だけ
internal static partial class FurnitureFloors
{
    internal static readonly Dictionary<string, FurnitureLift.Floor> Manual = new()
    {
        // Skeld 医務室のベッド: 置き場の床は縦に変わらない縦じま (同じ列を、ベッドの無い下の帯の行から引く)。
        // 下にはベッドが落とす影の四角 (約 0.24)・上は棚やほかのベッドの影・横は壁と仕切りがすぐ隣
        ["room_med/medBay_bed1"] = new FurnitureLift.Floor(0f, 98f, 0f, 90f, 0.05f, 0.02f, 0.25f, 0.07f),
        ["room_med/medBay_bed1 (2)"] = new FurnitureLift.Floor(0f, 98f, 0f, 90f, 0.05f, 0.02f, 0.25f, 0.07f),
        ["room_med/medBay_bed1 (1)"] = new FurnitureLift.Floor(0f, 98f, 0f, 90f, 0.02f, 0.09f, 0.25f, 0.07f),
        ["room_med/medBay_bed1 (3)"] = new FurnitureLift.Floor(0f, 98f, 0f, 90f, 0.02f, 0.09f, 0.25f, 0.07f),
    };
}
