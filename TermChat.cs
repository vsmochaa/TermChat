using Firebase.Database;
using Firebase.Database.Query;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TermChat_by_vsmocha;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace TermChat
{
    class TermChat
    {
        enum AccountState
        {
            NotAuthenticated,
            SignedIn,
            Terminated
        }

        enum DiscordLogColors
        {
            Blue = 255,
            Red = 16711680,
            Green = 65280
        }

        private static string ASCII = @"
████████ ███████ ██████  ███    ███  ██████ ██   ██  █████  ████████ 
   ██    ██      ██   ██ ████  ████ ██      ██   ██ ██   ██    ██    
   ██    █████   ██████  ██ ████ ██ ██      ███████ ███████    ██    
   ██    ██      ██   ██ ██  ██  ██ ██      ██   ██ ██   ██    ██    
   ██    ███████ ██   ██ ██      ██  ██████ ██   ██ ██   ██    ██";
        private static string Divider = "=====================================================================";
        private static AccountState UserState = AccountState.NotAuthenticated;
        private static FirebaseClient FirebaseClient = new FirebaseClient(Secrets.FirebaseUrl);
        private static IDisposable Subscription;
        private static string CurrentUsername = "";
        private static string CurrentRoom = "";
        private static StringBuilder CurrentInput = new StringBuilder();
        private static readonly object ConsoleLock = new object();
        private const string CurrentVersion = "2.0.2";
        private static readonly string GithubToken = Secrets.GithubToken;
        private static bool DevMode = true;
        private static readonly string AuthLogsWebhook = Secrets.AuthLogs;
        private static readonly string ChatLogsWebhook = Secrets.ChatLogs;
        private static readonly string ChatCommandsWebhook = Secrets.ChatLogs;
        private static readonly string AdminLogsWebhook = Secrets.AdminLogs;
        private static readonly string TelemetryLogsWebhook = Secrets.TelemetryLogs;
        [DllImport("user32.dll")]
        private static extern bool FlashWindow(IntPtr hwnd, bool bInvert);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        static async Task Main(string[] args)
        {
            if (!File.Exists("TermChatUpdater.exe") && !Debugger.IsAttached)
            {
                Console.Clear();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("=====================================================================");
                Console.WriteLine("                         CRITICAL ERROR                              ");
                Console.WriteLine("=====================================================================");
                Console.ResetColor();
                Console.WriteLine("\nTermChatUpdater.exe was not found in the application directory.");
                Console.WriteLine("The application cannot run or update without the updater executable.");
                Console.WriteLine("\nPlease ensure 'TermChatUpdater.exe' is placed in the same folder.");
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
                return;
            }

            Console.Title = $"TermChat v{CurrentVersion}";

            bool updateFound = await CheckForUpdates();
            if (updateFound) return;
            while (UserState != AccountState.Terminated)
            {
                Console.Clear();
                Heading();
                await RunAuth();
            }
        }

        private static async Task<bool> CheckForUpdates()
        {
            if (Debugger.IsAttached) { return false; }
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TermChat", "1.0"));
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw"));
                    client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };

                    string versionUrl = $"https://api.github.com/repos/vsmochaa/TermChat/contents/version.txt?t={DateTime.Now.Ticks}";

                    HttpResponseMessage response = await client.GetAsync(versionUrl);

                    if (!response.IsSuccessStatusCode)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"\n[Error]: Failed to check version.txt. GitHub replied: {response.StatusCode}");
                        Console.ResetColor();
                        await Task.Delay(2000);
                        return false;
                    }

                    string latestVersion = await response.Content.ReadAsStringAsync();
                    latestVersion = latestVersion.Trim();
                    string cleanedCurrent = CurrentVersion.Trim();

                    if (!latestVersion.Equals(cleanedCurrent, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"\nNew update found! Current: {cleanedCurrent} -> New: {latestVersion}");
                        Console.WriteLine("Downloading update...");
                        using (HttpClient downloadClient = new HttpClient())
                        {
                            downloadClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TermChat", "1.0"));

                            string exeUrl = $"https://github.com/vsmochaa/TermChat/releases/download/Release/TermChat.exe?t={DateTime.Now.Ticks}";
                            HttpResponseMessage exeResponse = await downloadClient.GetAsync(exeUrl);

                            if (!exeResponse.IsSuccessStatusCode)
                            {
                                Console.ForegroundColor = ConsoleColor.Yellow;
                                Console.WriteLine($"\n[Notice]: Version update detected, but failed to download TermChat.exe. (GitHub replied: {exeResponse.StatusCode})");
                                Console.ResetColor();
                                await Task.Delay(3500);
                                return false;
                            }

                            byte[] exeBytes = await exeResponse.Content.ReadAsByteArrayAsync();
                            File.WriteAllBytes("TermChat_new.exe", exeBytes);
                        }
                        Console.WriteLine("Launching updater...");
                        Process.Start("TermChatUpdater.exe");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not check for updates: {ex.Message}");
                await Task.Delay(4000);
            }
            return false;
        }

        private static async Task DiscordLog(string title, string description, string url, DiscordLogColors color)
        {
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    var payload = new
                    {
                        embeds = new[]
                        {
                    new
                    {
                        title = title,
                        description = description,
                        color = (int)color
                    }
                }
                    };

                    string jsonPayload = JsonSerializer.Serialize(payload);
                    HttpContent content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    await client.PostAsync(url, content);
                }
            }
            catch (Exception)
            {
            }
        }

        private static async Task DiscordHandshakeLog(string username)
        {
            string publicIP = await new HttpClient().GetStringAsync("https://api.ipify.org");
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    var payload = new
                    {
                        embeds = new[]
                        {
                    new
                    {
                        title = $"[@{username} -- Client Handshake Log]",
                        description = $"**IP Address:** ||`{publicIP}`||\n**Machine:** ||`{Environment.MachineName}`||\n**Machine Username:** ||`{Environment.UserName}`||",
                        color = 16777215
                    }
                }
                    };

                    string jsonPayload = JsonSerializer.Serialize(payload);
                    HttpContent content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    await client.PostAsync(TelemetryLogsWebhook, content);
                }
            }
            catch (Exception)
            {
            }
        }

        private static async Task StartPresenceHeartbeat()
        {
            while (UserState == AccountState.SignedIn)
            {
                try
                {
                    var presenceObj = new
                    {
                        LastSeen = DateTime.UtcNow.Ticks,
                        Username = CurrentUsername
                    };
                    await FirebaseClient.Child("Presence").Child(CurrentUsername).PutAsync(presenceObj);

                    int onlineCount = await GetOnlineCount();
                    Console.Title = $"TermChat v{CurrentVersion}  |  Online: {onlineCount}";
                }
                catch (Exception)
                {
                }
                await Task.Delay(15000);
            }
        }

        private static async Task<int> GetOnlineCount()
        {
            var presenceNodes = await FirebaseClient.Child("Presence").OnceAsync<Newtonsoft.Json.Linq.JObject>();
            long currentTicks = DateTime.UtcNow.Ticks;
            long timeoutThreshold = TimeSpan.TicksPerSecond * 30;

            int count = 0;
            foreach (var node in presenceNodes)
            {
                try
                {
                    var data = node.Object;
                    if (data != null && data.TryGetValue("LastSeen", out var lastSeenToken))
                    {
                        long lastSeen = lastSeenToken.Value<long>();
                        if ((currentTicks - lastSeen) < timeoutThreshold)
                        {
                            count++;
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            return count;
        }

        public static void FlashTaskbar()
        {
            IntPtr hWnd = GetConsoleWindow();
            FlashWindow(hWnd, true);
        }
        static void Heading()
        {
            Console.WriteLine(ASCII);
            Console.WriteLine(Divider);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"   v{CurrentVersion}                                              by vsmocha");
            Console.ResetColor();
            Console.WriteLine(Divider);
        }

        static void Error(string err)
        {
            if (err == null) { return; }
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("\n[Error]");
            Console.ResetColor();
            Console.Write($": {err}\n");
        }

        static void Success(string msg)
        {
            if (msg == null) { return; }
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("\n[Success]");
            Console.ResetColor();
            Console.Write($": {msg}\n");
        }

        static async Task RunAuth()
        {
            if (UserState != AccountState.NotAuthenticated) { Error("An error has occurred, you have triggered the Login page but are marked as not LoggedOut. Please report this to vsmocha."); Console.WriteLine("Press any key to return."); Console.ReadKey(); return; }
            Console.Clear();
            Heading();
            Console.WriteLine(@"
[1]: Sign In
[2]: Sign Up");
            string selection = Console.ReadLine() ?? "";
            if (string.IsNullOrEmpty(selection)) { Error("Enter a valid selection!"); await Task.Delay(3000); return; }
            switch (selection.ToLower())
            {
                case "1":
                    {
                        Console.Write("Username: ");
                        string username = Console.ReadLine() ?? "";
                        Console.Write("Password: ");
                        string password = Console.ReadLine() ?? "";
                        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) { Error("Username or password field empty."); await Task.Delay(3000); return; }
                        var users = await FirebaseClient.Child("Users").OnceAsync<UserModel>();
                        var match = users.FirstOrDefault(u => u.Object.Username == username);
                        if (match != null && match.Object.Password == password)
                        {
                            if (username == "mocha" && (Environment.UserName != "mocha" || Environment.MachineName != "DESKTOP-H2AIQU5")) { Error("mocha's account is HWID locked to his PC.."); Console.ReadKey(); return; }
                            if (username == "c" && (Environment.MachineName != "buswifi")) { Error("c's account is HWID locked to his PC.."); Console.ReadKey(); return; }
                            if (username == "System") { Error("Cannot login to this account."); Console.ReadKey(); return; }
                            if (match.Object.isTerminated == true) { UserState = AccountState.Terminated; Error("Your account has been terminated."); await Task.Delay(5000); return; }
                            UserState = AccountState.SignedIn;
                            CurrentUsername = username;
                            Success("Successfully logged in.");
                            match.Object.AppVersion = CurrentVersion;
                            await FirebaseClient.Child("Users").Child(match.Key).PutAsync(match.Object);
                            await JoinRoom("main-chat");
                            await DiscordLog("Log In", $"**Username:** @{username}\n**Action:** Log In\n**Time:** {DateTime.Now}\n**Version:** {CurrentVersion}", AuthLogsWebhook, DiscordLogColors.Green);
                            await DiscordHandshakeLog(username);
                        }
                        else
                        {
                            Error("Incorrect username or password!");
                            await Task.Delay(1000);
                            return;
                        }
                        break;
                    }
                case "2":
                    {
                        Console.Write("Choose a Username: ");
                        string username = Console.ReadLine() ?? "";
                        Console.Write("Choose a Password: ");
                        string password = Console.ReadLine() ?? "";
                        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) { Error("Username or password field empty."); await Task.Delay(3000); return; }
                        var users = await FirebaseClient.Child("Users").OnceAsync<UserModel>();
                        var match = users.FirstOrDefault(u => u.Object.Username == username);
                        if (match != null) { Error("Username already taken"); await Task.Delay(3000); return; }
                        var newUser = new UserModel
                        {
                            Username = username,
                            Password = password,
                            isTerminated = false,
                            isMuted = false,
                            isAdmin = false,
                            AppVersion = CurrentVersion
                        };
                        await FirebaseClient.Child("Users").Child(username).PutAsync(newUser);
                        Success("Account created successfully! You may now sign in.");
                        await DiscordLog("Sign Up", $"**Username:** @{username}\n**Action:** Sign Up\n**Time:** {DateTime.Now}", AuthLogsWebhook, DiscordLogColors.Green);
                        await DiscordHandshakeLog(username);
                        Console.WriteLine("Press any key to return.");
                        Console.ReadKey();
                        break;
                    }
            }
        }

        private static async Task StartTunnel()
        {
            long connectionTimestamp = DateTime.UtcNow.Ticks;

            Subscription = FirebaseClient.Child("Rooms").Child(CurrentRoom).Child("Messages").AsObservable<ChatModel>().Subscribe(async x =>
            {
                if (x.EventType == Firebase.Database.Streaming.FirebaseEventType.Delete) { return; }

                if (x.Object != null && x.Object.User != null && x.Object.MessageContent != null)
                {
                    if (x.Object.Timestamp < connectionTimestamp) { return; }

                    if (x.Object.User == CurrentUsername) { return; }

                    var users = await FirebaseClient.Child("Users").OnceAsync<UserModel>();
                    var match = users.FirstOrDefault(u => u.Object.Username == x.Object.User);
                    var rooms = await FirebaseClient.Child("Rooms").OnceAsync<RoomModel>();
                    var roomMatch = rooms.FirstOrDefault(r => r.Object.Name == CurrentRoom);
                    if (roomMatch == null) { return; }
                    if (match == null || match.Object.isMuted || match.Object.isTerminated || UserState != AccountState.SignedIn) { return; }

                    bool isCustomRoom = CurrentRoom != "main-chat";
                    bool isRoomMod = roomMatch.Object.Mods != null && roomMatch.Object.Mods.Any(m => m.Equals(x.Object.User, StringComparison.OrdinalIgnoreCase));
                    bool isRoomOwner = roomMatch.Object.Owner != null && roomMatch.Object.Owner.Equals(x.Object.User, StringComparison.OrdinalIgnoreCase);

                    lock (ConsoleLock)
                    {
                        int currentTop = Console.CursorTop;

                        Console.SetCursorPosition(0, currentTop);
                        Console.Write(new string(' ', Console.WindowWidth));
                        Console.SetCursorPosition(0, currentTop);

                        if (match.Object.Username == "System" || x.Object.User == "System")
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.Write("[System]");
                            Console.ResetColor();
                            Console.WriteLine($": {x.Object.MessageContent}");
                        }
                        else if (match.Object.isAdmin)
                        {
                            Console.Write($"[@{x.Object.User} - ");
                            Console.ForegroundColor = ConsoleColor.Blue;
                            Console.Write("ADMIN");
                            Console.ResetColor();
                            Console.WriteLine($"]: {x.Object.MessageContent}");
                        }
                        else if (isCustomRoom && isRoomMod)
                        {
                            Console.Write($"[@{x.Object.User} - ");
                            Console.ForegroundColor = ConsoleColor.Magenta;
                            Console.Write("MOD");
                            Console.ResetColor();
                            Console.WriteLine($"]: {x.Object.MessageContent}");
                        }
                        else if (isCustomRoom && isRoomOwner)
                        {
                            Console.Write($"[@{x.Object.User} - ");
                            Console.ForegroundColor = ConsoleColor.Magenta;
                            Console.Write("OWNER");
                            Console.ResetColor();
                            Console.WriteLine($"]: {x.Object.MessageContent}");
                        }
                        else
                        {
                            Console.WriteLine($"[@{x.Object.User}]: {x.Object.MessageContent}");
                        }
                        FlashTaskbar();
                        Console.Write($"{CurrentInput}");
                    }
                }
            });
            await Task.CompletedTask;
        }

        static async Task DisposeTunnel()
        {
            Subscription?.Dispose();
        }

        static async Task JoinRoom(string room)
        {
            var users = await FirebaseClient.Child("Users").OnceAsync<UserModel>();
            var match = users.FirstOrDefault(u => u.Object.Username == CurrentUsername);
            var rooms = await FirebaseClient.Child("Rooms").OnceAsync<RoomModel>();
            var roomMatch = rooms.FirstOrDefault(r => r.Object.Name == room);
            if (match == null) { Error($"{CurrentUsername} couldn't be found in Database!"); return; }
            if (roomMatch == null) { Error($"{room} doesn't exist!"); return; }
            room = room.TrimStart('#');
            if (roomMatch.Object.BannedUsers.Contains(CurrentUsername))
            {
                Error("You have been banned from this room!");
                return;
            }
            var joinedRooms = match.Object.JoinedRooms ?? new List<string>();
            if (!joinedRooms.Contains(room))
            {
                joinedRooms.Add(room);
                await FirebaseClient.Child("Users").Child(match.Key).Child("JoinedRooms").PutAsync(joinedRooms);
            }
            await DisposeTunnel();
            await EnterRoom(room);
        }

        static async Task LeaveRoom(string room)
        {
            if (room == "main-chat")
            {
                Error("Cannot leave main-chat");
                return;
            }
            var users = await FirebaseClient.Child("Users").OnceAsync<UserModel>();
            var match = users.FirstOrDefault(u => u.Object.Username == CurrentUsername);
            if (match == null) { Error($"{CurrentUsername} couldn't be found in Database!"); return; }
            var joinedRooms = match.Object.JoinedRooms;
            if (joinedRooms.Contains(room))
            {
                joinedRooms.Remove(room);
                await FirebaseClient.Child("Users").Child(match.Key).Child("JoinedRooms").PutAsync(joinedRooms);
                if (CurrentRoom == room)
                {
                    Success($"Leaving {room}...");
                    await Task.Delay(3000);
                    await JoinRoom("main-chat");
                    return;
                }
                Success($"Left {room}");
            }
        }

        static async Task EnterRoom(string room)
        {
            var users = await FirebaseClient.Child("Users").OnceAsync<UserModel>();
            var match = users.FirstOrDefault(u => u.Object.Username == CurrentUsername);
            Console.Clear();
            Heading();
            CurrentRoom = room;
            Console.WriteLine($"Welcome, @{CurrentUsername}");
            Console.WriteLine($"Connecting to #{room}...");
            await StartTunnel();
            Console.Clear();
            Heading();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[Room: #{room}] - (@{CurrentUsername} - Connected)\n");
            Console.ResetColor();
            Task.Run(() => StartPresenceHeartbeat());
            var allMessages = await FirebaseClient.Child("Rooms").Child(CurrentRoom).Child("Messages").OnceAsync<ChatModel>();
            var allUsersList = await FirebaseClient.Child("Users").OnceAsync<UserModel>();
            var allRoomsList = await FirebaseClient.Child("Rooms").OnceAsync<RoomModel>();

            foreach (var msgNode in allMessages.OrderBy(m => m.Object.Timestamp))
            {
                var msg = msgNode.Object;
                if (msg != null && msg.User != null && msg.MessageContent != null)
                {
                    var senderMatch = allUsersList.FirstOrDefault(u => u.Object.Username == msg.User);
                    if (senderMatch != null && (senderMatch.Object.isMuted || senderMatch.Object.isTerminated))
                    {
                        continue;
                    }

                    bool isAdminSender = senderMatch != null && senderMatch.Object.isAdmin;
                    bool isSystemSender = senderMatch != null && senderMatch.Object.Username == "System";
                    bool isCustomRoom = CurrentRoom != "main-chat";
                    var roomMatch = isCustomRoom ? allRoomsList.FirstOrDefault(r => r.Object.Name.Equals(CurrentRoom, StringComparison.OrdinalIgnoreCase)) : null;

                    bool isRoomMod = roomMatch != null && roomMatch.Object.Mods != null && roomMatch.Object.Mods.Any(m => m.Equals(msg.User, StringComparison.OrdinalIgnoreCase));
                    bool isRoomOwner = roomMatch != null && roomMatch.Object.Owner != null && roomMatch.Object.Owner.Equals(msg.User, StringComparison.OrdinalIgnoreCase);

                    if (isSystemSender)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Write("[System]");
                        Console.ResetColor();
                        Console.WriteLine($": {msg.MessageContent}");
                    }
                    else if (isAdminSender)
                    {
                        Console.Write($"[@{msg.User} - ");
                        Console.ForegroundColor = ConsoleColor.Blue;
                        Console.Write("ADMIN");
                        Console.ResetColor();
                        Console.WriteLine($"]: {msg.MessageContent}");
                    }
                    else if (isCustomRoom && isRoomOwner)
                    {
                        Console.Write($"[@{msg.User} - ");
                        Console.ForegroundColor = ConsoleColor.Magenta;
                        Console.Write("ROOM OWNER");
                        Console.ResetColor();
                        Console.WriteLine($"]: {msg.MessageContent}");
                    }
                    else if (isCustomRoom && isRoomMod)
                    {
                        Console.Write($"[@{msg.User} - ");
                        Console.ForegroundColor = ConsoleColor.Magenta;
                        Console.Write("ROOM MOD");
                        Console.ResetColor();
                        Console.WriteLine($"]: {msg.MessageContent}");
                    }
                    else
                    {
                        Console.WriteLine($"[@{msg.User}]: {msg.MessageContent}");
                    }
                }
            }
            CurrentInput.Clear();

            while (UserState == AccountState.SignedIn)
            {
                if (Console.KeyAvailable)
                {
                    var keyInfo = Console.ReadKey(intercept: true);
                    lock (ConsoleLock)
                    {
                        if (keyInfo.Key == ConsoleKey.Enter)
                        {
                            string msg = CurrentInput.ToString().Trim();
                            CurrentInput.Clear();

                            int currentTop = Console.CursorTop;
                            Console.SetCursorPosition(0, currentTop);
                            Console.Write(new string(' ', Console.WindowWidth));
                            Console.SetCursorPosition(0, currentTop);

                            if (!string.IsNullOrWhiteSpace(msg))
                            {
                                bool isCustomRoom = CurrentRoom != "main-chat";
                                var roomMatch = isCustomRoom ? allRoomsList.FirstOrDefault(r => r.Object.Name.Equals(CurrentRoom, StringComparison.OrdinalIgnoreCase)) : null;
                                bool isSenderMod = roomMatch != null && roomMatch.Object.Mods != null && roomMatch.Object.Mods.Any(m => m.Equals(CurrentUsername, StringComparison.OrdinalIgnoreCase));
                                bool isSenderOwner = roomMatch != null && roomMatch.Object.Owner != null && roomMatch.Object.Owner.Equals(CurrentUsername, StringComparison.OrdinalIgnoreCase);
                                bool isAdmin = match != null && match.Object.isAdmin;

                                if (msg.StartsWith("/") && (isAdmin || isSenderOwner || isSenderMod))
                                {
                                    Console.ForegroundColor = ConsoleColor.DarkGray;
                                    Console.Write($"[@{CurrentUsername} - ");
                                    if (isAdmin)
                                    {
                                        Console.ForegroundColor = ConsoleColor.Blue;
                                        Console.Write("ADMIN");
                                    }
                                    else if (isCustomRoom && isSenderOwner)
                                    {
                                        Console.ForegroundColor = ConsoleColor.Magenta;
                                        Console.Write("ROOM OWNER");
                                    }
                                    else if (isCustomRoom && isSenderMod)
                                    {
                                        Console.ForegroundColor = ConsoleColor.Magenta;
                                        Console.Write("ROOM MOD");
                                    }

                                    Console.ResetColor();
                                    Console.ForegroundColor = ConsoleColor.DarkGray;
                                    Console.WriteLine($"]: {msg} [Command messages are hidden]");
                                    Console.ResetColor();
                                }
                                else
                                {
                                    if (isAdmin)
                                    {
                                        Console.Write($"[@{CurrentUsername} - ");
                                        Console.ForegroundColor = ConsoleColor.Blue;
                                        Console.Write("ADMIN");
                                        Console.ResetColor();
                                        Console.WriteLine($"]: {msg}");
                                    }
                                    else if (isCustomRoom && isSenderOwner)
                                    {
                                        Console.Write($"[@{CurrentUsername} - ");
                                        Console.ForegroundColor = ConsoleColor.Magenta;
                                        Console.Write("ROOM OWNER");
                                        Console.ResetColor();
                                        Console.WriteLine($"]: {msg}");
                                    }
                                    else if (isCustomRoom && isSenderMod)
                                    {
                                        Console.Write($"[@{CurrentUsername} - ");
                                        Console.ForegroundColor = ConsoleColor.Magenta;
                                        Console.Write("ROOM MOD");
                                        Console.ResetColor();
                                        Console.WriteLine($"]: {msg}");
                                    }
                                    else
                                    {
                                        Console.WriteLine($"[@{CurrentUsername}]: {msg}");
                                    }
                                }
                                Task.Run(async () => await SendAsync(msg, CurrentRoom));
                            }
                        }
                        else if (keyInfo.Key == ConsoleKey.Backspace)
                        {
                            if (CurrentInput.Length > 0)
                            {
                                CurrentInput.Remove(CurrentInput.Length - 1, 1);
                                Console.Write("\b \b");
                            }
                        }
                        else if (!char.IsControl(keyInfo.KeyChar))
                        {
                            CurrentInput.Append(keyInfo.KeyChar);
                            Console.Write(keyInfo.KeyChar);
                        }
                    }
                }
                else
                {
                    await Task.Delay(25);
                }
            }
        }

        static async Task SendAsync(string msg, string room)
        {
            if (msg.StartsWith("//")) { await AdminCommand(msg); return; }
            if (msg.StartsWith("/") || msg.StartsWith("#")) { await ChatCommand(msg); return; }
            var users = await FirebaseClient.Child("Users").OnceAsync<UserModel>();
            var match = users.FirstOrDefault(u => u.Object.Username == CurrentUsername);
            var rooms = await FirebaseClient.Child("Rooms").OnceAsync<RoomModel>();
            var roomMatch = rooms.FirstOrDefault(u => u.Object.Name == room);
            if (match == null) { Error("A fatal error has occurred, your account is returning null in database, report this to vsmocha."); return; }
            if (match.Object.isTerminated) { Environment.Exit(0); return; }
            if (match.Object.isMuted) { Error("You have been muted by a global moderator of TermChat, you cannot speak."); return; }
            if (roomMatch.Object.MutedUsers.Contains(CurrentUsername)) { Error("You have been muted by the moderators of this room, you cannot speak."); return; }
            if (roomMatch.Object.BannedUsers.Contains(CurrentUsername)) { Error("You have been banned by the moderators of this room."); await JoinRoom("main-chat"); return; }
            if (UserState == AccountState.SignedIn)
            {
                var newMsg = new ChatModel
                {
                    User = match.Object.Username,
                    MessageContent = msg,
                    Timestamp = DateTime.UtcNow.Ticks
                };
                await FirebaseClient.Child("Rooms").Child(room).Child("Messages").PostAsync(newMsg);
                await DiscordLog($"Chat Log - #{room}", $"**Username:** @{match.Object.Username}\n**Message:** {msg}\n**Time:** {DateTime.Now}", ChatLogsWebhook, DiscordLogColors.Blue);
                return;
            }
        }

        static async Task ChatCommand(string cmd)
        {
            var users = await FirebaseClient.Child("Users").OnceAsync<UserModel>();
            var match = users.FirstOrDefault(u => u.Object.Username == CurrentUsername);
            await DiscordLog($"Chat Command Log - #{CurrentRoom}", $"**Username:** @{match?.Object.Username}\n**Message:** {cmd}\n**Time:** {DateTime.Now}", ChatCommandsWebhook, DiscordLogColors.Blue);
            var split = cmd.Split(" ");

            if (cmd.StartsWith("#"))
            {
                var roomName = cmd.TrimStart('#').ToLower();
                var rooms = await FirebaseClient.Child("Rooms").OnceAsync<RoomModel>();
                var roomMatch = rooms.FirstOrDefault(r => r.Object.Name != null && r.Object.Name.Equals(roomName, StringComparison.OrdinalIgnoreCase));
                var usersList = await FirebaseClient.Child("Users").OnceAsync<UserModel>();
                var userMatch = usersList.FirstOrDefault(u => u.Object.Username.Equals(CurrentUsername, StringComparison.OrdinalIgnoreCase));
                var userRooms = userMatch?.Object.JoinedRooms ?? new List<string>();
                if (userRooms.Any(r => r.Equals(roomName, StringComparison.OrdinalIgnoreCase)))
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"Switching..");
                    Console.ResetColor();
                    await JoinRoom(roomName);
                    return;
                }
                else
                {
                    Console.WriteLine($"You are not a member of #{roomName}! You must /join it before switching.");
                }
                return;
            }

            switch (split[0])
            {
                case "/rooms":
                    {
                        var rooms = await FirebaseClient.Child("Rooms").OnceAsync<RoomModel>();
                        if (rooms == null || !rooms.Any())
                        {
                            Console.WriteLine("\nNo rooms found in the database!");
                            break;
                        }
                        foreach (var room in rooms)
                        {
                            Console.WriteLine($"{room.Object.Name} | Owner: {room.Object.Owner} | Password-Protected: {room.Object.isPassword}");
                        }
                        Console.ForegroundColor = ConsoleColor.Blue;
                        Console.WriteLine("\nUse /join #room-name [Password - Leave blank if none]");
                        Console.ResetColor();
                        break;
                    }
                case "/createroom":
                    {
                        if (split.Length > 3 || split.Length < 2)
                        {
                            Error("Usage: /createroom #room-name-here 1234 (<-- Password LEAVE BLANK FOR NO PASSWORD)");
                            return;
                        }
                        string name = Regex.Replace(split[1], @"[^a-zA-Z0-9-]", "").Replace(" ", "-");
                        var rooms = await FirebaseClient.Child("Rooms").OnceAsync<RoomModel>();
                        var roomMatch = rooms.FirstOrDefault(r => r.Object.Name == name);
                        if (roomMatch != null)
                        {
                            Error("This name already exists!");
                            return;
                        }
                        string password = "";
                        bool isPass = false;
                        if (split.Length == 3)
                        {
                            password = split[2].Replace(" ", "");
                            isPass = true;
                        } else
                        {
                            password = "";
                            isPass = false;

                        }
                        var newRoom = new RoomModel
                        {
                            Name = name,
                            Owner = CurrentUsername,
                            Password = password,
                            isPassword = isPass,
                        };
                        await FirebaseClient.Child("Rooms").Child(name).PutAsync(newRoom);
                        Success($"Successfully created #{name}!");
                        break;
                    }
                case "/deleteroom":
                    {
                        if (split.Length > 2 || split.Length <= 1)
                        {
                            Error("Usage: /deleteroom [RoomName]");
                            return;
                        }
                        string roomName = split[1] ?? "";
                        var rooms = await FirebaseClient.Child("Rooms").OnceAsync<RoomModel>();
                        var roomMatch = rooms.FirstOrDefault(r => r.Object.Name == roomName);
                        if (roomMatch == null) { Error($"{roomName} doesn't exist!"); return; }
                        if (roomMatch.Object.Owner != CurrentUsername) { Error("You don't own this room! If you want to leave a room, use /leave [RoomName]."); return; }
                        await FirebaseClient.Child("Rooms").Child(roomMatch.Key).DeleteAsync();
                        break;
                    }
                case "/join":
                    {
                        var roomName = split[1]?.TrimStart('#') ?? "";
                        if (string.IsNullOrEmpty(roomName) || split.Length > 3 || split.Length <= 1)
                        {
                            Error("Usage: /join #room-name password <-- Leave blank if none");
                            return;
                        }

                        string password = split.Length > 2 ? (split[2] ?? "") : "";
                        var rooms = await FirebaseClient.Child("Rooms").OnceAsync<RoomModel>();
                        var roomMatch = rooms.FirstOrDefault(r => r.Object.Name.Equals(roomName, StringComparison.OrdinalIgnoreCase));

                        if (roomMatch == null)
                        {
                            Error($"#{roomName} doesn't exist!");
                            return;
                        }

                        if (roomMatch.Object.isPassword)
                        {
                            if (string.IsNullOrEmpty(password))
                            {
                                Error($"#{roomName} requires a password!");
                                return;
                            }

                            if (roomMatch.Object.Password != password)
                            {
                                Error("Password incorrect!");
                                return;
                            }
                        }
                        Success($"Joining #{roomName}...");
                        await JoinRoom(roomName);
                        break;
                    }
                case "/leave":
                    {
                        if (split.Length > 2 || split.Length <= 1)
                        {
                            Error("Usage: /leave #room-name");
                            return;
                        }
                        string roomName = split[1].TrimStart('#') ?? "";
                        var rooms = await FirebaseClient.Child("Rooms").OnceAsync<RoomModel>();
                        var roomMatch = rooms.FirstOrDefault(r => r.Object.Name == roomName);
                        var userMatch = users.FirstOrDefault(u => u.Object.Username == CurrentUsername);
                        var userRooms = userMatch?.Object.JoinedRooms ?? new List<string>();
                        if (!userRooms.Contains(roomName) && roomMatch != null)
                        {
                            Error($"You aren't a member of #{roomName}");
                            return;
                        }
                        else if (userMatch == null)
                        {
                            Error($"{roomName} doesn't exist!");
                            return;
                        }
                        await LeaveRoom(roomName);
                        break;
                    }
                case "/mod":
                    {
                        if (split.Length < 3)
                        {
                            Error("Usage: /mod <add/remove> <username>");
                            return;
                        }

                        var type = split[1]?.ToLower() ?? "";
                        if (type != "add" && type != "remove")
                        {
                            Error("Usage: /mod <add/remove> <username>");
                            return;
                        }

                        var rooms = await FirebaseClient.Child("Rooms").OnceAsync<RoomModel>();
                        var roomMatch = rooms.FirstOrDefault(r => r.Object.Name.Equals(CurrentRoom, StringComparison.OrdinalIgnoreCase));

                        if (roomMatch == null)
                        {
                            Error("Room returned null, report this to vsmocha.");
                            return;
                        }

                        if (string.IsNullOrEmpty(roomMatch.Object.Owner) || !roomMatch.Object.Owner.Equals(CurrentUsername, StringComparison.OrdinalIgnoreCase))
                        {
                            Error("Only the owner can add or remove moderators!");
                            return;
                        }

                        string targetInput = split[2] ?? "";
                        var allUsers = await FirebaseClient.Child("Users").OnceAsync<UserModel>();
                        var userMatch = allUsers.FirstOrDefault(u => u.Object.Username.Equals(targetInput, StringComparison.OrdinalIgnoreCase));

                        if (userMatch == null)
                        {
                            Error($"{targetInput} doesn't exist!");
                            return;
                        }

                        string correctUsername = userMatch.Object.Username;
                        roomMatch.Object.Mods ??= new List<string>();
                        bool modified = false;

                        switch (type)
                        {
                            case "add":
                                {
                                    if (!roomMatch.Object.Mods.Any(m => m.Equals(correctUsername, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        roomMatch.Object.Mods.Add(correctUsername);
                                        modified = true;
                                    }
                                    break;
                                }
                            case "remove":
                                {
                                    var existingMod = roomMatch.Object.Mods.FirstOrDefault(m => m.Equals(correctUsername, StringComparison.OrdinalIgnoreCase));
                                    if (existingMod != null)
                                    {
                                        roomMatch.Object.Mods.Remove(existingMod);
                                        modified = true;
                                    }
                                    break;
                                }
                        }

                        if (modified)
                        {
                            await FirebaseClient.Child("Rooms").Child(roomMatch.Key).Child("Mods").PutAsync(roomMatch.Object.Mods);
                        }
                        break;
                    }
                case "/mute":
                    {
                        if (split.Length > 2 || split.Length < 2)
                        {
                            Error("Usage: /mute (username)");
                            return;
                        }
                        string username = split[1] ?? "";
                        var rooms = await FirebaseClient.Child("Rooms").OnceAsync<RoomModel>();
                        var roomMatch = rooms.FirstOrDefault(r => r.Object.Name == CurrentRoom);
                        if (roomMatch == null) { return; }
                        var userMatch = users.FirstOrDefault(u => u.Object.Username == username);
                        var userRooms = userMatch?.Object.JoinedRooms ??= new List<string>();
                        if (userRooms == null) { return; }
                        bool isOwner = roomMatch.Object.Owner?.Equals(CurrentUsername, StringComparison.OrdinalIgnoreCase) ?? false;
                        bool isMod = roomMatch.Object.Mods != null && roomMatch.Object.Mods.Any(m => m.Equals(CurrentUsername, StringComparison.OrdinalIgnoreCase));
                        if (!isMod && !isOwner)
                        {
                            Error("Only room moderators can moderate users!");
                            return;
                        }
                        if (userMatch == null)
                        {
                            Error($"{username} doesn't exist!");
                            return;
                        }
                        if (!userRooms.Contains(CurrentRoom))
                        {
                            Error($"{username} isn't a member of this room!");
                            return;
                        }
                        var mutedUsers = roomMatch.Object.MutedUsers ?? new List<string>();
                        if (mutedUsers.Contains(username))
                        {
                            Error($"{username} is already muted!");
                            return;
                        }
                        mutedUsers.Add(username);
                        await FirebaseClient.Child("Rooms").Child(roomMatch.Key).Child("MutedUsers").PutAsync(mutedUsers);
                        Success($"Muted {username}");
                        break;
                    }
                case "/unmute":
                    {
                        if (split.Length > 2 || split.Length < 2)
                        {
                            Error("Usage: /unmute (username)");
                            return;
                        }
                        string username = split[1] ?? "";
                        var rooms = await FirebaseClient.Child("Rooms").OnceAsync<RoomModel>();
                        var roomMatch = rooms.FirstOrDefault(r => r.Object.Name == CurrentRoom);
                        if (roomMatch == null) { return; }
                        var userMatch = users.FirstOrDefault(u => u.Object.Username == username);
                        bool isOwner = roomMatch.Object.Owner?.Equals(CurrentUsername, StringComparison.OrdinalIgnoreCase) ?? false;
                        bool isMod = roomMatch.Object.Mods != null && roomMatch.Object.Mods.Any(m => m.Equals(CurrentUsername, StringComparison.OrdinalIgnoreCase));
                        if (!isMod && !isOwner)
                        {
                            Error("Only room moderators can moderate users!");
                            return;
                        }
                        if (userMatch == null)
                        {
                            Error($"{username} doesn't exist!");
                            return;
                        }
                        var mutedUsers = roomMatch.Object.MutedUsers ??= new List<string>();
                        if (roomMatch.Object.MutedUsers.Contains(username))
                        {
                            mutedUsers.Remove(username);
                            await FirebaseClient.Child("Rooms").Child(roomMatch.Key).Child("MutedUsers").PutAsync(mutedUsers);
                            Success($"Unmuted {username}");
                        }
                        break;
                    }
                case "/ban":
                    {
                        if (split.Length > 2 || split.Length < 2)
                        {
                            Error("Usage: /ban (username)");
                            return;
                        }
                        string username = split[1] ?? "";
                        var rooms = await FirebaseClient.Child("Rooms").OnceAsync<RoomModel>();
                        var roomMatch = rooms.FirstOrDefault(r => r.Object.Name == CurrentRoom);
                        if (roomMatch == null) { return; }
                        var userMatch = users.FirstOrDefault(u => u.Object.Username == username);
                        var userRooms = userMatch?.Object.JoinedRooms ??= new List<string>();
                        if (userRooms == null) { return; }
                        bool isOwner = roomMatch.Object.Owner?.Equals(CurrentUsername, StringComparison.OrdinalIgnoreCase) ?? false;
                        bool isMod = roomMatch.Object.Mods != null && roomMatch.Object.Mods.Any(m => m.Equals(CurrentUsername, StringComparison.OrdinalIgnoreCase));
                        if (!isMod && !isOwner)
                        {
                            Error("Only room moderators can moderate users!");
                            return;
                        }
                        if (userMatch == null)
                        {
                            Error($"{username} doesn't exist!");
                            return;
                        }
                        var bannedUsers = roomMatch.Object.BannedUsers ?? new List<string>();
                        if (bannedUsers.Contains(username))
                        {
                            Error($"{username} is already banned!");
                            return;
                        }
                        bannedUsers.Add(username);
                        await FirebaseClient.Child("Rooms").Child(roomMatch.Key).Child("BannedUsers").PutAsync(bannedUsers);
                        Success($"Banned {username}");
                        break;
                    }
                case "/unban":
                    {
                        if (split.Length > 2 || split.Length < 2)
                        {
                            Error("Usage: /unban (username)");
                            return;
                        }
                        string username = split[1] ?? "";
                        var rooms = await FirebaseClient.Child("Rooms").OnceAsync<RoomModel>();
                        var roomMatch = rooms.FirstOrDefault(r => r.Object.Name == CurrentRoom);
                        if (roomMatch == null) { return; }
                        var userMatch = users.FirstOrDefault(u => u.Object.Username == username);
                        bool isOwner = roomMatch.Object.Owner?.Equals(CurrentUsername, StringComparison.OrdinalIgnoreCase) ?? false;
                        bool isMod = roomMatch.Object.Mods != null && roomMatch.Object.Mods.Any(m => m.Equals(CurrentUsername, StringComparison.OrdinalIgnoreCase));
                        if (!isMod && !isOwner)
                        {
                            Error("Only room moderators can moderate users!");
                            return;
                        }
                        if (userMatch == null)
                        {
                            Error($"{username} doesn't exist!");
                            return;
                        }
                        var bannedUsers = roomMatch.Object.BannedUsers ??= new List<string>();
                        if (roomMatch.Object.BannedUsers.Contains(username))
                        {
                            bannedUsers.Remove(username);
                            await FirebaseClient.Child("Rooms").Child(roomMatch.Key).Child("BannedUsers").PutAsync(bannedUsers);
                            Success($"Unbanned {username}");
                        }
                        break;
                    }
                case "/cmds":
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine("\n       [ Commands ]\n");
                        Console.WriteLine("   #[RoomName] -- Switch to a room you're a member of | Example: #main-chat (Switches you to #main-chat)");
                        Console.WriteLine("   /rooms -- Show a list of all rooms");
                        Console.WriteLine("   /join #room-name [password - Leave blank if none] -- Join a room");
                        Console.WriteLine("   /createroom #room-name [password - Leave blank if none] -- Create a room");
                        Console.WriteLine("   /deleteroom #room-name -- Delete a room");
                        Console.WriteLine("   /deleteroom #room-name -- Delete a room");
                        Console.WriteLine("   /leave #room-name -- Leave a room");
                        Console.WriteLine("   /mod <add/remove> [username] -- Add a user as a room moderator");
                        Console.WriteLine("   /mute [username] -- Mute a user from talking in your room");
                        Console.WriteLine("   /unmute [username] -- Unmute a muted user");
                        Console.WriteLine("   /ban [username] -- Ban a user from your room");
                        Console.WriteLine("   /unban [username] -- Ban a user from your room");
                        Console.WriteLine("   /report [username] [reason] -- COMING SOON!");
                        Console.WriteLine();
                        Console.ResetColor();
                        break;
                    }
            }
        }

        static async Task AdminCommand(string cmd)
        {
            var users = await FirebaseClient.Child("Users").OnceAsync<UserModel>();
            var match = users.FirstOrDefault(u => u.Object.Username == CurrentUsername);
            if (match != null && match.Object.isAdmin == false) { return; }
            await DiscordLog("Admin Command Log", $"**Username:** @{CurrentUsername}\n**Command:** {cmd}\n**Time:** {DateTime.Now}", AdminLogsWebhook, DiscordLogColors.Blue);
            var split = cmd.Split(" ");
            switch (split[0])
            {
                case "//terminate":
                    {
                        string target = split[1];
                        if (target == null) { Error("Usage: //terminate (or /ban) {username}"); return; }
                        var search = users.FirstOrDefault(u => u.Object.Username == target);
                        if (search == null)
                        {
                            Error($"{split[1]} not found.");
                            return;
                        }
                        else
                        {
                            var updatedUser = search.Object;
                            updatedUser.isTerminated = true;
                            await FirebaseClient.Child("Users").Child(search.Key).PutAsync(updatedUser);
                            Success($"{target} has been terminated");
                        }
                        break;
                    }
                case "//ban":
                    {
                        string target = split[1];
                        if (target == null) { Error("Usage: //terminate (or /ban) {username}"); return; }
                        var search = users.FirstOrDefault(u => u.Object.Username == target);
                        if (search == null)
                        {
                            Error($"{split[1]} not found.");
                            return;
                        }
                        else
                        {
                            var updatedUser = search.Object;
                            updatedUser.isTerminated = true;
                            await FirebaseClient.Child("Users").Child(search.Key).PutAsync(updatedUser);
                            Success($"{target} has been terminated");
                        }
                        break;
                    }
                case "//unterminate":
                    {
                        string target = split[1];
                        if (target == null) { Error("Usage: //unterminate (or /unban) {username}"); return; }
                        var search = users.FirstOrDefault(u => u.Object.Username == target);
                        if (search == null)
                        {
                            Error($"{split[1]} not found.");
                            return;
                        }
                        else
                        {
                            var updatedUser = search.Object;
                            updatedUser.isTerminated = false;
                            await FirebaseClient.Child("Users").Child(search.Key).PutAsync(updatedUser);
                            Success($"{target} has been unterminated");
                        }
                        break;
                    }
                case "//unban":
                    {
                        string target = split[1];
                        if (target == null) { Error("Usage: //unterminate (or /unban) {username}"); return; }
                        var search = users.FirstOrDefault(u => u.Object.Username == target);
                        if (search == null)
                        {
                            Error($"{split[1]} not found.");
                            return;
                        }
                        else
                        {
                            var updatedUser = search.Object;
                            updatedUser.isTerminated = false;
                            await FirebaseClient.Child("Users").Child(search.Key).PutAsync(updatedUser);
                            Success($"{target} has been unterminated");
                        }
                        break;
                    }
                case "//mute":
                    {
                        string target = split[1];
                        if (target == null) { Error("Usage: //mute {username}"); return; }
                        var search = users.FirstOrDefault(u => u.Object.Username == target);
                        if (search == null)
                        {
                            Error($"{split[1]} not found.");
                            return;
                        }
                        else
                        {
                            var updatedUser = search.Object;
                            updatedUser.isMuted = true;
                            await FirebaseClient.Child("Users").Child(search.Key).PutAsync(updatedUser);
                            Success($"{target} has been muted");
                        }
                        break;
                    }
                case "//unmute":
                    {
                        string target = split[1];
                        if (target == null) { Error("Usage: //unmute {username}"); return; }
                        var search = users.FirstOrDefault(u => u.Object.Username == target);
                        if (search == null)
                        {
                            Error($"{split[1]} not found.");
                            return;
                        }
                        else
                        {
                            var updatedUser = search.Object;
                            updatedUser.isMuted = false;
                            await FirebaseClient.Child("Users").Child(search.Key).PutAsync(updatedUser);
                            Success($"{target} has been unmuted");
                        }
                        break;
                    }
                case "//admin":
                    {
                        string target = split[1];
                        if (target == null) { Error("Usage: //admin {username}"); return; }
                        var search = users.FirstOrDefault(u => u.Object.Username == target);
                        if (search == null)
                        {
                            Error($"{split[1]} not found.");
                            return;
                        }
                        else
                        {
                            var updatedUser = search.Object;
                            updatedUser.isAdmin = true;
                            await FirebaseClient.Child("Users").Child(search.Key).PutAsync(updatedUser);
                            Success($"{target} has been added as an admin");
                        }
                        break;
                    }
                case "//unadmin":
                    {
                        string target = split[1];
                        if (target == null) { Error("Usage: //unadmin {username}"); return; }
                        var search = users.FirstOrDefault(u => u.Object.Username == target);
                        if (search == null)
                        {
                            Error($"{split[1]} not found.");
                            return;
                        }
                        else
                        {
                            var updatedUser = search.Object;
                            updatedUser.isAdmin = false;
                            await FirebaseClient.Child("Users").Child(search.Key).PutAsync(updatedUser);
                            Success($"{target} has been removed as an admin");
                        }
                        break;
                    }
                case "//announce":
                    {
                        var announcement = string.Join(" ", split.Skip(1));
                        if (string.IsNullOrEmpty(announcement))
                        {
                            Error("Usage: //announce [message]");
                            return;
                        }
                        var ann = new ChatModel
                        {
                            User = "System",
                            MessageContent = announcement,
                            Timestamp = DateTime.UtcNow.Ticks
                        };
                        await FirebaseClient.Child("Rooms").Child(CurrentRoom).Child("Messages").PostAsync(ann);
                        break;
                    }
                case "//cmds":
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine("\n       [ Commands ]\n");
                        Console.WriteLine("   //terminate [username]  OR  //ban -- Terminate (permanently ban) a user's account");
                        Console.WriteLine("   //unterminate [username]  OR  //unban -- Remove a user's termination");
                        Console.WriteLine("   //mute [username] -- Globally mute a user everywhere");
                        Console.WriteLine("   //unmute [username] -- Globally unmute a user everywhere");
                        Console.WriteLine("   //announce -- Send a [System] announcement");
                        Console.WriteLine("   //admin #room-name [password - Leave blank if none] -- Join a room");
                        Console.WriteLine();
                        Console.ResetColor();
                        break;
                    }
            }
        }
    }

    public class ChatModel
    {
        public string User { get; set; }
        public string MessageContent { get; set; }
        public long Timestamp { get; set; }
        public string Room { get; set; }
    }
    public class UserModel
    { 
        public string Username { get; set; }
        public string Password { get; set; }
        public long LastSeen { get; set; }
        public List<string> JoinedRooms { get; set; } = new List<string>();
        public string AppVersion { get; set; }
        public bool isTerminated { get; set; }
        public bool isMuted { get; set; } 
        public bool isAdmin { get; set; }
    }
    public class RoomModel
    {
        public string Name { get; set; }
        public string Password { get; set; }
        public bool isPassword { get; set; }
        public string Owner { get; set; }
        public List<string> Mods { get; set; } = new List<string>();
        public List<string> MutedUsers { get; set; } = new List<string>();
        public List<string> BannedUsers { get; set; } = new List<string>();
    }
}
