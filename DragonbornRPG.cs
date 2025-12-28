using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using MySqlConnector;
using Microsoft.Extensions.Logging;
using Dapper;
using System.Text.Json;
using DragonbornRPG;

namespace DragonbornRPG;

[MinimumApiVersion(80)]
public class DragonbornRPGPlugin : BasePlugin, IPluginConfig<AdminControlConfig>
{
    public override string ModuleName => "Dragonborn RPG Plugin";
    public override string ModuleVersion => "2.0.0";
    public override string ModuleAuthor => "Annabel, dominhohobby, aurorapar/show_inferno.cs";
    public override string ModuleDescription => "RPG system with XP, levels, and talents for Dragonborn players.";

    private MySqlConnection _connection = null!;
    private Dictionary<ulong, PlayerStats> _playerStats = new();
    public AdminControlConfig Config { get; set; } = new();

    public void OnConfigParsed(AdminControlConfig config)
    {
        Config = config;
    }

    public override void Load(bool hotReload)
    {
        EnsureSharedConfigFilesExist();

        string connectionString = $"server={Config.Host};uid={Config.User};pwd={Config.Password};database={Config.Database}";
        Logger.LogInformation("Connecting to MySQL: {ConnectionString}", connectionString);

        _connection = new MySqlConnection(connectionString);
        _connection.Open();

        Task.Run(async () =>
        {
            await _connection.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS `rpg_players` (
                    `steamid` BIGINT UNSIGNED NOT NULL,
                    `xp` INT NOT NULL DEFAULT 0,
                    `level` INT NOT NULL DEFAULT 1,
                    `last_inferno` DATETIME,
                    PRIMARY KEY (`steamid`)
                );");
        });
    }

    private void EnsureSharedConfigFilesExist()
    {
        var configsDir = Path.Combine(ModuleDirectory, "../../configs/plugins/");
        Directory.CreateDirectory(configsDir);

        var configPath = Path.Combine(configsDir, "DragonbornRPG.json");

        if (!File.Exists(configPath))
        {
            var defaultConfig = new AdminControlConfig();
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(defaultConfig, options);
            File.WriteAllText(configPath, json);
            Logger.LogInformation("✅ Config file created at: {Path}", configPath);
        }
        else
        {
            Logger.LogInformation("ℹ️ Config file already exists: {Path}", configPath);
        }
    }

    [GameEventHandler]
    public HookResult OnPlayerConnect(EventPlayerConnect @event, GameEventInfo info)
    {
        var steamId = @event.Userid?.AuthorizedSteamID?.SteamId64;
        if (steamId == null) return HookResult.Continue;

        Task.Run(async () =>
        {
            var stats = await _connection.QueryFirstOrDefaultAsync<PlayerStats>(
                "SELECT * FROM rpg_players WHERE steamid = @SteamId;",
                new { SteamId = steamId });

            if (stats == null)
            {
                stats = new PlayerStats { SteamId = steamId.Value };
                await _connection.ExecuteAsync(
                    "INSERT INTO rpg_players (steamid) VALUES (@SteamId);",
                    new { SteamId = steamId });
            }

            _playerStats[steamId.Value] = stats;
        });

        return HookResult.Continue;
    }

    [ConsoleCommand("dragonborn_inferno", "Cast Dragonborn Inferno")]
    public void OnInfernoCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null) return;

        var steamId = player.AuthorizedSteamID?.SteamId64;
        if (steamId == null || !_playerStats.ContainsKey(steamId.Value)) return;

        var stats = _playerStats[steamId.Value];
        var now = DateTime.UtcNow;

        if (stats.LastInferno != null && (now - stats.LastInferno.Value).TotalSeconds < 30)
        {
            player.PrintToChat("⏳ Inferno is recharging!");
            return;
        }

        stats.LastInferno = now;
        stats.XP += 10;

        if (stats.XP >= stats.Level * 100)
        {
            stats.XP -= stats.Level * 100;
            stats.Level++;
            player.PrintToChat($"🎉 Level up! You are now level {stats.Level}!");
        }

        Task.Run(async () =>
        {
            await _connection.ExecuteAsync(@"
                UPDATE rpg_players SET xp = @XP, level = @Level, last_inferno = @LastInferno WHERE steamid = @SteamId;",
                new
                {
                    XP = stats.XP,
                    Level = stats.Level,
                    LastInferno = stats.LastInferno,
                    SteamId = steamId
                });
        });

        player.PrintToChat("🔥 Dragonborn Inferno cast!");
        Server.ExecuteCommand($"playgamesound \"dragonborn_inferno_cast.wav\"");
    }

    [ConsoleCommand("dragonborn_stats", "Show your RPG stats")]
    public void OnStatsCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null) return;

        var steamId = player.AuthorizedSteamID?.SteamId64;
        if (steamId == null || !_playerStats.ContainsKey(steamId.Value)) return;

        var stats = _playerStats[steamId.Value];
        player.PrintToChat($"📊 Level: {stats.Level} | XP: {stats.XP}/{stats.Level * 100}");
    }

}
