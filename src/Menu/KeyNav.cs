using UnityEngine;

namespace MoreRolesPlus.Menu;

// キーボードでメニューのボタンを押す。
//   Space / Enter: マウスを乗せているボタン、無ければ選んでいるボタン (本編のコントローラー用の選び) を押す
//   矢印キー: 選んでいるボタンをその向きのいちばん近いボタンへ移す (光って見える)
// 部屋の中で歩ける間は Space が本編の「使う」・矢印が歩きなので、重なった画面 (確認窓など) が開いている時だけ効かせる
// (会議・作業の画面は押し間違いになるので除く)。
// 字を打っている間は何もしない
internal static class KeyNav
{
    private static int _clickedFrame = -1;

    // Update から
    public static void Tick()
    {
        if (!Input.anyKeyDown) return; // 何も押していないフレームはキーを 1 つずつ読まない
        bool press = Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
        int dx = (Input.GetKeyDown(KeyCode.RightArrow) ? 1 : 0) - (Input.GetKeyDown(KeyCode.LeftArrow) ? 1 : 0);
        int dy = (Input.GetKeyDown(KeyCode.UpArrow) ? 1 : 0) - (Input.GetKeyDown(KeyCode.DownArrow) ? 1 : 0);
        if (!press && dx == 0 && dy == 0) return;
        if (Keybinds.Typing() || UI.TextField.Typing) return;
        var cm = ControllerManager.Instance;
        var st = cm ? cm.CurrentUiState : null;
        bool inRoom = PlayerControl.LocalPlayer && AmongUsClient.Instance && AmongUsClient.Instance.GameState != InnerNet.InnerNetClient.GameStates.NotJoined;
        if (inRoom && (st == null || st.IsScene)) return;
        // 会議 (投票)・作業の画面・追放の場面では押し間違いになるので効かせない
        if (inRoom && (MeetingHud.Instance || Minigame.Instance || ExileController.Instance)) return;

        var sel = st != null ? st.CurrentSelection : null;
        bool valid = sel && sel.isActiveAndEnabled;
        if (dx != 0 || dy != 0)
        {
            if (st == null) return;
            var next = valid ? Nearest(st, sel, new Vector2(dx, dy)) : First(st);
            if (next && next != sel)
            {
                // 本編の選びはコントローラーの時しか光らないので、マウスを乗せた時と同じ光り方 (と音) にする
                if (valid) sel.ReceiveMouseOut();
                cm.SetCurrentSelected(next);
                cm.HighlightSelection(next);
                next.ReceiveMouseOver();
            }
            return;
        }

        // 同じフレームで 2 回押さない (Space と Enter を同時など)
        if (_clickedFrame == Time.frameCount) return;
        var over = PassiveButtonManager.InstanceExists ? PassiveButtonManager.Instance.currentOver : null;
        var target = over && over.isActiveAndEnabled ? over : null;
        // 選びから押すのは、記事の中のリンク (ブラウザーが開く) を除く。リンクはマウスを乗せた時だけ
        if (!target && valid && !sel.name.Contains("Hyperlink")) target = sel.TryCast<PassiveUiElement>();
        if (!target || !target.isActiveAndEnabled) return;
        _clickedFrame = Time.frameCount;
        var button = target.TryCast<PassiveButton>();
        if (button)
        {
            if (button.OnClick == null) return;
            if (button.ClickSound && SoundManager.Instance) SoundManager.Instance.PlaySound(button.ClickSound, false);
            button.OnClick.Invoke();
            return;
        }
        // ボタンでない押せる物 (ニュースの一覧の札など) は、マウスで押して離したのと同じにする
        target.ReceiveClickDown();
        target.ReceiveClickUp();
    }

    // 確かめ用 (ブリッジ): 今の画面の名前と選んでいるボタン
    internal static string Describe()
    {
        var cm = ControllerManager.Instance;
        var st = cm ? cm.CurrentUiState : null;
        if (st == null) return "keynav menu=none";
        var sel = st.CurrentSelection;
        var over = PassiveButtonManager.InstanceExists ? PassiveButtonManager.Instance.currentOver : null;
        return $"keynav menu={st.MenuName} scene={st.IsScene} sel={(sel ? sel.name : "-")} over={(over ? over.name : "-")} count={(st.SelectableUiElements != null ? st.SelectableUiElements.Count : 0)} clicked={_clickedFrame}";
    }

    private static UiElement First(ControllerUiElementsState st)
    {
        var list = st.SelectableUiElements;
        if (list == null) return null;
        UiElement best = null;
        float bestScore = float.MaxValue;
        // 左上から
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            if (!e || !e.isActiveAndEnabled) continue;
            var p = e.transform.position;
            float score = p.x - p.y * 3f;
            if (score < bestScore) { bestScore = score; best = e; }
        }
        return best;
    }

    private static UiElement Nearest(ControllerUiElementsState st, UiElement from, Vector2 dir)
    {
        var list = st.SelectableUiElements;
        if (list == null) return null;
        Vector2 o = from.transform.position;
        UiElement best = null;
        float bestScore = float.MaxValue;
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            if (!e || e == from || !e.isActiveAndEnabled) continue;
            Vector2 d = (Vector2)e.transform.position - o;
            float along = Vector2.Dot(d, dir);
            if (along <= 0.05f) continue;
            float side = Mathf.Abs(dir.x * d.y - dir.y * d.x);
            float score = along + side * 2.5f;
            if (score < bestScore) { bestScore = score; best = e; }
        }
        return best;
    }
}
