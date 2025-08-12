using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Collections.Concurrent;
using Newtonsoft.Json;
using Eleon.Modding;
using Eleon;

public class PlayerStatusMod : IMod, ModInterface
{
    private IModApi api2;
    private ModGameAPI api1;
    private ConcurrentDictionary<string, DateTime> recentEvents = new ConcurrentDictionary<string, DateTime>();
    private ConcurrentDictionary<int, string> playerNameCache = new ConcurrentDictionary<int, string>();
    private ConcurrentDictionary<int, DateTime> pendingPlayerLookups = new ConcurrentDictionary<int, DateTime>();
    private string configFilePath;
    private string logFilePath;
    private string stateFilePath;
    private Timer scheduledMessageTimer;
    private PlayerStatusConfig config;
    private Dictionary<string, DateTime> messageTimestamps = new Dictionary<string, DateTime>();
    private readonly TimeSpan eventDeduplicationWindow = TimeSpan.FromSeconds(15);
    private DateTime configFileTimestamp = DateTime.MinValue;
    
    // AFK System
    private ConcurrentDictionary<int, AfkPlayerInfo> afkPlayers = new ConcurrentDictionary<int, AfkPlayerInfo>();

    // API2 Implementation
    public void Init(IModApi api)
    {
        this.api2 = api;
        var modDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        configFilePath = Path.Combine(modDirectory, "PlayerStatusConfig.json");
        logFilePath = Path.Combine(modDirectory, "PlayerStatusMod.log");
        stateFilePath = Path.Combine(modDirectory, "PlayerStatusState.json");
        Log("[PlayerStatusMod] API2 Initialized");
        LoadConfiguration();
        LoadState();
        SetupScheduledMessages();
        Log("[PlayerStatusMod] API2 systems initialized");
    }

    public void Shutdown()
    {
        scheduledMessageTimer?.Dispose();
        Log("[PlayerStatusMod] Shutdown");
    }

    // API1 Implementation
    public void Game_Start(ModGameAPI dediAPI)
    {
        api1 = dediAPI;
        Log("[PlayerStatusMod] API1 Initialized - events enabled");
    }

    public void Game_Exit()
    {
        Log("[PlayerStatusMod] API1 shutting down");
    }

    public void Game_Update() { }

    public void Game_Event(CmdId eventId, ushort seqNr, object data)
    {
        try
        {
            if (eventId == CmdId.Event_Player_Connected && config.welcome_enabled)
            {
                Log($"[Game_Event] Player connected event detected");
                // Handle connection event asynchronously like the old code
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        string playerName = await ExtractPlayerNameAsync(data);
                        if (playerName != null && playerName != "Unknown Player" && !playerName.StartsWith("Player_"))
                        {
                            string message = config.welcome_message.Replace("{playername}", playerName);
                            string key = $"welcome_{playerName}";
                            if (!IsEventDuplicate(key))
                            {
                                RecordEvent(key);
                                SendGlobalMessage(message);
                                Log($"[Game_Event] Sent welcome message for {playerName}");
                            }
                        }
                        else
                        {
                            Log($"[Game_Event] Skipping welcome message - could not resolve player name (got: {playerName})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[Game_Event] Error in welcome message: {ex.Message}");
                    }
                });
            }
            else if (eventId == CmdId.Event_Player_Disconnected && config.goodbye_enabled)
            {
                Log($"[Game_Event] Player disconnected event detected");
                // Handle disconnection event asynchronously
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        string playerName = await ExtractPlayerNameAsync(data);
                        if (playerName != null && playerName != "Unknown Player" && !playerName.StartsWith("Player_"))
                        {
                            string message = config.goodbye_message.Replace("{playername}", playerName);
                            string key = $"goodbye_{playerName}";
                            if (!IsEventDuplicate(key))
                            {
                                RecordEvent(key);
                                SendGlobalMessage(message);
                                Log($"[Game_Event] Sent goodbye message for {playerName}");
                            }
                        }
                        else
                        {
                            Log($"[Game_Event] Skipping goodbye message - could not resolve player name (got: {playerName})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[Game_Event] Error in goodbye message: {ex.Message}");
                    }
                });
            }
            // Handle player info responses for caching
            else if (eventId == CmdId.Event_Player_Info)
            {
                CachePlayerInfoFromResponse(data);
            }
            // Handle chat commands
            else if (eventId == CmdId.Event_ChatMessage)
            {
                HandleChatCommand(data);
            }
        }
        catch (Exception ex)
        {
            Log($"[Game_Event] Error: {ex.Message}");
        }
    }

    private int? ExtractPlayerId(object data)
    {
        try
        {
            // Handle Id type specifically
            if (data is Id idData)
            {
                Log($"[ExtractPlayerId] Found Id object with id: {idData.id}");
                return idData.id;
            }
            
            var dataType = data?.GetType();
            if (dataType == null) return null;
            
            Log($"[ExtractPlayerId] Data type: {dataType.Name}, checking all properties:");
            
            // Try common property names for player ID
            string[] idPropertyNames = { "id", "playerId", "entityId", "steamId" };
            
            foreach (var propName in idPropertyNames)
            {
                var property = dataType.GetProperty(propName);
                if (property != null)
                {
                    var value = property.GetValue(data);
                    if (value != null)
                    {
                        Log($"[ExtractPlayerId] Property {propName} = {value} (type: {value.GetType().Name})");
                        if (int.TryParse(value.ToString(), out int id))
                        {
                            Log($"[ExtractPlayerId] Extracted player ID {id} from property {propName}");
                            return id;
                        }
                    }
                }
            }
            
            // Fallback: try reflection on all properties
            var properties = dataType.GetProperties();
            foreach (var prop in properties)
            {
                try
                {
                    var value = prop.GetValue(data);
                    if (value != null)
                    {
                        Log($"[ExtractPlayerId] Property {prop.Name} = {value} (type: {value.GetType().Name})");
                        if (int.TryParse(value.ToString(), out int id) && id > 0)
                        {
                            Log($"[ExtractPlayerId] Found potential player ID {id} in property {prop.Name}");
                            return id;
                        }
                    }
                }
                catch { /* Continue checking other properties */ }
            }
            
            return null;
        }
        catch (Exception ex)
        {
            Log($"[ExtractPlayerId] Exception: {ex.Message}");
            return null;
        }
    }

    private async System.Threading.Tasks.Task<string> ExtractPlayerNameAsync(object data)
    {
        try
        {
            // Get data type for debugging
            var dataType = data?.GetType();
            Log($"[ExtractPlayerNameAsync] Event data type: {dataType?.Name}");
            
            // Try different possible data structures
            if (data is PlayerInfo playerInfo)
            {
                Log($"[ExtractPlayerNameAsync] Found PlayerInfo, name: {playerInfo.playerName}");
                var name = playerInfo.playerName ?? "Unknown Player";
                if (name != "Unknown Player")
                {
                    // Cache the player name with their ID
                    CachePlayerName(playerInfo.entityId, name);
                }
                return name;
            }
            
            // Try to extract player ID for API lookup
            int? playerId = ExtractPlayerId(data);
            if (playerId.HasValue)
            {
                Log($"[ExtractPlayerNameAsync] Found player ID: {playerId.Value}");
                
                // Check cache first
                if (playerNameCache.TryGetValue(playerId.Value, out string cachedName))
                {
                    Log($"[ExtractPlayerNameAsync] Using cached name for {playerId.Value}: {cachedName}");
                    return cachedName;
                }
                
                // Avoid duplicate lookups for the same player
                if (pendingPlayerLookups.ContainsKey(playerId.Value))
                {
                    Log($"[ExtractPlayerNameAsync] Player lookup already pending for {playerId.Value}");
                    return $"Player_{playerId.Value}";
                }
                
                // Request player info from game API
                try
                {
                    pendingPlayerLookups[playerId.Value] = DateTime.Now;
                    var playerName = await RequestPlayerInfo(playerId.Value);
                    pendingPlayerLookups.TryRemove(playerId.Value, out _);
                    
                    if (!string.IsNullOrEmpty(playerName) && playerName != "Unknown Player")
                    {
                        CachePlayerName(playerId.Value, playerName);
                        Log($"[ExtractPlayerNameAsync] SUCCESS: Resolved player {playerId.Value} to name: {playerName}");
                        return playerName;
                    }
                }
                catch (Exception ex)
                {
                    pendingPlayerLookups.TryRemove(playerId.Value, out _);
                    Log($"[ExtractPlayerNameAsync] ERROR: Failed to get player info for {playerId.Value}: {ex.Message}");
                }
                
                return $"Player_{playerId.Value}";
            }
            
            Log($"[ExtractPlayerNameAsync] Could not extract player ID from data structure");
            return "Unknown Player";
        }
        catch (Exception ex)
        {
            Log($"[ExtractPlayerNameAsync] Error extracting player name: {ex.Message}");
            return "Unknown Player";
        }
    }

    private async System.Threading.Tasks.Task<string> RequestPlayerInfo(int playerId)
    {
        try
        {
            // Use the game API to request player information
            api1.Game_Request(CmdId.Request_Player_Info, (ushort)DateTime.Now.Millisecond, new Id(playerId));
            
            // Wait a short time and check if we received the player info
            await System.Threading.Tasks.Task.Delay(500); // Wait 500ms for response
            
            // Check if the player info was received and cached
            if (playerNameCache.TryGetValue(playerId, out string name))
            {
                return name;
            }
            
            // If no response, return null to indicate failure
            return null;
        }
        catch (Exception ex)
        {
            Log($"[RequestPlayerInfo] Error requesting player info: {ex.Message}");
            return null;
        }
    }

    private void CachePlayerInfoFromResponse(object data)
    {
        try
        {
            // Handle player info response from API
            if (data is PlayerInfo playerInfo)
            {
                CachePlayerName(playerInfo.entityId, playerInfo.playerName);
                Log($"[CachePlayerInfoFromResponse] Cached player info from API response: {playerInfo.entityId} -> {playerInfo.playerName}");
            }
            else
            {
                Log($"[CachePlayerInfoFromResponse] Player info response data type: {data?.GetType()?.Name}");
                
                // Try reflection to extract player info
                var dataType = data?.GetType();
                if (dataType != null)
                {
                    var idProp = dataType.GetProperty("entityId") ?? dataType.GetProperty("id");
                    var nameProp = dataType.GetProperty("playerName") ?? dataType.GetProperty("name");
                    
                    if (idProp != null && nameProp != null)
                    {
                        var id = idProp.GetValue(data);
                        var name = nameProp.GetValue(data);
                        
                        if (id != null && name != null && int.TryParse(id.ToString(), out int playerId))
                        {
                            CachePlayerName(playerId, name.ToString());
                            Log($"[CachePlayerInfoFromResponse] Cached player info via reflection: {playerId} -> {name}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[CachePlayerInfoFromResponse] Error caching player info from response: {ex.Message}");
        }
    }

    private void CachePlayerName(int playerId, string playerName)
    {
        if (playerId > 0 && !string.IsNullOrEmpty(playerName) && playerName != "Unknown Player")
        {
            playerNameCache[playerId] = playerName;
            Log($"[CachePlayerName] Cached player name {playerId} -> {playerName}");
        }
    }

    private void SetupScheduledMessages()
    {
        try
        {
            scheduledMessageTimer = new Timer(CheckScheduledMessages, null, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));
            Log("[SetupScheduledMessages] Scheduled message timer started");
        }
        catch (Exception ex)
        {
            Log($"[SetupScheduledMessages] Timer setup error: {ex.Message}");
        }
    }

    private void CheckScheduledMessages(object state)
    {
        try
        {
            // Check if config file has been updated
            CheckForConfigFileChanges();
            
            var now = DateTime.UtcNow;
            
            foreach (var msg in config.scheduled_messages)
            {
                if (!msg.enabled || string.IsNullOrWhiteSpace(msg.text)) continue;
                
                var lastSent = messageTimestamps.ContainsKey(msg.text) ? messageTimestamps[msg.text] : DateTime.MinValue;
                var timeSinceLastSent = now - lastSent;
                Log($"[CheckScheduledMessages] '{msg.text}' - Last sent: {lastSent}, Time since: {timeSinceLastSent.TotalMinutes:F1} min, Interval: {msg.interval_minutes} min");
                
                if (timeSinceLastSent >= TimeSpan.FromMinutes(msg.interval_minutes))
                {
                    string key = $"sched_{msg.text.ToLowerInvariant()}";
                    if (IsEventDuplicate(key)) 
                    {
                        Log($"[CheckScheduledMessages] Duplicate detected for: {msg.text}");
                        continue;
                    }
                    
                    RecordEvent(key);
                    SendGlobalMessage(msg.text);
                    messageTimestamps[msg.text] = now;
                    SaveState();
                    Log($"[CheckScheduledMessages] Sent: {msg.text}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[CheckScheduledMessages] Error: {ex.Message}");
        }
    }

    private string GetPlayerName(object playerData)
    {
        try
        {
            Log($"[GetPlayerName] PlayerData type: {playerData.GetType().FullName}");
            
            var type = playerData.GetType();
            var allProperties = type.GetProperties();
            Log($"[GetPlayerName] Available properties: {string.Join(", ", allProperties.Select(p => p.Name))}");
            
            var nameProperty = type.GetProperty("Name") ?? 
                             type.GetProperty("PlayerName") ?? 
                             type.GetProperty("SteamName");
            
            if (nameProperty != null)
            {
                var name = nameProperty.GetValue(playerData)?.ToString();
                Log($"[GetPlayerName] Found name: {name} via property: {nameProperty.Name}");
                return name ?? "Unknown Player";
            }
            
            Log("[GetPlayerName] No name property found");
            return "Unknown Player";
        }
        catch (Exception ex)
        {
            Log($"[GetPlayerName] Error: {ex.Message}");
            return "Unknown Player";
        }
    }

    private void SendGlobalMessage(string message)
    {
        try
        {
            var messageData = new Eleon.MessageData
            {
                Channel = Eleon.MsgChannel.Global,
                Text = message,
                SenderNameOverride = "Server"
            };
            api2.Application.SendChatMessage(messageData);
            Log($"[SendGlobalMessage] Sent: {message}");
        }
        catch (Exception ex)
        {
            Log($"[SendGlobalMessage] Error: {ex.Message}");
        }
    }

    private void CheckForConfigFileChanges()
    {
        try
        {
            if (!File.Exists(configFilePath)) return;
            
            var fileInfo = new FileInfo(configFilePath);
            var currentTimestamp = fileInfo.LastWriteTime;
            
            if (currentTimestamp > configFileTimestamp)
            {
                Log($"[CheckForConfigFileChanges] Config file updated, reloading... (Old: {configFileTimestamp}, New: {currentTimestamp})");
                
                // Small delay to handle potential file locks during upload
                System.Threading.Thread.Sleep(100);
                
                LoadConfiguration(preserveMessageTimestamps: true);
                configFileTimestamp = currentTimestamp;
                
                Log($"[CheckForConfigFileChanges] Config reload completed successfully");
            }
        }
        catch (Exception ex)
        {
            Log($"[CheckForConfigFileChanges] Error checking config file: {ex.Message}");
        }
    }

    private void LoadConfiguration(bool preserveMessageTimestamps = false)
    {
        try
        {
            // Store existing timestamps if preserving
            var existingTimestamps = preserveMessageTimestamps ? 
                new Dictionary<string, DateTime>(messageTimestamps) : 
                null;
            
            if (File.Exists(configFilePath))
            {
                var json = File.ReadAllText(configFilePath);
                config = JsonConvert.DeserializeObject<PlayerStatusConfig>(json);
                Log($"[LoadConfiguration] Config loaded from {configFilePath}");
                Log($"[LoadConfiguration] Found {config.scheduled_messages.Count} scheduled messages");
                Log($"[LoadConfiguration] Welcome enabled: {config.welcome_enabled}");
                Log($"[LoadConfiguration] Goodbye enabled: {config.goodbye_enabled}");
                
                if (preserveMessageTimestamps && existingTimestamps != null)
                {
                    // Restore existing message timestamps
                    messageTimestamps = existingTimestamps;
                    Log($"[LoadConfiguration] Preserved {messageTimestamps.Count} existing message timestamps");
                }
            }
            else
            {
                config = new PlayerStatusConfig();
                var json = JsonConvert.SerializeObject(config, Formatting.Indented);
                File.WriteAllText(configFilePath, json);
                Log($"[LoadConfiguration] Default config created at {configFilePath}");
            }
            
            // Update config file timestamp on initial load
            if (!preserveMessageTimestamps && File.Exists(configFilePath))
            {
                configFileTimestamp = new FileInfo(configFilePath).LastWriteTime;
            }
        }
        catch (Exception ex)
        {
            Log($"[LoadConfiguration] Config load error: {ex.Message} - Creating default config");
            config = new PlayerStatusConfig();
            var json = JsonConvert.SerializeObject(config, Formatting.Indented);
            File.WriteAllText(configFilePath, json);
        }
    }

    private void LoadState()
    {
        try
        {
            if (File.Exists(stateFilePath))
            {
                var json = File.ReadAllText(stateFilePath);
                messageTimestamps = JsonConvert.DeserializeObject<Dictionary<string, DateTime>>(json) ?? new Dictionary<string, DateTime>();
                Log($"[LoadState] State loaded - {messageTimestamps.Count} message timestamps");
            }
            else
            {
                messageTimestamps = new Dictionary<string, DateTime>();
                Log("[LoadState] No state file found, starting fresh");
            }
        }
        catch (Exception ex)
        {
            Log($"[LoadState] Error: {ex.Message}");
            messageTimestamps = new Dictionary<string, DateTime>();
        }
    }

    private void SaveState()
    {
        try
        {
            var json = JsonConvert.SerializeObject(messageTimestamps, Formatting.Indented);
            File.WriteAllText(stateFilePath, json);
        }
        catch (Exception ex)
        {
            Log($"[SaveState] Error: {ex.Message}");
        }
    }

    private bool IsEventDuplicate(string key)
    {
        return recentEvents.TryGetValue(key, out var last) && DateTime.UtcNow - last < eventDeduplicationWindow;
    }

    private void RecordEvent(string key)
    {
        recentEvents[key] = DateTime.UtcNow;
    }

    private void Log(string message)
    {
        try
        {
            // Check if log rotation is needed
            RotateLogIfNeeded();
            
            File.AppendAllText(logFilePath, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} {message}\n");
        }
        catch { }
    }

    private void RotateLogIfNeeded()
    {
        try
        {
            if (!File.Exists(logFilePath)) return;
            
            var fileInfo = new FileInfo(logFilePath);
            var maxSizeBytes = config.log_max_file_size_mb * 1024 * 1024;
            
            if (fileInfo.Length > maxSizeBytes)
            {
                // Rotate existing backup files
                for (int i = config.log_files_to_keep - 1; i > 1; i--)
                {
                    string oldFile = $"{logFilePath}.{i - 1}";
                    string newFile = $"{logFilePath}.{i}";
                    
                    if (File.Exists(oldFile))
                    {
                        if (File.Exists(newFile)) File.Delete(newFile);
                        File.Move(oldFile, newFile);
                    }
                }
                
                // Move current log to .1
                string backupPath = $"{logFilePath}.1";
                if (File.Exists(backupPath)) File.Delete(backupPath);
                File.Move(logFilePath, backupPath);
            }
        }
        catch { }
    }

    // ===================
    // AFK SYSTEM METHODS
    // ===================


    private void HandleChatCommand(object data)
    {
        try
        {
            if (data is ChatInfo chatInfo)
            {
                var message = chatInfo.msg?.Trim();
                if (string.IsNullOrEmpty(message)) return;

                var playerId = chatInfo.playerId;
                var playerName = GetCachedPlayerName(playerId) ?? $"Player_{playerId}";

                Log($"[HandleChatCommand] Player {playerId} ({playerName}) sent command: '{message}'");

                // Handle AFK commands
                if (message.StartsWith("/afk", StringComparison.OrdinalIgnoreCase) && !message.StartsWith("/afklist", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"[HandleChatCommand] Detected /afk command from {playerId}");
                    HandleAfkCommand(playerId, playerName, message);
                }
                else if (message.Equals("/back", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"[HandleChatCommand] Detected /back command from {playerId}");
                    HandleBackCommand(playerId, playerName);
                }
                else if (message.Equals("/afklist", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"[HandleChatCommand] Detected /afklist command from {playerId}");
                    HandleAfkListCommand(playerId);
                }
                else if (message.Equals("/help", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"[HandleChatCommand] Detected /help command from {playerId}");
                    HandleHelpCommand(playerId);
                }
            }
            else
            {
                Log($"[HandleChatCommand] Received non-ChatInfo data: {data?.GetType()?.Name}");
            }
        }
        catch (Exception ex)
        {
            Log($"[HandleChatCommand] Error: {ex.Message}");
        }
    }

    private void HandleAfkCommand(int playerId, string playerName, string message)
    {
        try
        {
            string reason = config.afk_default_message;
            
            // Extract custom reason if provided
            var parts = message.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
            {
                reason = parts[1].Trim();
            }

            // Try to get a better player name if the cached one isn't good
            if (playerName.StartsWith("Player_"))
            {
                // Try to request fresh player info
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        var freshName = await RequestPlayerInfo(playerId);
                        if (!string.IsNullOrEmpty(freshName) && freshName != "Unknown Player" && !freshName.StartsWith("Player_"))
                        {
                            // Update the AFK record with the correct name
                            if (afkPlayers.TryGetValue(playerId, out AfkPlayerInfo existingInfo))
                            {
                                existingInfo.PlayerName = freshName;
                                afkPlayers[playerId] = existingInfo;
                                Log($"[HandleAfkCommand] Updated AFK player name from {playerName} to {freshName}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[HandleAfkCommand] Failed to update player name: {ex.Message}");
                    }
                });
            }

            var afkInfo = new AfkPlayerInfo
            {
                PlayerId = playerId,
                PlayerName = playerName,
                Reason = reason,
                StartTime = DateTime.UtcNow
            };

            afkPlayers[playerId] = afkInfo;

            if (config.afk_announce_messages)
            {
                string afkMessage = $"{playerName} is now AFK: {reason}";
                SendGlobalMessage(afkMessage);
            }

            Log($"[HandleAfkCommand] Player {playerName} ({playerId}) went AFK: {reason}. AFK players count: {afkPlayers.Count}");
        }
        catch (Exception ex)
        {
            Log($"[HandleAfkCommand] Error: {ex.Message}");
        }
    }

    private void HandleBackCommand(int playerId, string playerName)
    {
        try
        {
            if (afkPlayers.TryRemove(playerId, out AfkPlayerInfo afkInfo))
            {
                var duration = DateTime.UtcNow - afkInfo.StartTime;
                string durationText = duration.TotalHours >= 1 
                    ? $"{duration.TotalHours:F1}h"
                    : $"{duration.TotalMinutes:F0}m";

                if (config.afk_announce_messages)
                {
                    string backMessage = $"{playerName} is back after {durationText}";
                    SendGlobalMessage(backMessage);
                }

                Log($"[HandleBackCommand] Player {playerName} ({playerId}) returned from AFK after {durationText}");
            }
            else
            {
                Log($"[HandleBackCommand] Player {playerName} ({playerId}) was not AFK");
            }
        }
        catch (Exception ex)
        {
            Log($"[HandleBackCommand] Error: {ex.Message}");
        }
    }

    private void HandleAfkListCommand(int requesterId)
    {
        try
        {
            Log($"[HandleAfkListCommand] Processing /afklist from player {requesterId}. Config enabled: {config.afk_list_command_enabled}");
            
            if (!config.afk_list_command_enabled)
            {
                Log($"[HandleAfkListCommand] AFK list command is disabled in config");
                return;
            }

            Log($"[HandleAfkListCommand] Total AFK players in dictionary: {afkPlayers.Count}");
            foreach (var kvp in afkPlayers)
            {
                Log($"[HandleAfkListCommand] AFK Player: ID={kvp.Key}, Name={kvp.Value.PlayerName}, Reason={kvp.Value.Reason}");
            }

            var afkList = afkPlayers.Values.OrderBy(a => a.StartTime).ToList();
            
            if (!afkList.Any())
            {
                SendPrivateMessage(requesterId, "No players are currently AFK.");
                Log($"[HandleAfkListCommand] No AFK players found - sent empty message to {requesterId}");
                return;
            }

            var message = "AFK Players:\n";
            foreach (var afk in afkList)
            {
                var duration = DateTime.UtcNow - afk.StartTime;
                string durationText = duration.TotalHours >= 1 
                    ? $"{duration.TotalHours:F1}h"
                    : $"{duration.TotalMinutes:F0}m";
                
                message += $"• {afk.PlayerName}: {afk.Reason} ({durationText})\n";
            }

            SendPrivateMessage(requesterId, message);
            Log($"[HandleAfkListCommand] Sent AFK list to player {requesterId} ({afkList.Count} players): {message.Replace("\n", " | ")}");
        }
        catch (Exception ex)
        {
            Log($"[HandleAfkListCommand] Error: {ex.Message}");
        }
    }

    private void HandleHelpCommand(int requesterId)
    {
        try
        {
            Log($"[HandleHelpCommand] Processing /help from player {requesterId}. Config enabled: {config.help_command_enabled}");
            
            if (!config.help_command_enabled)
            {
                Log($"[HandleHelpCommand] Help command is disabled in config");
                return;
            }

            if (config.help_commands == null || !config.help_commands.Any())
            {
                SendPrivateMessage(requesterId, "No help commands configured.");
                Log($"[HandleHelpCommand] No help commands found in config");
                return;
            }

            var message = "Available Commands:\n";
            foreach (var helpCmd in config.help_commands)
            {
                if (!string.IsNullOrWhiteSpace(helpCmd.command) && !string.IsNullOrWhiteSpace(helpCmd.description))
                {
                    message += $"• {helpCmd.command}: {helpCmd.description}\n";
                }
            }

            SendPrivateMessage(requesterId, message);
            Log($"[HandleHelpCommand] Sent help to player {requesterId} ({config.help_commands.Count} commands)");
        }
        catch (Exception ex)
        {
            Log($"[HandleHelpCommand] Error: {ex.Message}");
        }
    }


    private string GetCachedPlayerName(int playerId)
    {
        return playerNameCache.TryGetValue(playerId, out string name) ? name : null;
    }

    private void SendPrivateMessage(int playerId, string message)
    {
        try
        {
            // Use API2 for private messaging to specific player
            var messageData = new Eleon.MessageData
            {
                Channel = Eleon.MsgChannel.SinglePlayer,
                Text = message,
                SenderNameOverride = "Server",
                RecipientEntityId = playerId
            };
            api2.Application.SendChatMessage(messageData);
            Log($"[SendPrivateMessage] Sent private message to {playerId}: {message}");
        }
        catch (Exception ex)
        {
            Log($"[SendPrivateMessage] Error: {ex.Message}");
            // Fallback to global message if private messaging fails
            SendGlobalMessage($"@{GetCachedPlayerName(playerId) ?? $"Player_{playerId}"}: {message}");
        }
    }
}

public class PlayerStatusConfig
{
    public bool welcome_enabled { get; set; } = true;
    public string welcome_message { get; set; } = "Welcome to the galaxy, {playername}!!";
    public bool goodbye_enabled { get; set; } = true;
    public string goodbye_message { get; set; } = "Player {playername} has left our galaxy";
    public bool afk_enabled { get; set; } = true;
    public bool afk_announce_messages { get; set; } = true;
    public int afk_auto_timeout_minutes { get; set; } = 15;
    public string afk_default_message { get; set; } = "Away from keyboard";
    public bool afk_list_command_enabled { get; set; } = true;
    public bool help_command_enabled { get; set; } = true;
    public int log_max_file_size_mb { get; set; } = 2;
    public int log_files_to_keep { get; set; } = 2;
    public List<HelpCommand> help_commands { get; set; } = new List<HelpCommand>
    {
        new HelpCommand { command = "/vb1", description = "Open virtual backpack 1" },
        new HelpCommand { command = "/vb2", description = "Open virtual backpack 2" },
        new HelpCommand { command = "/vb3", description = "Open virtual backpack 3" },
        new HelpCommand { command = "/vb4", description = "Open virtual backpack 4" },
        new HelpCommand { command = "/vb5", description = "Open virtual backpack 5" },
        new HelpCommand { command = "/afk [reason]", description = "Mark yourself as AFK with optional reason" },
        new HelpCommand { command = "/back", description = "Return from AFK status" },
        new HelpCommand { command = "/afklist", description = "View currently AFK players" }
    };
    public List<ScheduledMessage> scheduled_messages { get; set; } = new List<ScheduledMessage>();
}

public class ScheduledMessage
{
    public bool enabled { get; set; } = true;
    public string text { get; set; } = string.Empty;
    public int interval_minutes { get; set; } = 60;
    public DateTime last_sent { get; set; } = DateTime.MinValue;
}

public class AfkPlayerInfo
{
    public int PlayerId { get; set; }
    public string PlayerName { get; set; }
    public string Reason { get; set; }
    public DateTime StartTime { get; set; }
}

public class HelpCommand
{
    public string command { get; set; } = string.Empty;
    public string description { get; set; } = string.Empty;
}