using NuclearOption.Networking;
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NetTools;
using Mirage.SocketLayer;
using Steamworks;

namespace JetFoxServer
{
    public class RconServer
    {
        private TcpListener _listener;
        private bool _isRunning;
        private readonly string _password;
        private static readonly string BannedPlayersFilePath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "playerBans.json");
        private static readonly string WhitelistFilePath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "whitelistRcon.json");
        private List<IPAddressRange> _whitelist;

        public RconServer(int port, string password)
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _password = password;
            LoadWhitelist();
        }

        public void Start()
        {
            try
            {
                _isRunning = true;
                _listener.Start();
                Debug.Log("RCON server started and listening on port " + ((IPEndPoint)_listener.LocalEndpoint).Port);
                Thread listenerThread = new Thread(ListenForClients);
                listenerThread.Start();
                Debug.Log("Listener thread started.");
            }
            catch (Exception ex)
            {
                Debug.LogError("Failed to start RCON server: " + ex.Message);
                _isRunning = false;
            }
        }

        public void Stop()
        {
            try
            {
                _isRunning = false;
                _listener.Stop();
                Debug.Log("RCON server stopped.");
            }
            catch (Exception ex)
            {
                Debug.LogError("Failed to stop RCON server: " + ex.Message);
            }
        }

        private void ListenForClients()
        {
            Debug.Log("ListenForClients method started.");
            while (_isRunning)
            {
                try
                {
                    Debug.Log("Waiting for a client to connect...");
                    TcpClient client = _listener.AcceptTcpClient();
                    IPAddress clientIP = ((IPEndPoint)client.Client.RemoteEndPoint).Address;

                    if (!IsWhitelisted(clientIP))
                    {
                        Debug.LogWarning("Connection attempt from non-whitelisted IP: " + clientIP);
                        client.Close();
                        continue;
                    }

                    Debug.Log("Client connected.");
                    Thread clientThread = new Thread(HandleClientComm);
                    clientThread.Start(client);
                    Debug.Log("Client thread started.");
                }
                catch (SocketException ex)
                {
                    Debug.LogError("SocketException: " + ex.Message);
                }
                catch (Exception ex)
                {
                    Debug.LogError("Exception: " + ex.Message);
                }
            }
            Debug.Log("ListenForClients method ended.");
        }

        private void HandleClientComm(object clientObj)
        {
            TcpClient tcpClient = (TcpClient)clientObj;
            NetworkStream clientStream = tcpClient.GetStream();

            byte[] message = new byte[4096];
            int bytesRead;

            while (_isRunning)
            {
                bytesRead = 0;

                try
                {
                    bytesRead = clientStream.Read(message, 0, 4096);
                }
                catch (Exception ex)
                {
                    Debug.LogError("Exception while reading from client stream: " + ex.Message);
                    break;
                }

                if (bytesRead == 0)
                {
                    Debug.Log("Client disconnected.");
                    break;
                }

                string command = Encoding.ASCII.GetString(message, 0, bytesRead);
                string response = ProcessCommand(command);
                byte[] responseBytes = Encoding.ASCII.GetBytes(response);
                clientStream.Write(responseBytes, 0, responseBytes.Length);
            }

            tcpClient.Close();
            Debug.Log("Client connection closed.");
        }

        private string ProcessCommand(string command)
        {
            string[] parts = command.Trim().Split(' ');
            if (parts.Length < 2 || parts[0].ToLower() != _password.ToLower())
            {
                return "Invalid password.\n";
            }

            string mainCommand = parts[1].ToLower();
            string[] args = parts.Skip(2).ToArray();

            switch (mainCommand)
            {
                case "listplayers":
                    return ListPlayers();
                case "kick":
                    if (args.Length > 0)
                        return KickPlayer(args[0]);
                    return "Usage: kick [playername]\n";
                case "kicksteam":
                    if (args.Length > 0)
                        return KickPlayerSteam(args[0]);
                    return "Usage: kicksteam [steamid]\n";
                case "ban":
                    if (args.Length > 0)
                        return BanPlayer(args[0]);
                    return "Usage: ban [playername]\n";
                case "bansteam":
                    if (args.Length > 1)
                        return BanPlayerSteam(args[0], string.Join(" ", args.Skip(1)));
                    return "Usage: bansteam [steamid] [reason]\n";
                case "unban":
                    if (args.Length > 0)
                        return UnbanPlayer(args[0]);
                    return "Usage: unban [playername]\n";
                case "broadcast":
                    if (args.Length > 0)
                        return BroadcastMessage(string.Join(" ", args));
                    return "Usage: broadcast [message]\n";
                case "status":
                    return GetStatus();
                case "restart":
                    JetFoxServerPlugin.RestartServer(false);
                    return "Server is restarting...\n";
                case "stopmission":
                    JetFoxServerPlugin.StopMission();
                    return "Mission stopped.\n";
                case "shutdown":
                    Application.Quit();
                    return "Server is shutting down...\n";
                case "leavelobby":
                    return JetFoxServerPlugin.LeaveLobby();
                case "setcore":
                    if (args.Length > 0 && int.TryParse(args[0], out int fps2))
                    {
                        Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount = fps2;
                        return $"Target core rate set to {fps2}.\n";
                    }
                    return "Core count set.\n";
                case "setfps":
                    if (args.Length > 0 && int.TryParse(args[0], out int fps))
                    {
                        Application.targetFrameRate = fps;
                        return $"Target frame rate set to {fps}.\n";
                    }
                    return "Usage: setfps [value]\n";
                case "getfps":
                    return GetCurrentFPS();
                default:
                    return "Unknown command.\n";
            }
        }

        private string GetCurrentFPS()
        {
            return $"Current FPS: {1.0f / Time.deltaTime}\n";
        }
        private string ListPlayers()
        {
            var players = UnitRegistry.playerLookup;
            if (players.Count == 0)
            {
                return "No players are currently connected.\n";
            }

            return string.Join("\n", players.Select(p => $"{p.Value.PlayerName}({p.Value.SteamID})")) + "\n";
        }
        private string KickPlayer(string playerName)
        {
            var player = UnitRegistry.playerLookup.FirstOrDefault(p => p.Value.PlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase)).Value;
            if (player == null)
            {
                return $"Player '{playerName}' not found.\n";
            }

            NetworkManagerNuclearOption.i.KickPlayerAsync(player).Forget();
            return $"Player '{playerName}' has been kicked.\n";
        }
        private string KickPlayerSteam(string steamIDStr)
        {
            if (!ulong.TryParse(steamIDStr, out ulong steamID))
            {
                return $"Invalid SteamID '{steamIDStr}'.\n";
            }

            var player = UnitRegistry.playerLookup.FirstOrDefault(p => p.Value.SteamID == steamID).Value;
            if (player == null)
            {
                return $"Player with SteamID '{steamID}' not found.\n";
            }

            NetworkManagerNuclearOption.i.KickPlayerAsync(player).Forget();
            return $"Player with SteamID '{steamID}' has been kicked.\n";
        }

        private string BanPlayer(string playerName)
        {
            var player = UnitRegistry.playerLookup.FirstOrDefault(p => p.Value.PlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase)).Value;
            if (player == null)
            {
                return $"Player '{playerName}' not found.\n";
            }

            NetworkManagerNuclearOption.i.KickPlayerAsync(player).Forget();
            return $"Player '{playerName}' has been banned and kicked.\n";
        }

        private string BanPlayerSteam(string steamIDStr, string reason)
        {
            if (!ulong.TryParse(steamIDStr, out ulong steamID))
            {
                return $"Invalid SteamID '{steamIDStr}'.\n";
            }

            var player = UnitRegistry.playerLookup.FirstOrDefault(p => p.Value.SteamID == steamID).Value;
            if (player == null)
            {
                return $"Player with SteamID '{steamID}' not found.\n";
            }

            var bannedPlayers = LoadBannedPlayers();
            if (!bannedPlayers.Any(p => p.SteamID == steamID))
            {
                bannedPlayers.Add(new BannedPlayer { SteamID = steamID, PlayerName = player.PlayerName, Reason = reason });
                SaveBannedPlayers(bannedPlayers);
            }

            NetworkManagerNuclearOption.i.KickPlayerAsync(player).Forget();
            return $"{player.PlayerName} with SteamID '{steamID}' has been banned for reason: {reason}.\n";
        }

        private string UnbanPlayer(string playerName)
        {
            return $"Player '{playerName}' has been unbanned.\n";
        }
        private string BroadcastMessage(string message)
        {
            JetFoxServerPlugin.SendChatMessage(message);
            return "Message broadcasted.\n";
        }

        private string GetStatus()
        {
            return "Server is running.\n";
        }

        private List<BannedPlayer> LoadBannedPlayers()
        {
            if (!File.Exists(BannedPlayersFilePath))
            {
                return new List<BannedPlayer>();
            }

            var json = File.ReadAllText(BannedPlayersFilePath);
            return JsonConvert.DeserializeObject<List<BannedPlayer>>(json) ?? new List<BannedPlayer>();
        }

        private void SaveBannedPlayers(List<BannedPlayer> bannedPlayers)
        {
            var json = JsonConvert.SerializeObject(bannedPlayers, Formatting.Indented);
            File.WriteAllText(BannedPlayersFilePath, json);
        }

        private void LoadWhitelist()
        {
            if (!File.Exists(WhitelistFilePath))
            {
                _whitelist = new List<IPAddressRange>();
                return;
            }

            var json = File.ReadAllText(WhitelistFilePath);
            var whitelistData = JsonConvert.DeserializeObject<WhitelistData>(json);
            _whitelist = whitelistData.Whitelist.Select(range => IPAddressRange.Parse(range)).ToList();
        }

        private bool IsWhitelisted(IPAddress clientIP)
        {
            if (_whitelist == null || !_whitelist.Any())
            {
                return true;
            }
            return _whitelist.Any(range => range.Contains(clientIP));
        }

        public class BannedPlayer
        {
            public ulong SteamID { get; set; }
            public string PlayerName { get; set; }
            public string Reason { get; set; }
        }

        private class WhitelistData
        {
            public List<string> Whitelist { get; set; }
        }
    }
}
