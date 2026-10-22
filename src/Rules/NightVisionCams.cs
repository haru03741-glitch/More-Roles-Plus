// Based on https://github.com/TheOtherRolesAU/TheOtherRoles Patches/UsablesPatch.cs (night vision cameras, GPL-3.0)
using System.Collections.Generic;
using UnityEngine;

namespace MoreRolesPlus.Rules;

// 停電中に監視カメラ (Skeld・Polus・Airship のカメラと Fungle の双眼鏡) を見ると暗視になる: 画面の材質を緑を掛ける物に差し替え、
// 写る人を全員同じ色にして帽子・名前・ペットを隠す (誰か分からない)。見ている人の端末の見た目だけで、通信はしない
internal static class NightVisionCams
{
    private const int Every = 10;     // 0.2 秒ごとに見直す (人の見た目は本編が着せ替えのたびに戻すので付け直す)
    private const int PlainSlot = 11; // 塗る色の番号 (本編のライム)。作った色が入っている番号なら空いている番号を探す
    private static readonly Color NightColor = new(0.45f, 1f, 0.5f, 1f);

    private static int _tick;
    private static bool _on;
    private static int _slot;
    private sealed class View
    {
        public MeshRenderer Renderer;
        public Material Original, Night;
    }

    private static readonly List<View> Views = new();
    private static readonly HashSet<byte> HiddenPets = new();
    private static Shader _shader;

    internal static void Tick()
    {
        if (++_tick < Every) return;
        _tick = 0;
        var mg = VanillaTweaks.NightCams && ShipStatus.Instance ? Minigame.Instance : null;
        var views = mg && LightsOut() ? Viewports(mg) : null;
        if (views == null)
        {
            if (_on) Restore();
            else RestoreCosmetics();
            return;
        }
        if (_on && (Views.Count != views.Count || Views[0].Renderer != views[0])) Restore(); // 閉じてすぐ別のカメラを開いた
        if (!_on)
        {
            _on = true;
            _slot = Menu.CustomColors.IsPlainSlot(PlainSlot) ? PlainSlot : Menu.CustomColors.FirstPlainSlot();
            foreach (var v in views) Views.Add(new View { Renderer = v });
        }
        foreach (var v in Views) Tint(v);
        Paint();
    }

    private static bool LightsOut()
    {
        var sys = ShipStatus.Instance.Systems;
        if (!sys.TryGetValue(SystemTypes.Electrical, out var s)) return false;
        var sw = s.TryCast<SwitchSystem>();
        return sw != null && sw.ActualSwitches != sw.ExpectedSwitches;
    }

    private static List<MeshRenderer> Viewports(Minigame mg)
    {
        var list = new List<MeshRenderer>();
        var skeld = mg.TryCast<SurveillanceMinigame>();
        if (skeld != null)
        {
            foreach (var v in skeld.ViewPorts) if (v) list.Add(v);
            return list;
        }
        var planet = mg.TryCast<PlanetSurveillanceMinigame>();
        if (planet != null)
        {
            if (planet.ViewPort) list.Add(planet.ViewPort);
            return list;
        }
        var fungle = mg.TryCast<FungleSurveillanceMinigame>();
        if (fungle != null && fungle.viewport) list.Add(fungle.viewport);
        return list.Count > 0 ? list : null;
    }

    // 画面の材質を、同じ写し (描き先のテクスチャ) に緑を掛ける材質へ差し替える。
    // 本編は通信の妨害で画面の材質を砂嵐に替えるので、写しの材質 (Unlit/Texture) の時だけ差し替える
    private static void Tint(View v)
    {
        var r = v.Renderer;
        if (!r) return;
        var cur = r.sharedMaterial;
        if (!cur || cur == v.Night) return;
        if (cur.shader.name != "Unlit/Texture") return;
        v.Original = cur;
        if (!v.Night)
        {
            var shader = _shader ??= Shader.Find("Sprites/Default");
            if (!shader) return;
            v.Night = new Material(shader) { name = "MrpNightVision" };
            v.Night.color = NightColor;
        }
        v.Night.mainTexture = cur.mainTexture;
        r.sharedMaterial = v.Night;
    }

    // 隠した物だけを覚えて戻す (本編がベントの中などで隠していた物は出さない)
    private static readonly HashSet<byte> HiddenNames = new(), HiddenCosmetics = new(), PendingCosmetics = new();

    private static void Paint()
    {
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.cosmetics == null || p.Data == null) continue;
            var c = p.cosmetics;
            byte id = p.PlayerId;
            if (c.ColorId != _slot) c.SetColor(_slot); // 本編が着せ替えを戻した時だけ塗り直す
            if (HiddenCosmetics.Add(id)) c.SetBodyCosmeticsVisible(false);
            if (c.nameText && c.nameText.gameObject.activeSelf)
            {
                c.nameText.gameObject.SetActive(false);
                HiddenNames.Add(id);
            }
            var pet = c.currentPet;
            if (pet && pet.Visible)
            {
                c.SetPetVisible(false);
                HiddenPets.Add(id);
            }
        }
        foreach (var body in Object.FindObjectsOfType<DeadBody>())
        {
            if (!body) continue;
            foreach (var r in body.bodyRenderers) if (r) PlayerMaterial.SetColors(_slot, r);
        }
    }

    private static void Restore()
    {
        _on = false;
        foreach (var v in Views)
        {
            if (v.Renderer && v.Original && v.Renderer.sharedMaterial == v.Night) v.Renderer.sharedMaterial = v.Original;
            if (v.Night) Object.Destroy(v.Night);
        }
        Views.Clear();
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || p.cosmetics == null || p.Data == null) continue;
            var c = p.cosmetics;
            byte id = p.PlayerId;
            c.SetColor(p.CurrentOutfit.ColorId);
            if (HiddenNames.Contains(id) && c.nameText) c.nameText.gameObject.SetActive(true);
            if (HiddenPets.Contains(id)) c.SetPetVisible(true);
            if (HiddenCosmetics.Contains(id)) PendingCosmetics.Add(id);
        }
        HiddenNames.Clear();
        HiddenPets.Clear();
        HiddenCosmetics.Clear();
        RestoreCosmetics();
        // 死体は本編どおりの塗り方 (グラデーションは付けない) で元の色へ
        foreach (var body in Object.FindObjectsOfType<DeadBody>())
        {
            if (!body) continue;
            var owner = GameData.Instance ? GameData.Instance.GetPlayerById(body.ParentId) : null;
            if (owner == null) continue;
            int id = owner.DefaultOutfit.ColorId;
            foreach (var r in body.bodyRenderers) if (r) PlayerMaterial.SetColors(id, r);
        }
    }

    // 帽子・バイザー・服を戻す。見えていない人 (ベントの中など) は見えるようになってから戻す
    private static void RestoreCosmetics()
    {
        if (PendingCosmetics.Count == 0) return;
        foreach (var p in PlayerControl.AllPlayerControls)
        {
            if (!p || !PendingCosmetics.Contains(p.PlayerId) || p.cosmetics == null) continue;
            if (!p.Visible) continue;
            p.cosmetics.SetBodyCosmeticsVisible(true);
            PendingCosmetics.Remove(p.PlayerId);
        }
        if (!ShipStatus.Instance) PendingCosmetics.Clear();
    }

    internal static bool Active => _on;

    internal static string Describe() => $"OK nightcams on={_on} slot={_slot} views={Views.Count} tinted={Views.FindAll(v => v.Renderer && v.Renderer.sharedMaterial == v.Night).Count} pets={HiddenPets.Count}";
}
