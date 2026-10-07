using System.Collections.Generic;

namespace MoreRolesPlus.Terrain;

// 部屋の絵に描き込まれた家具の跡を埋める床の測り方 (FurnitureLift.Floor)。tools/measure-room-floors.py が作る。
// キー = 部屋の絵の名前/当たり判定の名前 (家具ごと) か 部屋の絵の名前 (部屋の全部の家具)。
// 道具が測れなかった家具は FurnitureFloorsManual.cs (手で測った表) にある
internal static partial class FurnitureFloors
{
    internal static readonly Dictionary<string, FurnitureLift.Floor> Table = new()
    {
        ["room_cafeteria/Table (1)"] = new FurnitureLift.Floor(399f, 589f, 124.38f, 0.00f, 0.3f),
        ["room_cafeteria/Table (3)"] = new FurnitureLift.Floor(200f, 401f, 124.43f, 124.30f, 0.3f),
        ["room_cafeteria/Table (4)"] = new FurnitureLift.Floor(616f, 401f, 123.88f, 124.50f, 0.3f),
    };
}
