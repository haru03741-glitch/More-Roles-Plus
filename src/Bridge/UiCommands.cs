using System;
using System.Diagnostics;
using System.Globalization;
using MoreRolesPlus.UI;
using UnityEngine;

namespace MoreRolesPlus.Bridge;

// 自前の画面の部品 (src/UI) の確認用コマンド
internal static class UiCommands
{
    private static ScrollView _view;
    private static int _clicks;

    public static void Register()
    {
        TestBridge.Register("uiwin", "[open <カード数> | rebuild <カード数> | scroll <0〜1> | close | info] 部品の試作の窓 (カードの縦並び・スクロール欄)。作るのにかかった ms を返す", (args, reply) =>
        {
            var a = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string op = a.Length > 0 ? a[0] : "info";
            int n = a.Length > 1 && int.TryParse(a[1], out int v) ? v : 20;
            switch (op)
            {
                case "open":
                {
                    var sw = Stopwatch.StartNew();
                    var w = UiWindow.Open("Test", new Vector2(8f, 4.6f), () => _view = null);
                    if (w == null) { reply("ERR uiwin not ready " + UiAssets.Diag()); return; }
                    _view = new ScrollView(new Vector2(5f, 3.6f), Cards(n)) { Tag = "uiwin" };
                    var body = new Stack(false,
                        new Label("部品の試作", 2.4f) { Bold = true, MenuFont = true, Color = new Color(1f, 0.85f, 0.3f) },
                        new Stack(true, _view, new Label("右の欄", 1.6f) { Width = 2.4f }) { Gap = 0.2f })
                    { Gap = 0.1f };
                    w.Body.Set(body);
                    reply($"OK uiwin open ms={sw.Elapsed.TotalMilliseconds:0.0} slots={UiActions.Census()}");
                    return;
                }
                case "rebuild":
                {
                    if (_view == null) { reply("ERR uiwin not open"); return; }
                    var sw = Stopwatch.StartNew();
                    _view.SetContent(Cards(n));
                    reply($"OK uiwin rebuild ms={sw.Elapsed.TotalMilliseconds:0.0} slots={UiActions.Census()}");
                    return;
                }
                case "scroll":
                {
                    if (_view?.Scroller == null) { reply("ERR uiwin not open"); return; }
                    float p = a.Length > 1 && float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 1f;
                    _view.Scroller.ScrollPercentY(p);
                    reply($"OK uiwin scroll y={_view.Scroller.Inner.localPosition.y:0.00}");
                    return;
                }
                case "stencil":
                {
                    float id = a.Length > 1 && float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float f1) ? f1 : 1f;
                    float comp = a.Length > 2 && float.TryParse(a[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float f2) ? f2 : 4f;
                    reply("OK uiwin stencil " + UiAssets.DebugStencil(id, comp));
                    return;
                }
                case "click":
                case "hover":
                {
                    if (_view?.Inner == null || !_view.Inner.Alive) { reply("ERR uiwin not open"); return; }
                    var bs = _view.Inner.Root.GetComponentsInChildren<PassiveButton>();
                    if (n < 0 || n >= bs.Length) { reply($"ERR uiwin {op} {n}/{bs.Length}"); return; }
                    if (op == "click") bs[n].OnClick.Invoke();
                    else bs[n].OnMouseOver.Invoke();
                    var back = bs[n].transform.Find("Back")?.GetComponent<SpriteRenderer>();
                    reply($"OK uiwin {op} {n} clicks={_clicks} back={(back ? back.color.ToString() : "-")}");
                    return;
                }
                case "close":
                    UiWindow.Current?.Close();
                    reply($"OK uiwin close slots={UiActions.Census()}");
                    return;
                default:
                    reply($"OK uiwin {UiAssets.Diag()} open={UiWindow.IsOpen} clicks={_clicks} slots={UiActions.Census()}");
                    return;
            }
        });
    }

    private static Widget Cards(int n)
    {
        var col = new Stack(false) { Gap = 0.08f, Width = 4.8f };
        for (int i = 0; i < n; i++)
        {
            int k = i;
            var c = i % 3 == 0 ? new Color(0.45f, 0.45f, 0.45f) : i % 3 == 1 ? Color.white : new Color(1f, 0.85f, 0.2f);
            col.Add(new Button(new Stack(false,
                    new Label($"カード {i + 1}", 2.0f) { Bold = true, MenuFont = true, Color = new Color(0.4f, 0.85f, 1f) },
                    new Label("説明の1行目。長い時は折り返してカードが高くなるかを見るための文です。", 1.2f) { Width = 4.4f }) { Gap = 0.02f, Width = 4.5f },
                () => { _clicks++; Plugin.Logger.LogInfo($"uiwin click {k}"); })
            { Size = new Vector2(4.8f, 0f), Color = c, MinSize = new Vector2(4.8f, 0.5f) });
        }
        return col;
    }
}
