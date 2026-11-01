using UnityEngine;

namespace MoreRolesPlus.Roles;

// サムライが刀で光線を押し止めている間: 光線とキャノンの照射を伸ばし、押し切られたら細って消す
internal static partial class NovaFx
{
    // p の今の光線を、今から seconds 秒は照射し続ける (キャノンも撃った形のまま)
    internal static void HoldBeam(PlayerControl p, float seconds)
    {
        float now = Time.time;
        foreach (var b in Beams)
        {
            if (b.P != p) continue;
            float life = now - b.T0 + seconds;
            if (life > b.Life) b.Life = life;
        }
        foreach (var s in Shots)
        {
            if (s.P != p || s.FiredAt < 0f) continue;
            float extra = now - s.FiredAt + seconds - BeamLife;
            if (extra > s.Extra) s.Extra = extra;
        }
    }

    // p の光線を今すぐ細らせて消す (押し切られた・会議)。キャノンはここから冷まして湯気を吐く
    internal static void CutBeam(PlayerControl p)
    {
        float now = Time.time;
        foreach (var b in Beams)
        {
            if (b.P != p) continue;
            float t = now - b.T0;
            if (t < b.Life - Thread) b.Life = t + Thread;
        }
        foreach (var s in Shots)
        {
            if (s.P != p || s.FiredAt < 0f) continue;
            float extra = now - s.FiredAt - BeamLife;
            s.Extra = extra > 0f ? extra : 0f;
        }
    }
}
