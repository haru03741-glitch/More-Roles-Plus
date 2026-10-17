// Based on the auto rejoin of https://github.com/Gurge44/EndlessHostRoles and https://github.com/AU-Avengers/TOU-Mira
// (Modules/AutoRejoin) (GPL-3.0), both after https://github.com/Maxi0fc/AutoRejoin
using System;
using HarmonyLib;
using InnerNet;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoreRolesPlus.Menu;

// 部屋へ自動で戻る (端末ごとの設定)。
// - 試合の終わりの画面で数秒待って「もう一度プレイ」を押す (本編のボタンと同じ処理)。終了を押せば取りやめ。
// - オンラインの部屋で回線の都合で切れた時 (時間切れ・通信の誤り・裏へ回ったなど) は、メインメニューに戻って
//   落ち着いてから同じ部屋のコードへ 1 回だけ入り直す。キック・BAN・自分で抜けた時・部屋の放置切れでは入り直さない。
// 見るのは FixedUpdate で、待っている時だけ時刻を比べる
internal static class AutoRejoin
{
    private const float PlayAgainSeconds = 5f;
    private const float SettleSeconds = 3f, GiveUpSeconds = 30f;

    // ---- 終わりの画面から戻る ----
    private static EndGameManager _egm;
    private static float _playAgainAt;
    private static TextMeshPro _countdown;
    private static int _shownSec = -1;

    public static void OnEndScreen(EndGameManager egm)
    {
        if (!ClientOptions.AutoPlayAgain.Value || !egm || !AmongUsClient.Instance) return;
        if (AmongUsClient.Instance.NetworkMode == NetworkModes.FreePlay) return;
        _egm = egm;
        _playAgainAt = Time.realtimeSinceStartup + PlayAgainSeconds;
        _shownSec = -1;
        if (egm.WinText)
        {
            var go = Object.Instantiate(egm.WinText.gameObject, egm.transform);
            go.name = "MrpAutoPlayAgain";
            go.transform.localPosition = egm.WinText.transform.localPosition + new Vector3(0f, -4.2f, 0f);
            go.transform.localScale = egm.WinText.transform.localScale * 0.3f;
            _countdown = go.GetComponent<TextMeshPro>();
            _countdown.color = Color.white;
        }
    }

    // ---- 切れた部屋へ入り直す ----
    private static int _rejoinId = -1;
    private static float _rejoinSince, _cleanSince;
    private static bool _joinRequested;

    private static bool Recoverable(DisconnectReasons r) => r is DisconnectReasons.Error or DisconnectReasons.ClientTimeout
        or DisconnectReasons.FocusLost or DisconnectReasons.FocusLostBackground or DisconnectReasons.Unknown or DisconnectReasons.ServerError;

    public static void OnExit(DisconnectReasons reason)
    {
        // 自分で抜けた (終わりの画面の終了も) = 自動の戻りは全部取りやめ
        _egm = null;
        if (_countdown) Object.Destroy(_countdown.gameObject);
        _countdown = null;
        var c = AmongUsClient.Instance;
        if (!ClientOptions.Reconnect.Value || !c || c.NetworkMode != NetworkModes.OnlineGame || c.GameId == 0 || !Recoverable(reason)) return;
        if (_rejoinId != -1) return; // 入り直しの途中で切れた時は繰り返さない
        _rejoinId = c.GameId;
        _rejoinSince = Time.realtimeSinceStartup;
        _cleanSince = 0f;
        _joinRequested = false;
        Plugin.Logger.LogInfo($"auto rejoin: disconnected ({reason}), will rejoin {GameCode.IntToGameName(_rejoinId)}");
    }

    public static void OnJoined()
    {
        if (_rejoinId != -1 && AmongUsClient.Instance && AmongUsClient.Instance.GameId == _rejoinId)
            Plugin.Logger.LogInfo($"auto rejoin: back in {GameCode.IntToGameName(_rejoinId)}");
        _rejoinId = -1;
    }

    public static void Tick()
    {
        if (_egm == null && _rejoinId == -1) return;
        float now = Time.realtimeSinceStartup;

        if (_egm != null)
        {
            if (!_egm) { _egm = null; _countdown = null; }
            else
            {
                int sec = (int)Math.Ceiling(_playAgainAt - now);
                if (_countdown && sec != _shownSec && sec >= 0)
                {
                    _shownSec = sec;
                    _countdown.text = Lang.IsJapanese ? $"{sec} 秒後にロビーへ戻ります" : $"Back to the lobby in {sec}s";
                }
                if (now >= _playAgainAt)
                {
                    var egm = _egm;
                    _egm = null;
                    try { if (egm.Navigation) egm.Navigation.NextGame(); }
                    catch (Exception e) { Plugin.Logger.LogError($"auto play again: {e.Message}"); }
                }
            }
        }

        if (_rejoinId == -1) return;
        if (now - _rejoinSince > GiveUpSeconds)
        {
            Plugin.Logger.LogInfo($"auto rejoin: gave up {GameCode.IntToGameName(_rejoinId)}");
            _rejoinId = -1;
            return;
        }
        if (_joinRequested) return;
        // 前の接続の後片付けの最中に入ると公式サーバーに切られるので、メインメニューが続いてから入る
        var c = AmongUsClient.Instance;
        bool clean = c && c.GameState == InnerNetClient.GameStates.NotJoined && !LobbyBehaviour.Instance
                     && Object.FindObjectOfType<MainMenuManager>();
        if (!clean) { _cleanSince = 0f; return; }
        if (_cleanSince == 0f) _cleanSince = now;
        if (now - _cleanSince < SettleSeconds) return;
        _joinRequested = true;
        Plugin.Logger.LogInfo($"auto rejoin: joining {GameCode.IntToGameName(_rejoinId)}");
        c.StartCoroutine(c.CoFindGameInfoFromCodeAndJoin(_rejoinId));
    }

    internal static string Describe() =>
        $"OK rejoin playAgain={ClientOptions.AutoPlayAgain.Value} reconnect={ClientOptions.Reconnect.Value} pendingEnd={(_egm != null)} rejoinId={_rejoinId} requested={_joinRequested}";
}

[HarmonyPatch]
internal static class AutoRejoinPatches
{
    [HarmonyPatch(typeof(EndGameManager), nameof(EndGameManager.Start)), HarmonyPostfix]
    public static void EndStart(EndGameManager __instance)
    {
        try { AutoRejoin.OnEndScreen(__instance); }
        catch (Exception e) { Plugin.Logger.LogError($"auto play again: {e.Message}"); }
    }

    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.ExitGame)), HarmonyPrefix]
    public static void Exit([HarmonyArgument(0)] DisconnectReasons reason)
    {
        try { AutoRejoin.OnExit(reason); }
        catch (Exception e) { Plugin.Logger.LogError($"auto rejoin: {e.Message}"); }
    }

    [HarmonyPatch(typeof(AmongUsClient), "OnGameJoined"), HarmonyPostfix]
    public static void Joined() => AutoRejoin.OnJoined();
}
