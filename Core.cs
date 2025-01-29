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


namespace JetFoxServer
{
    [BepInPlugin("com.jetfox.server", "JetFox Server", "1.2.3")]
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

        //private static ManualLogSource loggers;
        private static readonly string LogFilePath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "playerLog.json");
        private static readonly ManualLogSource LoggerHook = BepInEx.Logging.Logger.CreateLogSource("PlayerLogger");
        private static readonly string BannedPlayersFilePath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "playerBans.json");

        private Callback<LobbyCreated_t> _lobbyCreatedCallback;
        public async void Awake()
        {
            _instance = this;
            Logger.LogInfo("JetFox Server Plugin loaded");

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
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to read configuration from config file: " + ex.Message);
                return;
            }

            // Start RCON server if not already running
            if (_rconServer == null)
            {
                _rconServer = new RconServer(_config.RconPort, _config.RconPassword);
                _rconServer.Start();
                Logger.LogInfo("Rcon server started on port " + _config.RconPort);
            }

            // Initialize Harmony
            _harmony = new Harmony("com.jetfox.server");
            _harmony.PatchAll();

            await Task.Delay(10000);

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
            await Task.Delay(5000);
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
            await Task.Delay(5000);
            Logger.LogInfo("Starting game-server...");

            // Get the current mission name
            string currentMissionName = config.MissionNames[_currentMissionIndex];
            _currentMissionIndex = (_currentMissionIndex + 1) % config.MissionNames.Length;

            // Create a MissionKey for the current mission name in the User group
            MissionGroup.MissionKey missionKey = new MissionGroup.MissionKey(currentMissionName, MissionGroup.User);

            // Try to load the mission
            if (missionKey.TryLoad(out Mission mission, out string error))
            {
                Logger.LogInfo("Mission loaded successfully: " + mission.Name);
                MissionManager.SetMission(mission, true);
                // You can now use the loaded mission
            }
            else
            {
                Debug.LogError("Failed to load mission: " + error);
            }

            var hostOptions = new HostOptions
            {
                SocketType = NuclearOption.Networking.SocketType.Steam,
                MaxConnections = config.PlayerCount, // Set the maximum number of connections
                //UdpPort = 7777, // Set the UDP port
            };

            NetworkManagerNuclearOption.i.StartHost(hostOptions);
            await Task.Delay(2500);

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

            Logger.LogInfo("Server Started - FPS Limiter applied.");
            // Send Chat Message servers up
            //QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = config.TargetFrameRate;

            //

            // Disable shadows
            QualitySettings.shadows = ShadowQuality.Disable;

            // Disable anti-aliasing
            QualitySettings.antiAliasing = 0;

            // Apply graphics settings only once
            if (!_graphicsSettingsApplied)
            {
                // Disable v-sync
                QualitySettings.vSyncCount = 0;
                // Set texture quality to half
                //QualitySettings.globalTextureMipmapLimit = 3;

                // Set texture quality to the highest mipmap limit to effectively disable textures
                QualitySettings.globalTextureMipmapLimit = int.MaxValue;
                // Disable texture streaming
                QualitySettings.streamingMipmapsActive = false;
                // Disable fog
                RenderSettings.fog = false;
                _graphicsSettingsApplied = true;
            }

            // Disable anisotropic filtering
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.Disable;
            RenderSettings.reflectionBounces = 0;

            // Disable all lights
            Light[] lights = FindObjectsOfType<Light>();
            foreach (Light light in lights)
            {
                light.enabled = false;
            }

            // Set the quality level to the lowest
            QualitySettings.SetQualityLevel(0, true);

            // Disable soft particles
            QualitySettings.softParticles = false;

            // Disable real-time reflection probes
            QualitySettings.realtimeReflectionProbes = false;

            // Disable billboards face camera position
            QualitySettings.billboardsFaceCameraPosition = false;

            // Unload unused assets
            Resources.UnloadUnusedAssets();
            // Force garbage collection
            GC.Collect();
            GC.WaitForPendingFinalizers();
            // Log the changes
            Logger.LogInfo("Graphics settings have been disabled and textures reduced.");
            // Start sending MOTD messages if not already running
            if (!_isMOTDTaskRunning)
            {
                _isMOTDTaskRunning = true;
                _motdCancellationTokenSource = new CancellationTokenSource();
                StartSendingMOTDMessages(config.MOTD, _motdCancellationTokenSource.Token);
            }
        }
        

        void OnLobbyCreated(LobbyCreated_t callback)
        {
            if (callback.m_eResult == EResult.k_EResultOK)
            {
                CSteamID lobbyID = new CSteamID(callback.m_ulSteamIDLobby);
                Logger.LogInfo("Lobby created successfully with ID: " + lobbyID);

                // Set lobby data
                // Generate a random string for HostAddress
                //string randomHostAddress = Guid.NewGuid().ToString();

                // Set lobby data
                //SteamMatchmaking.SetLobbyData(lobbyID, "HostAddress", "12345"); if you set this you brick anyone who joins lmao
                SteamMatchmaking.SetLobbyData(lobbyID, "HostAddress", SteamUser.GetSteamID().ToString());
                //string ipAddress = "45.119.210.199:7777"; // Replace with your server's IP address
                //SteamMatchmaking.SetLobbyData(lobbyID, "HostAddress", $"{ipAddress}");

                SteamMatchmaking.SetLobbyData(lobbyID, "name", _config.LobbyName);
                SteamMatchmaking.SetLobbyData(lobbyID, "version", Application.version);

                //string udpHost = "45.119.210.199"; // Replace with your actual UDP host IP
                //int udpPort = 7777; // Replace with your actual UDP port

                //SteamMatchmaking.SetLobbyData(lobbyID, "UdpHost", udpHost);
                //SteamMatchmaking.SetLobbyData(lobbyID, "UdpPort", udpPort.ToString());
                //Logger.LogInfo("HostAddress set to: " + randomHostAddress);
                // Optionally, set UDP-specific data
                //SteamMatchmaking.SetLobbyData(lobbyID, "UDPPort", "7777"); // Example UDP port
                // Set the game server information for the lobby
                //uint gameServerIP = BitConverter.ToUInt32(IPAddress.Parse("158.69.118.213").GetAddressBytes(), 0);
                //ushort gameServerPort = 7777; // Replace with the actual port of the game server
                //SteamMatchmaking.SetLobbyGameServer(lobbyID, gameServerIP, gameServerPort, SteamUser.GetSteamID());


                SteamNetworkPingLocation_t location;
                SteamNetworkingUtils.GetLocalPingLocation(out location);
                string pingLocationString;
                SteamNetworkingUtils.ConvertPingLocationToString(ref location, out pingLocationString, 512);
                SteamMatchmaking.SetLobbyData(lobbyID, "HostPing", pingLocationString);
                //SteamLobby.instance.HostDataLocationLoop().Forget();
            }
            else
            {
                Logger.LogError("Failed to create lobby: " + callback.m_eResult);
            }
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

        [HarmonyPatch(typeof(NetworkAuthenticatorNuclearOption), "SteamAuthenticate")]
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
            // Assuming there is a ChatManager class with a static method to send chat messages
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
            public float NetworkTimeoutDuration { get; set; }
            //public int ServerPort { get; set; }
        }
    }
}
