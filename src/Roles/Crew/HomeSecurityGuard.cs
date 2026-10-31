// Ported from https://github.com/ykundesu/SuperNewRoles SuperNewRoles/Roles/CrewMate/HomeSecurityGuard.cs (GPL-3.0)
using UnityEngine;

namespace MoreRolesPlus.Roles.Crew;

// 自宅警備員 (クルー): 能力もタスクも無い。タスク欄には自宅警備の「お仕事」が 1 つ書かれ、会議の後に変わる
public sealed class HomeSecurityGuard : RoleBase
{
    public override Team Team => Team.Crew;
    public override string Color => "#00FF00";
    public override Text Name => new("HomeSecurityGuard.Name");
    public override string Reading => "じたくけいびいん";
    public override Text Blurb => new("HomeSecurityGuard.Blurb");
    public override string Origin => "SNR";
    public override bool TasksCount => false;

    private const int TextCount = 12;
    private ImportantTextTask _task;
    private int _index = -1;

    public override void OnAssigned()
    {
        if (!IsLocal) return;
        PortKit.After(0.5f, () =>
        {
            if (RoleState.Local != this) return;
            SnrKit.AssignOwnTasks(0, 0, 0);
            NextText();
        });
        Lifespan.OnRelease(() =>
        {
            if (!_task) return;
            if (Player && Player.myTasks != null) Player.myTasks.Remove(_task.Cast<PlayerTask>());
            Object.Destroy(_task.gameObject);
            _task = null;
        });
    }

    [LocalOnly]
    private void OnMeetingEnd(MeetingEndEvent e)
    {
        if (PortKit.Alive(Player)) NextText();
    }

    // 今と違う「お仕事」を選んでタスク欄に書く
    private void NextText()
    {
        int next;
        do next = Random.Range(0, TextCount);
        while (next == _index);
        _index = next;
        if (!_task)
        {
            var go = new GameObject("HomeSecurityGuardTask");
            go.transform.SetParent(Player.transform, false);
            _task = go.AddComponent<ImportantTextTask>();
            _task.HasLocation = false;
        }
        var t = _task.Cast<PlayerTask>();
        if (!Player.myTasks.Contains(t)) Player.myTasks.Insert(Mathf.Min(1, Player.myTasks.Count), t);
        _task.Text = $"<color={Color}>{new Text($"HomeSecurityGuard.Task{_index + 1}")}</color>";
    }
}
