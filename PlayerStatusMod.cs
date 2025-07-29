using Eleon;
using Eleon.Modding;
using System;
using System.IO;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

public class PlayerStatusConfig
{
    public bool welcome_enabled { get; set; } = true;
    public string welcome_message { get; set; } = "Welcome to the galaxy, {playername}!";
    public bool goodbye_enabled { get; set; } = true;
    public string goodbye_message { get; set; } = "Player {playername} has left our galaxy";
    public List<ScheduledMessage> scheduled_messages { get; set; } = new List<ScheduledMessage>();
}

public class ScheduledMessage
{
    public bool enabled { get; set; } = false;
    public string text { get; set; } = "";
    public int interval_minutes { get; set; } = 30;
    public DateTime last_sent { get; set; } = DateTime.MinValue;
}

public class PlayerStatusMod : ModInterface
{
    private ModGameAPI api;
    private PlayerStatusConfig config;
    private Timer scheduledMessageTimer;
    private FileSystemWatcher configWatcher;
    private string configFilePath;
    
    // Player name caching to avoid "Unknown Player" issues
    private ConcurrentDictionary<int, string> playerNameCache = new ConcurrentDictionary<int, string>();
    private ConcurrentDictionary<int, DateTime> pendingPlayerLookups = new ConcurrentDictionary<int, DateTime>();

    public void Game_Start(ModGameAPI dediAPI)
    {
        api = dediAPI;
        api.Console_Write("PlayerStatusMod loaded with configuration support.");
        
        LoadConfiguration();
        SetupConfigurationWatcher();
        SetupScheduledMessages();
        
        // Test message to verify messaging works
        Task.Run(async () =>
        {
            await Task.Delay(5000); // Wait 5 seconds after mod loads
            SendGlobalMessage("PlayerStatusMod has loaded successfully!");
        });
    }

    public void Game_Exit()
    {
        scheduledMessageTimer?.Dispose();
        configWatcher?.Dispose();
        api?.Console_Write("PlayerStatusMod shutting down.");
    }

    public void Game_Update() { }

    public void Game_Event(CmdId eventId, ushort seqNr, object data)
    {
        // Log ALL events temporarily to see what we're actually receiving
        api.Console_Write($"[PlayerStatusMod] DEBUG: Event {eventId} received (Data: {data?.GetType()?.Name})");
        
        // Log relevant events only
        if (eventId == CmdId.Event_Player_Connected || eventId == CmdId.Event_Player_Disconnected)
        {
            api.Console_Write($"[PlayerStatusMod] Processing event: {eventId}");
        }
        
        if (eventId == CmdId.Event_Player_Connected)
        {
            if (!config.welcome_enabled) return;
            
            // Handle connection event asynchronously
            Task.Run(async () =>
            {
                try
                {
                    string playerName = await ExtractPlayerNameAsync(data);
                    if (playerName != null && playerName != "Unknown Player" && !playerName.StartsWith("Player_"))
                    {
                        string message = config.welcome_message.Replace("{playername}", playerName);
                        SendGlobalMessage(message);
                        api.Console_Write($"[PlayerStatusMod] Sent welcome message for {playerName}");
                    }
                    else
                    {
                        api.Console_Write($"[PlayerStatusMod] Skipping welcome message - could not resolve player name (got: {playerName})");
                    }
                }
                catch (Exception ex)
                {
                    api.Console_Write($"[PlayerStatusMod] Error in welcome message: {ex.Message}");
                }
            });
        }
        else if (eventId == CmdId.Event_Player_Disconnected)
        {
            if (!config.goodbye_enabled) return;
            
            // Handle disconnection event asynchronously
            Task.Run(async () =>
            {
                try
                {
                    string playerName = await ExtractPlayerNameAsync(data);
                    if (playerName != null && playerName != "Unknown Player" && !playerName.StartsWith("Player_"))
                    {
                        string message = config.goodbye_message.Replace("{playername}", playerName);
                        SendGlobalMessage(message);
                        api.Console_Write($"[PlayerStatusMod] Sent goodbye message for {playerName}");
                    }
                    else
                    {
                        api.Console_Write($"[PlayerStatusMod] Skipping goodbye message - could not resolve player name (got: {playerName})");
                    }
                }
                catch (Exception ex)
                {
                    api.Console_Write($"[PlayerStatusMod] Error in goodbye message: {ex.Message}");
                }
            });
        }
        // Handle player info responses for caching
        else if (eventId == CmdId.Event_Player_Info)
        {
            CachePlayerInfoFromResponse(data);
        }
        // Handle other events that might give us player info
        else if (eventId == CmdId.Event_Statistics)
        {
            // Statistics events often contain player information
            // We can cache player names from these events for later use
            CachePlayerInfoFromStatistics(data);
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
                api.Console_Write($"[PlayerStatusMod] INFO: Cached player info from API response: {playerInfo.entityId} -> {playerInfo.playerName}");
            }
            else
            {
                api.Console_Write($"[PlayerStatusMod] DEBUG: Player info response data type: {data?.GetType()?.Name}");
                
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
                            api.Console_Write($"[PlayerStatusMod] INFO: Cached player info via reflection: {playerId} -> {name}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            api.Console_Write($"[PlayerStatusMod] Error caching player info from response: {ex.Message}");
        }
    }
    
    private void CachePlayerInfoFromStatistics(object data)
    {
        try
        {
            // Statistics events might contain player information
            api.Console_Write($"[PlayerStatusMod] DEBUG: Statistics event data type: {data?.GetType()?.Name}");
            
            // Try to extract player info from statistics data
            var dataType = data?.GetType();
            if (dataType != null)
            {
                var properties = dataType.GetProperties();
                foreach (var prop in properties)
                {
                    try
                    {
                        var value = prop.GetValue(data);
                        
                        // Look for nested player information
                        if (value is PlayerInfo playerInfo)
                        {
                            CachePlayerName(playerInfo.entityId, playerInfo.playerName);
                            api.Console_Write($"[PlayerStatusMod] INFO: Cached player from statistics: {playerInfo.entityId} -> {playerInfo.playerName}");
                        }
                        // Look for collections that might contain player info
                        else if (value is System.Collections.IEnumerable enumerable && !(value is string))
                        {
                            foreach (var item in enumerable)
                            {
                                if (item is PlayerInfo player)
                                {
                                    CachePlayerName(player.entityId, player.playerName);
                                    api.Console_Write($"[PlayerStatusMod] INFO: Cached player from statistics collection: {player.entityId} -> {player.playerName}");
                                }
                            }
                        }
                    }
                    catch { /* Continue checking other properties */ }
                }
            }
        }
        catch (Exception ex)
        {
            api.Console_Write($"[PlayerStatusMod] Error handling statistics: {ex.Message}");
        }
    }
    
    private async Task<string> ExtractPlayerNameAsync(object data)
    {
        try
        {
            // Get data type for debugging
            var dataType = data?.GetType();
            api.Console_Write($"[PlayerStatusMod] DEBUG: Event data type: {dataType?.Name}");
            
            // Try different possible data structures
            if (data is PlayerInfo playerInfo)
            {
                api.Console_Write($"[PlayerStatusMod] DEBUG: Found PlayerInfo, name: {playerInfo.playerName}");
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
                api.Console_Write($"[PlayerStatusMod] DEBUG: Found player ID: {playerId.Value}");
                
                // Check cache first
                if (playerNameCache.TryGetValue(playerId.Value, out string cachedName))
                {
                    api.Console_Write($"[PlayerStatusMod] DEBUG: Using cached name for {playerId.Value}: {cachedName}");
                    return cachedName;
                }
                
                // Avoid duplicate lookups for the same player
                if (pendingPlayerLookups.ContainsKey(playerId.Value))
                {
                    api.Console_Write($"[PlayerStatusMod] DEBUG: Player lookup already pending for {playerId.Value}");
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
                        api.Console_Write($"[PlayerStatusMod] SUCCESS: Resolved player {playerId.Value} to name: {playerName}");
                        return playerName;
                    }
                }
                catch (Exception ex)
                {
                    pendingPlayerLookups.TryRemove(playerId.Value, out _);
                    api.Console_Write($"[PlayerStatusMod] ERROR: Failed to get player info for {playerId.Value}: {ex.Message}");
                }
                
                return $"Player_{playerId.Value}";
            }
            
            api.Console_Write($"[PlayerStatusMod] DEBUG: Could not extract player ID from data structure");
            return "Unknown Player";
        }
        catch (Exception ex)
        {
            api.Console_Write($"[PlayerStatusMod] Error extracting player name: {ex.Message}");
            return "Unknown Player";
        }
    }
    
    private int? ExtractPlayerId(object data)
    {
        try
        {
            // Handle Id type specifically
            if (data is Id idData)
            {
                api.Console_Write($"[PlayerStatusMod] DEBUG: Found Id object with id: {idData.id}");
                return idData.id;
            }
            
            var dataType = data?.GetType();
            if (dataType == null) return null;
            
            api.Console_Write($"[PlayerStatusMod] DEBUG: Data type: {dataType.Name}, checking all properties:");
            
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
                        api.Console_Write($"[PlayerStatusMod] DEBUG: Property {propName} = {value} (type: {value.GetType().Name})");
                        if (int.TryParse(value.ToString(), out int id))
                        {
                            api.Console_Write($"[PlayerStatusMod] DEBUG: Extracted player ID {id} from property {propName}");
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
                        api.Console_Write($"[PlayerStatusMod] DEBUG: Property {prop.Name} = {value} (type: {value.GetType().Name})");
                        if (int.TryParse(value.ToString(), out int id) && id > 0)
                        {
                            api.Console_Write($"[PlayerStatusMod] DEBUG: Found potential player ID {id} in property {prop.Name}");
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
            api.Console_Write($"[PlayerStatusMod] DEBUG: Exception in ExtractPlayerId: {ex.Message}");
            return null;
        }
    }
    
    private async Task<string> RequestPlayerInfo(int playerId)
    {
        try
        {
            // Create a task completion source for the async operation
            var tcs = new TaskCompletionSource<string>();
            var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // 5 second timeout
            
            // Use the game API to request player information
            api.Game_Request(CmdId.Request_Player_Info, (ushort)DateTime.Now.Millisecond, new Id(playerId));
            
            // Wait a short time and check if we received the player info
            await Task.Delay(500); // Wait 500ms for response
            
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
            api.Console_Write($"[PlayerStatusMod] Error requesting player info: {ex.Message}");
            return null;
        }
    }
    
    private void CachePlayerName(int playerId, string playerName)
    {
        if (playerId > 0 && !string.IsNullOrEmpty(playerName) && playerName != "Unknown Player")
        {
            playerNameCache[playerId] = playerName;
            api.Console_Write($"[PlayerStatusMod] DEBUG: Cached player name {playerId} -> {playerName}");
        }
    }
    
    private void LoadConfiguration()
    {
        try
        {
            configFilePath = Path.Combine(Path.GetDirectoryName(typeof(PlayerStatusMod).Assembly.Location), "PlayerStatusConfig.json");
            api.Console_Write($"[PlayerStatusMod] DEBUG: Looking for config at: {configFilePath}");
            api.Console_Write($"[PlayerStatusMod] DEBUG: Base directory is: {AppDomain.CurrentDomain.BaseDirectory}");
            api.Console_Write($"[PlayerStatusMod] DEBUG: File exists: {File.Exists(configFilePath)}");
            
            if (File.Exists(configFilePath))
            {
                string jsonContent = File.ReadAllText(configFilePath);
                config = JsonConvert.DeserializeObject<PlayerStatusConfig>(jsonContent);
                api.Console_Write($"[PlayerStatusMod] Configuration loaded from {configFilePath}");
                
                // Log scheduled message timestamps on load for debugging
                api.Console_Write($"[PlayerStatusMod] DEBUG: Loaded {config.scheduled_messages.Count} scheduled messages");
                foreach (var msg in config.scheduled_messages)
                {
                    if (msg.enabled)
                    {
                        api.Console_Write($"[PlayerStatusMod] DEBUG: Loaded message '{msg.text.Substring(0, Math.Min(30, msg.text.Length))}...' last_sent: {msg.last_sent:yyyy-MM-dd HH:mm:ss}");
                    }
                }
            }
            else
            {
                config = new PlayerStatusConfig();
                SaveConfiguration();
                api.Console_Write($"[PlayerStatusMod] Created default configuration at {configFilePath}");
            }
        }
        catch (Exception ex)
        {
            api.Console_Write($"[PlayerStatusMod] Error loading configuration: {ex.Message}");
            config = new PlayerStatusConfig();
        }
    }
    
    private void SaveConfiguration()
    {
        try
        {
            api.Console_Write($"[PlayerStatusMod] DEBUG: Saving configuration to {configFilePath}");
            string jsonContent = JsonConvert.SerializeObject(config, Formatting.Indented);
            File.WriteAllText(configFilePath, jsonContent);
            api.Console_Write($"[PlayerStatusMod] DEBUG: Configuration saved successfully. File size: {new FileInfo(configFilePath).Length} bytes");
            
            // Log scheduled message timestamps for debugging
            foreach (var msg in config.scheduled_messages)
            {
                if (msg.enabled)
                {
                    api.Console_Write($"[PlayerStatusMod] DEBUG: Message '{msg.text.Substring(0, Math.Min(30, msg.text.Length))}...' last_sent: {msg.last_sent:yyyy-MM-dd HH:mm:ss}");
                }
            }
        }
        catch (Exception ex)
        {
            api.Console_Write($"[PlayerStatusMod] ERROR: Failed to save configuration: {ex.Message}");
            api.Console_Write($"[PlayerStatusMod] ERROR: Config path: {configFilePath}");
            api.Console_Write($"[PlayerStatusMod] ERROR: Directory exists: {Directory.Exists(Path.GetDirectoryName(configFilePath))}");
        }
    }
    
    private void SetupConfigurationWatcher()
    {
        try
        {
            string directory = Path.GetDirectoryName(configFilePath);
            string fileName = Path.GetFileName(configFilePath);
            
            configWatcher = new FileSystemWatcher(directory, fileName);
            configWatcher.Changed += OnConfigurationChanged;
            configWatcher.EnableRaisingEvents = true;
            
            api.Console_Write("[PlayerStatusMod] Configuration file watcher enabled");
        }
        catch (Exception ex)
        {
            api.Console_Write($"[PlayerStatusMod] Error setting up configuration watcher: {ex.Message}");
        }
    }
    
    private void OnConfigurationChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            Thread.Sleep(100); // Wait for file write to complete
            LoadConfiguration();
            api.Console_Write("[PlayerStatusMod] Configuration reloaded due to file change");
        }
        catch (Exception ex)
        {
            api.Console_Write($"[PlayerStatusMod] Error reloading configuration: {ex.Message}");
        }
    }
    
    private void SetupScheduledMessages()
    {
        // Start timer that checks every minute for scheduled messages
        scheduledMessageTimer = new Timer(CheckScheduledMessages, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        api.Console_Write("[PlayerStatusMod] Scheduled message timer started");
    }
    
    private void CheckScheduledMessages(object state)
    {
        try
        {
            DateTime now = DateTime.Now;
            bool configChanged = false;
            
            foreach (var message in config.scheduled_messages)
            {
                if (!message.enabled || string.IsNullOrWhiteSpace(message.text))
                    continue;
                    
                // Check if it's time to send this message
                if (message.last_sent == DateTime.MinValue || 
                    now.Subtract(message.last_sent).TotalMinutes >= message.interval_minutes)
                {
                    SendGlobalMessage(message.text);
                    message.last_sent = now;
                    configChanged = true;
                    api.Console_Write($"[PlayerStatusMod] Sent scheduled message: {message.text}");
                    api.Console_Write($"[PlayerStatusMod] DEBUG: Updated last_sent timestamp to {now:yyyy-MM-dd HH:mm:ss}");
                }
            }
            
            // CRITICAL FIX: Save configuration to persist timestamp changes
            if (configChanged)
            {
                try
                {
                    SaveConfiguration();
                    api.Console_Write("[PlayerStatusMod] DEBUG: Configuration saved successfully after scheduled message");
                }
                catch (Exception saveEx)
                {
                    api.Console_Write($"[PlayerStatusMod] ERROR: Failed to save configuration after scheduled message: {saveEx.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            api.Console_Write($"[PlayerStatusMod] Error in scheduled message check: {ex.Message}");
        }
    }
    
    private void SendGlobalMessage(string message)
    {
        try
        {
            // Based on empyrion-web-helper analysis, console command is most reliable
            var consoleCommand = new PString($"say '{message}'");
            api.Game_Request(CmdId.Request_ConsoleCommand, (ushort)DateTime.Now.Millisecond, consoleCommand);
            api.Console_Write($"[PlayerStatusMod] Sent global message: {message}");
        }
        catch (Exception ex)
        {
            api.Console_Write($"[PlayerStatusMod] Error sending message '{message}': {ex.Message}");
        }
    }
}