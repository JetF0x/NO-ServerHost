using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using System.IO;
using BepInEx;
using UnityEngine;
using HarmonyLib;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using Newtonsoft.Json;
using Steamworks;
using BepInEx.Logging;
using Mirage;
using Mirage.Authentication;
using Mirage.SteamworksSocket;
using Microsoft.Extensions.Logging;
using NuclearOption.SceneLoading;
using static MapSettingsManager;
using Cysharp.Threading.Tasks;
using NetTools.Extensions;
using NetTools.Logger;
namespace JetFoxServer
{
    [BepInPlugin("com.jetfox.server", "JetFox Server", "1.3.6")]
    public class JetFoxServerPlugin : BaseUnityPlugin
    {
        private Harmony _harmony;
        private static JetFoxServerPlugin _instance;
        private static int _currentMissionIndex = 0;
        private static RconServer _rconServer;
        private static bool _isMOTDTaskRunning = false;
        private Config _config;
        private static bool _graphicsSettingsApplied = false;
        private static CancellationTokenSource _motdCancellationTokenSource;
        private static CSteamID _currentLobbyId;
        private int? _pendingPortOverride = null;
        //private static ManualLogSource loggers;
        private static readonly string LogFilePath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "playerLog.json");
        private static readonly ManualLogSource LoggerHook = BepInEx.Logging.Logger.CreateLogSource("PlayerLogger");
        private static readonly string BannedPlayersFilePath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "playerBans.json");

        private Callback<LobbyCreated_t> _lobbyCreatedCallback;

        public MapLoader mapLoader;
        public async void Awake()
        {
            _instance = this;
            Logger.LogInfo("JetFox Server Plugin loaded");


            // Parse and log command-line arguments
            string[] args = Environment.GetCommandLineArgs();
            Logger.LogInfo("Launch arguments: " + string.Join(" ", args));

            // Check for -port=XXXX argument
            string portArg = args.FirstOrDefault(a => a.StartsWith("-port=", StringComparison.OrdinalIgnoreCase));
            if (portArg != null)
            {
                if (int.TryParse(portArg.Substring(6), out int portValue))
                {
                    Logger.LogInfo($"Overriding udpPort and RconPort from launch argument: {portValue}");
                    if (_config != null)
                    {
                        _config.udpPort = portValue.ToString();
                        _config.RconPort = portValue + 1;
                    }
                    else
                    {
                        _pendingPortOverride = portValue;
                    }
                }
                else
                {
                    Logger.LogWarning($"Invalid port value in argument: {portArg}");
                }
            }

            // --- Robust -MissionSelected= parsing (handles spaces and quotes) ---
            string pendingMissionSelected = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("-MissionSelected=", StringComparison.OrdinalIgnoreCase))
                {
                    string value = args[i].Substring(17); // "-MissionSelected=".Length == 16

                    // If it starts with a quote, join until the closing quote is found
                    if (value.StartsWith("\""))
                    {
                        value = value.Substring(1); // Remove leading quote
                        while (!value.EndsWith("\"") && i + 1 < args.Length)
                        {
                            i++;
                            value += " " + args[i];
                        }
                        if (value.EndsWith("\""))
                            value = value.Substring(0, value.Length - 1); // Remove trailing quote
                    }
                    // Remove any leading/trailing whitespace
                    pendingMissionSelected = value.Trim();
                    Logger.LogInfo($"MissionSelected override from argument: {pendingMissionSelected}");
                    break;
                }
            }

            // Check for -LobbyName= argument
            string lobbyNameArg = args.FirstOrDefault(a => a.StartsWith("-LobbyName=", StringComparison.OrdinalIgnoreCase));
            string pendingLobbyNameOverride = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("-LobbyName=", StringComparison.OrdinalIgnoreCase))
                {
                    string value = args[i].Substring(11);

                    // If it starts with a quote, join until the closing quote is found
                    if (value.StartsWith("\""))
                    {
                        value = value.Substring(1); // Remove leading quote
                        while (!value.EndsWith("\"") && i + 1 < args.Length)
                        {
                            i++;
                            value += " " + args[i];
                        }
                        if (value.EndsWith("\""))
                            value = value.Substring(0, value.Length - 1); // Remove trailing quote
                    }
                    else
                    {
                        // If not quoted, just use the value as is
                    }

                    pendingLobbyNameOverride = value.Trim();
                    Logger.LogInfo($"Lobby name override from argument: {pendingLobbyNameOverride}");
                    break;
                }
            }

            // Register the callback for steamLobby
            _lobbyCreatedCallback = Callback<LobbyCreated_t>.Create(OnLobbyCreated);

            // Read configuration from config file
            string configFilePath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "config.txt");
            try
            {
                var json = File.ReadAllText(configFilePath);
                _config = JsonConvert.DeserializeObject<Config>(json);
                if (_config == null || _config.MissionNames == null || _config.MissionNames.Length == 0 || string.IsNullOrEmpty(_config.LobbyName) || _config.PlayerCount <= 0 || _config.TargetFrameRate <= 0 || _config.RconPort <= 0 || string.IsNullOrEmpty(_config.RconPassword))
                {
                    Logger.LogError("Invalid configuration file.");
                    return;
                }

                // Apply pending port override if set
                if (_pendingPortOverride.HasValue)
                {
                    _config.udpPort = _pendingPortOverride.Value.ToString();
                    _config.RconPort = _pendingPortOverride.Value + 1;
                    Logger.LogInfo($"Applied port override from launch argument: udpPort={_config.udpPort}, RconPort={_config.RconPort}");
                }

                // Apply lobby name override if set
                if (!string.IsNullOrEmpty(pendingLobbyNameOverride))
                {
                    _config.LobbyName = pendingLobbyNameOverride;
                }

                // Always append FoxHosting to the lobby name
                const string foxPrefix = "{FoxHosting} ";
                if (!_config.LobbyName.StartsWith(foxPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    _config.LobbyName = foxPrefix + _config.LobbyName.TrimStart();
                }
                Logger.LogInfo($"Final lobby name: {_config.LobbyName}");
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to read configuration from config file: " + ex.Message);
                return;
            }

            // Apply mission selected override if set (always override)
            if (!string.IsNullOrEmpty(pendingMissionSelected))
            {
                _config.MissionNames = new[] { pendingMissionSelected };
                Logger.LogInfo($"MissionSelected override active. Using only: {pendingMissionSelected}");
                _currentMissionIndex = 0; // Always start at 0 if only one mission
            }

            // Start RCON server if not already running
            if (_rconServer == null)
            {
                _rconServer = new RconServer(_config.RconPort, _config.RconPassword);
                _rconServer.Start();
                Logger.LogInfo("Rcon server started on port " + _config.RconPort);
            }
            System.Environment.SetEnvironmentVariable("COMPlus_gcConcurrent", "0");
            System.Environment.SetEnvironmentVariable("COMPlus_gcServer", "1");
            //System.Environment.SetEnvironmentVariable("COMPlus_GCHeapHardLimit", "2"); // Set the heap hard limit to 2 frames

            // Initialize Harmony
            _harmony = new Harmony("com.jetfox.server");
            _harmony.PatchAll();

            await Task.Delay(3000);

            await StartServer(_config);
        }


        private void ListAllConvoyGroups()
        {
            Player localPlayer = GameManager.LocalPlayer;
            if (localPlayer == null)
            {
                Debug.LogWarning("Local player not found.");
                return;
            }

            if (localPlayer.HQ == null)
            {
                Debug.LogWarning("Player does not have an HQ assigned.");
                return;
            }

            List<Faction.ConvoyGroup> convoyGroups = localPlayer.HQ.faction.GetConvoyGroups();
            if (convoyGroups == null || convoyGroups.Count == 0)
            {
                Debug.Log("No ConvoyGroups found.");
                return;
            }

            foreach (var convoyGroup in convoyGroups)
            {
                Debug.Log($"ConvoyGroup Name: {convoyGroup.Name}");
                Debug.Log($"Cost: {convoyGroup.GetCost()}");
                Debug.Log("Constituents:");
                foreach (var convoyUnit in convoyGroup.Constituents)
                {
                    Debug.Log($"  Unit Type: {convoyUnit.Type.unitName}, Count: {convoyUnit.Count}");
                }
            }
        }
        private void AddNewConvoyGroups()
        {
            // Find the faction you want to add convoy groups to
            Faction faction = FindFactionByName("Boscali");

            if (faction != null)
            {
                // Create new convoy groups and add them to the faction
                Faction.ConvoyGroup newConvoyGroup = new Faction.ConvoyGroup
                {
                    Name = "Testing AeroSentry X2",
                   
                    Constituents = new List<Faction.ConvoyUnit>
                {
                    new Faction.ConvoyUnit { Type = FindUnitDefinitionByName("AeroSentry SPAAG"), Count = 2 }
                }
                };

                //newConvoyGroup.Name = "Test";

                faction.GetConvoyGroups().Add(newConvoyGroup);
                Debug.Log("New convoy group added to faction: " + faction.factionName);
            }
            else
            {
                Debug.LogWarning("Faction not found.");
            }
        }

        private Faction FindFactionByName(string factionName)
        {
            // Implement logic to find and return the faction by name
            // This is a placeholder implementation
            return Resources.FindObjectsOfTypeAll<Faction>().FirstOrDefault(f => f.factionName == factionName);
        }

        private UnitDefinition FindUnitDefinitionByName(string unitName)
        {
            // Implement logic to find and return the unit definition by name
            // This is a placeholder implementation
            return Resources.FindObjectsOfTypeAll<UnitDefinition>().FirstOrDefault(u => u.unitName == unitName);
        }
        private async Task StartServer(Config config)
        {
            // Sleep for 10 seconds to allow other plugins to load
            await Task.Delay(2000);
            if (NetworkManagerNuclearOption.i.Server.Active)
            {
                // Notify all clients that the host is ending the game
                NetworkManagerNuclearOption.i.Server.SendToAll(new HostEndedMessage
                {
                    HostName = GameManager.LocalPlayer.PlayerName
                }, false, true, Mirage.Channel.Reliable);

                // Stop the server
                NetworkManagerNuclearOption.i.Stop(true);

                // Return to the main menu
                GameManager.SetGameState(GameManager.GameState.Menu);
                Logger.LogInfo("Handled host end message");
            }

            StopMission();


            Logger.LogInfo("Stopped the game-server.");
            // Delay execution by 5 seconds
            await Task.Delay(2000);
            //Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount = 32;
            Logger.LogInfo("Starting game-server...");

            // Get the current mission name
            string currentMissionName = config.MissionNames[_currentMissionIndex];
            _currentMissionIndex = (_currentMissionIndex + 1) % config.MissionNames.Length;

            // Create a MissionKey for the current mission name in the User group

            //MissionGroup.MissionKey missionKey = new MissionGroup.MissionKey(currentMissionName, MissionGroup.BuiltIn);
            MissionGroup missionGroup;
            switch (config.MissionGroup.ToLower())
            {
                case "builtin":
                    missionGroup = MissionGroup.BuiltIn;
                    break;
                case "user":
                    missionGroup = MissionGroup.User;
                    break;
                case "workshop":
                    missionGroup = MissionGroup.WorkShop;
                    break;
                default:
                    Logger.LogError("Invalid mission group specified in configuration.");
                    return;
            }

            // Create a MissionKey for the current mission name in the specified group
            MissionKey missionKey = new MissionKey(currentMissionName, currentMissionName, missionGroup);

            // Try to load the mission
            if (missionKey.TryLoad(out Mission mission, out string error))
            {
                Logger.LogInfo("Mission loaded successfully: " + mission.Name);
                MissionManager.SetMission(mission, true);
                await Task.Delay(1000);
                //MissionManager.StartMission();
                // You can now use the loaded mission
                var hostOptions = new HostOptions
                {
                    SocketType = config.UdpSteam == "udp" ? NuclearOption.Networking.SocketType.UDP : NuclearOption.Networking.SocketType.Steam,
                    MaxConnections = config.PlayerCount, // Set the maximum number of connections
                    Map = mission.MapKey
                };

                if (config.UdpSteam == "udp")
                {
                    hostOptions.UdpPort = int.Parse(config.udpPort); // Set the UDP port

                }


                NetworkManagerNuclearOption.i.StartHost(hostOptions);
                await Task.Delay(2500);

                //Broke using new method below
                /*SteamLobby steamLobby = SteamLobby.instance;
                if (steamLobby == null)
                {
                    Logger.LogError("Failed to get SteamLobby instance.");
                    return;
                }

                await steamLobby.HostLobby(config.LobbyName, true, config.PlayerCount);
                */

                SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, config.PlayerCount);
                Logger.LogInfo("Lobby hosted with name: " + config.LobbyName);

                await Task.Delay(5000);

                OptimizeServerPerformance();
                // Start sending MOTD messages if not already running
                if (!_isMOTDTaskRunning)
                {
                    _isMOTDTaskRunning = true;
                    _motdCancellationTokenSource = new CancellationTokenSource();
                    StartSendingMOTDMessages(config.MOTD, _motdCancellationTokenSource.Token);
                }
            }
            else
            {
                Debug.LogError("Failed to load mission: " + error);
            }

            
        }

        private void OptimizeServerPerformance()
        {
            // Reduce physics simulation frequency
            //Time.fixedDeltaTime = 1.0f / 30.0f;
            //Time.maximumDeltaTime = 1.0f / 30.0f;

            // Disable automatic physics simulation if not needed
            //Physics.autoSimulation = false;

            // Disable unused physics features
            // Example: Disable collision between certain layers
            //Physics.IgnoreLayerCollision(0, 1, true);

            // Enable incremental garbage collection
            //UnityEngine.Scripting.GarbageCollector.incremental = true;

            // Unload unused assets periodically
            //InvokeRepeating(nameof(UnloadUnusedAssets), 300f, 300f); // Every 5 minutes

            Application.targetFrameRate = _config.TargetFrameRate;
            // Log the initial value of JobWorkerCount
            int initialJobWorkerCount = Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount;
            Logger.LogInfo($"Initial JobWorkerCount: {initialJobWorkerCount}");
            // Limit job worker threads (adjust based on your server's CPU)
            Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount = _config.threadTest;

            // Disable real-time global illumination
            //UnityEngine.DynamicGI.updateThreshold = 1e9f;

            // Set process priority to high
            System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.High;
            
            // Log optimization completion
            Logger.LogInfo("Performance optimizations applied.");
        }
        void OnLobbyCreated(LobbyCreated_t callback)
        {
            if (callback.m_eResult == EResult.k_EResultOK)
            {
                CSteamID lobbyID = new CSteamID(callback.m_ulSteamIDLobby);
                Logger.LogInfo("Lobby created successfully with ID: " + lobbyID);

                // Set lobby data
                if (_config.UdpSteam == "steam")
                {
                    SteamMatchmaking.SetLobbyData(lobbyID, "HostAddress", SteamUser.GetSteamID().ToString());
                    SteamMatchmaking.SetLobbyData(lobbyID, "name", _config.LobbyName);
                    SteamMatchmaking.SetLobbyData(lobbyID, "version", Application.version);
                    SteamNetworkPingLocation_t location;
                    SteamNetworkingUtils.GetLocalPingLocation(out location);
                    string pingLocationString;
                    SteamNetworkingUtils.ConvertPingLocationToString(ref location, out pingLocationString, 512);
                    SteamMatchmaking.SetLobbyData(lobbyID, "HostPing", pingLocationString);
                }
                else if (_config.UdpSteam == "udp")
                {
                    _currentLobbyId = lobbyID; // Store the lobby ID for later use
                    //ulong randomSteamID = GenerateRandomSteamID();
                    //SteamMatchmaking.SetLobbyData(lobbyID, "HostAddress", randomSteamID.ToString());
                    SteamMatchmaking.SetLobbyData(lobbyID, "HostAddress", SteamUser.GetSteamID().ToString());
                    SteamMatchmaking.SetLobbyData(lobbyID, "name", _config.LobbyName);
                    SteamMatchmaking.SetLobbyData(lobbyID, "version", Application.version);
                    SteamMatchmaking.SetLobbyData(lobbyID, "UDP_Address", _config.udpHost);
                    SteamMatchmaking.SetLobbyData(lobbyID, "UDP_Port", _config.udpPort);
                    SteamNetworkPingLocation_t location;
                    SteamNetworkingUtils.GetLocalPingLocation(out location);
                    string pingLocationString;
                    SteamNetworkingUtils.ConvertPingLocationToString(ref location, out pingLocationString, 512);
                    SteamMatchmaking.SetLobbyData(lobbyID, "HostPing", pingLocationString);
                }


                //SteamLobby.instance.HostDataLocationLoop().Forget();
            }
            else
            {
                Logger.LogError("Failed to create lobby: " + callback.m_eResult);
            }
        }

        public static string LeaveLobby()
        {
            if (_currentLobbyId.IsValid())
            {
                SteamMatchmaking.LeaveLobby(_currentLobbyId);
                return "Left the Steam lobby.\n";
            }
            return "No valid lobby to leave.\n";
        }
        private static ulong GenerateRandomSteamID()
        {
            // SteamID64 for individual accounts starts at this base
            const ulong steamID64Base = 76561197960265728;
            // The range for user accounts is large, but you can limit it for practical purposes
            const ulong maxOffset = 9999999999999; // Adjust as needed

            var rng = new System.Random();
            // Generate a random offset
            ulong offset = ((ulong)rng.Next(0, int.MaxValue) << 32) | (uint)rng.Next(0, int.MaxValue);
            offset %= maxOffset;

            return steamID64Base + offset;
        }
        public static void RestartServer(bool endMessage)
        {
            // Send a chat message when the game ends
            if (endMessage)
            {
                SendChatMessage("The game has ended. Restarting the server...");
            }

            // Cancel the MOTD task
            _motdCancellationTokenSource?.Cancel();

            // Restart the server
            _instance.StartServer(_instance._config);
        }

        public static void StopMission()
        {
            NetworkManagerNuclearOption.i.Stop(true);
        }

        public static void FetchPlayers()
        {
            FactionHQ factionHQ = FindObjectOfType<FactionHQ>();
            if (factionHQ != null)
            {
                List<Player> players = factionHQ.GetPlayers(true); // Fetch players and sort by score
                foreach (Player player in players)
                {
                    Debug.Log(player.PlayerName); // Display player names in the console
                }
            }
            else
            {
                Debug.LogError("FactionHQ instance not found.");
            }
        }

        private void OnDestroy()
        {
            // Stop RCON server
            //_rconServer.Stop();

        }

        [HarmonyPatch(typeof(FactionHQ), "RpcDeclareEndGame")]
        public class FactionHQ_RpcDeclareEndGame_Patch
        {
            static void Postfix()
            {
                
                JetFoxServerPlugin.RestartServer(true);
            }
        }

        [HarmonyPatch(typeof(FactionHQ), "AddPlayer")]
        public class AddPlayerPatch
        {
            private static void Postfix(Player player)
            {
                try
                {
                    if (player?.SteamID == null)
                    {
                        LoggerHook.LogWarning("Player SteamID is null");
                        return;
                    }

                    if (player?.PlayerName == null)
                    {
                        LoggerHook.LogWarning("Player name is null");
                        return;
                    }

                    var playerName = player.PlayerName;
                    var steamId = player.SteamID;

                    var playerData = new PlayerData
                    {
                        PlayerName = playerName,
                        SteamID = steamId
                    };

                    var players = LoadPlayerData();
                    if (!players.Exists(p => p.SteamID == steamId && p.PlayerName == playerName))
                    {
                        players.Add(playerData);
                        SavePlayerData(players);
                    }
                }
                catch (Exception ex)
                {
                    LoggerHook.LogError($"Failed to log player data: {ex}");
                }
            }
        }

        /*[HarmonyPatch(typeof(NetworkAuthenticatorNuclearOption), "SteamAuthenticate")]
            public class SteamAuthenticatePatch
            {
                private static void Postfix(NetworkAuthenticatorNuclearOption __instance, INetworkPlayer player, ref AuthenticationResult __result)
                {
                    if (player.Address is SteamEndPoint sep)
                    {
                        var steamId = sep.Connection.SteamID;
                        
                        var bannedPlayers = LoadBannedPlayerSteamIDs();
                        
                        if (bannedPlayers.Contains(steamId.m_SteamID))
                        {
                            LoggerHook.LogInfo($"Denied steamID {steamId.m_SteamID} banned from joining");
                            __result = AuthenticationResult.CreateFail("Player in ban list", __instance);
                            return;
                        }
                    }
                }
            }*/

        [HarmonyPatch(typeof(NetworkAuthenticatorNuclearOption))]
        [HarmonyPatch("SteamAuthenticate")]
        public static class SteamAuthenticatePatch
        {
            public static void Prefix(INetworkPlayer player)
            {
                try
                {
                    // Get the SteamID using the same method the game uses
                    CSteamID steamId = GetSteamId(player);

                    if (steamId.IsValid())
                    {
                        // Get the player's Steam name using the Steamworks API
                        string playerName = SteamFriends.GetFriendPersonaName(steamId);
                        LoggerHook.LogInfo($"Player connecting: {playerName} (SteamID: {steamId})");
                    }
                }
                catch (System.Exception ex)
                {
                    LoggerHook.LogError($"Error getting Steam username: {ex.Message}");
                }
            }

            // Copy of the game's GetSteamId method
            private static CSteamID GetSteamId(INetworkPlayer player)
            {
                if (player.IsHost)
                {
                    return SteamUser.GetSteamID();
                }

                // Try to get SteamEndPoint from the player's address
                var steamEndPoint = player.Address as SteamEndPoint;
                if (steamEndPoint != null)
                {
                    return steamEndPoint.Connection.SteamID;
                }

                return default(CSteamID);
            }
        }



        private static List<ulong> LoadBannedPlayerSteamIDs()
            {
                if (!File.Exists(BannedPlayersFilePath))
                {
                    return new List<ulong>();
                }

                var json = File.ReadAllText(BannedPlayersFilePath);
                var bannedPlayers = JsonConvert.DeserializeObject<List<RconServer.BannedPlayer>>(json) ?? new List<RconServer.BannedPlayer>();
                return bannedPlayers.Select(bp => bp.SteamID).ToList();
            }

            private static List<PlayerData> LoadPlayerData()
            {
                if (!File.Exists(LogFilePath))
                {
                    return new List<PlayerData>();
                }

                var json = File.ReadAllText(LogFilePath);
                return JsonConvert.DeserializeObject<List<PlayerData>>(json) ?? new List<PlayerData>();
            }

            private static void SavePlayerData(List<PlayerData> players)
            {
                var json = JsonConvert.SerializeObject(players, Formatting.Indented);
                File.WriteAllText(LogFilePath, json);
            }
        

        public class PlayerData
        {
            public string PlayerName { get; set; }
            public ulong SteamID { get; set; }
        }

        internal static void SendChatMessage(string message)
        {
            ChatManager.SendChatMessage(message, true);
        }

        private async void StartSendingMOTDMessages(string[] motd, CancellationToken cancellationToken)
        {
            if (motd == null || motd.Length == 0)
                return;

            int index = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                SendChatMessage(motd[index]);
                index = (index + 1) % motd.Length;
                // Delay for 5 minutes
                try
                {
                    await Task.Delay(300000, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    // Task was canceled, exit the loop
                    break;
                }
            }
        }

        private class Config
        {
            public string[] MissionNames { get; set; }
            public string LobbyName { get; set; }
            public int PlayerCount { get; set; }
            public int TargetFrameRate { get; set; }
            public string[] MOTD { get; set; }
            public int RconPort { get; set; }
            public string RconPassword { get; set; }
            public string UdpSteam { get; set; }
            public string udpHost { get; set; }
            public string udpPort { get; set; }
            //public int ServerPort { get; set; }
            public int threadTest { get; set; }
            public string MissionGroup { get; set; } // Add this line
        }
    }
}
