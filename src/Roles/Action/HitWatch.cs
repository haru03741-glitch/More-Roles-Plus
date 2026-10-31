using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Roles;

// 確かめ用: 刀の絵が相手の体の絵に重なったのに、ホストの当たりが外れになった振りをログへ出す (On の時だけ・ホストの端末)。
// 絵の重なりは SamuraiBlade が描くたびに、当たり外れは Samurai.HostHit が振り終わりに決めた時に知らせる (その振りの間の重なりだけを突き合わせる)
internal static class HitWatch
{
    internal static bool On;
    internal static int Contacts, Misses, Mismatches;

    private const float Slack = 0.5f; // 一振りの前の、ゆっくり動かした間の重なりも拾う (重なった時に一振りの中だったかは note に)
    private static readonly Dictionary<int, float> LastContact = new();
    private static readonly Dictionary<int, string> ContactNote = new();

    private static int Key(byte a, byte t) => a << 8 | t;

    internal static void Contact(byte a, byte t, string note)
    {
        float now = Time.time;
        int k = Key(a, t);
        if (!LastContact.TryGetValue(k, out float last) || now - last > 0.05f) Contacts++;
        LastContact[k] = now;
        ContactNote[k] = note;
    }

    // secs = その振りの長さ (これより前の重なりは別の振りの物)
    internal static void Miss(byte a, byte t, string note, float secs)
    {
        float now = Time.time;
        int k = Key(a, t);
        Misses++;
        if (LastContact.TryGetValue(k, out float c) && now - c <= secs + Slack)
        {
            LastContact.Remove(k);
            Report(a, t, note, ContactNote.TryGetValue(k, out var cn) ? cn : "-", c - now);
        }
    }

    internal static void Hit(byte a, byte t)
    {
        LastContact.Remove(Key(a, t));
    }

    // dt = 絵が重なった時刻 − 外れと決めた時刻
    private static void Report(byte a, byte t, string miss, string contact, float dt)
    {
        Mismatches++;
        Plugin.Logger.LogWarning($"HITWATCH {a}->{t} dt={dt:0.00} miss[{miss}] contact[{contact}]");
    }

    internal static string Describe() => $"hitwatch on={On} contacts={Contacts} misses={Misses} mismatches={Mismatches}";
}
