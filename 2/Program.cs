using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace DiscordBot
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var bot = new Bot();
            await bot.RunAsync();
        }
    }

    public class Bot
    {
        private readonly DiscordSocketClient _client;
        private readonly CommandService _commands;
        private readonly IServiceProvider _services;

        public Bot()
        {
            _client = new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent
            });

            _commands = new CommandService();

            _services = new ServiceCollection()
                .AddSingleton(_client)
                .AddSingleton(_commands)
                .AddSingleton<RiotModule>()
                .AddSingleton<PingModule>()
                .AddSingleton<AramModule>()
                .BuildServiceProvider();
        }

        public async Task RunAsync()
        {
            _client.Log += LogAsync;
            _client.Ready += OnReadyAsync;
            _client.MessageReceived += HandleCommandAsync;

            await _commands.AddModulesAsync(Assembly.GetEntryAssembly(), _services);

            string token = "your discord token ";  // Discord Token

            await _client.LoginAsync(TokenType.Bot, token);
            await _client.StartAsync();

            await Task.Delay(-1);
        }

        private async Task HandleCommandAsync(SocketMessage messageParam)
        {
            if (!(messageParam is SocketUserMessage message)) return;
            int argPos = 0;

            if (!message.HasCharPrefix('!', ref argPos) || message.Author.IsBot) return;

            var context = new SocketCommandContext(_client, message);
            var result = await _commands.ExecuteAsync(context, argPos, _services);

            if (!result.IsSuccess)
            {
                Console.WriteLine($"Command error: {result.ErrorReason}");
                await context.Channel.SendMessageAsync("Command failed.");
            }
        }

        private Task LogAsync(LogMessage log)
        {
            Console.WriteLine(log.ToString());
            return Task.CompletedTask;
        }

        private Task OnReadyAsync()
        {
            Console.WriteLine("✅ Bot is online!");
            return Task.CompletedTask;
        }
    }

    public class RiotModule : ModuleBase<SocketCommandContext>
    {
        private const string RiotApiKey = "Your Rioat apı";  //Apı

        [Command("lol")]
        public async Task GetPlayerInfo([Remainder] string riotId)
        {
            try
            {
                var parts = riotId.Split('#');
                if (parts.Length != 2)
                {
                    await ReplyAsync("❌ **Invalid Format!** Correct usage: `!lol Username#TAG`\nExample: `!lol Faker#KR1`");
                    return;
                }

                string gameName = parts[0];
                string tagLine = parts[1];
                string fullRiotId = $"{gameName}#{tagLine}";

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("X-Riot-Token", RiotApiKey);

                // 1. Get account info
                var accountResponse = await client.GetAsync($"https://europe.api.riotgames.com/riot/account/v1/accounts/by-riot-id/{gameName}/{tagLine}");
                if (!accountResponse.IsSuccessStatusCode)
                {
                    await ReplyAsync("🔍 **User not found!** Please check the details.");
                    return;
                }

                var accountJson = JObject.Parse(await accountResponse.Content.ReadAsStringAsync());
                string puuid = accountJson["puuid"]?.ToString();

                // 2. Get summoner info
                string foundPlatform = null;
                JObject summonerJson = null;

                foreach (var (platform, _) in new[] { ("kr", "asia"), ("tr1", "europe"), ("euw1", "europe"), ("na1", "americas") })
                {
                    var summonerResp = await client.GetAsync($"https://{platform}.api.riotgames.com/lol/summoner/v4/summoners/by-puuid/{puuid}");
                    if (summonerResp.IsSuccessStatusCode)
                    {
                        foundPlatform = platform;
                        summonerJson = JObject.Parse(await summonerResp.Content.ReadAsStringAsync());
                        break;
                    }
                }

                if (summonerJson == null)
                {
                    await ReplyAsync("⚡ **Couldn't get summoner info!**");
                    return;
                }

                string summonerId = summonerJson["id"]?.ToString();
                string summonerName = summonerJson["name"]?.ToString();
                int summonerLevel = summonerJson["summonerLevel"]?.Value<int>() ?? 0;
                int profileIconId = summonerJson["profileIconId"]?.Value<int>() ?? 0;

                // 3. Get rank info (SoloQ only)
                string rankInfo = "UNRANKED";
                var leagueResponse = await client.GetAsync($"https://{foundPlatform}.api.riotgames.com/lol/league/v4/entries/by-summoner/{summonerId}");

                if (leagueResponse.IsSuccessStatusCode)
                {
                    var leagues = JArray.Parse(await leagueResponse.Content.ReadAsStringAsync());
                    var soloQ = leagues.FirstOrDefault(x => x["queueType"]?.ToString() == "RANKED_SOLO_5x5");

                    if (soloQ != null)
                    {
                        string tier = soloQ["tier"]?.ToString()?.ToUpper();
                        string rank = soloQ["rank"]?.ToString();
                        int lp = soloQ["leaguePoints"]?.Value<int>() ?? 0;
                        rankInfo = $"{tier} {rank} ({lp} LP)";
                    }
                }

                // 4. Get champion mastery (Top 3 Champions)
                var masteryResponse = await client.GetAsync($"https://{foundPlatform}.api.riotgames.com/lol/champion-mastery/v4/champion-masteries/by-puuid/{puuid}/top?count=3");
                var topChampions = new List<string>();
                long totalMasteryPoints = 0;

                if (masteryResponse.IsSuccessStatusCode)
                {
                    var masteries = JArray.Parse(await masteryResponse.Content.ReadAsStringAsync());
                    foreach (var mastery in masteries)
                    {
                        long championId = mastery["championId"]?.Value<long>() ?? 0;
                        long championPoints = mastery["championPoints"]?.Value<long>() ?? 0;
                        totalMasteryPoints += championPoints;

                        // Get champion name from ID
                        var versionsResponse = await client.GetAsync("https://ddragon.leagueoflegends.com/api/versions.json");
                        string latestVersion = versionsResponse.IsSuccessStatusCode
                            ? JArray.Parse(await versionsResponse.Content.ReadAsStringAsync())[0]?.ToString()
                            : "13.24.1";

                        var championsResponse = await client.GetAsync($"https://ddragon.leagueoflegends.com/cdn/{latestVersion}/data/en_US/champion.json");
                        if (championsResponse.IsSuccessStatusCode)
                        {
                            var championsData = JObject.Parse(await championsResponse.Content.ReadAsStringAsync());
                            foreach (var champ in championsData["data"])
                            {
                                if (champ.First["key"]?.ToString() == championId.ToString())
                                {
                                    topChampions.Add(champ.First["name"]?.ToString());
                                    break;
                                }
                            }
                        }
                    }
                }

                // 5. Get last 10 SoloQ matches
                string regionForMatchApi = foundPlatform switch
                {
                    "kr" => "asia",
                    "tr1" or "euw1" => "europe",
                    "na1" => "americas",
                    _ => "europe"
                };

                var matchesResponse = await client.GetAsync($"https://{regionForMatchApi}.api.riotgames.com/lol/match/v5/matches/by-puuid/{puuid}/ids?queue=420&start=0&count=10");
                var matchStats = new StringBuilder();
                int totalKills = 0, totalDeaths = 0, totalAssists = 0, wins = 0, matchCount = 0;

                if (matchesResponse.IsSuccessStatusCode)
                {
                    var matchIds = JArray.Parse(await matchesResponse.Content.ReadAsStringAsync());
                    matchCount = matchIds.Count;

                    foreach (var matchId in matchIds)
                    {
                        var matchResponse = await client.GetAsync($"https://{regionForMatchApi}.api.riotgames.com/lol/match/v5/matches/{matchId}");
                        if (!matchResponse.IsSuccessStatusCode) continue;

                        var matchData = JObject.Parse(await matchResponse.Content.ReadAsStringAsync());
                        var participant = matchData["info"]["participants"].FirstOrDefault(p => p["puuid"]?.ToString() == puuid);

                        if (participant != null)
                        {
                            string champ = participant["championName"]?.ToString();
                            int kills = participant["kills"]?.Value<int>() ?? 0;
                            int deaths = participant["deaths"]?.Value<int>() ?? 0;
                            int assists = participant["assists"]?.Value<int>() ?? 0;
                            bool win = participant["win"]?.Value<bool>() ?? false;

                            totalKills += kills;
                            totalDeaths += deaths;
                            totalAssists += assists;
                            if (win) wins++;

                            matchStats.AppendLine($"{champ}: {kills}/{deaths}/{assists} - {(win ? "✅ Won" : "❌ Lost")}");
                        }
                    }
                }

                // Calculate average KDA
                string kdaString;
                if (matchCount > 0)
                {
                    double avgKills = Math.Round((double)totalKills / matchCount, 1);
                    double avgDeaths = Math.Round((double)totalDeaths / matchCount, 1);
                    double avgAssists = Math.Round((double)totalAssists / matchCount, 1);
                    double kdaRatio = avgDeaths > 0 ? Math.Round((avgKills + avgAssists) / avgDeaths, 2) : avgKills + avgAssists;
                    kdaString = $"{avgKills}/{avgDeaths}/{avgAssists} ({kdaRatio} KDA)";
                }
                else
                {
                    kdaString = "No matches";
                }

                // 6. Build embed
                var embed = new EmbedBuilder()
                    .WithTitle($"📊 {fullRiotId} - Level {summonerLevel}")
                    .WithColor(GetRankColor(rankInfo))
                    .WithThumbnailUrl($"https://ddragon.leagueoflegends.com/cdn/13.24.1/img/profileicon/{profileIconId}.png")
                    .AddField("Rank", rankInfo, true)
                    .AddField("Fav Champions", string.Join(", ", topChampions.Take(3)), true)
                    .AddField("Avg KDA (Last 10)", kdaString, true)
                    .AddField("Win Rate", matchCount > 0 ? $"{wins}/{matchCount} ({Math.Round((double)wins / matchCount * 100)}%)" : "No matches", true)
                    .AddField("Last 10 SoloQ Matches", matchStats.Length > 0 ? matchStats.ToString() : "No matches found", false)
                    .WithFooter($"LoL Stats • {DateTime.Now:dd.MM.yyyy HH:mm} | Region: {foundPlatform.ToUpper()}")
                    .Build();

                await ReplyAsync(embed: embed);
            }
            catch (Exception ex)
            {
                await ReplyAsync($"⚠️ **Error:** {ex.Message}");
                Console.WriteLine(ex);
            }
        }

        private Color GetRankColor(string rank)
        {
            return rank switch
            {
                string r when r.Contains("IRON") => new Color(94, 94, 94),
                string r when r.Contains("BRONZE") => new Color(110, 58, 7),
                string r when r.Contains("SILVER") => new Color(142, 142, 147),
                string r when r.Contains("GOLD") => new Color(212, 175, 55),
                string r when r.Contains("PLATINUM") => new Color(0, 185, 185),
                string r when r.Contains("DIAMOND") => new Color(56, 103, 214),
                string r when r.Contains("MASTER") => new Color(158, 58, 158),
                string r when r.Contains("GRANDMASTER") => new Color(204, 51, 51),
                string r when r.Contains("CHALLENGER") => new Color(11, 163, 207),
                _ => Color.DarkPurple
            };
        }
    }

    public class PingModule : ModuleBase<SocketCommandContext>
    {
        [Command("ping")]
        public async Task PingAsync()
        {
            await ReplyAsync("Pong!");
        }
    }

    public class AramModule : ModuleBase<SocketCommandContext>
    {
        private const string RiotApiKey = "RGAPI-4916ffe4-9240-4d93-b738-ec6e5e15dd2e";

        [Command("aram")]
        public async Task AramInfoAsync([Remainder] string riotId)
        {
            try
            {
                var parts = riotId.Split('#');
                if (parts.Length != 2)
                {
                    await ReplyAsync("❌ **Invalid Format!** Correct usage: `!aram Username#TAG`\nExample: `!aram Faker#KR1`");
                    return;
                }

                string gameName = parts[0];
                string tagLine = parts[1];
                string fullRiotId = $"{gameName}#{tagLine}";

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("X-Riot-Token", RiotApiKey);

                // 1. Get account info
                var accountResponse = await client.GetAsync($"https://europe.api.riotgames.com/riot/account/v1/accounts/by-riot-id/{gameName}/{tagLine}");
                if (!accountResponse.IsSuccessStatusCode)
                {
                    await ReplyAsync("🔍 **User not found!** Please check the details.");
                    return;
                }

                var accountJson = JObject.Parse(await accountResponse.Content.ReadAsStringAsync());
                string puuid = accountJson["puuid"]?.ToString();

                // 2. Get summoner info (for profile icon)
                string foundPlatform = null;
                JObject summonerJson = null;

                foreach (var (platform, _) in new[] { ("kr", "asia"), ("tr1", "europe"), ("euw1", "europe"), ("na1", "americas") })
                {
                    var summonerResp = await client.GetAsync($"https://{platform}.api.riotgames.com/lol/summoner/v4/summoners/by-puuid/{puuid}");
                    if (summonerResp.IsSuccessStatusCode)
                    {
                        foundPlatform = platform;
                        summonerJson = JObject.Parse(await summonerResp.Content.ReadAsStringAsync());
                        break;
                    }
                }

                if (summonerJson == null)
                {
                    await ReplyAsync("⚡ **Couldn't get summoner info!**");
                    return;
                }

                int profileIconId = summonerJson["profileIconId"]?.Value<int>() ?? 0;

                // 3. Get last ARAM match
                string regionForMatchApi = foundPlatform switch
                {
                    "kr" => "asia",
                    "tr1" or "euw1" => "europe",
                    "na1" => "americas",
                    _ => "europe"
                };

                var matchesResponse = await client.GetAsync($"https://{regionForMatchApi}.api.riotgames.com/lol/match/v5/matches/by-puuid/{puuid}/ids?queue=450&start=0&count=1");

                if (!matchesResponse.IsSuccessStatusCode || matchesResponse.Content.Headers.ContentLength == 0)
                {
                    await ReplyAsync("🔍 **No ARAM matches found!** This player hasn't played ARAM recently.");
                    return;
                }

                var matchIds = JArray.Parse(await matchesResponse.Content.ReadAsStringAsync());
                if (matchIds.Count == 0)
                {
                    await ReplyAsync("🔍 **No ARAM matches found!** This player hasn't played ARAM recently.");
                    return;
                }

                var matchResponse = await client.GetAsync($"https://{regionForMatchApi}.api.riotgames.com/lol/match/v5/matches/{matchIds[0]}");
                if (!matchResponse.IsSuccessStatusCode)
                {
                    await ReplyAsync("⚠️ **Couldn't fetch match details!**");
                    return;
                }

                var matchData = JObject.Parse(await matchResponse.Content.ReadAsStringAsync());
                var participant = matchData["info"]["participants"].FirstOrDefault(p => p["puuid"]?.ToString() == puuid);

                if (participant == null)
                {
                    await ReplyAsync("⚠️ **Couldn't find player in match!**");
                    return;
                }

                // Extract match details
                string champ = participant["championName"]?.ToString();
                int kills = participant["kills"]?.Value<int>() ?? 0;
                int deaths = participant["deaths"]?.Value<int>() ?? 0;
                int assists = participant["assists"]?.Value<int>() ?? 0;
                bool win = participant["win"]?.Value<bool>() ?? false;
                int champLevel = participant["champLevel"]?.Value<int>() ?? 0;
                int totalDamage = participant["totalDamageDealtToChampions"]?.Value<int>() ?? 0;
                int healing = participant["totalHeal"]?.Value<int>() ?? 0;
                int damageTaken = participant["totalDamageTaken"]?.Value<int>() ?? 0;
                int creepScore = participant["totalMinionsKilled"]?.Value<int>() ?? 0;
                int visionScore = participant["visionScore"]?.Value<int>() ?? 0;
                int goldEarned = participant["goldEarned"]?.Value<int>() ?? 0;
                double kdaRatio = deaths > 0 ? Math.Round((double)(kills + assists) / deaths, 2) : kills + assists;

                // Get champion image
                var versionsResponse = await client.GetAsync("https://ddragon.leagueoflegends.com/api/versions.json");
                string latestVersion = versionsResponse.IsSuccessStatusCode
                    ? JArray.Parse(await versionsResponse.Content.ReadAsStringAsync())[0]?.ToString()
                    : "13.24.1";

                // Build embed
                var embed = new EmbedBuilder()
                    .WithTitle($"🎯 Last ARAM Match - {fullRiotId}")
                    .WithColor(win ? Color.Green : Color.Red)
                    .WithThumbnailUrl($"https://ddragon.leagueoflegends.com/cdn/{latestVersion}/img/champion/{champ}.png")
                    .AddField("Champion", champ, true)
                    .AddField("Result", win ? "✅ Victory" : "❌ Defeat", true)
                    .AddField("KDA", $"{kills}/{deaths}/{assists}", true)
                    .AddField("KDA Ratio", kdaRatio.ToString("0.00"), true)
                    .AddField("Champ Level", champLevel, true)
                    .AddField("Damage", totalDamage.ToString("N0"), true)
                    .AddField("Healing", healing.ToString("N0"), true)
                    .AddField("Damage Taken", damageTaken.ToString("N0"), true)
                    .AddField("Creep Score", creepScore, true)
                    .AddField("Vision Score", visionScore, true)
                    .AddField("Gold Earned", goldEarned.ToString("N0"), true)
                    .WithFooter($"ARAM Stats • {DateTime.Now:dd.MM.yyyy HH:mm} | Region: {foundPlatform.ToUpper()}")
                    .Build();

                await ReplyAsync(embed: embed);
            }
            catch (Exception ex)
            {
                await ReplyAsync($"⚠️ **Error:** {ex.Message}");
                Console.WriteLine(ex);
            }
        }
    }
}