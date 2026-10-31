// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/Impostor/Mushroomer.cs, Roles/Ability/MushroomerAbility.cs,
// CustomObject/CustomSpores.cs, Modules/MapLoader.cs, MapCustoms/5_TheFungle/HideSporeMask.cs (GPL-3.0)
using System.Collections;
using System.Collections.Generic;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using MoreRolesPlus.Net;
using MoreRolesPlus.Options;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace MoreRolesPlus.Roles.Impostor;

// マッシュルーマー (インポスター): 「植える」で足元にファングルのキノコを植える (次の会議の後から胞子が出る)。
// 植え終わったら「爆発」で、船のキノコから一斉に胞子を出させる。どのマップでも使える
public sealed class Mushroomer : RoleBase
{
    public override Team Team => Team.Impostor;
    public override string Color => "#FF1919";
    public override Text Name => new("Mushroomer.Name");
    public override string Reading => "まっしゅるーまー";
    public override Text Blurb => new("Mushroomer.Blurb");
    public override string Origin => "SNR";
    public override HelpSection[] HelpSections => new HelpSection[] { new("Mushroomer.Plant", "snr_mushroom"), new("Mushroomer.Explode", "snr_spore") };

    public static readonly BoolOpt HasGasMask = new("Mushroomer.HasGasMask", true);
    public static readonly FloatOpt PlantCooldown = new("Mushroomer.PlantCooldown", 20f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt PlantCount = new("Mushroomer.PlantCount", 3, 1, 10);
    public static readonly FloatOpt ExplodeCooldown = new("Mushroomer.ExplodeCooldown", 30f, 2.5f, 60f, 2.5f, "s");
    public static readonly IntOpt ExplodeCount = new("Mushroomer.ExplodeCount", 2, 1, 10);
    public static readonly FloatOpt ExplodeRange = new("Mushroomer.ExplodeRange", 0.5f, 0.25f, 3f, 0.25f, "x");
    public static readonly FloatOpt ExplodeDuration = new("Mushroomer.ExplodeDuration", 2.5f, 0.5f, 10f, 0.5f, "s");
    public static readonly BoolOpt ActiveUsedMushroom = new("Mushroomer.ActiveUsedMushroom", true);

    private Ability _plant;
    private readonly List<Mushroom> _nextTurn = new();

    public override void OnAssigned()
    {
        _plant = AddAbility(new Ability(new Text("Mushroomer.Plant"), "snr_mushroom")
        {
            Cooldown = () => PlantCooldown,
            MaxUses = () => PlantCount,
            OnUse = () =>
            {
                if (!Player.CanMove) return false;
                Vector2 at = Player.transform.position;
                PlantCall.Send(at);
                Plant(Player, at);
                return true;
            },
        });
        AddAbility(new Ability(new Text("Mushroomer.Explode"), "snr_spore")
        {
            Cooldown = () => ExplodeCooldown,
            MaxUses = () => ExplodeCount,
            OnUse = () =>
            {
                if (!Player.CanMove || _plant.UsesLeft > 0 || _nextTurn.Count > 0) return false;
                ExplodeCall.Send(0);
                Explode();
                return true;
            },
        });
        Lifespan.OnRelease(Spores.Clear);
    }

    // 会議の後、植えたキノコが動き出す
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        foreach (var m in _nextTurn) SetActive(m, true);
        _nextTurn.Clear();
    }

    private static void SetActive(Mushroom m, bool active)
    {
        if (!m) return;
        m.gameObject.SetActive(true);
        m.enabled = active;
        var r = m.GetComponent<SpriteRenderer>();
        if (r) r.color = new Color(1f, 1f, 1f, active ? 1f : 0.6f);
    }

    private static readonly RemoteCall<Vector2> PlantCall = new("Mushroomer.Plant", Route.Anyone,
        (w, at) => { w.Write(at.x); w.Write(at.y); }, r => new Vector2(r.ReadSingle(), r.ReadSingle()), (sender, at) => Plant(sender, at));
    private static readonly RemoteCall<byte> ExplodeCall = new("Mushroomer.Explode", Route.Anyone,
        (w, _) => w.Write((byte)0), r => r.ReadByte(), (_, _) => Explode());

    private static void Plant(PlayerControl owner, Vector2 at)
    {
        if (RoleState.Of(owner) is not Mushroomer mu) return;
        Spores.Add(at, m =>
        {
            if (!m) return;
            m.gameObject.SetActive(false);
            if (m.mushroom) m.mushroom.enabled = !ActiveUsedMushroom || owner.AmOwner;
            mu._nextTurn.Add(m);
            if (PortKit.IsImpostor(PlayerControl.LocalPlayer)) SetActive(m, false);
            else m.gameObject.SetActive(false);
        });
    }

    // 船の動いているキノコから一斉に胞子を出す
    private static void Explode()
    {
        var fungle = ShipStatus.Instance ? ShipStatus.Instance.TryCast<FungleShipStatus>() : null;
        if (fungle != null)
        {
            foreach (var kv in fungle.sporeMushrooms)
                if (kv.Value && kv.Value.isActiveAndEnabled) Trigger(kv.Value);
        }
        else
        {
            foreach (var m in Spores.All.Values)
                if (m && m.isActiveAndEnabled) Trigger(m);
        }
    }

    private static void Trigger(Mushroom m)
    {
        m.secondsSporeIsActive = ExplodeDuration;
        m.transform.localScale = Vector3.one * ExplodeRange;
        m.TriggerSpores();
        if (m.sporeMask)
        {
            m.sporeMask.gameObject.SetActive(true);
            m.sporeMask.transform.localScale = MaskScale(m);
        }
    }

    // 胞子の霧の型の元の大きさ。型は裏面を描かないので、船が左右反転していたら横を負にして表向きに保つ
    internal static Vector3 MaskScale(Mushroom m)
    {
        var l = m.transform.lossyScale;
        return new Vector3(l.x * l.y < 0f ? -2.4f : 2.4f, 2.4f, 1.2f);
    }

    // ---- ファングル以外の船にキノコを置く (SNR の CustomSpores) ----

    internal static class Spores
    {
        internal static readonly Dictionary<int, Mushroom> All = new();

        internal static void Clear() => All.Clear();

        internal static void Add(Vector2 at, System.Action<Mushroom> done)
        {
            FungleLoader.Load(template =>
            {
                if (!template || !ShipStatus.Instance) { done(null); return; }
                var ship = ShipStatus.Instance.TryCast<FungleShipStatus>();
                int id;
                if (ship != null)
                {
                    id = 0;
                    foreach (var kv in ship.sporeMushrooms) if (kv.Key > id) id = kv.Key;
                    id++;
                }
                else
                {
                    id = 0;
                    foreach (var k in All.Keys) if (k >= id) id = k + 1;
                }
                var m = Object.Instantiate(template, ShipStatus.Instance.transform);
                var pos = new Vector3(at.x, at.y, 1f);
                m.transform.position = pos;
                m.id = id;
                m.origPosition = pos;
                if (m.sporeMask) MapFlip.FaceUp(m.sporeMask.transform);
                if (ship != null) ship.sporeMushrooms.Add(id, m);
                else All[id] = m;
                done(m);
            });
        }

        internal static bool Managed(Mushroom m) => m && All.TryGetValue(m.id, out var x) && x == m;

        internal static readonly RemoteCall<int> TriggerCall = new("Mushroomer.Spore", Route.Anyone,
            (w, id) => w.WritePacked(id), r => r.ReadPackedInt32(), (_, id) => { if (All.TryGetValue(id, out var m) && m) m.TriggerSpores(); });
    }
}

// ファングルの船の部品 (キノコの元) を読み込む。ファングルの試合ならその船のキノコを使う
internal static class FungleLoader
{
    private const int FungleMap = 5;
    private static AsyncOperationHandle<GameObject> _op;
    private static bool _started;
    private static Mushroom _template;
    private static readonly List<System.Action<Mushroom>> Waiting = new();

    internal static void Load(System.Action<Mushroom> done)
    {
        var cur = ShipStatus.Instance ? ShipStatus.Instance.TryCast<FungleShipStatus>() : null;
        if (cur != null)
        {
            done?.Invoke(cur.GetComponentInChildren<Mushroom>(true));
            return;
        }
        if (_template)
        {
            done?.Invoke(_template);
            return;
        }
        if (done != null) Waiting.Add(done);
        if (_started) return;
        var prefabs = AmongUsClient.Instance ? AmongUsClient.Instance.ShipPrefabs : null;
        if (prefabs == null || prefabs.Count <= FungleMap) { Flush(); return; }
        _started = true;
        // 最初に植えた時にだけ読む (船 1 つぶん重いので配られた時には読まない)。試合が終わったら手放す
        _ref = prefabs[FungleMap];
        _op = _ref.LoadAssetAsync<GameObject>();
        RoleState.Match.OnRelease(Release);
        AmongUsClient.Instance.StartCoroutine(Wait().WrapToIl2Cpp());
    }

    private static AssetReference _ref;

    // 試合の終わり (植えたキノコはもう消えている): 読んだ船を手放し、途中で抜けた読み込みの待ちも捨てる
    private static void Release()
    {
        Waiting.Clear();
        _started = false;
        _template = null;
        if (_ref == null) return;
        try { _ref.ReleaseAsset(); }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"mushroomer: release {e.Message}"); }
        _ref = null;
    }

    private static IEnumerator Wait()
    {
        while (_ref != null && !_op.IsDone) yield return null;
        if (_ref == null) yield break; // 読み終わる前に試合が終わった
        _started = false;
        var go = _op.Status == AsyncOperationStatus.Succeeded ? _op.Result : null;
        _template = go ? go.GetComponentInChildren<Mushroom>(true) : null;
        if (!_template) Plugin.Logger.LogWarning("mushroomer: could not load the Fungle mushroom");
        Flush();
    }

    private static void Flush()
    {
        var list = new List<System.Action<Mushroom>>(Waiting);
        Waiting.Clear();
        foreach (var a in list)
        {
            try { a(_template); }
            catch (System.Exception e) { Plugin.Logger.LogError($"mushroomer: {e}"); }
        }
    }
}

// ファングル以外に置いたキノコは本編の胞子の電文で探せないので、踏んだ人が全員に知らせる
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckSporeTrigger))]
internal static class MushroomerSporePatch
{
    public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] Mushroom mushroom)
    {
        if (!Mushroomer.Spores.Managed(mushroom)) return true;
        if (!__instance.AmOwner) return false;
        mushroom.TriggerSpores();
        Mushroomer.Spores.TriggerCall.Send(mushroom.id);
        return false;
    }
}

// 爆発で大きさを変えたキノコを元に戻す
[HarmonyPatch(typeof(Mushroom), nameof(Mushroom.ResetState))]
internal static class MushroomerResetPatch
{
    public static void Postfix(Mushroom __instance)
    {
        __instance.transform.localScale = Vector3.one;
        __instance.secondsSporeIsActive = 5f;
        if (__instance.sporeMask) __instance.sporeMask.transform.localScale = Mushroomer.MaskScale(__instance);
    }
}

// ガスマスク: マッシュルーマーには胞子の霧がかからない
[HarmonyPatch(typeof(Mushroom), nameof(Mushroom.FixedUpdate))]
internal static class MushroomerGasMaskPatch
{
    public static void Postfix(Mushroom __instance)
    {
        if (RoleState.Local is not Mushroomer || !__instance.sporeMask) return;
        if (RoleState.Local is Mushroomer && Mushroomer.HasGasMask) __instance.sporeMask.transform.localScale = Vector3.zero;
        else if (__instance.sporeMask.transform.localScale.x == 0f) __instance.sporeMask.transform.localScale = Mushroomer.MaskScale(__instance);
    }
}
