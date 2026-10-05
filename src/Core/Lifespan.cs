using System;
using System.Collections.Generic;

namespace MoreRolesPlus;

// 寿命。Release されると、それに付けたイベントの購読・GameObject・後片付けの処理がまとめて終わる。
// 役職 1 人分に 1 つ (RoleBase.Lifespan)・試合 1 つに 1 つ (RoleState.Match) あり、試合が終わる・部屋を抜ける・
// 役職を配り直す時に試合の寿命ごと切れる。役職の中で作った物は役職の寿命に付けておけば消し忘れない。
public sealed class Lifespan
{
    public bool IsDead { get; private set; }

    private List<Lifespan> _children;
    private List<Action> _onRelease;
    private List<UnityEngine.Object> _objects;

    // この寿命が切れたら一緒に切れる寿命
    public Lifespan Child()
    {
        var c = new Lifespan();
        if (IsDead) c.Release();
        else (_children ??= new List<Lifespan>()).Add(c);
        return c;
    }

    // 切れた時に呼ぶ (もう切れていればすぐ呼ぶ)。Unity の UnityEvent にはここで外す処理を書く
    public void OnRelease(Action action)
    {
        if (IsDead) { Invoke(action); return; }
        (_onRelease ??= new List<Action>()).Add(action);
    }

    // 切れた時に Destroy する (GameObject・マテリアル・テクスチャ等)
    public T Bind<T>(T obj) where T : UnityEngine.Object
    {
        if (IsDead) { if (obj) UnityEngine.Object.Destroy(obj); return obj; }
        (_objects ??= new List<UnityEngine.Object>()).Add(obj);
        return obj;
    }

    public void Release()
    {
        if (IsDead) return;
        IsDead = true;
        if (_children != null) foreach (var c in _children) c.Release();
        if (_onRelease != null) foreach (var a in _onRelease) Invoke(a);
        if (_objects != null) foreach (var o in _objects) if (o) UnityEngine.Object.Destroy(o);
        _children = null;
        _onRelease = null;
        _objects = null;
    }

    private static void Invoke(Action a)
    {
        try { a(); }
        catch (Exception e) { Plugin.Logger.LogError($"lifespan release: {e}"); }
    }
}
