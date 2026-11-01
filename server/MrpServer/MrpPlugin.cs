using Impostor.Api.Config;
using Impostor.Api.Events;
using Impostor.Api.Games.Managers;
using Impostor.Api.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MrpServer;

[ImpostorPlugin("win.nkzk.mrp")]
public sealed class MrpPlugin : PluginBase
{
    private readonly ILogger<MrpPlugin> _log;
    private readonly MrpConfig _cfg;
    private readonly Store _store;
    private readonly AntiCheatConfig _antiCheat;
    private readonly IGameManager _games;

    public MrpPlugin(ILogger<MrpPlugin> log, MrpConfig cfg, Store store, IOptions<AntiCheatConfig> antiCheat, IGameManager games)
    {
        _games = games;
        _log = log;
        _cfg = cfg;
        _store = store;
        _antiCheat = antiCheat.Value;
    }

    public override ValueTask EnableAsync()
    {
        // ロビーでクラシック/かくれんぼを切り替えてもホストが切断されないように
        GameModeFix.Install(_games, _log);

        // Impostor の不正対策は本編に合わせてあり、MRP の通信 (版の確認・設定・役職) を止めたり、役職の動きを不正とみなして追い出したりする
        var ac = _antiCheat;
        if (ac.Enabled)
        {
            if (ac.ForbidProtocolExtensions)
                _log.LogError("MRP: config.json の AntiCheat.ForbidProtocolExtensions が true です。MRP の通信が止められ、ホストに版の確認が届かないのでゲームを開始できません。false にしてください");
            if (ac.EnableRoleChecks || ac.EnableGameFlowChecks)
                _log.LogWarning("MRP: config.json の AntiCheat.EnableRoleChecks / EnableGameFlowChecks が true です。MRP の役職の能力が不正とみなされて追い出されることがあります。false にしてください");
        }

        if (!_cfg.DiscordReady) _log.LogWarning("MRP: {File} の Discord.BotToken が空なので、バグ報告とマッチメイキングの投稿は止めています", MrpConfig.FileName);
        else _log.LogInformation("MRP: バグ報告 = {Report} / マッチメイキング = {Match}", _cfg.Discord.ReportChannelId, _cfg.Discord.MatchmakingChannelId);
        return default;
    }

    public override ValueTask DisableAsync()
    {
        _store.Flush();
        return default;
    }
}

public sealed class MrpStartup : IPluginHttpStartup
{
    public void ConfigureHost(IHostBuilder host) { }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(MrpConfig.Load());
        services.AddSingleton<Store>();
        services.AddSingleton<RateLimiter>();
        services.AddSingleton(sp => new DiscordApi(sp.GetRequiredService<MrpConfig>(), sp.GetRequiredService<ILogger<DiscordApi>>()));
        services.AddSingleton<Reports>();
        services.AddSingleton<Http>();
        services.AddSingleton<IEventListener, PlayerTracker>();
        services.AddSingleton<Matchmaking>();
        services.AddSingleton<IEventListener>(sp => sp.GetRequiredService<Matchmaking>());
        services.AddHostedService(sp => sp.GetRequiredService<Matchmaking>());
        services.AddHostedService<DiscordGateway>();
    }

    public void ConfigureWebApplication(IApplicationBuilder app)
    {
        app.Map("/mrp", branch => branch.Run(ctx => ctx.RequestServices.GetRequiredService<Http>().Handle(ctx)));
    }
}
