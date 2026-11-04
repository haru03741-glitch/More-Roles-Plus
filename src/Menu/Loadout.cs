using System;
using System.IO;
using System.Text.Json;
using BepInEx;
using MoreRolesPlus.Fx;
using MoreRolesPlus.Net;

namespace MoreRolesPlus.Menu;

// エモートの扇の 4 つの枠と、会議のスタンプの 8 つの枠に、それぞれ何を入れるか (自分の端末に控える)。
// 持っていない物 (ショップで買っていない物) は入れられず、入っていても初めの物に戻して使う
internal static class Loadout
{
    public const int EmoteSlots = 4, StampSlots = 8;

    private sealed class Data
    {
        public int[] Emotes { get; set; } = { 0, 1, 2, 3 };
        public int[] Stamps { get; set; } = { 0, 1, 2, 3, 4, 5, 6, 7 };
    }

    private static Data _data;
    public static int Version { get; private set; }

    private static string FilePath => Path.Combine(Paths.ConfigPath, "MoreRolesPlus", "loadout.json");

    private static Data D
    {
        get
        {
            if (_data != null) return _data;
            try
            {
                if (File.Exists(FilePath)) _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath));
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"loadout load: {e.Message}"); }
            _data ??= new Data();
            if (_data.Emotes == null || _data.Emotes.Length != EmoteSlots) _data.Emotes = new Data().Emotes;
            if (_data.Stamps == null || _data.Stamps.Length != StampSlots) _data.Stamps = new Data().Stamps;
            return _data;
        }
    }

    private static void Save()
    {
        Version++;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_data));
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"loadout save: {e.Message}"); }
    }

    // 枠に入っているエモート (持っていなければ初めの物)
    public static byte Emote(int slot)
    {
        int k = D.Emotes[slot];
        return (byte)(k is >= 0 and < EmoteArt.Count && MrpAccount.OwnsEmote(k) ? k : slot);
    }

    public static byte Stamp(int slot)
    {
        int k = D.Stamps[slot];
        return (byte)(k is >= 0 and < MeetingStamps.Count && MrpAccount.OwnsStamp(k) ? k : slot);
    }

    // 枠に入れる。同じ物が別の枠にあれば入れ替える
    public static bool SetEmote(int slot, int kind)
    {
        if (slot is < 0 or >= EmoteSlots || kind is < 0 or >= EmoteArt.Count || !MrpAccount.OwnsEmote(kind)) return false;
        Swap(D.Emotes, slot, kind, Emote);
        Save();
        return true;
    }

    public static bool SetStamp(int slot, int kind)
    {
        if (slot is < 0 or >= StampSlots || kind is < 0 or >= MeetingStamps.Count || !MrpAccount.OwnsStamp(kind)) return false;
        Swap(D.Stamps, slot, kind, Stamp);
        Save();
        return true;
    }

    private static void Swap(int[] slots, int slot, int kind, Func<int, byte> now)
    {
        int old = now(slot);
        for (int i = 0; i < slots.Length; i++)
            if (i != slot && now(i) == kind) slots[i] = old;
        slots[slot] = kind;
    }
}
